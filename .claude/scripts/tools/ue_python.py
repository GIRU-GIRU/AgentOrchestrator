"""Run a Python file inside the already-running editor of this project.

    python .claude/scripts/tools/ue_python.py <script.py> [--set key=value ...] [--timeout 900]

Uses Unreal's own remote-execution channel (UDP discovery on 127.0.0.1, then a TCP
command connection), so the script gets the full `unreal` module. The in-editor MCP
toolsets only expose the tools they were written for and cannot, for instance,
import an animation-only FBX - this can.

Needs `bRemoteExecution=True` under
`[/Script/PythonScriptPlugin.PythonScriptPluginSettings]` in Config/DefaultEngine.ini.
The plugin reads that key at startup, so if the editor was launched before the key
existed, it costs one editor restart.

The project is the single *.uproject in the project root (the folder that holds .claude/),
or the file ORCH_UPROJECT names. Unreal's remote_execution.py is found through
UE_PYTHON_PLUGIN_DIR, UE_ROOT, the engine the project's EngineAssociation names, or any
engine the registry lists as installed.

`--set k=v` pairs arrive in the target script as a module-level `ARGS` dict. Read them
with `ARGS = globals().get("ARGS", {})` so the script still runs when pasted into the
editor's own Python console.

Under an agent, a script without `ORCH: read-only` in its first 2000 bytes runs only while
that agent holds the editor lane (`.claude/scripts/orch.cmd guard`), so a background test
chain cannot outlive its grant. A human in a plain terminal has no agent to ask about.
"""
import argparse
import glob
import json
import os
import shlex
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
ORCH_CMD = os.path.abspath(os.path.join(HERE, "..", "orch.cmd"))
ORCH_GUARD_TIMEOUT_SECONDS = 5
READ_ONLY_MARKER = "ORCH: read-only"
READ_ONLY_HEADER_BYTES = 2000
EDITOR_TARGET_MARKER = "ORCH_EDITOR_TARGET="
PLUGIN_PYTHON = os.path.join("Engine", "Plugins", "Experimental", "PythonScriptPlugin", "Content", "Python")
INSTALLS_KEY = r"SOFTWARE\EpicGames\Unreal Engine"
BUILDS_KEY = r"Software\Epic Games\Unreal Engine\Builds"


def find_uproject():
    """ORCH_UPROJECT (absolute, or relative to the project root), else the single *.uproject in the root; '' otherwise."""
    override = os.environ.get("ORCH_UPROJECT")
    if override:
        return os.path.abspath(os.path.join(PROJECT_DIR, override))
    found = glob.glob(os.path.join(glob.escape(PROJECT_DIR), "*.uproject"))
    return os.path.abspath(found[0]) if len(found) == 1 else ""


def engine_association(uproject):
    try:
        with open(uproject, encoding="utf-8-sig") as f:
            association = json.load(f).get("EngineAssociation") or ""
    except (OSError, ValueError, AttributeError):
        return ""
    return association if isinstance(association, str) else ""


def registry_engine_roots(association):
    """The engine `association` names (a launcher version or a registered source build) first, then every launcher install."""
    try:
        import winreg
    except ImportError:
        return []
    lookups = []
    if association:
        lookups.append((winreg.HKEY_CURRENT_USER, BUILDS_KEY, association))
        lookups.append((winreg.HKEY_LOCAL_MACHINE, INSTALLS_KEY + "\\" + association, "InstalledDirectory"))
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, INSTALLS_KEY) as installs:
            index = 0
            while True:
                try:
                    version = winreg.EnumKey(installs, index)
                except OSError:
                    break
                lookups.append((winreg.HKEY_LOCAL_MACHINE, INSTALLS_KEY + "\\" + version, "InstalledDirectory"))
                index += 1
    except OSError:
        pass
    roots = []
    for hive, key, value in lookups:
        try:
            with winreg.OpenKey(hive, key) as handle:
                roots.append(winreg.QueryValueEx(handle, value)[0])
        except OSError:
            continue
    return roots


def find_engine_python():
    """Directory holding Unreal's remote_execution.py, or None."""
    override = os.environ.get("UE_PYTHON_PLUGIN_DIR")
    if override and os.path.isfile(os.path.join(override, "remote_execution.py")):
        return override

    association = engine_association(find_uproject())
    roots = [os.environ.get("UE_ROOT") or ""] + registry_engine_roots(association)
    if association and os.environ.get("ProgramFiles"):
        roots.append(os.path.join(os.environ["ProgramFiles"], "Epic Games", "UE_" + association))

    for root in roots:
        cand = os.path.join(root, PLUGIN_PYTHON)
        if root and os.path.isfile(os.path.join(cand, "remote_execution.py")):
            return cand
    return None


_engine_py = find_engine_python()
if not _engine_py:
    raise SystemExit(
        "Could not locate Unreal's PythonScriptPlugin Content/Python folder. "
        "Set UE_ROOT to the engine folder, or UE_PYTHON_PLUGIN_DIR to that folder itself.")
