"""Wait for a harness without waiting out a run that already died. No dependencies; takes no lane.

    python .claude/scripts/tools/wait_result.py <result file> --timeout 600 [--driver-pid N] [--log <editor log>] [--since <epoch>]
    python .claude/scripts/tools/wait_result.py --pie-ready --timeout 120 [--players 2]
    python .claude/scripts/tools/wait_result.py --pie-stopped --timeout 60

Returns as soon as one of these happens; the last line printed names it:

    0  RESULT, PIE_READY or PIE_STOPPED: what you waited for
    2  TIMEOUT
    3  TRACEBACK: a Python traceback or an engine crash reached the editor log after this wait began; the lines follow
    4  DRIVER_EXITED: the process given by --driver-pid ended with no result
    1  a bad command line

A traceback may be harmless: read it and decide, and call this again to keep waiting (only lines after that call count).

A result file already there when the wait begins counts only once it is rewritten, so a stale one is never read as this
run's. Delete it before the run, or pass --since <epoch seconds>: a file written at or after that counts at once. In Git
Bash `$!` is not a Windows pid; pass `--driver-pid $(cat /proc/$!/winpid)`.

--pie-ready and --pie-stopped ask the editor through pie_ready_probe.py, read-only, so no grant is needed for the asking.
Ready means the authority world has a game mode, all client worlds exist and --players controllers with player states
have joined. A harness that also needs pawns waits for them itself.

The editor log defaults to Saved/Logs/<Name>.log of the project: the single *.uproject in the project root (the folder
that holds .claude/), or the file ORCH_UPROJECT names. With neither, pass --log.
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
EDITOR_RUNNER = os.path.join(HERE, "ue_python.py")
PIE_PROBE = os.path.join(HERE, "pie_ready_probe.py")

EXIT_DONE = 0
EXIT_BAD_COMMAND_LINE = 1
EXIT_TIMEOUT = 2
EXIT_TRACEBACK = 3
EXIT_DRIVER_EXITED = 4

POLL_SECONDS = 0.5
PROBE_SPACING_SECONDS = 1.0
PROBE_TIMEOUT_SECONDS = 30.0
FAILURE_SETTLE_SECONDS = 0.5
MAX_FAILURE_LINES = 60
DEFAULT_PLAYERS = 2

FAILURE = re.compile(r"LogPython: Error: (?:Traceback|\w*(?:Error|Exception)\b)|=== Critical error|Fatal error[:!]")
FAILURE_CONTINUES = re.compile(r"LogPython: Error:|LogWindows: Error:|LogCore: Error:|LogOutputDevice: Error:")
PROBE_LINE = re.compile(r"PIE_READY_PROBE (\{.*\})")


def default_log():
    """Saved/Logs/<Name>.log of ORCH_UPROJECT, else of the single *.uproject in the project root; None when neither says."""
    override = os.environ.get("ORCH_UPROJECT")
    if override:
        uproject = os.path.join(ROOT, override)
    else:
        found = glob.glob(os.path.join(glob.escape(ROOT), "*.uproject"))
        if len(found) != 1:
            return None
        uproject = found[0]
    return os.path.join(ROOT, "Saved", "Logs", os.path.splitext(os.path.basename(uproject))[0] + ".log")


class ArgumentParser(argparse.ArgumentParser):
    def error(self, message):
        self.print_usage(sys.stderr)
        self.exit(EXIT_BAD_COMMAND_LINE, "%s: error: %s\n" % (self.prog, message))


class LogWatch:
    """New lines of the editor log since the wait began. A log replaced by an editor restart is read from its start."""

    def __init__(self, path):
        self.path = path
        self.offset, self.identity = self._stat()
        self.partial = b""
        self.lines = []
        self.block_open = False
        self.first_hit_at = None

    def _stat(self):
        try:
            info = os.stat(self.path)
            return info.st_size, info.st_ino
        except OSError:
            return 0, None

    def poll(self):
        size, identity = self._stat()
        if identity is None:
            return
        if identity != self.identity or size < self.offset:
            self.offset = 0
            self.partial = b""
            self.identity = identity
        if size == self.offset:
            return
        try:
            with open(self.path, "rb") as f:
                f.seek(self.offset)
                data = f.read()
        except OSError:
            return
        self.offset += len(data)
        chunks = (self.partial + data).split(b"\n")
        self.partial = chunks.pop()
        for raw in chunks:
            self._scan(raw.decode("utf-8", "replace").rstrip("\r"))

    def _scan(self, line):
        if self.block_open:
            if FAILURE_CONTINUES.search(line):
                if len(self.lines) < MAX_FAILURE_LINES:
                    self.lines.append(line)
            else:
                self.block_open = False
        elif not self.lines and FAILURE.search(line):
            self.lines.append(line)
            self.block_open = True
            self.first_hit_at = time.monotonic()

    def failed(self):
        if not self.lines:
            return False
        return not self.block_open or time.monotonic() - self.first_hit_at >= FAILURE_SETTLE_SECONDS


class DriverWatch:
    """One process, held by handle on Windows so a reused pid cannot read as still running."""

    SYNCHRONIZE = 0x00100000
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    WAIT_OBJECT_0 = 0
    ERROR_ACCESS_DENIED = 5

    def __init__(self, pid):
        self.pid = pid
        self.handle = None
        self.unwatchable = False
        self.was_running = True
        if os.name != "nt":
            return
        import ctypes
        from ctypes import wintypes
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel32.OpenProcess.restype = wintypes.HANDLE
        self.kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        self.kernel32.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        self.kernel32.GetExitCodeProcess.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD))
        self.ctypes = ctypes
        self.wintypes = wintypes
        self.handle = self.kernel32.OpenProcess(self.SYNCHRONIZE | self.PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        self.unwatchable = not self.handle and ctypes.get_last_error() == self.ERROR_ACCESS_DENIED
        self.was_running = bool(self.handle) or self.unwatchable

    def exited(self):
        if self.unwatchable:
            return False
        if os.name != "nt":
            try:
                os.kill(self.pid, 0)
                return False
            except ProcessLookupError:
                return True
            except PermissionError:
                return False
        return not self.handle or self.kernel32.WaitForSingleObject(self.handle, 0) == self.WAIT_OBJECT_0

    def describe(self):
        if not self.was_running:
            return "pid %d was not running when the wait began (in Git Bash pass $(cat /proc/$!/winpid), not $!)" % self.pid
        if os.name == "nt" and self.handle:
            code = self.wintypes.DWORD()
            if self.kernel32.GetExitCodeProcess(self.handle, self.ctypes.byref(code)):
                return "pid %d, exit code %d" % (self.pid, code.value)
        return "pid %d" % self.pid


class ResultGoal:
    def __init__(self, path, since):
        self.path = path
        self.since = since
        self.size_seen = None
        self.mtime_before = self._mtime()
        if self.mtime_before is not None and not self._is_fresh(self.mtime_before):
            print("wait_result: %s is from before this wait; waiting for it to be rewritten" % path, flush=True)

    def _mtime(self):
        try:
            return os.stat(self.path).st_mtime
        except OSError:
            return None

    def _is_fresh(self, mtime):
        return mtime != self.mtime_before or (self.since is not None and mtime >= self.since)

    def reached(self, writer_is_gone):
        try:
            info = os.stat(self.path)
        except OSError:
            return False
        if not self._is_fresh(info.st_mtime):
            return False
        if writer_is_gone or info.st_size == self.size_seen:
            return True
        self.size_seen = info.st_size
        return False

    def stop(self):
        pass

    def success(self, elapsed):
        return "RESULT %s after %.1f s" % (self.path, elapsed)

    def waiting_for(self):
        return self.path


def probe_command():
    return [sys.executable, EDITOR_RUNNER, PIE_PROBE, "--timeout", str(int(PROBE_TIMEOUT_SECONDS))]


def parse_probe(output):
    match = PROBE_LINE.search(output)
    if not match:
        return None
    try:
        return json.loads(match.group(1))
    except ValueError:
        return None


def pie_is_ready(facts, players):
    if not facts.get("game_world") or not facts.get("authority"):
        return False
    clients_needed = players if facts.get("dedicated") else players - 1
    return facts.get("clients", 0) >= clients_needed and facts.get("players", 0) >= players and facts.get("player_states", 0) >= players


class PieGoal:
    """The editor is asked one probe at a time. A probe that hangs or finds no editor only means not yet: a game thread
    busy starting PIE answers nothing, and the log and driver watches keep running meanwhile."""

    def __init__(self, want_ready, players):
        self.want_ready = want_ready
        self.players = players
        self.process = None
        self.output = None
        self.started_at = 0.0
        self.next_probe_at = 0.0
        self.last_said = "no answer yet"

    def _start(self):
        self.output = tempfile.TemporaryFile()
        self.process = subprocess.Popen(probe_command(), stdin=subprocess.DEVNULL, stdout=self.output, stderr=self.output,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.started_at = time.monotonic()

    def _finish(self):
        self.output.seek(0)
        text = self.output.read().decode("utf-8", "replace")
        self.output.close()
        self.process = None
        self.next_probe_at = time.monotonic() + PROBE_SPACING_SECONDS
        return text

    def reached(self, writer_is_gone):
        now = time.monotonic()
        if self.process is None:
            if now >= self.next_probe_at:
                self._start()
            return False
        if self.process.poll() is None:
            if now - self.started_at > PROBE_TIMEOUT_SECONDS:
                self.process.kill()
                self.process.wait()
                self._finish()
                self.last_said = "the probe got no answer in %d s" % PROBE_TIMEOUT_SECONDS
            return False
        text = self._finish()
        facts = parse_probe(text)
        if facts is None:
            self.last_said = "no editor answered"
            return False
        self.last_said = json.dumps(facts)
        if self.want_ready:
            return pie_is_ready(facts, self.players)
        return not facts.get("game_world")

    def stop(self):
        if self.process is not None:
            self.process.kill()
            self.process.wait()
            self._finish()

    def success(self, elapsed):
        if self.want_ready:
            return "PIE_READY after %.1f s: %s" % (elapsed, self.last_said)
        return "PIE_STOPPED after %.1f s" % elapsed

    def waiting_for(self):
        state = "PIE ready for %d players" % self.players if self.want_ready else "PIE to stop"
        return "%s (last: %s)" % (state, self.last_said)


def wait(goal, log, drivers, timeout, interval):
    began = time.monotonic()
    deadline = began + timeout
    while True:
        gone = next((d for d in drivers if d.exited()), None)
        if goal.reached(gone is not None):
            print(goal.success(time.monotonic() - began), flush=True)
            return EXIT_DONE
        log.poll()
        if log.failed() or (gone is not None and log.lines):
            print("TRACEBACK in %s after %.1f s:" % (log.path, time.monotonic() - began))
            for line in log.lines:
                print(line)
            sys.stdout.flush()
            return EXIT_TRACEBACK
        if gone is not None:
            print("DRIVER_EXITED %s after %.1f s, while waiting for %s" % (gone.describe(), time.monotonic() - began, goal.waiting_for()), flush=True)
            return EXIT_DRIVER_EXITED
        left = deadline - time.monotonic()
        if left <= 0:
            print("TIMEOUT after %.1f s waiting for %s" % (time.monotonic() - began, goal.waiting_for()), flush=True)
            return EXIT_TIMEOUT
        time.sleep(min(interval, left))


def main(argv=None):
    parser = ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("result", nargs="?", help="the file the harness writes when it finishes")
    parser.add_argument("--pie-ready", action="store_true", help="wait until a harness can start in PIE")
    parser.add_argument("--pie-stopped", action="store_true", help="wait until PIE has ended")
    parser.add_argument("--timeout", type=float, required=True, help="seconds")
    parser.add_argument("--driver-pid", type=int, action="append", default=[], metavar="PID", help="repeatable; a Windows pid")
    parser.add_argument("--log", default=None, help="the editor log to watch (default Saved/Logs/<Name>.log of the project)")
    parser.add_argument("--since", type=float, help="epoch seconds; a result file written at or after this counts at once")
    parser.add_argument("--players", type=int, default=DEFAULT_PLAYERS, help="controllers --pie-ready waits for")
    parser.add_argument("--interval", type=float, default=POLL_SECONDS, help="seconds between checks")
    args = parser.parse_args(argv)
    if sum((bool(args.result), args.pie_ready, args.pie_stopped)) != 1:
        parser.error("give exactly one of: a result file, --pie-ready, --pie-stopped")
    args.log = args.log or default_log()
    if not args.log:
        parser.error("no single *.uproject in %s and no ORCH_UPROJECT, so pass --log <editor log>" % ROOT)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")

    log = LogWatch(args.log)
    drivers = [DriverWatch(pid) for pid in args.driver_pid]
    goal = ResultGoal(args.result, args.since) if args.result else PieGoal(args.pie_ready, args.players)
    try:
        return wait(goal, log, drivers, args.timeout, args.interval)
    finally:
        goal.stop()


if __name__ == "__main__":
    sys.exit(main())