sys.path.insert(0, _engine_py)
import remote_execution as rex  # noqa: E402


PROJECT_ROOT = os.path.normcase(PROJECT_DIR)


def _is_this_project(node):
    root = node.get("project_root")
    return bool(root) and os.path.normcase(os.path.abspath(root)) == PROJECT_ROOT


def _is_editor_connection(conn):
    # Game/server processes advertise the same project as the editor. Editor APIs can crash them.
    probe = conn.run_command(
        'import json, os, unreal; print("%s" + json.dumps({"pid": os.getpid(), '
        '"command_line": unreal.SystemLibrary.get_command_line()}))' % EDITOR_TARGET_MARKER,
        unattended=True, exec_mode=rex.MODE_EXEC_STATEMENT, raise_on_failure=False)
    if not probe.get("success"):
        return False
    for line in probe.get("output") or []:
        output = line.get("output", "").strip()
        if not output.startswith(EDITOR_TARGET_MARKER):
            continue
        try:
            target = json.loads(output[len(EDITOR_TARGET_MARKER):])
            tokens = [token.strip('"').lower() for token in shlex.split(target["command_line"], posix=False)]
        except (ValueError, KeyError, TypeError):
            return False
        return not any(token in ("-game", "-server", "-pieviaconsole") or token.startswith("-run=")
                       for token in tokens)
    return False


def is_read_only(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return READ_ONLY_MARKER in f.read(READ_ONLY_HEADER_BYTES)
    except OSError:
        return False


def lane_refusal(script):
    """The orchestrator's DENIED message when this agent holds no lane grant, else None.
    Anything short of a clear DENIED lets the script run."""
    if is_read_only(script) or not os.path.isfile(ORCH_CMD):
        return None
    try:
        # A file, not a pipe: after a timeout, reading a pipe would wait for the orchestrator process the kill does not reach.
        with tempfile.TemporaryFile() as output:
            done = subprocess.run([ORCH_CMD, "guard", "--operation", "Work"], stdin=subprocess.DEVNULL, stdout=output, stderr=output,
                                  timeout=ORCH_GUARD_TIMEOUT_SECONDS, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            output.seek(0)
            text = output.read().decode("ascii", "ignore").strip()
    except Exception:
        return None
    return text if done.returncode != 0 and text.startswith("DENIED") else None


def run_file(path, args=None, timeout=900.0):
    """Execute `path` in the editor with `args` exposed to it as ARGS.

    Returns the remote-execution result dict: `success`, `result`, `output`."""
    body = open(path, "r", encoding="utf-8").read()
    staged = os.path.abspath(path)
    if args:
        fd, staged = tempfile.mkstemp(suffix=".py", prefix="ue_python_")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write("ARGS = %r\n" % (dict(args),) + body)

    conn = rex.RemoteExecution()
    conn.start()
    try:
        # Discovery includes standalone games and servers, even when a real editor is also open.
        deadline = time.time() + 20
        node = None
        checked = set()
        while node is None and time.time() < deadline:
            for candidate in conn.remote_nodes:
                if not _is_this_project(candidate) or candidate["node_id"] in checked:
                    continue
                checked.add(candidate["node_id"])
                try:
                    conn.open_command_connection(candidate["node_id"])
                    if _is_editor_connection(conn):
                        node = candidate
                        break
                except Exception:
                    pass
                conn.close_command_connection()
            if node is None:
                time.sleep(0.5)
        if node is None:
            others = ", ".join(n.get("project_name", "?") for n in conn.remote_nodes) or "none"
            raise RuntimeError(
                "No editor on this project answered remote execution (other editors: %s). Either no "
                "editor is open on this project (game/server nodes are excluded), or bRemoteExecution is off in "
                "DefaultEngine.ini (and the editor has not been restarted since it was "
                "turned on)." % others)
        # The default command-channel timeout is far shorter than a batch import.
        try:
            conn._command_connection._command_channel_socket.settimeout(timeout)
        except Exception:
            pass
        return conn.run_command(staged, unattended=True,
                                exec_mode=rex.MODE_EXEC_FILE, raise_on_failure=False)
    finally:
        conn.stop()
        if args and staged != os.path.abspath(path):
            try:
                os.unlink(staged)
            except OSError:
                pass


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("script")
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                    help="value placed in the script's ARGS dict; repeatable")
    ap.add_argument("--timeout", type=float, default=900.0)
    opts = ap.parse_args()

    args = {}
    for pair in opts.set:
        k, _, v = pair.partition("=")
        args[k] = v

    refusal = lane_refusal(opts.script)
    if refusal:
        sys.stdout.write(refusal + "\n")
        return 1

    res = run_file(opts.script, args, opts.timeout)
    for line in res.get("output") or []:
        sys.stdout.write("[%s] %s" % (line["type"], line["output"]))
        if not line["output"].endswith("\n"):
            sys.stdout.write("\n")
    if not res.get("success"):
        sys.stdout.write("FAILED: %s\n" % res.get("result"))
    return 0 if res.get("success") else 1


if __name__ == "__main__":
    sys.exit(main())
