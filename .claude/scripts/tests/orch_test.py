"""Scenario tests for .claude/scripts/orch/orch.py against a fake process table and clock.

    python .claude/scripts/tests/orch_test.py

Isolated from the machine it runs on: state goes to a temporary folder (ORCH_STATE), the process table, the clock, sleeps,
kills, keyboard idle and every call into an editor are fakes in this process, and the copies of orch.py and ue_python.py
it starts run with ORCH_SANDBOX and ORCH_NO_DAEMON, so they kill nothing, ask no editor and start no daemon. Those copies
read the real process table, read-only. The project is ORCH_UPROJECT=MyGame.uproject, which no real editor opens.
"""
import contextlib
import io
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import uuid

STATE = tempfile.mkdtemp(prefix='orch_test_')
os.environ['ORCH_STATE'] = STATE
os.environ['ORCH_NO_DAEMON'] = '1'
os.environ['ORCH_SANDBOX'] = '1'
os.environ['ORCH_UPROJECT'] = 'MyGame.uproject'
for _name in ('ORCH_OWNER', 'ORCH_DEPLOY_PATTERN'):
	os.environ.pop(_name, None)
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'orch'))
import orch  # noqa: E402

# The sandbox switch is for the copies this file starts; in this process the fakes below replace those side effects, so the tests can watch them.
orch.SANDBOX = False

CLOCK = [1_900_000_000.0]
orch.now = lambda: CLOCK[0]
orch.time.sleep = lambda s: advance(s)
KILLED = []
orch.kill_tree = lambda pid: KILLED.append(pid)
orch.human_idle_seconds = lambda: 0
orch.UBT_LOG_DIR = os.path.join(STATE, 'no-build-log')
# The real editor_exec would reach for a real editor; a test that needs answers puts its own fake in.
orch.editor_exec = lambda code, timeout=2.5: None
orch.CLAUDE_SESSIONS_DIR = os.path.join(STATE, 'claude-sessions')

PROCS = {}
CURRENT = [None]
FAILURES = []


def advance(seconds):
	CLOCK[0] += seconds


def proc(pid, ppid, name, start=None, cmd=''):
	PROCS[pid] = orch.Proc(pid, ppid, name, start or pid * 10, cmd)


def kill(pid):
	PROCS.pop(pid, None)


orch.SNAPSHOT = lambda: orch.Snapshot([orch.Proc(p.pid, p.ppid, p.name, p.start, p.cmd) for p in PROCS.values()])


def fake_agent(snap, pid=None):
	p = snap.procs.get(CURRENT[0])
	return ('claude', p) if p else (None, None)


orch.find_agent_process = fake_agent


def reset():
	fresh = tempfile.mkdtemp(dir=STATE)
	orch.STATE_DIR = fresh
	orch.DB_PATH = os.path.join(fresh, 'orch.db')
	orch.MIRROR_PATH = os.path.join(fresh, 'editor-session.json')
	orch.EVENTS_PATH = os.path.join(fresh, 'orch-events.log')
	orch.HOOK_FIELDS_PATH = os.path.join(fresh, 'orch-hook-fields.log')
	orch.STATUS_PATH = os.path.join(fresh, 'orch-status.json')
	PROCS.clear()
	KILLED.clear()
	proc(1, 0, 'explorer.exe')
	for pid in (100, 200, 300):
		proc(pid, 1, 'claude.exe')


def owner(pid):
	return 'claude:%d:%d' % (pid, pid * 10)


def as_agent(pid):
	CURRENT[0] = pid


def run_hook(pid, event, **payload):
	as_agent(pid)
	buf = io.StringIO()
	with contextlib.redirect_stdout(buf):
		orch.hook(event, payload)
	out = buf.getvalue()
	return json.loads(out) if out else None


def bash(pid, command, **extra):
	return run_hook(pid, 'PreToolUse', tool_name='Bash', tool_input=dict(command=command, **extra), session_id='s%d' % pid)


def post(pid, tool='Bash', tool_input=None, tool_response=None):
	return run_hook(pid, 'PostToolUse', tool_name=tool, tool_input=tool_input or {}, tool_response=tool_response or {}, session_id='s%d' % pid)


def cli(pid, *argv):
	as_agent(pid)
	out, err = io.StringIO(), io.StringIO()
	with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
		try:
			code = orch.main(list(argv))
		except SystemExit as exc:
			code = exc.code
	return code, out.getvalue() + err.getvalue()


def denied(result):
	return bool(result and result.get('hookSpecificOutput', {}).get('permissionDecision') == 'deny')


def check(name, condition, detail=''):
	print('%s %s%s' % ('PASS' if condition else 'FAIL', name, ('  -- ' + detail) if detail and not condition else ''))
	if not condition:
		FAILURES.append(name)


def active(kind=None):
	db = orch.connect()
	return [g for g in orch.active_grants(db) if kind is None or g['kind'] == kind]


BUILD = '"C:/UE/Engine/Build/BatchFiles/Build.bat" MyGameEditor Win64 Development -Project="C:/x/MyGame.uproject" -waitmutex'


def test_classification():
	cases = [
		('grep -n "Build.bat" CLAUDE.md', None),
		(BUILD, 'build'),
		(BUILD + ' -singlefile="C:/x/A.cpp"', 'free'),
		('ls Binaries | grep UnrealEditor-MyGame', None),
		('Start-Process "C:\\UE\\UnrealEditor.exe" -ArgumentList \'"C:\\x\\MyGame.uproject"\'', 'editor'),
		('& "C:\\UE\\UnrealEditor.exe" "C:\\x\\MyGame.uproject" -server -game -log -nosteam -NoLiveCoding', 'test'),
		("Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'Unreal|Coding' }", None),
		('python .claude/scripts/tools/ue_python.py probe.py', 'editor'),
		('p4 edit Source/MyGame/A.cpp', 'p4'),
		('p4 -c ws sync //depot/...', 'sync'),
		('p4 revert -k Source/A.cpp', 'p4'),
		('p4 opened', None),
		('p4 status Source/...', None),
		("cat > f.py <<'EOF'\nsubprocess.run(['Build.bat'])\nEOF\npython f.py", None),
		("python - <<'EOF'\nsubprocess.run(['UnrealEditor.exe', 'x'])\nEOF", 'editor'),
		('echo "run Build.bat later"', None),
		('if (Test-Path x) { & "C:\\UE\\Build.bat" MyGameEditor Win64 Development }', 'build'),
		('$p = Get-Process UnrealEditor -ErrorAction SilentlyContinue; if ($p) { "open" }', None),
		('taskkill /F /IM UnrealEditor.exe', 'editor'),
		('tools/deploy.cmd --upload', 'deploy'),
		('steamcmd +login builder +run_app_build app_build.vdf +quit', 'deploy'),
		('powershell -NoProfile -File tools/report.ps1 -Action Status', None),
	]
	for cmd, want in cases:
		got = orch.classify_command(cmd)[0]
		check('classify %r' % cmd[:60], got == want, 'got %s want %s' % (got, want))


def test_read_only_editor_queries():
	folder = tempfile.mkdtemp()
	reader = os.path.join(folder, 'peek.py').replace('\\', '/')
	writer = os.path.join(folder, 'poke.py').replace('\\', '/')
	with open(reader, 'w') as f:
		f.write('"""Prints the audio state. ORCH: read-only"""\nimport unreal\n')
	with open(writer, 'w') as f:
		f.write('import unreal\nunreal.EditorLevelLibrary.spawn_actor_from_class(None, None)\n')
	cases = [
		('python .claude/scripts/tools/ue_python.py %s' % reader, 'inspect'),
		('timeout 30 python .claude/scripts/tools/ue_python.py "%s" --set mode=read 2>&1 | tail -3' % reader, 'inspect'),
		('python .claude/scripts/tools/ue_python.py %s' % writer, 'editor'),
		('python .claude/scripts/tools/ue_python.py $SP/peek.py', 'editor'),
		('python .claude/scripts/tools/ue_python.py %s --set x=editor_request_begin_play' % reader, 'editor'),
		('python .claude/scripts/tools/ue_python.py %s; python .claude/scripts/tools/ue_python.py %s' % (reader, writer), 'editor'),
	]
	for cmd, want in cases:
		got = orch.classify_command(cmd)[0]
		check('read-only classify %r' % cmd[-50:], got == want, 'got %s want %s' % (got, want))


def test_explicit_grant_and_stop_release():
	reset()
	code, out = cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A: build', '--wait', '0')
	check('A acquires a free lane', code == 0 and 'GRANTED' in out, out)
	res = bash(200, BUILD)
	check('B is denied while A works', denied(res))
	check('deny names the holder and how to queue', denied(res) and 'acquire --kind Build' in res['hookSpecificOutput']['permissionDecisionReason'])
	res = bash(200, 'grep -rn "Build.bat" Source')
	check('B grep mentioning Build.bat is not blocked', not denied(res))
	post(200)
	run_hook(100, 'Stop', stop_hook_active=True)
	advance(30)
	check('A still holds within the stop grace', not denied(None) and len(active('Build')) == 1)
	check('B still denied inside the grace', denied(bash(200, BUILD)))
	advance(100)
	res = bash(200, BUILD)
	check('B allowed once A stopped working', not denied(res), json.dumps(res))
	check('A grant reclaimed', all(g['owner'] != owner(100) for g in active()))
	post(200)
	check('B implicit grant released after its command', not active())


def test_background_shell_keeps_grant():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A: pie', '--wait', '0')
	run_hook(100, 'Stop', stop_hook_active=True)
	proc(101, 100, 'bash.exe')
	advance(10 * 60)
	check('background shell keeps A working after Stop', denied(bash(200, BUILD)))
	advance(55 * 60)
	check('stale background shell no longer holds the lane', not denied(bash(200, BUILD)))


def test_dead_owner_and_queue():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A: pie', '--wait', '0')
	code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B: build', '--wait', '0')
	check('B queued behind A', code == 3 and 'WAITING #1' in out, out)
	proc(301, 300, 'bash.exe')
	code, out = cli(300, 'acquire', '--kind', 'Editor', '--purpose', 'C: editor', '--wait', '0')
	check('C queued second', code == 3 and 'WAITING #2' in out, out)
	kill(100)
	code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B: build', '--wait', '0')
	check('dead owner released immediately, B granted', code == 0 and 'GRANTED' in out, out)


def test_head_grace():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	proc(301, 300, 'bash.exe')
	cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	cli(300, 'acquire', '--kind', 'Build', '--purpose', 'C', '--wait', '0')
	cli(100, 'finish', '--evidence', 'idle')
	code, out = cli(300, 'acquire', '--kind', 'Build', '--purpose', 'C', '--wait', '0')
	check('C cannot jump B right after the lane frees', code == 3, out)
	advance(orch.HEAD_GRACE + 10)
	code, out = cli(300, 'acquire', '--kind', 'Build', '--purpose', 'C', '--wait', '0')
	check('C passes a head that leaves the free lane unclaimed', code == 0, out)


def test_reorder():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	proc(301, 300, 'bash.exe')
	_, build = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B: long build', '--wait', '0')
	_, pie = cli(300, 'acquire', '--kind', 'PIE', '--purpose', 'C: short run', '--wait', '0')
	short = pie.split('id=')[1][:8]
	code, out = cli(100, 'reorder', '--id', short, '--to', '1', '--reason', 'shortest first')
	check('reorder moves a queued grant', code == 0 and 'MOVED' in out, out)
	cli(100, 'finish', '--evidence', 'done')
	code, out = cli(200, 'acquire', '--id', build.split('id=')[1][:8], '--wait', '0')
	check('the moved-back grant now waits behind it', code == 3 and 'WAITING #2' in out, out)
	code, out = cli(300, 'acquire', '--id', short, '--wait', '0')
	check('the moved grant is served first', code == 0 and 'GRANTED' in out, out)
	code, out = cli(100, 'reorder', '--id', 'nosuch', '--to', '1', '--reason', 'x')
	check('reorder refuses an unknown grant', code == 2, out)


def test_a_grant_never_reads_as_waiting():
	reset()
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A: run', '--wait', '0')
	check('GRANTED text does not say waiting: a loop matching that word would spin on its own grant', code == 0 and 'waiting' not in out.lower(), out)
	for kind in LANE_KINDS:
		reset()
		cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A: run', '--wait', '0')
		_, queued = cli(200, 'acquire', '--kind', kind, '--purpose', 'B: run', '--wait', '0')
		cli(300, 'acquire', '--kind', 'Build', '--purpose', 'C: run', '--wait', '0')
		kill(100)
		code, out = cli(200, 'acquire', '--id', queued.split('id=')[1][:8], '--wait', '0')
		check('%s: GRANTED off the queue, someone behind it, still does not say waiting' % kind, code == 0 and 'GRANTED' in out and 'waiting' not in out.lower(), out)


LANE_KINDS = ('PIE', 'Build', 'Editor', 'Sync', 'Test')


def test_plan_line_on_every_lane_grant():
	for kind in LANE_KINDS:
		reset()
		code, out = cli(100, 'acquire', '--kind', kind, '--purpose', 'A: run', '--wait', '0')
		lines = out.strip().splitlines()
		id8 = out.split('id=')[1][:8]
		check('%s grant starts GRANTED then id=, as head readers expect' % kind, code == 0 and lines[0].startswith('GRANTED ') and lines[1].startswith('id='), out)
		check('%s grant ends on the plan with its id: most agents read only the tail' % kind, lines[-1].startswith('PLAN: one prepared run, then finish --id ' + id8), out)
		check('%s plan says it in the first 110 columns, past which agents cut' % kind, 'after you release' in lines[-1][:110], lines[-1])
	reset()
	_, out = cli(100, 'acquire', '--kind', 'Deploy', '--purpose', 'A: upload', '--wait', '0')
	check('a Deploy grant has no plan: it is not the lane', 'GRANTED' in out and 'PLAN:' not in out, out)
	reset()
	_, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A: run', '--wait', '0')
	check('the turn line keeps the limit and leaves the instruction to the plan', 'TURN: 20 min' in out and 'needs thought' not in out, out)


def edit_file(pid, path, tool='Edit'):
	pre = run_hook(pid, 'PreToolUse', tool_name=tool, tool_input={'file_path': path}, session_id='s%d' % pid)
	return pre, post(pid, tool, {'file_path': path})


def additional_context(result):
	return (result or {}).get('hookSpecificOutput', {}).get('additionalContext', '')


def own_grant_id(pid):
	row = orch.connect().execute("SELECT id FROM grants WHERE owner=? AND state IN ('queued','active') ORDER BY seq", (owner(pid),)).fetchone()
	return row['id'][:8]


def test_lane_patch_note():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A: run', '--wait', '0')
	gid = active('PIE')[0]['id']
	_, result = edit_file(100, 'C:/x/harness.py')
	check('nobody queued: no note', result is None, str(result))

	run_hook(200, 'UserPromptSubmit', prompt='x', session_title='Menu fix', session_id='s200')
	cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	advance(420)
	pre, result = edit_file(100, 'C:/x/harness.py', 'Write')
	note = additional_context(result)
	check('a Write beside a queue is not refused', pre is None, str(pre))
	check('a note says who is queued, how long, and what to do', 'Menu fix (queued 7m ago)' in note and 'finish --id ' + gid[:8] in note and 'queue again' in note, note)
	check('it is one line, marked as the orchestrator, never a decision', note.startswith('[orchestrator] ') and len(note.splitlines()) == 1
		and 'decision' not in result and 'permissionDecision' not in result['hookSpecificOutput'], str(result))
	check('the grant keeps running', active('PIE')[0]['id'] == gid and not denied(bash(100, BUILD)))
	post(100)

	_, result = edit_file(100, 'C:/x/Widget.cpp')
	check('at most once per grant', result is None, str(result))
	with open(orch.EVENTS_PATH, encoding='utf-8') as f:
		check('the journal records it once, for the after-measure', f.read().count('patch-note') == 1)

	cli(100, 'finish', '--id', gid[:8], '--evidence', 'proven')
	row = orch.connect().execute('SELECT evidence FROM grants WHERE id=?', (gid,)).fetchone()
	evidence = json.loads(row['evidence'])
	check('finish keeps what the note stored and its own evidence', evidence.get('patchNoted') and evidence.get('idleEvidence') == 'proven', row['evidence'])


def test_lane_patch_note_fires_per_grant_and_per_extension():
	for extension in ('.cpp', '.h', '.py', '.ps1', '.sh', '.js', '.CPP'):
		reset()
		cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
		cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
		_, result = edit_file(100, 'C:/x/thing' + extension)
		check('%s fires' % extension, 'You hold the lane' in additional_context(result), str(result))
	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	for extension in ('.md', '.uasset', '.cs', '.json', '.hpp', '.txt', '.h.bak', 'py'):
		_, result = edit_file(100, 'C:/x/thing' + extension)
		check('%s stays quiet' % extension, result is None, str(result))
	_, result = edit_file(100, 'C:/x/thing.py', 'MultiEdit')
	check('and those did not use up the note of the grant', 'You hold the lane' in additional_context(result), str(result))

	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	result = post(100, 'Read', {'file_path': 'C:/x/harness.py'})
	check('reading a script is not a patch', result is None, str(result))
	_, result = edit_file(200, 'C:/x/harness.py')
	check('the agent in the queue writing its script is doing it right', result is None, str(result))
	_, result = edit_file(100, 'C:/x/harness.py')
	check('the holder is told', 'You hold the lane' in additional_context(result), str(result))
	kill(100)
	cli(200, 'acquire', '--id', own_grant_id(200), '--wait', '0')
	cli(300, 'acquire', '--kind', 'Build', '--purpose', 'C', '--wait', '0')
	_, result = edit_file(200, 'C:/x/harness.py')
	check('a new grant gets its own note', 'You hold the lane' in additional_context(result), str(result))


def test_lane_patch_note_exemptions():
	reset()
	cli(100, 'acquire', '--kind', 'Deploy', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'Deploy', '--purpose', 'B', '--wait', '0')
	_, result = edit_file(100, 'C:/x/harness.py')
	check('a Deploy holder is not on the lane', result is None, str(result))

	reset()
	bash(100, BUILD)
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	_, result = edit_file(100, 'C:/x/harness.py')
	check('a lone command grant ends with its command, so it has nothing to finish', result is None, str(result))

	reset()
	cli(100, 'acquire', '--kind', 'Test', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'Test', '--purpose', 'B', '--wait', '0')
	_, result = edit_file(100, 'C:/x/harness.py')
	check('a Test beside another Test blocks nobody', result is None, str(result))
	cli(300, 'acquire', '--kind', 'PIE', '--purpose', 'C', '--wait', '0')
	_, result = edit_file(100, 'C:/x/harness.py')
	check('a Test ahead of a PIE does', 'You hold the lane' in additional_context(result), str(result))

	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	cli(None, 'hold', 'on')
	_, result = edit_file(100, 'C:/x/harness.py')
	check('during the hold of the user, releasing would move nobody', 'You hold the lane' not in additional_context(result), str(result))
	cli(None, 'hold', 'off')
	_, result = edit_file(100, 'C:/x/harness.py')
	check('after it the note still has its one shot', 'You hold the lane' in additional_context(result), str(result))

	reset()
	for pid in (400, 500):
		proc(pid, 1, 'claude.exe')
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	for pid in (200, 300, 400, 500):
		cli(pid, 'acquire', '--kind', 'PIE', '--purpose', 'queued', '--wait', '0')
	orch.notify(orch.connect(), owner(100), 'a message for the holder')
	_, result = edit_file(100, 'C:/x/harness.py')
	note = additional_context(result)
	check('a long queue names three and counts the rest', owner(400) in note and owner(500) not in note and 'and 1 more behind you' in note, note)
	check('the inbox still reaches the holder beside the note', 'a message for the holder' in note, note)


def test_until_granted_blocks_past_the_wait():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	_, queued = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	started = CLOCK[0]
	real_sleep = orch.time.sleep

	def holder_dies_later(seconds):
		advance(seconds)
		if CLOCK[0] - started > 600:
			kill(100)

	orch.time.sleep = holder_dies_later
	code, out = cli(200, 'acquire', '--id', queued.split('id=')[1][:8], '--until-granted')
	orch.time.sleep = real_sleep
	check('--until-granted blocks until the grant comes', code == 0 and 'GRANTED' in out and CLOCK[0] - started > 600, out)


def test_a_waiter_that_ended_its_turn_keeps_its_grant():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	_, queued = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	queued_id = queued.split('id=')[1][:8]
	run_hook(200, 'Stop')
	run_hook(200, 'Stop', stop_hook_active=True)
	advance(orch.STOP_GRACE + 30)
	cli(200, 'heartbeat')
	cli(100, 'finish', '--evidence', 'done')
	code, out = cli(200, 'acquire', '--id', queued_id, '--wait', '0')
	check('the waiter gets its grant', code == 0 and 'GRANTED' in out, out)
	kill(201)
	advance(6)
	orch.tick(orch.connect(), force=True)
	check('the grant survives the seconds before the agent wakes', lane_state(queued_id) == 'active')
	advance(orch.STOP_GRACE + 1)
	orch.tick(orch.connect(), force=True)
	check('an agent that never wakes still loses it', lane_state(queued_id) != 'active')


def test_the_hold_stops_the_turn_clock_after_it_ends():
	gid = hold_lane_with_one_waiting()
	cli(None, 'hold', 'on')
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 60)
	cli(100, 'heartbeat')
	cli(200, 'heartbeat')
	cli(None, 'hold', 'off')
	orch.tick(orch.connect(), force=True)
	check("releasing the hold does not end the turn of the agent that waited it out", lane_state(gid) == 'active')
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 60)
	cli(100, 'heartbeat')
	cli(200, 'heartbeat')
	orch.tick(orch.connect(), force=True)
	check('the clock runs again after the hold', lane_state(gid) != 'active')


def test_release_pie():
	reset()
	real_exec = orch.editor_exec
	orch.editor_exec = lambda *a, **k: 'ORCH_STOPPED'
	proc(50, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:\\x\\MyGame.uproject')
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	its_pie_came_up = orch.now() + 1
	advance(2)
	cli(100, 'finish', '--evidence', 'its chain left a PIE up')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	cli(200, 'finish', '--keep-pie', '--evidence', 'found it there')
	write_report(50, True, since=its_pie_came_up)
	code, out = cli(300, 'release-pie')
	check('an agent that never held the lane may not stop it', code == 2, out)
	code, out = cli(300, 'release-pie', '--master')
	check('the override needs a reason', code == 2, out)
	code, out = cli(100, 'release-pie')
	check("a later agent's --keep-pie does not take the PIE from the agent that left it", code == 0 and 'stopped the PIE' in out, out)
	code, out = cli(300, 'release-pie', '--master', '--reason', 'idle, blocks the queue')
	check('the Master Agent can override', code == 0 and 'stopped the PIE' in out, out)
	write_report(50, False)
	code, out = cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'D', '--wait', '0')
	check('someone holds the lane', code == 0 and 'GRANTED' in out, out)
	code, out = cli(300, 'release-pie', '--master', '--reason', 'looks stuck')
	check("the override refuses while someone holds the lane: that PIE may be the holder's own", code == 2 and 'stopped the PIE' not in out, out)
	code, out = cli(300, 'release-pie', '--master', '--past-holder', '--reason', 'its PIE is not running anything')
	check('--past-holder reaches a PIE that is blocking the holder', code == 0 and 'stopped the PIE' in out, out)
	cli(200, 'finish', '--evidence', 'done')
	cli(None, 'hold', 'on')
	code, out = cli(300, 'release-pie', '--master', '--reason', 'x')
	check("the override stands aside during the user's hold", 'stopped the PIE' not in out, out)
	cli(None, 'hold', 'off')
	orch.editor_exec = real_exec


def test_test_processes_cleanup():
	reset()
	res = bash(100, '& "C:\\UE\\UnrealEditor.exe" "C:\\x\\MyGame.uproject" -server -game -NoLiveCoding')
	check('test launch allowed', not denied(res))
	proc(150, 1, 'unrealeditor.exe', start=int(CLOCK[0] * 1e7) + orch.DOTNET_TICKS_AT_UNIX_EPOCH, cmd='UnrealEditor.exe MyGame.uproject -server -game')
	post(100)
	orch.tick(orch.connect(), force=True)
	check('test grant survives its tool call while the process runs', len(active('Test')) == 1)
	run_hook(100, 'Stop', stop_hook_active=True)
	advance(orch.STOP_GRACE + 5)
	orch.tick(orch.connect(), force=True)
	check('orphaned test process killed after the owner stopped', 150 in KILLED, str(KILLED))


def test_stop_hook():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	res = run_hook(200, 'Stop')
	check('queued agent is blocked from stopping once', res and res.get('decision') == 'block' and 'queued' in res['reason'], json.dumps(res))
	res = run_hook(200, 'Stop', stop_hook_active=True)
	check('second stop is allowed', res is None)
	code, out = cli(300, 'task', 'start', 'Fix the thing', '--next', 'build')
	res = run_hook(300, 'Stop')
	check('in-progress task blocks the stop', res and 'Task' in res['reason'], json.dumps(res))
	tid = out.split()[1]
	cli(300, 'task', 'block', tid, '--on', 'user', '--why', 'which map?')
	res = run_hook(300, 'Stop')
	check('task blocked on the user lets it stop', res is None, json.dumps(res))


def test_no_wait_while_holding():
	reset()
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	cli(100, 'acquire', '--kind', 'Deploy', '--purpose', 'A deploy', '--wait', '0')
	code, out = cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A build', '--wait', '0')
	check('waiting while holding another grant is refused', code == 2 and 'deadlock' in out, out)


def test_revert_protection_and_blame():
	reset()
	path = os.path.join(orch.PROJECT, 'Source', 'MyGame', 'Zzz.cpp')
	run_hook(200, 'PreToolUse', tool_name='Write', tool_input={'file_path': path}, session_id='s200')
	post(200, 'Write', {'file_path': path})
	res = bash(100, 'p4 revert Source/MyGame/Zzz.cpp')
	check('revert of a live session\'s file refused', denied(res), json.dumps(res))
	res = bash(100, 'p4 revert //depot/main/...')
	check('wildcard revert refused while others edit', denied(res))
	bash(100, BUILD)
	res = post(100, tool_input={'command': BUILD}, tool_response={'stdout': '%s(12): error C2065: x undeclared' % path})
	text = json.dumps(res)
	check('builder told the errors are someone else\'s', res and 'not by you' in text, text)
	res = post(200, 'Read')
	check('editing session gets the inbox note', res and 'failed on files you are editing' in json.dumps(res), json.dumps(res))


def test_resume_adoption():
	reset()
	code, out = cli(100, 'task', 'start', 'Long job')
	run_hook(100, 'PostToolUse', tool_name='Read', tool_input={}, session_id='sess-A')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE tasks SET session_id='sess-A'")
	kill(100)
	orch.tick(orch.connect(), force=True)
	db = orch.connect()
	check('task orphaned when its owner dies without SessionEnd', db.execute("SELECT state FROM tasks").fetchone()['state'] == 'orphaned')
	proc(400, 1, 'claude.exe')
	run_hook(400, 'SessionStart', session_id='sess-A')
	db = orch.connect()
	row = db.execute('SELECT owner, state FROM tasks').fetchone()
	check('resumed session adopts the task', row['owner'] == owner(400) and row['state'] == 'in_progress', dict(row).__repr__())


def test_resurrect_skips_a_running_session():
	reset()
	cli(100, 'task', 'start', 'Long job')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE tasks SET session_id='sess-R'")
	kill(100)
	orch.tick(orch.connect(), force=True)
	launched = []
	real_popen, real_find, real_dir = orch.subprocess.Popen, orch._find_claude_cli, orch.CLAUDE_SESSIONS_DIR
	orch.subprocess.Popen = lambda args, **kwargs: launched.append(args)
	orch._find_claude_cli = lambda: 'claude.exe'
	orch.CLAUDE_SESSIONS_DIR = tempfile.mkdtemp(dir=STATE)
	entry = os.path.join(orch.CLAUDE_SESSIONS_DIR, '500.json')
	proc(500, 1, 'claude.exe')
	with open(entry, 'w', encoding='utf-8') as f:
		json.dump({'pid': 500, 'sessionId': 'sess-R'}, f)
	try:
		orch.resurrect(orch.connect(), orch.SNAPSHOT())
		check('a session the user already restored is not relaunched headless', not launched, repr(launched))
		os.remove(entry)
		orch.resurrect(orch.connect(), orch.SNAPSHOT())
		check('an orphaned session nobody runs is still relaunched', len(launched) == 1 and 'sess-R' in launched[0], repr(launched))
	finally:
		orch.subprocess.Popen, orch._find_claude_cli, orch.CLAUDE_SESSIONS_DIR = real_popen, real_find, real_dir


def status_feed():
	with open(orch.STATUS_PATH) as f:
		return json.load(f)


def test_human_hold():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='fix the main menu layout please', session_id='s100')
	run_hook(100, 'UserPromptSubmit', prompt='next', session_id='s100', session_title='Menu layout fix')
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A: verify the menu', '--wait', '0')
	proc(201, 200, 'bash.exe')
	cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B: build', '--wait', '0')
	feed = status_feed()
	check('status feed names the lane holder by session title', feed['lane'] and feed['lane']['title'] == 'Menu layout fix', json.dumps(feed.get('lane')))
	code, out = cli(100, 'hold', 'on')
	check('an agent cannot take the hold', code == 2 and 'only the user' in out, out)
	code, out = cli(None, 'hold', 'on', '--reason', 'tuning a material', '--source', 'editor')
	check('the user takes the hold', code == 0 and 'HELD' in out, out)
	res = bash(100, BUILD)
	reason = res['hookSpecificOutput']['permissionDecisionReason'] if denied(res) else ''
	check('the lane holder is paused, not refused outright', 'PAUSED' in reason and 'hold wait' in reason and 'stays yours' in reason, reason)
	check('the paused holder keeps its grant', [g['owner'] for g in active()] == [owner(100)])
	check('readers still run during a hold', not denied(bash(100, 'grep -rn "Build.bat" Source')))
	check('-singlefile compiles still run during a hold', not denied(bash(100, BUILD + ' -singlefile="C:/x/A.cpp"')))
	post(100)
	code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B: build', '--wait', '0')
	check('a queued agent stays queued behind the hold', code == 3 and 'has taken the editor' in out, out)
	code, out = cli(300, 'guard', '--operation', 'PieStart')
	check('guard refuses PIE start during a hold', code == 2 and 'taken the editor' in out, out)
	code, out = cli(100, 'hold', 'wait', '--wait', '0')
	check('hold wait reports the hold', code == 3 and 'STILL HELD' in out, out)
	feed = status_feed()
	check('status feed shows the hold and the paused agent', feed['hold']['on'] and owner(100) in feed['hold']['paused']
		and any(a['owner'] == owner(100) and a['paused'] for a in feed['agents']), json.dumps(feed['hold']))
	res = run_hook(100, 'Stop')
	check('stop hook tells a paused holder how to keep its grant', res and 'hold wait' in res.get('reason', ''), json.dumps(res))
	code, out = cli(None, 'hold', 'off')
	check('the user releases the hold', code == 0 and 'RELEASED' in out, out)
	res = run_hook(100, 'UserPromptSubmit', prompt='go on', session_id='s100')
	check('the paused agent is told the editor is free again', res and 'released the editor' in json.dumps(res), json.dumps(res))
	res = bash(100, BUILD)
	check('the holder resumes with its own grant', not denied(res), json.dumps(res))
	post(100)
	code, out = cli(100, 'hold', 'wait', '--wait', '0')
	check('hold wait returns RELEASED once lifted', code == 0 and 'RELEASED' in out, out)
	cli(100, 'finish', '--evidence', 'idle')
	code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B: build', '--wait', '0')
	check('the queue moves again after the release', code == 0 and 'GRANTED' in out, out)


def test_hold_survives_holder_stopping():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	cli(None, 'hold', 'on')
	run_hook(100, 'Stop', stop_hook_active=True)
	advance(orch.STOP_GRACE + 5)
	orch.tick(orch.connect(), force=True)
	check('a holder that stops during a hold loses its grant as usual', not active())
	check('the hold outlives the reclaimed grant', orch.hold_active(orch.connect()))
	check('a fresh lane command is still paused', denied(bash(200, BUILD)))


def hold_lane_with_one_waiting(started_pie=False):
	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	if started_pie:
		db = orch.connect()
		with orch.Tx(db):
			db.execute("UPDATE grants SET pie_started=1, started_at=acquired_at WHERE kind='Build'")
	return active('Build')[0]['id'][:8]


def lane_state(gid):
	return orch.connect().execute("SELECT state FROM grants WHERE id LIKE ?", (gid + '%',)).fetchone()['state']


def test_lane_turn_limits_only_a_hold_someone_waits_behind():
	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	held = active('Build')[0]['id']
	for _ in range(3):
		advance(orch.LANE_TURN)
		bash(100, 'echo hi')
		post(100)
	check('with nobody waiting a hold has no limit', not denied(bash(100, BUILD)) and active('Build')[0]['id'] == held)
	post(100)

	gid = hold_lane_with_one_waiting()
	advance(orch.LANE_TURN - 60)
	check('a holder inside its turn keeps working', not denied(bash(100, BUILD)))
	post(100)
	advance(120)
	result = bash(100, BUILD)
	check('a holder past its turn is refused its next lane command', denied(result))
	reason = result['hookSpecificOutput']['permissionDecisionReason'] if result else ''
	check('the refusal says how to release and queue again', 'finish --id ' + gid in reason and 'acquire --kind Build' in reason, reason)
	check('reading is never refused', not denied(bash(100, 'ls Saved')))
	post(100)
	code, _ = cli(100, 'finish', '--id', gid, '--evidence', 'done')
	check('a holder past its turn can still finish', code == 0)
	code, out = cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	check('the agent waiting gets the lane', code == 0 and bool(active('PIE')), out)


def test_lane_turn_gives_notice_to_a_long_hold():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	advance(orch.LANE_TURN + 300)
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	advance(orch.LANE_TURN_NOTICE - 30)
	check('a long hold gets notice when the first agent queues', not denied(bash(100, 'python .claude/scripts/tools/ue_python.py probe.py')))
	post(100)
	advance(60)
	check('after the notice its next lane command is refused', denied(bash(100, 'python .claude/scripts/tools/ue_python.py probe.py')))


def test_lane_turn_never_stops_a_running_command_before_the_hard_stop():
	gid = hold_lane_with_one_waiting()
	bash(100, BUILD)
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 60)
	orch.tick(orch.connect(), force=True)
	check('a command still running keeps the grant past the grace', lane_state(gid) == 'active')
	advance(orch.LANE_TURN_HARD_STOP)
	cli(200, 'heartbeat')
	orch.tick(orch.connect(), force=True)
	check('the hard stop takes the lane back whatever it is doing', lane_state(gid) != 'active')


def test_lane_turn_reclaims_a_holder_that_ignores_it():
	gid = hold_lane_with_one_waiting()
	bash(100, 'echo hi')
	post(100)
	advance(orch.LANE_TURN + 30)
	orch.tick(orch.connect(), force=True)
	check('it keeps the grant through the grace', lane_state(gid) == 'active')
	advance(orch.LANE_TURN_GRACE)
	orch.tick(orch.connect(), force=True)
	check('past the grace with nothing running the lane comes back', lane_state(gid) != 'active')
	held = post(100)
	text = json.dumps(held or {})
	check('the holder is told why and how to queue again', 'your turn ended' in text and 'acquire --kind Build' in text, text)


def test_lane_turn_exemptions():
	gid = hold_lane_with_one_waiting()
	cli(None, 'hold', 'on')
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 60)
	orch.tick(orch.connect(), force=True)
	check("the user's hold stops the clock", lane_state(gid) == 'active')
	cli(None, 'hold', 'off')

	gid = hold_lane_with_one_waiting()
	cli(100, 'config', 'lane-turn', 'off')
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 60)
	orch.tick(orch.connect(), force=True)
	check('the switch turns the limit off', not denied(bash(100, BUILD)) and lane_state(gid) == 'active')
	post(100)

	reset()
	bash(100, BUILD)
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'B', '--wait', '0')
	advance(orch.LANE_TURN * 2)
	db = orch.connect()
	implicit = [g for g in orch.active_grants(db) if g['implicit']]
	check('a lone command grant has no turn', implicit and not orch.lane_turn_overrun(db, implicit[0], orch.now()))


def test_hookless_owner_stops_when_the_registry_says_idle():
	real_dir = orch.CLAUDE_SESSIONS_DIR
	orch.CLAUDE_SESSIONS_DIR = tempfile.mkdtemp(dir=STATE)
	entry = os.path.join(orch.CLAUDE_SESSIONS_DIR, '100.json')

	def registry(status, since):
		with open(entry, 'w', encoding='utf-8') as f:
			json.dump({'pid': 100, 'status': status, 'statusUpdatedAt': since * 1000}, f)

	try:
		reset()
		cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
		registry('busy', orch.now())
		advance(orch.STOP_GRACE * 3)
		orch.tick(orch.connect(), force=True)
		check('a hookless agent the registry calls busy keeps its grant', bool(active()))
		registry('idle', orch.now())
		advance(orch.STOP_GRACE - 10)
		orch.tick(orch.connect(), force=True)
		check('a hookless agent keeps its grant through the grace', bool(active()))
		advance(30)
		orch.tick(orch.connect(), force=True)
		check('a hookless agent idle past the grace loses its grant', not active())
		reset()
		os.remove(entry)
		cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
		advance(orch.STOP_GRACE * 3)
		orch.tick(orch.connect(), force=True)
		check('with no registry entry the silence rule still decides', bool(active()))
	finally:
		orch.CLAUDE_SESSIONS_DIR = real_dir


@contextlib.contextmanager
def fake_editor(answer):
	"""Starts an editor in the fake process table. answer(code) -> what it prints, or None for no answer. Yields the code it was asked to run."""
	calls = []

	def fake_exec(code, timeout=2.5):
		calls.append(code)
		return answer(code)

	real_exec = orch.editor_exec
	orch.editor_exec = fake_exec
	proc(50, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:\\x\\MyGame.uproject')
	try:
		yield calls
	finally:
		orch.editor_exec = real_exec


def stopped_pie(code):
	return 'ORCH_STOPPED' if code == orch.PIE_STOP else 'ORCH_PIE=0'


def remote_python_running(pid=60):
	proc(pid, 1, 'python.exe', cmd='python .claude/scripts/tools/ue_' + 'python.py step.py')


def turn_over():
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 30)
	orch.tick(orch.connect(), force=True)


def queued_head():
	return orch.queued_grants(orch.connect())[0]['id'][:8]


def journal_text():
	with open(orch.EVENTS_PATH, encoding='utf-8') as f:
		return f.read()


def test_a_turn_ended_reclaim_stops_the_pie_before_the_next_grant():
	gid = hold_lane_with_one_waiting(started_pie=True)
	head = queued_head()
	seen = {}

	def answer(code):
		if code == orch.PIE_STOP:
			seen['holder'] = lane_state(gid)
			seen['head'] = lane_state(head)
		return stopped_pie(code)

	with fake_editor(answer) as calls:
		turn_over()
		check('taking the lane back from a Build grant stops the PIE', calls.count(orch.PIE_STOP) == 1, repr(calls))
		check('at the stop the lane is still shut: the old grant is releasing', seen.get('holder') == 'releasing', repr(seen))
		check('at the stop the next grant has not been given, so its PIE cannot be the one stopped', seen.get('head') == 'queued', repr(seen))
		check('the stop is journalled', 'stopped PIE: reclaimed grant ' + gid in journal_text())
		check('the release then finishes', lane_state(gid) == 'done')
		code, out = cli(200, 'acquire', '--id', head, '--wait', '0')
		check('the head gets the lane after the stop', code == 0 and 'GRANTED' in out, out)
		check('giving the next grant stops nothing more', calls.count(orch.PIE_STOP) == 1, repr(calls))


def test_a_reclaim_waits_out_a_running_remote_python():
	gid = hold_lane_with_one_waiting(started_pie=True)
	head = queued_head()
	with fake_editor(stopped_pie) as calls:
		remote_python_running()
		turn_over()
		check('a remote python still running holds the stop back: it may be about to start a PIE', orch.PIE_STOP not in calls, repr(calls))
		check('the lane stays shut while the stop waits', lane_state(gid) == 'releasing')
		code, out = cli(200, 'acquire', '--id', head, '--wait', '0')
		check('nobody is granted while the stop waits', code == 3, out)
		kill(60)
		orch.tick(orch.connect(), force=True)
		check('the stop comes once the remote python ends', calls.count(orch.PIE_STOP) == 1, repr(calls))
		check('and only then does the lane come back', lane_state(gid) == 'done')
		code, out = cli(200, 'acquire', '--id', head, '--wait', '0')
		check('the head then gets it', code == 0, out)


def test_a_reclaim_stops_the_pie_past_the_wait_whatever_runs():
	gid = hold_lane_with_one_waiting(started_pie=True)
	with fake_editor(stopped_pie) as calls:
		remote_python_running()
		turn_over()
		advance(orch.RECLAIM_PIE_WAIT)
		orch.tick(orch.connect(), force=True)
		check('a remote python that never ends cannot hold the lane shut', calls.count(orch.PIE_STOP) == 1 and lane_state(gid) == 'done', repr(calls))


def test_a_reclaim_retries_an_editor_that_does_not_answer():
	gid = hold_lane_with_one_waiting(started_pie=True)
	db = orch.connect()
	with orch.Tx(db):
		orch.meta_set(db, 'pie_probe', json.dumps({'at': 7, 'running': True, 'editor_pid': 50}))
	with fake_editor(lambda code: None) as calls:
		turn_over()
		check('no answer: the lane stays shut and the stop is tried again', lane_state(gid) == 'releasing' and calls.count(orch.PIE_STOP) == 1, repr(calls))
		orch.tick(orch.connect(), force=True)
		check('the next settle tries again', calls.count(orch.PIE_STOP) == 2, repr(calls))
		advance(orch.RECLAIM_PIE_WAIT)
		orch.tick(orch.connect(), force=True)
		check('past the wait the lane comes back', lane_state(gid) == 'done')
		check('and the journal says the stop failed', 'could not stop PIE' in journal_text())
		probe = json.loads(orch.meta_get(orch.connect(), 'pie_probe'))
		check('no answer is not recorded as "no PIE"', probe.get('running') is True and probe.get('at') == 7, repr(probe))
		text = json.dumps(post(100) or {})
		check('the owner is told the stop failed and how to do it', 'did not answer' in text and 'release-pie' in text, text)


def test_a_reclaim_leaves_the_editor_alone_during_the_hold():
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
		del calls[:]
		cli(None, 'hold', 'on')
		run_hook(100, 'Stop', stop_hook_active=True)
		advance(orch.STOP_GRACE + 5)
		orch.tick(orch.connect(), force=True)
		check('a reclaim during the hold still frees the grant', not active())
		check("a reclaim during the hold runs nothing in the user's editor", not calls, repr(calls))
		cli(None, 'hold', 'off')


def test_a_plain_finish_of_a_build_leaves_the_pie_alone():
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
		cli(100, 'finish', '--evidence', 'built')
		check('finish of a Build grant does not end a PIE the user started', orch.PIE_STOP not in calls, repr(calls))


def message_after(seconds, text, then=None):
	"""A fake sleep in which `text` reaches agent 200 once `seconds` have passed, then `then` runs on every later sleep."""
	started = CLOCK[0]
	sent = []

	def sleep(duration):
		advance(duration)
		if CLOCK[0] - started >= seconds and not sent:
			sent.append(True)
			db = orch.connect()
			with orch.Tx(db):
				orch.notify(db, owner(200), text)
		if then:
			then(CLOCK[0] - started)

	return sleep, started


def test_a_message_ends_a_foreground_wait_early():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	_, queued = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	qid = queued.split('id=')[1][:8]
	sleep, started = message_after(30, 'Message from #6: that PIE is yours ' + chr(0x2192))
	real_sleep = orch.time.sleep
	orch.time.sleep = sleep
	try:
		code, out = cli(200, 'acquire', '--id', qid, '--wait', '540')
	finally:
		orch.time.sleep = real_sleep
	check('a message ends the wait long before --wait', CLOCK[0] - started < 60, str(CLOCK[0] - started))
	check('the exit code is the WAITING one', code == 3, out)
	check('the message is printed', 'Message from #6: that PIE is yours' in out, out)
	check('the usual WAITING text follows, so a filter on the last lines still shows it', out.rstrip().endswith('2 dropped.') and 'WAITING #1' in out, out)
	check('the printed copy is ASCII: a piped stdout must not crash the waiter', out.isascii(), out)
	started = CLOCK[0]
	code, out = cli(200, 'acquire', '--id', qid, '--wait', '30')
	check('the re-run keeps its place', code == 3 and 'WAITING #1' in out, out)
	check('the same message does not wake it again, so a loop on the exit code cannot spin', CLOCK[0] - started >= 30 and 'that PIE is yours' not in out, out)
	res = post(200, 'Read')
	check('the hook still delivers it whole: most waiters filter their output', chr(0x2192) in json.loads(json.dumps(res))['hookSpecificOutput']['additionalContext'], json.dumps(res))
	check('and only once', post(200, 'Read') is None)
	cli(100, 'msg', '--to', owner(200), 'second')
	code, out = cli(200, 'acquire', '--id', qid, '--wait', '30')
	check('a later message wakes it again', code == 3 and 'second' in out, out)
	code, out = cli(200, 'inbox')
	check('`inbox` reads what a waiter printed: a client with no hooks loses nothing to a filter either', 'second' in out, out)
	code, out = cli(200, 'inbox')
	check('and then it is gone', 'inbox empty' in out, out)


def test_a_first_acquire_wakes_for_a_message_too():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	cli(200, 'heartbeat')
	sleep, started = message_after(30, 'Message from #6: hello')
	real_sleep = orch.time.sleep
	orch.time.sleep = sleep
	try:
		code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '540')
	finally:
		orch.time.sleep = real_sleep
	check('the first acquire of a queue entry is no deafer than a re-run', code == 3 and 'hello' in out and CLOCK[0] - started < 60, out)


def test_until_granted_does_not_wake_for_a_message():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	proc(201, 200, 'bash.exe')
	_, queued = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	sleep, started = message_after(30, 'Message from #6: hello', lambda elapsed: kill(100) if elapsed > 600 else None)
	real_sleep = orch.time.sleep
	orch.time.sleep = sleep
	try:
		code, out = cli(200, 'acquire', '--id', queued.split('id=')[1][:8], '--until-granted')
	finally:
		orch.time.sleep = real_sleep
	check('--until-granted ignores messages: its contract is exit 0 granted, 2 dropped', code == 0 and 'GRANTED' in out and CLOCK[0] - started > 600, out)
	check('and prints none of them', 'hello' not in out, out)
	check('the hook delivers them', 'hello' in json.dumps(post(200, 'Read') or {}))


def test_a_busy_editor_is_not_no_pie():
	reset()
	answers = ['ORCH_PIE=1']
	with fake_editor(lambda code: answers[0]) as calls:
		code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
		check('a PIE the editor reports shuts the lane', code == 3 and 'PIE is running' in out, out)
		qid = out.split('id=')[1][:8]
		asked = len(calls)
		before = orch.meta_get(orch.connect(), 'pie_probe')
		remote_python_running()
		advance(orch.PIE_PROBE_TTL + 1)
		code, out = cli(100, 'acquire', '--id', qid, '--wait', '0')
		check('busy: the editor is not asked', len(calls) == asked, repr(calls))
		check('busy: the PIE still shuts the lane', code == 3 and 'PIE is running' in out, out)
		check('busy: the last answer and its time stand', orch.meta_get(orch.connect(), 'pie_probe') == before)
		kill(60)
		answers[0] = 'ORCH_PIE=0'
		code, out = cli(100, 'acquire', '--id', qid, '--wait', '0')
		check('no longer busy: the next call asks, and the lane opens', code == 0 and len(calls) == asked + 1, out)


def test_a_stale_probe_never_shuts_the_lane_for_good():
	reset()
	with fake_editor(lambda code: 'ORCH_PIE=1'):
		code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
		qid = out.split('id=')[1][:8]
		remote_python_running()
		advance(orch.PIE_PROBE_TTL * 3 - 1)
		code, out = cli(100, 'acquire', '--id', qid, '--wait', '0')
		check('inside PIE_PROBE_TTL * 3 the last answer shuts the lane', code == 3 and 'PIE is running' in out, out)
		advance(2)
		code, out = cli(100, 'acquire', '--id', qid, '--wait', '0')
		check('past PIE_PROBE_TTL * 3 a python that never ends cannot keep it shut', code == 0 and 'GRANTED' in out, out)


def test_guard_follows_the_grant():
	reset()
	command = 'python .claude/scripts/tools/ue_' + 'python.py probe.py'
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('no grant: DENIED', code == 2 and out.startswith('DENIED'), out)
	check('a foreground call under the hook\'s implicit grant is allowed by the hook', not denied(bash(100, command)))
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('and passes the guard it runs while the tool call is open', code == 0 and out.startswith('ALLOWED'), out)
	post(100)
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('once the call ends the implicit grant is gone', code == 2 and out.startswith('DENIED'), out)
	bash(100, command, run_in_background=True)
	post(100)
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('a background call under an implicit grant passes as it starts', code == 0, out)
	advance(25)
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('that grant does not carry a chain on', code == 2 and out.startswith('DENIED'), out)
	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('a Build grant passes, and so does a subagent: the owner is the agent process, which subagents share', code == 0, out)
	code, out = cli(200, 'guard', '--operation', 'Work')
	check('another agent does not', code == 2 and 'held by' in out, out)
	cli(100, 'finish', '--evidence', 'done')
	code, out = cli(100, 'guard', '--operation', 'Work')
	check('after finish the chain is refused', code == 2 and out.startswith('DENIED'), out)


def test_guard_finds_a_chain_cut_off_from_its_agent():
	reset()
	saved = os.environ.get('CLAUDE_PID')
	try:
		os.environ.pop('CLAUDE_PID', None)
		code, out = cli(None, 'guard', '--operation', 'Work')
		check('a human in a plain terminal is nobody', 'cannot tell' in str(code), repr(code))
		cli(100, 'heartbeat')
		os.environ['CLAUDE_PID'] = '100'
		code, out = cli(None, 'guard', '--operation', 'Work')
		check('a chain whose tool shell exited is still its agent\'s, and has no grant', code == 2 and out.startswith('DENIED'), repr(code) + out)
		code, out = cli(None, 'heartbeat')
		check('only guard reads the variable', 'cannot tell' in str(code), repr(code))
		cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
		code, out = cli(None, 'guard', '--operation', 'Work')
		check('while the agent holds the lane its chain may run', code == 0 and out.startswith('ALLOWED'), repr(code) + out)
		db = orch.connect()
		before = orch.get_owner(db, owner(100))['last_active']
		advance(50)
		cli(None, 'guard', '--operation', 'Work')
		check('a cut-off chain does not count as its agent working', orch.get_owner(db, owner(100))['last_active'] == before)
		os.environ['CLAUDE_PID'] = '999'
		code, out = cli(None, 'guard', '--operation', 'Work')
		check('an agent that is gone cannot be named: it stays "cannot tell"', 'cannot tell' in str(code), repr(code))
		proc(998, 1, 'notepad.exe')
		os.environ['CLAUDE_PID'] = '998'
		code, out = cli(None, 'guard', '--operation', 'Work')
		check('a reused pid is not an agent', 'cannot tell' in str(code), repr(code))
	finally:
		os.environ.pop('CLAUDE_PID', None)
		if saved is not None:
			os.environ['CLAUDE_PID'] = saved


FAKE_REMOTE_EXECUTION = '''import json, os
MODE_EXEC_FILE = 'ExecuteFile'
MODE_EXEC_STATEMENT = 'ExecuteStatement'
EDITOR = 'ORCH_EDITOR_TARGET=' + json.dumps({'pid': 1, 'command_line': 'UnrealEditor.exe MyGame.uproject'})
class RemoteExecution(object):
	remote_nodes = [{'project_root': os.environ['FAKE_PROJECT_ROOT'], 'node_id': 'n'}]
	def start(self):
		open(os.environ['FAKE_CONNECTED'], 'w').close()
	def stop(self):
		pass
	def open_command_connection(self, node):
		pass
	def close_command_connection(self):
		pass
	def run_command(self, command, exec_mode=MODE_EXEC_FILE, **kwargs):
		output = [{'type': 'Info', 'output': EDITOR}] if exec_mode == MODE_EXEC_STATEMENT else []
		return {'success': True, 'output': output, 'result': ''}
'''

GUARD_DENIED = '@echo DENIED: you hold no editor/build lane grant 1>&2\r\n@exit /b 2\r\n'
GUARD_ALLOWED = '@echo ALLOWED: yours\r\n@exit /b 0\r\n'
GUARD_NO_AGENT = '@echo ORCHESTRATOR: cannot tell which agent you are. 1>&2\r\n@exit /b 1\r\n'
GUARD_CRASH = '@echo Traceback (most recent call last): 1>&2\r\n@exit /b 1\r\n'
GUARD_DENIED_ZERO = '@echo DENIED: but exit 0\r\n@exit /b 0\r\n'
GUARD_SILENT_FAILURE = '@exit /b 9009\r\n'
UE_PYTHON_FILE = 'ue_' + 'python.py'


def ue_python_sandbox(orch_cmd=None, real_orch=False, guard_timeout=None):
	"""A project tree holding a copy of the real script, a remote_execution stub that records a connection, and the given orch.cmd."""
	root = tempfile.mkdtemp(dir=STATE)
	tools = os.path.join(root, '.claude', 'scripts', 'tools')
	os.makedirs(tools)
	with open(os.path.join(orch.PROJECT, '.claude', 'scripts', 'tools', UE_PYTHON_FILE), encoding='utf-8') as f:
		body = f.read()
	if guard_timeout:
		replaced = body.replace('ORCH_GUARD_TIMEOUT_SECONDS = 5', 'ORCH_GUARD_TIMEOUT_SECONDS = %d' % guard_timeout)
		check('the sandbox can shorten the guard timeout', replaced != body)
		body = replaced
	with open(os.path.join(tools, UE_PYTHON_FILE), 'w', encoding='utf-8') as f:
		f.write(body)
	plugin = os.path.join(root, 'plugin')
	os.makedirs(plugin)
	with open(os.path.join(plugin, 'remote_execution.py'), 'w') as f:
		f.write(FAKE_REMOTE_EXECUTION)
	scripts = os.path.join(root, '.claude', 'scripts')
	if orch_cmd is not None:
		with open(os.path.join(scripts, 'orch.cmd'), 'w', newline='') as f:
			f.write('@echo called> "%~dp0called.txt"\r\n' + orch_cmd)
	if real_orch:
		shutil.copy(os.path.join(orch.PROJECT, '.claude', 'scripts', 'orch.cmd'), scripts)
		shutil.copytree(orch.HERE, os.path.join(scripts, 'orch'), ignore=shutil.ignore_patterns('__pycache__'))
	return root


def ue_python_env(root, **extra):
	env = dict(os.environ)
	for name in ('CLAUDE_PID', 'ORCH_OWNER', 'ORCH_UPROJECT'):
		env.pop(name, None)
	env.update(UE_PYTHON_PLUGIN_DIR=os.path.join(root, 'plugin'), FAKE_PROJECT_ROOT=root, FAKE_CONNECTED=os.path.join(root, 'connected'),
		ORCH_STATE=os.path.join(root, 'state'), ORCH_NO_DAEMON='1', ORCH_SANDBOX='1')
	env.update(extra)
	return env


def write_script(root, text='import unreal\n', name='step.py'):
	path = os.path.join(root, name)
	with open(path, 'w') as f:
		f.write(text)
	return path


def ue_python_command(root, script):
	return [sys.executable, '-B', os.path.join(root, '.claude', 'scripts', 'tools', UE_PYTHON_FILE), script]


def run_ue_python(root, script, **extra):
	"""-> (exit code, output, whether it connected to the "editor")."""
	done = subprocess.run(ue_python_command(root, script), env=ue_python_env(root, **extra), capture_output=True, text=True, timeout=60)
	return done.returncode, done.stdout + done.stderr, os.path.exists(os.path.join(root, 'connected'))


def test_ue_python_asks_before_it_connects():
	root = ue_python_sandbox(GUARD_DENIED)
	code, out, connected = run_ue_python(root, write_script(root))
	check('DENIED: nothing runs', not connected, out)
	check('DENIED: a failure', code != 0, str(code))
	check('DENIED: the message is printed', out.startswith('DENIED: you hold no editor/build lane grant'), out)

	root = ue_python_sandbox(GUARD_ALLOWED)
	code, out, connected = run_ue_python(root, write_script(root))
	check('ALLOWED: it runs', code == 0 and connected, out)

	root = ue_python_sandbox(GUARD_DENIED)
	code, out, connected = run_ue_python(root, write_script(root, '"""ORCH: read-only"""\nimport unreal\n'))
	check('a read-only script runs under a refusal', code == 0 and connected, out)
	check('a read-only script does not even ask', not os.path.exists(os.path.join(root, '.claude', 'scripts', 'called.txt')))

	root = ue_python_sandbox(GUARD_DENIED)
	code, out, connected = run_ue_python(root, write_script(root, '#' * 2100 + '\n"""ORCH: read-only"""\n'))
	check('a marker past the first 2000 bytes does not count, as in orch.py', not connected and code != 0, out)


def test_ue_python_fails_open_on_everything_but_denied():
	for name, stub in (('"cannot tell which agent"', GUARD_NO_AGENT), ('an orchestrator crash', GUARD_CRASH),
			('a failure with no message', GUARD_SILENT_FAILURE), ('DENIED text with exit 0', GUARD_DENIED_ZERO)):
		root = ue_python_sandbox(stub)
		code, out, connected = run_ue_python(root, write_script(root))
		check('fails open on ' + name, code == 0 and connected, out)
	root = ue_python_sandbox(None)
	code, out, connected = run_ue_python(root, write_script(root))
	check('fails open with no orch.cmd', code == 0 and connected, out)


def test_ue_python_timeout_is_short_even_when_the_orchestrator_hangs():
	root = ue_python_sandbox('@"%s" -c "import threading; threading.Event().wait(15)"\r\n' % sys.executable, guard_timeout=1)
	started = time.time()
	code, out, connected = run_ue_python(root, write_script(root))
	elapsed = time.time() - started
	check('fails open on a hung orchestrator', code == 0 and connected, out)
	check('the hang costs the timeout, not the hung process: %.1f s' % elapsed, elapsed < 10, '%.1f s' % elapsed)


def fake_claude(folder):
	"""A copy of python.exe named claude.exe, sleeping: the real orch.py finds an agent by that name."""
	os.makedirs(folder)
	exe = os.path.join(folder, 'claude.exe')
	shutil.copy2(sys.executable, exe)
	for name in ('python%d%d.dll' % sys.version_info[:2], 'python3.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'):
		source = os.path.join(os.path.dirname(sys.executable), name)
		if os.path.isfile(source):
			shutil.copy2(source, folder)
	env = dict(os.environ, PYTHONHOME=sys.base_prefix)
	return subprocess.Popen([exe, '-c', 'import threading; threading.Event().wait(120)'], env=env, stdin=subprocess.DEVNULL,
		stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def run_ue_python_detached(root, script, **extra):
	"""What a `( ... ) &` chain is once its tool shell has exited: a process whose parent is gone. The launcher starts the waiter and exits
	before ue_python.py gets as far as asking, so the orchestrator's walk up the process tree stops at the waiter."""
	result = os.path.join(root, 'result.json')
	if os.path.exists(result):
		os.remove(result)
	connected = os.path.join(root, 'connected')
	if os.path.exists(connected):
		os.remove(connected)
	waiter = ("import json, subprocess, sys\n"
		"done = subprocess.run(sys.argv[2:], capture_output=True, text=True)\n"
		"json.dump({'code': done.returncode, 'out': done.stdout + done.stderr}, open(sys.argv[1], 'w'))\n")
	launcher = ("import subprocess, sys\n"
		"subprocess.Popen([sys.executable, '-B', '-c', sys.argv[1]] + sys.argv[2:], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)\n")
	subprocess.run([sys.executable, '-B', '-c', launcher, waiter, result] + ue_python_command(root, script), env=ue_python_env(root, **extra), timeout=30)
	deadline = time.time() + 45
	while time.time() < deadline and not os.path.exists(result):
		threading.Event().wait(0.1)
	threading.Event().wait(0.1)
	with open(result) as f:
		outcome = json.load(f)
	return outcome['code'], outcome['out'], os.path.exists(connected)


def grant_lane_to(root, owner_id):
	t = time.time()
	db = sqlite3.connect(os.path.join(root, 'state', 'orch.db'))
	db.execute("""INSERT INTO grants(id, owner, kind, resources, state, phase, queued_at, acquired_at, updated_at)
		VALUES(?,?,'Build','{"lane": "X"}','active','preparing',?,?,?)""", (uuid.uuid4().hex, owner_id, t, t, t))
	db.commit()
	db.close()


def owners_in(root):
	db = sqlite3.connect(os.path.join(root, 'state', 'orch.db'))
	rows = [r[0] for r in db.execute('SELECT owner FROM owners')]
	db.close()
	return rows


def test_ue_python_against_the_real_guard():
	root = ue_python_sandbox(real_orch=True)
	script = write_script(root)
	code, out, connected = run_ue_python_detached(root, script)
	check('real guard: a human in a plain terminal runs', code == 0 and connected, out)

	code, out, connected = run_ue_python_detached(root, script, ORCH_OWNER='orchtest:e2e')
	check('real guard: no grant, no run', code != 0 and not connected and out.startswith('DENIED'), out)
	grant_lane_to(root, 'orchtest:e2e')
	code, out, connected = run_ue_python_detached(root, script, ORCH_OWNER='orchtest:e2e')
	check('real guard: with the grant it runs', code == 0 and connected, out)

	claude = fake_claude(os.path.join(root, 'fake-claude'))
	try:
		code, out, connected = run_ue_python_detached(root, script, CLAUDE_PID=str(claude.pid))
		check('real guard: a chain cut off from its agent, no grant, nothing runs', code != 0 and not connected and out.startswith('DENIED'), out)
		mine = [o for o in owners_in(root) if o.startswith('claude:%d:' % claude.pid)]
		check('real guard: it is refused as that agent', len(mine) == 1, repr(owners_in(root)))
		grant_lane_to(root, mine[0])
		code, out, connected = run_ue_python_detached(root, script, CLAUDE_PID=str(claude.pid))
		check('real guard: while that agent holds the lane the chain runs', code == 0 and connected, out)
	finally:
		claude.kill()
		claude.wait()
	code, out, connected = run_ue_python_detached(root, script, CLAUDE_PID=str(claude.pid))
	check('real guard: a chain whose agent is gone fails open (known limit)', code == 0 and connected, out)


@contextlib.contextmanager
def build_world(command='"C:\\UE\\Engine\\Binaries\\DotNET\\UnrealBuildTool\\UnrealBuildTool.dll" MyGameEditor Win64 Development -Project={project} -waitmutex',
		result='Succeeded', total='30.00'):
	"""A project tree and a UBT log in which a full build started an hour ago and nothing has changed since."""
	root = tempfile.mkdtemp(dir=STATE)
	logs = os.path.join(root, 'ubt')
	os.makedirs(logs)
	started = int(time.time()) - 3600
	world = {'root': root, 'logs': logs, 'started': started, 'before': started - 500, 'during': started + 5, 'after': started + 600}
	for relative in ('MyGame.uproject', 'Source/MyGame/A.cpp', 'Source/MyGame/Readme.md', 'Shaders/Private/B.usf', 'Plugins/Foo/Foo.uplugin',
			'Plugins/Foo/Source/Foo/F.cpp', 'Plugins/Foo/Binaries/Win64/Foo.dll', 'Plugins/Foo/Intermediate/Gen/F.gen.cpp', 'Plugins/Foo/Content/Audio/x.h',
			'Plugins/Nest/Inner/Inner.uplugin', 'Plugins/Nest/Inner/Source/G.cpp', 'Plugins/Deep/A/B/C/D/Source/H.cpp',
			'Intermediate/Build/Win64/MyGameEditor/MyGameEditor.uhttimestamps'):
		path = os.path.join(root, *relative.split('/'))
		os.makedirs(os.path.dirname(path), exist_ok=True)
		open(path, 'w').close()
	for folder, _, names in os.walk(root):
		for name in names:
			os.utime(os.path.join(folder, name), (world['before'], world['before']))
		os.utime(folder, (world['before'], world['before']))
	real_project, real_logs, real_uproject = orch.PROJECT, orch.UBT_LOG_DIR, orch.UPROJECT
	orch.PROJECT, orch.UBT_LOG_DIR = root, logs
	orch.use_project(os.path.join(root, 'MyGame.uproject'))
	world['write_log'] = lambda name, cmd=command, res=result, tot=total, at=None: write_build_log(root, logs, name, cmd, res, tot, at or started)
	world['write_log']('Log.txt')
	try:
		yield world
	finally:
		orch.PROJECT, orch.UBT_LOG_DIR = real_project, real_logs
		orch.use_project(real_uproject)


def write_build_log(root, logs, name, command, result, total, started):
	lines = ['Log started at %s (%s)' % (time.strftime('%m/%d/%Y %H:%M:%S', time.gmtime(started)), time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(started))), 'Configuration will be read from:',
		'Command line: ' + command.replace('{project}', os.path.join(root, 'MyGame.uproject')), 'Log file: x', 'Building MyGameEditor...', '']
	if result is not None:
		lines.append('Result: ' + result)
	if total is not None:
		lines.append('Total execution time: %s seconds' % total)
	with open(os.path.join(logs, name), 'w', newline='\r\n') as f:
		f.write('\n'.join(lines) + '\n')


def touch(world, relative, when='after'):
	path = os.path.join(world['root'], *relative.split('/'))
	os.utime(path, (world[when], world[when]))


SINGLEFILE = '"C:\\UE\\UnrealBuildTool.dll" MyGameEditor Win64 Development -Project={project} -waitmutex -singlefile="C:\\x\\A.cpp"'


def test_build_advice_when_nothing_changed():
	with build_world() as world:
		expected = ('BUILD NOT NEEDED: no source changed since the full MyGameEditor build that started %s. Skip the build; keep or cancel this grant as you need.'
			% time.strftime('%H:%M:%S', time.gmtime(world['started'])))
		check('a full build with nothing newer covers the tree', orch.build_not_needed_line() == expected, orch.build_not_needed_line())
		touch(world, 'Source/MyGame/Readme.md')
		check('a file with no source extension does not count', orch.build_not_needed_line() == expected)
		for ignored in ('Plugins/Foo/Binaries/Win64/Foo.dll', 'Plugins/Foo/Intermediate/Gen/F.gen.cpp', 'Plugins/Foo/Content/Audio/x.h'):
			touch(world, ignored)
		check('what a build writes (plugin Binaries, Intermediate) and Content do not count', orch.build_not_needed_line() == expected)
		touch(world, 'Source/MyGame/A.cpp', 'before')
		world['write_log']('Log-backup-2000.01.01-00.00.00.txt', at=world['started'] - 900)
		check('the newest of several full builds is the one named', orch.build_not_needed_line() == expected)
		world['write_log']('Log-backup-2000.01.01-00.00.00.txt')
		world['write_log']('Log.txt', SINGLEFILE)
		check('a -singlefile run rotated the full build into a backup: it is still found', orch.build_not_needed_line() == expected, orch.build_not_needed_line())


def test_build_advice_silent_when_source_changed():
	for relative in ('Source/MyGame/A.cpp', 'Shaders/Private/B.usf', 'Plugins/Foo/Foo.uplugin', 'Plugins/Foo/Source/Foo/F.cpp',
			'Plugins/Nest/Inner/Inner.uplugin', 'Plugins/Nest/Inner/Source/G.cpp', 'MyGame.uproject'):
		with build_world() as world:
			touch(world, relative)
			check('silent when %s changed' % relative, orch.build_not_needed_line() == '', orch.build_not_needed_line())
	with build_world() as world:
		touch(world, 'Source/MyGame/A.cpp', 'during')
		check('silent for an edit made while that build ran: it may have missed it', orch.build_not_needed_line() == '')
	with build_world() as world:
		os.remove(os.path.join(world['root'], 'Source', 'MyGame', 'A.cpp'))
		check('silent when a source file was deleted', orch.build_not_needed_line() == '')
	with build_world() as world:
		os.remove(os.path.join(world['root'], 'Plugins', 'Foo', 'Source', 'Foo', 'F.cpp'))
		check('silent when a plugin source file was deleted', orch.build_not_needed_line() == '')
	with build_world() as world:
		moved = os.path.join(world['root'], 'Source', 'MyGame', 'Moved.cpp')
		open(moved, 'w').close()
		os.utime(moved, (world['before'], world['before']))
		check('silent when a file arrived with an old time', orch.build_not_needed_line() == '')
	for name in ('Source', 'Shaders'):
		with build_world() as world:
			shutil.rmtree(os.path.join(world['root'], name))
			check('silent when %s cannot be walked' % name, orch.build_not_needed_line() == '')


def test_build_advice_reads_the_log_strictly():
	with build_world(result='Failed (OtherCompilationError)') as world:
		check('silent after a failed build', orch.build_not_needed_line() == '')
	with build_world(result=None, total=None) as world:
		check('silent while a build runs or after one was killed (no Result)', orch.build_not_needed_line() == '')
	with build_world(total=None) as world:
		check('silent with no end to measure from', orch.build_not_needed_line() == '')
	with build_world() as world:
		world['write_log']('Log-backup-2000.01.01-00.00.00.txt', at=world['started'] - 900)
		world['write_log']('Log.txt', res='Failed (OtherCompilationError)')
		check('silent when the newest full build failed, whatever an older one did', orch.build_not_needed_line() == '')
	with build_world() as world:
		world['write_log']('Log.txt', SINGLEFILE)
		check('silent when -singlefile runs have rotated the full build away', orch.build_not_needed_line() == '')
		world['write_log']('Log-backup-2000.01.01-00.00.00.txt', SINGLEFILE)
		check('silent when every log is a -singlefile run', orch.build_not_needed_line() == '')
	base = '"C:\\UE\\UnrealBuildTool.dll" %s -Project={project} -waitmutex'
	for name, command in (
			('another target', base % 'MyGameServer Win64 Development'),
			('another configuration', base % 'MyGameEditor Win64 DebugGame'),
			('another project', '"C:\\UE\\UnrealBuildTool.dll" MyGameEditor Win64 Development -Project=C:\\Other\\Work\\MyGame.uproject'),
			('no project', '"C:\\UE\\UnrealBuildTool.dll" MyGameEditor Win64 Development'),
			('an unknown flag', base % 'MyGameEditor Win64 Development -NoLink'),
			('another tool', '"C:\\UE\\Other.dll" MyGameEditor Win64 Development -Project={project}'),
			('a one-module build', base % 'MyGameEditor Win64 Development -Module=MyGame')):
		with build_world(command=command) as world:
			check('silent for %s' % name, orch.build_not_needed_line() == '', orch.build_not_needed_line())
	with build_world() as world:
		os.remove(os.path.join(world['logs'], 'Log.txt'))
		check('silent with no Log.txt, even with an old backup', orch.build_not_needed_line() == '')
	with build_world() as world:
		shutil.rmtree(world['logs'])
		check('silent with no log folder', orch.build_not_needed_line() == '')
	with build_world() as world:
		with open(os.path.join(world['logs'], 'Log.txt'), 'w') as f:
			f.write('Log started at nonsense\n')
		check('silent on a log it cannot read', orch.build_not_needed_line() == '')


def test_build_advice_follows_generated_headers():
	stamps = 'Intermediate/Build/Win64/MyGameEditor/MyGameEditor.uhttimestamps'
	with build_world() as world:
		touch(world, stamps, 'during')
		check('UHT writing its timestamps during that build is fine', orch.build_not_needed_line() != '')
		touch(world, stamps, 'after')
		check('newer than that build: a -singlefile run rewrote them: silent', orch.build_not_needed_line() == '')
	with build_world() as world:
		os.remove(os.path.join(world['root'], *stamps.split('/')))
		check('deleted: silent', orch.build_not_needed_line() == '')


def test_build_advice_only_advises():
	with build_world() as world:
		reset()
		code, out = cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
		check('a Build grant is told, last', code == 0 and out.rstrip().splitlines()[-1].startswith('BUILD NOT NEEDED: '), out)
		check('and it is still granted', code == 0 and len(active('Build')) == 1, out)
		_, queued = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
		check('a queued Build is told too, last, and still waits', queued.rstrip().splitlines()[-1].startswith('BUILD NOT NEEDED: ') and 'WAITING #1' in queued, queued)
		_, other = cli(300, 'acquire', '--kind', 'PIE', '--purpose', 'C', '--wait', '0')
		check('nobody else is', 'BUILD NOT NEEDED' not in other, other)
		cli(100, 'finish', '--evidence', 'x')
		code, again = cli(200, 'acquire', '--id', queued.split('id=')[1][:8], '--wait', '0')
		check('the waiter gets its grant anyway', code == 0 and 'GRANTED' in again and 'BUILD NOT NEEDED' in again, again)
		cli(200, 'finish', '--evidence', 'x')
		code, out = cli(300, 'acquire', '--kind', 'PIE', '--purpose', 'C', '--wait', '0')
		check('a PIE grant is not told', code == 0 and 'BUILD NOT NEEDED' not in out, out)
		touch(world, 'Source/MyGame/A.cpp')
		cli(300, 'finish', '--evidence', 'x')
		code, out = cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
		check('with a changed tree nothing is said', code == 0 and 'BUILD NOT NEEDED' not in out, out)
		check('a lone command grant is never told', not denied(bash(200, 'ls')))


def test_build_advice_measured_on_a_big_tree():
	with build_world() as world:
		folder = os.path.join(world['root'], 'Source', 'MyGame')
		for i in range(2000):
			path = os.path.join(folder, 'F%d.h' % i)
			open(path, 'w').close()
			os.utime(path, (world['before'], world['before']))
		os.utime(folder, (world['before'], world['before']))
		started = time.perf_counter()
		line = orch.build_not_needed_line()
		elapsed = (time.perf_counter() - started) * 1000
		check('a walk of 2000 files stays cheap: %.0f ms' % elapsed, line != '' and elapsed < 1000, '%.0f ms' % elapsed)


def chained_p4(pid=100):
	"""One tool call: p4 edit, then a long run in the same shell."""
	result = bash(pid, 'p4 edit Source/A.cpp && python long_test.py')
	proc(pid + 1, pid, 'bash.exe')
	proc(pid + 2, pid + 1, 'p4.exe', cmd='p4 edit Source/A.cpp')
	return result


def test_a_chained_p4_holds_the_mutex_only_while_p4_runs():
	reset()
	check('the chained call takes the implicit P4 grant', not denied(chained_p4()) and len(active('P4')) == 1)
	advance(orch.P4_IDLE * 3)
	orch.tick(orch.connect(), force=True)
	check('it holds while p4 runs, however long', len(active('P4')) == 1)
	started = orch.now()
	result = bash(200, 'p4 edit Source/B.cpp')
	check("another agent's p4 waits out a p4 still running", denied(result) and orch.now() - started >= orch.P4_WAIT, json.dumps(result))
	kill(102)
	started = orch.now()
	result = bash(200, 'p4 edit Source/B.cpp')
	check('with only the long run left, the next p4 waits no longer than the idle window', not denied(result) and orch.now() - started <= orch.P4_IDLE + 4,
		'%s after %ds' % (json.dumps(result), orch.now() - started))
	check('the mutex now belongs to that p4', [g['owner'] for g in active('P4')] == [owner(200)])
	check('the release is journalled', 'no p4 ran under its owner' in journal_text())
	post(200)
	check('the long tool call itself is still open', orch.get_owner(orch.connect(), owner(100))['open_tools'] == 1)
	post(100)
	check('the end of the long call finds nothing left to release', not active())


def test_a_short_gap_between_p4_commands_keeps_the_mutex():
	reset()
	chained_p4()
	kill(102)
	advance(orch.P4_IDLE - 5)
	orch.tick(orch.connect(), force=True)
	check('a gap shorter than the window keeps the grant', len(active('P4')) == 1)
	proc(103, 101, 'p4.exe', cmd='p4 add Source/B.cpp')
	advance(orch.P4_IDLE)
	orch.tick(orch.connect(), force=True)
	check('the next p4 in the chain restarts the window', len(active('P4')) == 1)
	kill(103)
	advance(orch.P4_IDLE)
	orch.tick(orch.connect(), force=True)
	check('the window ends with the last p4', not active('P4'))


def test_a_p4_under_another_agent_does_not_keep_the_mutex():
	reset()
	chained_p4()
	kill(102)
	proc(201, 200, 'bash.exe')
	proc(202, 201, 'p4.exe', cmd='p4 opened')
	advance(orch.P4_IDLE)
	orch.tick(orch.connect(), force=True)
	check("only p4 under the grant's owner counts", not active('P4'))


def test_a_background_p4_keeps_the_mutex_while_it_runs():
	reset()
	bash(100, 'p4 submit -c 5', run_in_background=True)
	proc(101, 100, 'bash.exe')
	proc(102, 101, 'p4.exe', cmd='p4 submit -c 5')
	post(100)
	advance(30)
	orch.tick(orch.connect(), force=True)
	check('a submit in the background keeps the mutex past the 20 s a lone command gets', len(active('P4')) == 1)
	kill(102)
	advance(orch.P4_IDLE)
	orch.tick(orch.connect(), force=True)
	check('and gives it up when p4 exits', not active('P4'))


def test_the_idle_window_is_only_for_implicit_p4():
	reset()
	bash(100, 'p4 sync //depot/... && python long_test.py')
	advance(orch.P4_IDLE * 3)
	orch.tick(orch.connect(), force=True)
	check('a Sync keeps the lane for its whole tool call', len(active('Sync')) == 1)
	post(100)


def members_of(work):
	return json.loads(orch.meta_get(orch.connect(), orch.CHANNEL_MEMBERS_KEY + work, '[]') or '[]')


def context_of(result):
	return (result or {}).get('hookSpecificOutput', {}).get('additionalContext', '')


ASTRA = ('--as', 'astra-build-fix')


def test_inbox_survives_what_the_code_page_lacks():
	reset()
	brief = os.path.join(STATE, 'bom_brief.txt')
	with open(brief, 'wb') as f:
		f.write('\ufeffRound 5 \u2192 passes'.encode('utf-8'))
	script = os.path.join(os.path.dirname(os.path.abspath(orch.__file__)), 'orch.py')
	env = dict(os.environ, PYTHONIOENCODING='cp1252')
	subprocess.run([sys.executable, script, '--as', 'bom-writer', 'msg', '--to', 'bom-work', '--file', brief], env=env, capture_output=True)
	done = subprocess.run([sys.executable, script, '--as', 'bom-reader', 'inbox', 'bom-work'], env=env, capture_output=True)
	text = done.stdout.decode('cp1252')
	check('a character the code page lacks does not crash the inbox', done.returncode == 0, done.stderr)
	check('and the message reaches the reader', 'Round 5 \\u2192 passes' in text, text)
	check('with the byte-order mark of its file gone', '\\ufeff' not in text, text)


def test_a_hold_taken_in_the_editor_ends_with_that_editor():
	reset()
	proc(900, 1, 'UnrealEditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	cli(None, 'hold', 'on', '--source', 'editor', '--by', 'someone')
	orch.tick(orch.connect(), force=True)
	check('a hold stays while its editor runs', orch.hold_get(orch.connect()).get('on'))
	advance(orch.ORPHANED_HOLD_SECONDS * 3)
	orch.tick(orch.connect(), force=True)
	check('however long that is', orch.hold_get(orch.connect()).get('on'))
	kill(900)
	orch.tick(orch.connect(), force=True)
	advance(orch.ORPHANED_HOLD_SECONDS - 5)
	orch.tick(orch.connect(), force=True)
	check('a restart of the editor keeps the hold', orch.hold_get(orch.connect()).get('on'))
	proc(901, 1, 'UnrealEditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	orch.tick(orch.connect(), force=True)
	advance(orch.ORPHANED_HOLD_SECONDS * 2)
	orch.tick(orch.connect(), force=True)
	check('and it holds on the relaunched editor', orch.hold_get(orch.connect()).get('on'))
	kill(901)
	orch.tick(orch.connect(), force=True)
	advance(orch.ORPHANED_HOLD_SECONDS + 1)
	orch.tick(orch.connect(), force=True)
	check('with the editor gone, nobody can see or release it, so it ends', not orch.hold_get(orch.connect()).get('on'))
	reset()
	cli(None, 'hold', 'on', '--source', 'cli', '--by', 'someone')
	advance(orch.ORPHANED_HOLD_SECONDS * 3)
	orch.tick(orch.connect(), force=True)
	check('a hold from a terminal does not depend on any editor', orch.hold_get(orch.connect()).get('on'))
	cli(None, 'hold', 'off')


def test_a_restart_keeps_channels_and_mail():
	reset()
	run_hook(100, 'SessionStart', session_id='sess-A')
	cli(100, 'inbox', 'build-fix')
	cli(200, *ASTRA, 'inbox', 'build-fix')
	cli(300, 'inbox', 'build-fix')
	cli(200, *ASTRA, 'msg', '--to', owner(100), 'direct before the restart')
	kill(100)
	orch.tick(orch.connect(), force=True)
	_, out = cli(200, *ASTRA, 'msg', '--to', 'build-fix', 'sent while it was down')
	check('with its old owner dead the fan-out still names it', owner(100) in out, out)

	proc(400, 1, 'claude.exe')
	started = run_hook(400, 'SessionStart', session_id='sess-A')
	text = context_of(started)
	check('the resumed session gets its direct mail as it starts', 'direct before the restart' in text, text)
	check('and what the channel said while it was down', 'sent while it was down' in text, text)
	check('it takes the old place in the channel', members_of('build-fix') == [owner(400), 'agent:astra-build-fix', owner(300)], repr(members_of('build-fix')))
	check('the take-over is journalled', 'took over 2 message(s) and 1 channel(s) from %s' % owner(100) in journal_text(), journal_text())

	_, out = cli(200, *ASTRA, 'msg', '--to', 'build-fix', 'after the restart')
	check('the next message goes to the new owner, not the dead one', owner(400) in out and owner(100) not in out, out)
	text = context_of(run_hook(400, 'PostToolUse', tool_name='Read', tool_input={}, session_id='sess-A'))
	check('and arrives on its next tool call, without running inbox', 'after the restart' in text, text)
	check('once', not context_of(run_hook(400, 'PostToolUse', tool_name='Read', tool_input={}, session_id='sess-A')))

	cli(200, *ASTRA, 'msg', '--to', owner(100), 'a reply to the old address')
	run_hook(400, 'PreToolUse', tool_name='Read', tool_input={}, session_id='sess-A')
	text = context_of(run_hook(400, 'PostToolUse', tool_name='Read', tool_input={}, session_id='sess-A'))
	check('mail that still reaches the dead address follows too', 'a reply to the old address' in text, text)


def test_a_restart_with_a_channel_rejoined_by_hand():
	reset()
	run_hook(100, 'SessionStart', session_id='sess-A')
	cli(100, 'inbox', 'build-fix')
	cli(200, *ASTRA, 'inbox', 'build-fix')
	kill(100)
	orch.tick(orch.connect(), force=True)
	proc(400, 1, 'claude.exe')
	cli(400, 'inbox', 'build-fix')
	run_hook(400, 'SessionStart', session_id='sess-A')
	check('a session already back on the channel is not on it twice', members_of('build-fix') == [owner(400), 'agent:astra-build-fix'], repr(members_of('build-fix')))


def test_a_live_owner_keeps_its_mail():
	reset()
	run_hook(100, 'SessionStart', session_id='sess-A')
	cli(100, 'inbox', 'build-fix')
	cli(200, 'msg', '--to', owner(100), 'still yours')
	proc(400, 1, 'claude.exe')
	run_hook(400, 'SessionStart', session_id='sess-A')
	check('a second window on the session takes nothing from a live owner', owner(100) in members_of('build-fix') and owner(400) not in members_of('build-fix'))
	_, out = cli(100, 'inbox')
	check('its mail stays with it', 'still yours' in out, out)


def test_an_owner_with_no_session_id_moves_nothing():
	reset()
	cli(100, 'inbox', 'build-fix')
	cli(200, 'msg', '--to', owner(100), 'for the first')
	kill(100)
	orch.tick(orch.connect(), force=True)
	proc(400, 1, 'claude.exe')
	run_hook(400, 'PreToolUse', tool_name='Read', tool_input={})
	check('no session id matches nothing', owner(100) in members_of('build-fix') and owner(400) not in members_of('build-fix'))
	check('and leaves its mail', orch.connect().execute('SELECT COUNT(*) c FROM inbox WHERE owner=? AND delivered=0', (owner(100),)).fetchone()['c'] == 1)


def test_a_restart_of_a_session_with_no_hooks():
	real_dir = orch.CLAUDE_SESSIONS_DIR
	orch.CLAUDE_SESSIONS_DIR = tempfile.mkdtemp(dir=STATE)

	def registered(pid, body):
		with open(os.path.join(orch.CLAUDE_SESSIONS_DIR, '%d.json' % pid), 'w', encoding='utf-8') as f:
			f.write(body)

	try:
		reset()
		registered(100, json.dumps({'pid': 100, 'sessionId': 'sess-H'}))
		cli(100, 'inbox', 'build-fix')
		db = orch.connect()
		row = orch.get_owner(db, owner(100))
		check('a hookless Claude takes its session id from the registry', row['session_id'] == 'sess-H' and not row['hooked'], dict(row).__repr__())
		cli(100, 'task', 'start', 'Long job')
		check('but not onto a task, where it would become a relaunch candidate', db.execute('SELECT session_id FROM tasks').fetchone()['session_id'] == '')
		cli(200, *ASTRA, 'inbox', 'build-fix')
		kill(100)
		orch.tick(orch.connect(), force=True)
		cli(200, *ASTRA, 'msg', '--to', 'build-fix', 'sent while it was down')
		proc(400, 1, 'claude.exe')
		registered(400, json.dumps({'pid': 400, 'sessionId': 'sess-H'}))
		_, out = cli(400, 'inbox')
		check('its first plain inbox after the restart reads what it missed', 'sent while it was down' in out, out)
		check('and it is back on the channel', members_of('build-fix') == [owner(400), 'agent:astra-build-fix'], repr(members_of('build-fix')))

		reset()
		for body in ('[1, 2]', '"text"', '7', 'null', '{"pid": 100, "sessionId": 5}', '{"pid": 9, "sessionId": "other"}', '{not json'):
			registered(100, body)
			code, out = cli(100, 'heartbeat')
			check('a registry file holding %s gives no session id and no error' % body, code == 0 and orch.get_owner(orch.connect(), owner(100))['session_id'] == '', out)
	finally:
		orch.CLAUDE_SESSIONS_DIR = real_dir


def test_msg_to_a_bare_agent_name():
	reset()
	cli(200, *ASTRA, 'heartbeat')
	code, out = cli(100, 'msg', '--to', 'astra-build-fix', 'hello')
	check('a bare name that is an agent reaches that agent', code == 0 and 'SENT to agent:astra-build-fix' in out, out)
	check('and makes no channel', orch.meta_get(orch.connect(), orch.CHANNEL_MEMBERS_KEY + 'astra-build-fix') is None)
	_, out = cli(200, *ASTRA, 'inbox')
	check('the agent reads it', 'hello' in out, out)
	_, out = cli(100, 'msg', '--to', ' Astra-Build-Fix ', 'again')
	check('whatever its case and spacing', 'SENT to agent:astra-build-fix' in out, out)

	cli(200, '--as', 'astra-y', 'heartbeat')
	db = orch.connect()
	with orch.Tx(db):
		orch.meta_set(db, orch.CHANNEL_MEMBERS_KEY + 'astra-y', json.dumps([owner(100)]))
	_, out = cli(100, 'msg', '--to', 'astra-y', 'past the mistake')
	check('a channel made by that mistake does not keep catching the name', 'SENT to agent:astra-y' in out, out)

	code, out = cli(100, 'msg', '--to', 'agent:nobody', 'x')
	check('an agent: id nobody has is refused, not a channel', code == 2 and 'no agent' in out and orch.meta_get(orch.connect(), orch.CHANNEL_MEMBERS_KEY + 'agent:nobody') is None, out)


def test_channels_still_work_beside_agent_names():
	reset()
	_, out = cli(100, 'msg', '--to', 'build-fix', 'the brief')
	check('a new name is a new channel, and says so', 'SENT to the build-fix backlog' in out and 'new channel' in out, out)
	_, out = cli(100, 'msg', '--to', 'build-fix', 'more')
	check('only the first post says it', 'SENT to the build-fix backlog' in out and 'new channel' not in out, out)
	_, out = cli(200, *ASTRA, 'inbox', 'build-fix')
	check('the first reader gets the backlog', 'the brief' in out and 'more' in out, out)

	cli(200, '--as', 'menu', 'inbox', 'menu')
	cli(300, 'inbox', 'menu')
	_, out = cli(100, 'msg', '--to', 'menu', 'to everyone')
	check('a channel its namesake agent is on stays a channel', 'agent:menu' in out and owner(300) in out, out)
	_, out = cli(200, '--as', 'menu', 'msg', '--to', 'menu', 'from the namesake')
	check('and the namesake posting on it reaches the others', owner(300) in out and 'agent:menu' not in out, out)
	_, out = cli(200, '--as', 'solo', 'msg', '--to', 'solo', 'not to myself')
	check('an agent never messages itself by its own name', 'SENT to the solo backlog' in out, out)


def test_hook_messages_teach_the_waiter_that_goes_by_exit_codes():
	reset()
	proc(201, 200, 'bash.exe')
	_, out = cli(100, 'acquire', '--kind', 'Build', '--purpose', 'A', '--wait', '0')
	refused = bash(200, BUILD)
	reason = refused['hookSpecificOutput']['permissionDecisionReason'] if denied(refused) else ''
	check('a refusal points at the background waiter', '--until-granted' in reason and 'GRANTED' not in reason, reason)
	cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	kill(201)
	queued = bash(200, BUILD)
	reason = queued['hookSpecificOutput']['permissionDecisionReason'] if denied(queued) else ''
	check('a queued refusal does too', '--until-granted' in reason and 'GRANTED' not in reason, reason)
	stop = run_hook(200, 'Stop', session_id='s200')
	text = json.dumps(stop or {})
	check('and the Stop hook', '--until-granted' in text and 'GRANTED' not in text, text)
	brief = run_hook(300, 'SessionStart', session_id='s300')
	text = json.dumps(brief or {})
	check('and the session brief', '--until-granted' in text and 'GRANTED' not in text, text)


def queue_three_behind_a_holder():
	reset()
	proc(401, 1, 'claude.exe')
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
	ids = []
	for pid in (200, 300, 401):
		proc(pid * 10 + 1, pid, 'bash.exe')
		_, out = cli(pid, 'acquire', '--kind', 'Build', '--purpose', 'P%d' % pid, '--wait', '0')
		ids.append(out.split('id=')[1][:32])
	return ids


def grant_row(gid):
	return orch.connect().execute('SELECT * FROM grants WHERE id=?', (gid,)).fetchone()


def test_a_no_op_reorder_changes_nothing():
	first, second, third = queue_three_behind_a_holder()
	cli(100, 'finish', '--evidence', 'done')
	orch.tick(orch.connect(), force=True)
	since = grant_row(first)['claimable_since']
	check('the head is claimable', since > 0)
	advance(orch.HEAD_GRACE + 10)
	code, out = cli(100, 'reorder', '--id', first[:8], '--to', '1', '--reason', 'no-op')
	check('a move to where it already is succeeds and says so', code == 0 and 'already there' in out, out)
	check('it does not put a sleeping head back on a fresh grace', grant_row(first)['claimable_since'] == since)
	check('and is not journalled as a move', 'no-op' not in journal_text())
	code, out = cli(300, 'acquire', '--id', second[:8], '--wait', '0')
	check('the entry behind can still pass the sleeping head', code == 0 and 'GRANTED' in out, out)


def test_a_real_reorder_only_resets_the_moved_row():
	first, second, third = queue_three_behind_a_holder()
	cli(100, 'finish', '--evidence', 'done')
	orch.tick(orch.connect(), force=True)
	since = grant_row(first)['claimable_since']
	code, out = cli(100, 'reorder', '--id', third[:8], '--to', '2', '--reason', 'behind the head')
	check('the move happens', code == 0 and 'MOVED' in out and 'already' not in out, out)
	db = orch.connect()
	check('the entries are in the new order', [q['id'] for q in orch.queued_grants(db)] == [first, third, second])
	check('the sleeping head keeps its clock', grant_row(first)['claimable_since'] == since)


def test_reorder_counts_what_queue_position_counts():
	reset()
	for pid in (500, 600):
		proc(pid, 1, 'claude.exe')
	cli(500, 'acquire', '--kind', 'Deploy', '--purpose', 'holds deploy', '--wait', '0', '--keep-processes')
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'holds the lane', '--wait', '0')
	_, out = cli(600, 'acquire', '--kind', 'Deploy', '--purpose', 'queued deploy', '--wait', '0', '--keep-processes')
	deploy = out.split('id=')[1][:32]
	_, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'long', '--wait', '0')
	long_job = out.split('id=')[1][:32]
	_, out = cli(300, 'acquire', '--kind', 'PIE', '--purpose', 'short', '--wait', '0')
	short_job = out.split('id=')[1][:32]
	code, out = cli(100, 'reorder', '--id', short_job[:8], '--to', '2', '--reason', 'already second on the lane')
	db = orch.connect()
	check('--to 2 with a Deploy entry first is where it already was', code == 0 and 'already there' in out and orch.queue_position(db, grant_row(short_job)) == 2, out)
	code, out = cli(100, 'reorder', '--id', short_job[:8], '--to', '1', '--reason', 'shortest first')
	db = orch.connect()
	check('--to 1 is lane position 1', code == 0 and 'to #1' in out and orch.queue_position(db, grant_row(short_job)) == 1, out)
	check('the other lane entry moved behind it', orch.queue_position(db, grant_row(long_job)) == 2)
	check('the entry for another resource did not move', grant_row(deploy)['seq'] == min(q['seq'] for q in orch.queued_grants(db)) and orch.queue_position(db, grant_row(deploy)) == 1)
	code, out = cli(100, 'reorder', '--id', short_job[:8], '--to', '9', '--reason', 'past the end')
	db = orch.connect()
	check('a position past the end is the last place among the rows it conflicts with', 'to #2' in out and orch.queue_position(db, grant_row(short_job)) == 2, out)


def test_reorder_refuses_an_ambiguous_prefix():
	first, second, third = queue_three_behind_a_holder()
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE grants SET id='abcd' || substr(id, 5) WHERE id IN (?, ?)", (first, second))
	code, out = cli(100, 'reorder', '--id', 'abcd', '--to', '3', '--reason', 'which one?')
	check('a prefix matching two entries is refused', code == 2 and 'matches 2' in out, out)
	db = orch.connect()
	check('and nothing moved', [q['id'][4:] for q in orch.queued_grants(db)][:2] == [first[4:], second[4:]])


def test_a_dead_waiter_does_not_end_a_turn():
	gid = hold_lane_with_one_waiting()
	advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 30)
	kill(200)
	orch.tick(orch.connect(), force=True)
	check('a waiter that died in this pass cannot end the holder\'s turn', lane_state(gid) == 'active')
	check('its entry is gone', not orch.queued_grants(orch.connect()))
	check('and the holder keeps working', not denied(bash(100, BUILD)))
	post(100)


def test_a_transfer_starts_a_fresh_turn():
	gid = hold_lane_with_one_waiting()
	advance(orch.LANE_TURN + 60)
	cli(300, 'heartbeat')
	cli(200, 'heartbeat')
	code, out = cli(100, 'transfer', '--id', gid, '--to', owner(300))
	check('the past-its-turn holder can hand the grant over', code == 0 and 'TRANSFERRED' in out, out)
	result = bash(300, BUILD)
	check('the new holder is not refused its first lane command', not denied(result), json.dumps(result))
	post(300)
	advance(orch.LANE_TURN - 60)
	cli(200, 'heartbeat')
	check('it has a whole turn', not denied(bash(300, BUILD)))
	post(300)
	advance(120)
	cli(200, 'heartbeat')
	check('and then the limit applies to it too', denied(bash(300, BUILD)))

	first, second, third = queue_three_behind_a_holder()
	cli(200, 'transfer', '--id', first[:8], '--to', owner(300))
	check('a queued grant stays ungranted when handed over', grant_row(first)['acquired_at'] == 0 and grant_row(first)['owner'] == owner(300))


def test_p4_processes_are_tracked_by_verb():
	cases = [
		('"C:\\Program Files\\Perforce\\p4.exe" sync //depot/main/...', 'p4-sync'),
		('p4 -c my_workspace sync', 'p4-sync'),
		('p4.exe unshelve -s 5', 'p4-sync'),
		('p4 revert Source/A.cpp', 'p4-sync'),
		('p4 sync -n //depot/...', None),
		('p4 revert -a Source/...', None),
		('p4 edit Source/A.cpp', None),
		('p4 submit -c 5', None),
		('p4 opened', None),
		('', None),
	]
	for cmd, want in cases:
		got = orch.ue_process_kind('p4.exe', cmd)
		check('p4 process %r is %s' % (cmd[:40], want), got == want, repr(got))
	reset()
	proc(50, 1, 'p4.exe', cmd='p4 sync')
	result = bash(100, 'taskkill /PID 50 /F')
	check('killing a p4 by pid is still not an editor command', not denied(result) and not active(), json.dumps(result))
	post(100)


def test_a_release_waits_out_a_running_p4_sync():
	reset()
	code, out = cli(100, 'acquire', '--kind', 'Sync', '--purpose', 'A: pull latest', '--wait', '0')
	gid = active('Sync')[0]['id']
	cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	bash(100, 'p4 sync //depot/...')
	proc(101, 100, 'bash.exe')
	proc(102, 101, 'p4.exe', cmd='p4 sync //depot/...')
	orch.tick(orch.connect(), force=True)
	advance(orch.LANE_TURN + orch.LANE_TURN_HARD_STOP + 60)
	cli(200, 'heartbeat')
	orch.tick(orch.connect(), force=True)
	check('the hard stop marks the Sync for release', grant_row(gid)['state'] == 'releasing', dict(grant_row(gid)).__repr__())
	code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	check('the next holder waits: a half-synced tree must not be built', code == 3 and 'WAITING' in out, out)
	kill(102)
	cli(200, 'heartbeat')
	code, out = cli(200, 'acquire', '--kind', 'Build', '--purpose', 'B', '--wait', '0')
	check('it gets the lane once p4 has gone', code == 0 and 'GRANTED' in out, out)
	check('the Sync is done', grant_row(gid)['state'] == 'done')


def test_a_plain_finish_waits_out_a_running_p4_sync():
	reset()
	cli(100, 'acquire', '--kind', 'Sync', '--purpose', 'A', '--wait', '0')
	gid = active('Sync')[0]['id']
	proc(101, 100, 'bash.exe')
	proc(102, 101, 'p4.exe', cmd='p4 sync //depot/...')
	orch.tick(orch.connect(), force=True)
	code, out = cli(100, 'finish', '--id', gid[:8], '--evidence', 'left it running')
	check('finish answers at once', code == 0 and 'FINISHED' in out, out)
	check('and the lane stays shut while p4 runs', grant_row(gid)['state'] == 'releasing')
	kill(102)
	orch.tick(orch.connect(), force=True)
	check('then it opens', grant_row(gid)['state'] == 'done')


def test_a_background_p4_sync_keeps_its_implicit_grant():
	reset()
	bash(100, 'p4 sync //depot/...', run_in_background=True)
	proc(101, 100, 'bash.exe')
	proc(102, 101, 'p4.exe', cmd='p4 sync //depot/...')
	post(100)
	orch.tick(orch.connect(), force=True)
	advance(60)
	orch.tick(orch.connect(), force=True)
	check('a background p4 sync keeps the lane past 20 s', len(active('Sync')) == 1)
	kill(102)
	advance(5)
	orch.tick(orch.connect(), force=True)
	check('and frees it when p4 exits', not active())


def test_a_registry_file_that_is_not_an_object():
	real_dir = orch.CLAUDE_SESSIONS_DIR
	orch.CLAUDE_SESSIONS_DIR = tempfile.mkdtemp(dir=STATE)
	try:
		reset()
		cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'A', '--wait', '0')
		for body in ('[1, 2]', '"text"', '7', 'null'):
			with open(os.path.join(orch.CLAUDE_SESSIONS_DIR, '100.json'), 'w', encoding='utf-8') as f:
				f.write(body)
			check('owner_state survives a registry file holding %s' % body, orch.registry_idle_since(100) == 0)
			check('running_sessions too', orch.running_sessions(orch.SNAPSHOT()) == set())
			check('and a lane command is not refused for every agent over it', denied(bash(200, BUILD)) and 'internal error' not in json.dumps(bash(200, BUILD) or {}))
	finally:
		orch.CLAUDE_SESSIONS_DIR = real_dir


def test_a_locked_database_does_not_end_the_waiter():
	reset()
	cli(100, 'acquire', '--kind', 'Deploy', '--purpose', 'A', '--wait', '0', '--keep-processes')
	cli(200, 'acquire', '--kind', 'Deploy', '--purpose', 'B', '--wait', '0', '--keep-processes')
	queued = orch.queued_grants(orch.connect())[0]['id'][:8]
	real_activate, failures = orch.try_activate, [3]

	def locked_then_real(db, snap, g):
		if failures[0]:
			failures[0] -= 1
			raise orch.sqlite3.OperationalError('database is locked')
		return real_activate(db, snap, g)

	orch.try_activate = locked_then_real
	try:
		kill(100)
		code, out = cli(200, 'acquire', '--id', queued, '--until-granted')
		check('--until-granted rides out a locked database and is granted', code == 0 and 'GRANTED' in out and failures[0] == 0, repr((code, out[-200:])))
		check('the retries are journalled', 'a waiter retries: database is locked' in journal_text())
		check('a retry keeps the kind it queued for', active('Deploy') and active('Deploy')[0]['id'].startswith(queued), repr([dict(g)['kind'] for g in active()]))

		hold_lane_with_one_waiting()
		queued = orch.queued_grants(orch.connect())[0]['id'][:8]
		failures[0] = 1
		raised = False
		try:
			cli(200, 'acquire', '--id', queued, '--wait', '0')
		except orch.sqlite3.OperationalError:
			raised = True
		check('a foreground acquire still reports it', raised)
		failures[0] = 0

		orch.try_activate = lambda db, snap, g: (_ for _ in ()).throw(orch.sqlite3.OperationalError('no such table: grants'))
		raised = False
		try:
			cli(200, 'acquire', '--id', queued, '--until-granted')
		except orch.sqlite3.OperationalError:
			raised = True
		check('any other database error is not retried for ever', raised)
	finally:
		orch.try_activate = real_activate


def fresh_feed():
	orch.tick(orch.connect(), force=True)
	return status_feed()


def task_named(feed, title):
	return next((t for t in feed['tasks'] if t['title'] == title), None)


def test_feed_keeps_live_tasks_ahead_of_left_over_ones():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='s100', session_title='Live one')
	cli(100, 'task', 'start', 'an older live task')
	advance(5)
	run_hook(200, 'UserPromptSubmit', prompt='y', session_id='s200', session_title='Gone one')
	for n in range(orch.FEED_TASKS + 5):
		advance(1)
		cli(200, 'task', 'start', 'dead %d' % n)
		cli(200, 'task', 'block', orch.connect().execute("SELECT id FROM tasks WHERE title=?", ('dead %d' % n,)).fetchone()['id'][:8], '--on', 'user', '--why', 'w')
	kill(200)
	feed = fresh_feed()
	live = task_named(feed, 'an older live task')
	check('a live task older than every dead one is still in the feed', live is not None)
	check('live tasks come first', feed['tasks'][0]['title'] == 'an older live task')
	check('and say so', live['ownerAlive'] is True and live['ownerName'] == 'Live one')
	left = [t for t in feed['tasks'] if not t['ownerAlive']]
	check('dead owners\' tasks are marked', len(left) == orch.FEED_LEFTOVER_TASKS and left[0]['ownerName'] == 'Gone one')
	check('the feed counts them all', feed['tasksLeftOver'] == orch.FEED_TASKS + 5)
	check('newest left-over first', left[0]['title'] == 'dead %d' % (orch.FEED_TASKS + 4))
	reset()
	for n in range(orch.FEED_TASKS + 3):
		cli(100, 'task', 'start', 'live %d' % n)
	feed = fresh_feed()
	check('live tasks have their own cap', len(feed['tasks']) == orch.FEED_TASKS and feed['tasksLeftOver'] == 0)
	reset()
	cli(None, '--as', 'quiet', 'task', 'start', 'quiet agent')
	check('an agent: owner is alive while it talks', task_named(fresh_feed(), 'quiet agent')['ownerAlive'] is True)
	advance(orch.CLI_SILENCE + 1)
	check('and gone once silent', task_named(fresh_feed(), 'quiet agent')['ownerAlive'] is False)
	cli(None, '--as', 'quiet', 'heartbeat')
	check('its next call brings it back', task_named(fresh_feed(), 'quiet agent')['ownerAlive'] is True)
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='s100')
	cli(100, 'task', 'start', 'relaunching')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE tasks SET state='resuming', resumed_at=?", (orch.now(),))
	kill(100)
	check('a task being relaunched is not left over', task_named(fresh_feed(), 'relaunching')['ownerAlive'] is True)


def test_a_resumed_session_keeps_its_old_tasks_live():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='same', session_title='Old name')
	cli(100, 'task', 'start', 'waiting on the user')
	tid = orch.connect().execute('SELECT id FROM tasks').fetchone()['id'][:8]
	cli(100, 'task', 'block', tid, '--on', 'user', '--why', 'w')
	kill(100)
	check('with its session gone the task is left over', task_named(fresh_feed(), 'waiting on the user')['ownerAlive'] is False)
	proc(400, 1, 'claude.exe')
	run_hook(400, 'UserPromptSubmit', prompt='continue', session_id='same', session_title='New name')
	task = task_named(fresh_feed(), 'waiting on the user')
	check('the same session under a new owner carries it', task['ownerAlive'] is True and task['owner'] == owner(100))
	check('under its new name', task['ownerName'] == 'New name')


def transcript_file(*entries):
	path = os.path.join(tempfile.mkdtemp(dir=STATE), 'session.jsonl')
	add_lines(path, *entries)
	return path


def add_lines(path, *entries):
	with open(path, 'a', encoding='utf-8', newline='\n') as f:
		for entry in entries:
			f.write(json.dumps(entry) + '\n')


def user_line(text):
	return {'type': 'user', 'message': {'role': 'user', 'content': text}, 'sessionId': 's'}


def ai_line(text):
	return {'type': 'ai-title', 'aiTitle': text, 'sessionId': 's'}


def title_of(pid):
	return orch.get_owner(orch.connect(), owner(pid))['title']


def refresh(seen):
	orch.refresh_titles(orch.connect(), orch.SNAPSHOT(), seen)


def test_titles_follow_one_rule():
	reset()
	path = transcript_file(user_line('first thing i asked'))
	seen = {}
	run_hook(100, 'SessionStart', session_id='s', transcript_path=path)
	check('a transcript with only a prompt gives the prompt', title_of(100) == 'first thing i asked')
	add_lines(path, ai_line('Generated title'))
	refresh(seen)
	check('the generated title replaces the prompt', title_of(100) == 'Generated title')
	add_lines(path, ai_line('Better generated title'))
	refresh(seen)
	check('a new generated title replaces the old', title_of(100) == 'Better generated title')
	offset = seen[path]['offset']
	refresh(seen)
	check('nothing new reads nothing', seen[path]['offset'] == offset and title_of(100) == 'Better generated title')
	add_lines(path, {'type': 'custom-title', 'customTitle': 'My name', 'sessionId': 's'})
	refresh(seen)
	check('the user\'s rename beats the generated title', title_of(100) == 'My name')
	add_lines(path, ai_line('Late generated title'))
	refresh(seen)
	check('and stays over a later one', title_of(100) == 'My name')
	add_lines(path, {'type': 'agent-name', 'agentName': 'Other name', 'sessionId': 's'})
	refresh(seen)
	check('agent-name is the user\'s rename too, and the last one counts', title_of(100) == 'Other name')
	with open(path, 'ab') as f:
		f.write(b'{"type":"custom-title","customTitle":"half written')
	refresh(seen)
	check('a half-written line waits', title_of(100) == 'Other name')
	with open(path, 'ab') as f:
		f.write(b'"}\n')
	refresh(seen)
	check('and reads once it is whole', title_of(100) == 'half written')
	reset()
	path = transcript_file(user_line('p'), ai_line('Generated'))
	run_hook(100, 'UserPromptSubmit', prompt='p', session_id='s', transcript_path=path)
	cli(100, 'name', 'Self named')
	refresh({})
	check('a name an agent gave itself stays over a generated title', title_of(100) == 'Self named')
	add_lines(path, {'type': 'custom-title', 'customTitle': 'User named'})
	refresh({})
	check('and yields to the user\'s rename', title_of(100) == 'User named')
	reset()
	path = transcript_file(ai_line('Generated'))
	run_hook(100, 'UserPromptSubmit', prompt='p', session_id='s', session_title='Desktop title', transcript_path=path)
	refresh({})
	check('the desktop app\'s session title stays over a generated title', title_of(100) == 'Desktop title')
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='no transcript yet', session_id='s', transcript_path=os.path.join(STATE, 'nothing-here.jsonl'))
	refresh({})
	check('a prompt stands in with no transcript', title_of(100) == 'no transcript yet')
	reset()
	path = transcript_file(ai_line('Old session'))
	seen = {}
	run_hook(100, 'UserPromptSubmit', prompt='first', session_id='s1', transcript_path=path)
	refresh(seen)
	other = transcript_file(user_line('after /clear'))
	run_hook(100, 'UserPromptSubmit', prompt='after /clear', session_id='s2', transcript_path=other)
	refresh(seen)
	check('/clear in the same process follows the new transcript', title_of(100) == 'after /clear')
	with open(other, 'w', encoding='utf-8', newline='\n') as f:
		f.write(json.dumps(ai_line('Rewritten')) + '\n')
	refresh(seen)
	check('a transcript that shrank is read again from the top', title_of(100) == 'Rewritten')


def test_a_resumed_session_is_not_called_continue():
	reset()
	path = transcript_file(user_line('make the login page'), ai_line('Login page'))
	run_hook(100, 'UserPromptSubmit', prompt='make the login page', session_id='s', transcript_path=path)
	kill(100)
	proc(400, 1, 'claude.exe')
	run_hook(400, 'UserPromptSubmit', prompt='continue', session_id='s', transcript_path=path)
	check('a resumed session keeps the generated title', title_of(400) == 'Login page')
	reset()
	path = transcript_file(user_line('make the login page'))
	proc(400, 1, 'claude.exe')
	run_hook(400, 'UserPromptSubmit', prompt='continue', session_id='s', transcript_path=path)
	check('with none yet, the session\'s first prompt, not this one', title_of(400) == 'make the login page')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE owners SET title='continue' WHERE owner=?", (owner(400),))
	add_lines(path, ai_line('Login page'))
	refresh({})
	check('a title recorded before this rule is corrected', title_of(400) == 'Login page')


def test_a_title_ignores_what_a_person_did_not_type():
	reset()
	long_prompt = 'word ' * 100
	path = transcript_file(
		{'type': 'user', 'isMeta': True, 'message': {'content': 'meta'}},
		{'type': 'user', 'isSidechain': True, 'message': {'content': 'a subagent'}},
		{'type': 'user', 'toolUseResult': {}, 'message': {'content': 'tool output'}},
		{'type': 'user', 'origin': {'kind': 'task-notification'}, 'message': {'content': 'a notification'}},
		user_line('<command-name>/clear</command-name>'),
		user_line('  Caveat: the messages below'),
		user_line('Another Claude session sent a message'),
		user_line('This session is being continued from a previous conversation'),
		{'type': 'user', 'message': {'content': [{'type': 'tool_result', 'content': 'x'}, {'type': 'text', 'text': 'beside a result'}]}},
		{'type': 'user', 'message': {'content': [{'type': 'image'}, {'type': 'text', 'text': long_prompt}]}},
		user_line('a later prompt'))
	titles = orch.transcript_titles(path)
	check('the first prompt a person typed', titles['prompt'].startswith('word word'))
	check('in one line, cut to size', len(titles['prompt']) <= orch.TITLE_PROMPT_MAX and '  ' not in titles['prompt'])
	path = transcript_file({'type': 'assistant', 'message': {'content': 'the words "ai-title" and "user" in a reply'}}, 'not an object', ai_line(''), {'type': 'ai-title', 'aiTitle': 5})
	titles = orch.transcript_titles(path)
	check('other lines and empty or odd titles read as nothing', titles['ai'] == '' and titles['name'] == '' and titles['prompt'] == '')
	with open(path, 'ab') as f:
		f.write(b'{not json\n\xff\xfe"ai-title"\n')
	add_lines(path, ai_line('Still found'))
	check('a broken line is skipped', orch.transcript_titles(path)['ai'] == 'Still found')
	check('no path is no title', orch.transcript_titles('')['ai'] == '' and orch.transcript_titles(None)['ai'] == '')


def test_a_session_with_no_hooks_gets_its_name_from_the_registry():
	real_dir = orch.CLAUDE_SESSIONS_DIR
	root = tempfile.mkdtemp(dir=STATE)
	orch.CLAUDE_SESSIONS_DIR = os.path.join(root, 'sessions')
	os.makedirs(orch.CLAUDE_SESSIONS_DIR)

	def registry(pid, **fields):
		with open(os.path.join(orch.CLAUDE_SESSIONS_DIR, '%d.json' % pid), 'w', encoding='utf-8') as f:
			json.dump(dict(pid=pid, **fields), f)

	try:
		reset()
		registry(100, sessionId='abc', cwd='C:\\work\\project', name='Lock Fix', nameSource='user')
		cli(100, 'heartbeat')
		refresh({})
		o = orch.get_owner(orch.connect(), owner(100))
		check('a name the user gave reads from the registry', o['title'] == 'Lock Fix')
		folder = os.path.join(root, 'projects', 'C--work-project')
		check('its transcript path comes from the registry', o['transcript'] == os.path.join(folder, 'abc.jsonl'))
		check('and its working folder', o['cwd'] == 'C:\\work\\project')
		reset()
		registry(100, sessionId='abc', cwd='C:\\work\\project', name='project-8b', nameSource='derived')
		cli(100, 'heartbeat')
		refresh({})
		check('a derived registry name means nothing', title_of(100) == '')
		os.makedirs(folder)
		add_lines(os.path.join(folder, 'abc.jsonl'), user_line('hi there'), ai_line('From its transcript'))
		refresh({})
		check('and the transcript names it', title_of(100) == 'From its transcript')
		reset()
		registry(100, sessionId='abc', cwd='C:\\x', name='MASTER_AGENT', nameSource='user')
		run_hook(100, 'UserPromptSubmit', prompt='hello', session_id='abc', transcript_path='')
		refresh({})
		fresh_feed()
		agent = next(a for a in status_feed()['agents'] if a['owner'] == owner(100))
		check('the Master Agent reads his name whatever his session is called', agent['displayName'] == orch.MASTER_AGENT_NAME and agent['title'] == 'MASTER_AGENT')
		for odd in (['not', 'a', 'dict'], {'pid': 100, 'name': 5, 'nameSource': 'user', 'cwd': 7, 'sessionId': 8}):
			with open(os.path.join(orch.CLAUDE_SESSIONS_DIR, '100.json'), 'w', encoding='utf-8') as f:
				json.dump(odd, f)
			refresh({})
		check('an odd registry entry raises nothing', True)
	finally:
		orch.CLAUDE_SESSIONS_DIR = real_dir


def test_feed_display_names():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='p', session_id='s', session_title='Menu fix')
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	cli(200, 'heartbeat')
	cli(None, '--as', 'astra-shader-cache', 'heartbeat')
	cli(None, '--as', 'bom-writer', 'heartbeat')
	cli(None, '--as', 'named', 'name', 'Given Name')
	feed = fresh_feed()
	names = {a['owner']: a['displayName'] for a in feed['agents']}
	check('a title is the name', names[owner(100)] == 'Menu fix')
	check('no title yet reads as the kind and pid', names[owner(200)] == 'Claude 200')
	check('an Astra reads by its work', names['agent:astra-shader-cache'] == 'Astra: shader-cache')
	check('another named agent reads by its name', names['agent:bom-writer'] == 'bom-writer')
	check('a given name beats the id', names['agent:named'] == 'Given Name')
	check('on grants too', feed['lane']['displayName'] == 'Menu fix' and feed['grants'][0]['displayName'] == 'Menu fix')
	check('the title field is unchanged', next(a for a in feed['agents'] if a['owner'] == 'agent:bom-writer')['title'] == '')
	check('an unknown owner id reads as itself', orch.display_name('orchtest:A', None) == 'orchtest:A')
	check('the panel\'s idle filter is fed', next(a for a in feed['agents'] if a['owner'] == owner(100))['working'] is True)
	cli(None, 'hold', 'on', '--source', 'editor', '--by', 'u')
	check('the paused list has names', fresh_feed()['hold']['pausedNames'] == ['Menu fix'])
	cli(None, 'hold', 'off')


def test_feed_activity_says_what_a_grant_is_doing():
	def lane():
		return fresh_feed()['lane']

	def editor(pie):
		proc(700, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
		write_report(700, pie)

	reset()
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'x', '--wait', '0')
	check('a Build grant with nothing running says so, not "preparing"', lane()['activity'] == 'has the editor for a build')
	check('the internal phase is unchanged', lane()['phase'] == 'preparing')
	proc(150, 100, 'dotnet.exe', cmd='dotnet UnrealBuildTool.dll MyGameEditor Win64 Development')
	check('a build tool under its owner is building', lane()['activity'] == 'building')
	check('still with the phase it had', lane()['phase'] == 'preparing')
	kill(150)
	proc(151, 1, 'dotnet.exe', cmd='dotnet UnrealBuildTool.dll MyGameEditor Win64 Development')
	check('a detached build tool during a Build grant is building', lane()['activity'] == 'building')
	code, text = cli(100, 'status', '--history', '0')
	check('the CLI still prints the phase', 'preparing' in text and 'building' not in text)
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a PIE grant with no editor is holding', lane()['activity'] == 'has the editor for PIE')
	proc(150, 1, 'dotnet.exe', cmd='dotnet UnrealBuildTool.dll MyGameEditor Win64 Development')
	check('a detached build tool is not a PIE grant\'s', lane()['activity'] == 'has the editor for PIE')
	kill(150)
	editor(True)
	check('an editor reporting PIE puts it in PIE', lane()['activity'] == 'in PIE')
	check('with the phase it had', lane()['phase'] == 'preparing')
	editor(False)
	check('an editor reporting none is not', lane()['activity'] == 'has the editor for PIE')
	proc(160, 100, 'python.exe', cmd='python .claude/scripts/tools/ue_' + 'python.py step.py')
	check('a remote python under its owner is using the editor', lane()['activity'] == 'using the editor')
	kill(160)
	proc(160, 200, 'python.exe', cmd='python .claude/scripts/tools/ue_' + 'python.py step.py')
	check('another agent\'s is not', lane()['activity'] == 'has the editor for PIE')
	kill(160)
	run_hook(100, 'PreToolUse', tool_name='mcp__unreal-engine__list_toolsets', tool_input={}, session_id='s')
	check('an Unreal MCP call is using the editor', lane()['activity'] == 'using the editor')
	post(100, 'mcp__unreal-engine__list_toolsets')
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	db = orch.connect()
	with orch.Tx(db):
		db.execute('UPDATE grants SET pie_started=1')
	check('with no way to ask, the PIE it started counts', lane()['activity'] == 'in PIE')
	editor(False)
	check('but what the editor says wins', lane()['activity'] == 'has the editor for PIE')
	reset()
	cli(100, 'acquire', '--kind', 'Editor', '--purpose', 'x', '--wait', '0')
	check('an Editor grant is using the editor', lane()['activity'] == 'using the editor')
	reset()
	cli(100, 'acquire', '--kind', 'Sync', '--purpose', 'x', '--wait', '0')
	check('a Sync grant with nothing running is holding', lane()['activity'] == 'has the editor for a p4 sync')
	proc(150, 100, 'p4.exe', cmd='p4 sync //depot/...')
	check('a p4 sync under its owner is syncing', lane()['activity'] == 'syncing from p4')
	kill(150)
	reset()
	bash(100, BUILD)
	check('a lone build command is building before its tool shows', lane()['activity'] == 'building' and lane()['implicit'])
	post(100)
	bash(100, 'python .claude/scripts/tools/ue_' + 'python.py probe.py')
	check('a lone editor command is using the editor', lane()['activity'] == 'using the editor')
	post(100)
	reset()
	cli(100, 'acquire', '--kind', 'Test', '--purpose', 'x', '--wait', '0')
	feed = fresh_feed()
	check('a Test grant is not the exclusive lane', feed['lane'] is None and feed['grants'][0]['activity'] == 'has the editor for tests' and feed['grants'][0]['editorPid'] == 0)
	proc(150, 100, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -game')
	check('a test process under its owner is testing', fresh_feed()['grants'][0]['activity'] == 'testing')
	kill(150)
	reset()
	cli(100, 'acquire', '--kind', 'Deploy', '--purpose', 'x', '--wait', '0')
	check('a Deploy grant is deploying', fresh_feed()['grants'][0]['activity'] == 'deploying')
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	check('a queued entry is queued', fresh_feed()['queue'][0]['activity'] == 'queued')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE grants SET state='releasing' WHERE owner=?", (owner(100),))
	orch.write_status(db, orch.SNAPSHOT())
	check('a grant on its way out is finishing up', status_feed()['lane']['activity'] == 'finishing up')


def write_report(pid, pie, at=None, reporter=None, since=None, by_input=False):
	folder = os.path.join(orch.STATE_DIR, 'editors')
	os.makedirs(folder, exist_ok=True)
	report = {'pid': reporter or pid, 'pie': pie, 'at': orch.now() if at is None else at}
	if since is not None:
		report.update(pieSince=since, pieByInput=by_input)
	with open(os.path.join(folder, '%d.json' % pid), 'w', encoding='utf-8') as f:
		json.dump(report, f)


def test_feed_lists_every_editor():
	reset()
	check('no editor, no rows', fresh_feed()['editors'] == [])
	proc(40, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -nullrhi -unattended')
	proc(60, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe "C:/x/MyGame.uproject" -slnodlssg')
	proc(70, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/Other.uproject')
	proc(80, 1, 'unrealeditor-cmd.exe', cmd='UnrealEditor-Cmd.exe C:/x/MyGame.uproject -run=Foo')
	proc(90, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -game')
	rows = fresh_feed()['editors']
	check('one row per editor on this project', [r['pid'] for r in rows] == [60, 40])
	check('the rendering one is the main one, listed first', rows[0]['role'] == 'main' and rows[0]['rendering'] is True)
	check('-nullrhi is headless', rows[1]['role'] == 'headless' and rows[1]['rendering'] is False)
	check('the rules see the main editor first too', orch.editor_pids(orch.SNAPSHOT()) == [60, 40])
	proc(30, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject', start=100000)
	rows = fresh_feed()['editors']
	check('the oldest rendering editor stays main', [(r['pid'], r['role']) for r in rows] == [(60, 'main'), (30, 'second'), (40, 'headless')])
	kill(60)
	kill(30)
	check('with no rendering editor nothing is main', [r['role'] for r in fresh_feed()['editors']] == ['headless'])
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('the lane means the main editor', fresh_feed()['lane']['editorPid'] == 60)
	proc(61, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	gid = active()[0]['id']
	cli(100, 'begin', '--id', gid, '--editor-pid', '61')
	check('unless the grant named another running one', fresh_feed()['lane']['editorPid'] == 61)
	kill(61)
	check('and only while it runs', fresh_feed()['lane']['editorPid'] == 60)


def test_feed_pie_state_is_never_a_guess():
	def pie(index=0):
		return fresh_feed()['editors'][index]['pie']

	def probe(**fields):
		db = orch.connect()
		with orch.Tx(db):
			orch.meta_set(db, 'pie_probe', json.dumps(fields))

	reset()
	proc(60, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	proc(40, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -nullrhi')
	check('nothing known reads null', pie() is None and pie(1) is None)
	probe(at=orch.now(), running=True, editor_pid=60, reachable=True)
	check('a fresh probe of that editor answers for it', pie() is True)
	check('and only for it', pie(1) is None)
	probe(at=orch.now(), running=False, editor_pid=60)
	check('a no answers too', pie() is False)
	probe(at=orch.now(), running=False, editor_pid=60, reachable=False)
	check('an editor that did not answer is unknown, not no', pie() is None)
	probe(at=orch.now() - orch.PIE_PROBE_TTL * 3 - 1, running=True, editor_pid=60)
	check('a stale probe is unknown', pie() is None)
	write_report(60, True)
	check('the editor\'s own report answers while no probe can', pie() is True)
	probe(at=orch.now(), running=False, editor_pid=60)
	check('and beats the probe', pie() is True)
	write_report(60, False)
	probe(at=orch.now(), running=True, editor_pid=60)
	check('its no beats it too', pie() is False)
	probe()
	write_report(60, True, at=orch.now() - orch.EDITOR_REPORT_TTL - 1)
	check('a report that stopped is unknown: a frozen editor writes none', pie() is None)
	write_report(60, True, reporter=61)
	check('a report from another process is ignored', pie() is None)
	for odd in ('{not json', '[1]', '{"pid": 60, "pie": 1, "at": 1900000000}', '{"pid": 60, "pie": true}', '{"pid": 60, "pie": true, "at": "soon"}'):
		with open(os.path.join(orch.STATE_DIR, 'editors', '60.json'), 'w', encoding='utf-8') as f:
			f.write(odd)
		check('an unreadable report is unknown: %s' % odd, pie() is None)


def test_feed_says_who_is_on_each_editor():
	def row(pid):
		return next(r for r in fresh_feed()['editors'] if r['pid'] == pid)

	reset()
	run_hook(100, 'UserPromptSubmit', prompt='p', session_id='s', session_title='Menu fix')
	proc(60, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	check('nobody on it reads empty', row(60)['onIt'] == '' and row(60)['onItName'] == '')
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('the lane holder is on the main editor', row(60)['onIt'] == owner(100) and row(60)['onItName'] == 'Menu fix')
	proc(140, 100, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -nullrhi')
	check('a headless editor it launched is its own', row(140)['onIt'] == owner(100))
	cli(None, 'hold', 'on', '--source', 'editor', '--by', 'u')
	check('under the user\'s hold the user is', row(60)['onIt'] == 'user' and row(60)['onItName'] == 'you')
	cli(None, 'hold', 'off')
	reset()
	proc(40, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -nullrhi', start=int((orch.now() - 600) * 1e7) + orch.DOTNET_TICKS_AT_UNIX_EPOCH)
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a headless editor older than the lane holder\'s grant is nobody\'s', row(40)['onIt'] == '')
	proc(41, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject -nullrhi', start=int(orch.now() * 1e7) + orch.DOTNET_TICKS_AT_UNIX_EPOCH)
	check('one started under the grant is the holder\'s', row(41)['onIt'] == owner(100))


def test_a_frozen_editor_shows_in_the_feed():
	real = orch.hung_window_pids
	check('the real check runs', isinstance(real(), set))

	def watch(hung):
		orch.hung_window_pids = lambda: set(hung)
		db = orch.connect()
		orch.refresh_editor_watch(db, orch.SNAPSHOT(), orch.now())
		return fresh_feed()['editors'][0]

	try:
		reset()
		proc(60, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
		row = watch([])
		check('an editor that answers is not flagged', row['notRespondingSince'] == 0 and row['frozen'] is False)
		row = watch([60, 999])
		since = orch.now()
		check('one that stops is timed from then', row['notRespondingSince'] == since and row['frozen'] is False)
		advance(orch.FROZEN_EDITOR_SECONDS - 1)
		row = watch([60])
		check('a long load is not frozen yet', row['notRespondingSince'] == since and row['frozen'] is False)
		advance(2)
		check('past the limit it is', watch([60])['frozen'] is True)
		check('and the chip turns red', status_feed()['summary']['dot'] == 'red')
		row = watch([])
		check('and it clears when it answers', row['notRespondingSince'] == 0 and row['frozen'] is False)
		watch([60])
		kill(60)
		orch.refresh_editor_watch(orch.connect(), orch.SNAPSHOT(), orch.now())
		check('a gone editor leaves nothing behind', orch.meta_get(orch.connect(), orch.EDITOR_HUNG_KEY) == '{}')
	finally:
		orch.hung_window_pids = real


def test_feed_turn_clock():
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	lane = fresh_feed()['lane']
	check('with nobody waiting a turn has no clock', lane['turnDueAt'] == 0 and lane['turnBackAt'] == 0)
	acquired = lane['acquiredAt']
	advance(60)
	queued_at = orch.now()
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	lane = fresh_feed()['lane']
	check('someone waiting starts it from the grant', lane['turnDueAt'] == acquired + orch.LANE_TURN)
	check('and says when the lane comes back at the latest', lane['turnBackAt'] == lane['turnDueAt'] + orch.LANE_TURN_HARD_STOP)
	check('a queued entry has no clock', fresh_feed()['queue'][0]['turnDueAt'] == 0)
	advance(orch.LANE_TURN - 200)
	cli(300, 'heartbeat')
	lane = fresh_feed()['lane']
	check('a late waiter does not move it', lane['turnDueAt'] == acquired + orch.LANE_TURN)
	db = orch.connect()
	check('the overrun rule uses the same time', orch.lane_turn_overrun(db, orch.lane_holder(db), lane['turnDueAt'] + 5) == 5)
	check('and is 0 before it', orch.lane_turn_overrun(db, orch.lane_holder(db), lane['turnDueAt'] - 5) == 0)
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	advance(orch.LANE_TURN + 300)
	queued_at = orch.now()
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	check('a long hold gets its notice from the first waiter', fresh_feed()['lane']['turnDueAt'] == queued_at + orch.LANE_TURN_NOTICE)
	cli(None, 'hold', 'on', '--source', 'editor', '--by', 'u')
	check('the user\'s hold stops the clock', fresh_feed()['lane']['turnDueAt'] == 0)
	cli(None, 'hold', 'off')
	reset()
	bash(100, BUILD)
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	check('a lone command has no turn', fresh_feed()['lane']['turnDueAt'] == 0)


def test_feed_summary_decides_the_chip():
	def summary():
		return fresh_feed()['summary']

	reset()
	run_hook(100, 'UserPromptSubmit', prompt='p', session_id='s', session_title='Menu fix')
	result = summary()
	check('free and green', result['state'] == 'free' and result['dot'] == 'green' and result['text'] == 'Editor free - 1 agent working')
	check('taking a free editor pauses nobody', result['takePauses'] == [])
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'x', '--wait', '0')
	result = summary()
	check('a holder is orange and named', result['state'] == 'lane' and result['dot'] == 'orange' and result['text'] == 'Menu fix - has the editor for a build')
	check('taking it pauses the holder', result['takePauses'] == ['Menu fix'])
	run_hook(200, 'UserPromptSubmit', prompt='q', session_id='s2', session_title='Other')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	check('and whoever queues behind it, each once', summary()['takePauses'] == ['Menu fix', 'Other'])
	cli(100, 'finish', '--evidence', 'x')
	cli(200, 'cancel')
	result = summary()
	check('finished: free again', result['state'] == 'free' and result['text'] == 'Editor free - 2 agents working')
	cli(200, 'acquire', '--kind', 'Deploy', '--purpose', 'd', '--wait', '0')
	cli(100, 'acquire', '--kind', 'Deploy', '--purpose', 'e', '--wait', '0')
	result = summary()
	check('a Deploy queue is no wait for the editor', result['state'] == 'free' and result['takePauses'] == [])
	reset()
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	cli(100, 'finish', '--evidence', 'x')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE grants SET state='queued' WHERE owner=?", (owner(200),))
	orch.write_status(db, orch.SNAPSHOT())
	result = status_feed()['summary']
	check('waiting with nobody holding is orange', result['state'] == 'waiting' and result['dot'] == 'orange' and result['text'] == '1 agent waiting for the editor')
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:/x/MyGame.uproject')
	write_report(60, True)
	result = summary()
	check('a PIE with no grant is the user\'s, and stays green', result['state'] == 'pie' and result['dot'] == 'green' and result['text'] == 'Your PIE - no agent waiting')
	check('and says which editor', result['piePid'] == 60)
	write_report(60, False)
	proc(800, 1, 'dotnet.exe', orch.DOTNET_TICKS_AT_UNIX_EPOCH + int((orch.now() - 30) * 1e7), 'dotnet UnrealBuildTool.dll MyGameEditor Win64 Development')
	result = summary()
	check('a build nobody claims stays green too', result['state'] == 'occupied' and result['dot'] == 'green' and result['piePid'] == 0)
	kill(800)
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	cli(None, 'hold', 'on', '--source', 'editor', '--by', 'u')
	result = summary()
	check('the user\'s hold is blue', result['state'] == 'hold' and result['dot'] == 'blue')
	check('with nothing more to pause', result['takePauses'] == [])
	cli(None, 'hold', 'off')


AGE_OUT = orch.TASK_AGE_OUT_DAYS * 86400


def task_row(title):
	return orch.connect().execute('SELECT * FROM tasks WHERE title=?', (title,)).fetchone()


def test_old_tasks_with_no_live_owner_expire():
	reset()
	run_hook(200, 'UserPromptSubmit', prompt='y', session_id='s200', session_title='Gone one')
	cli(200, 'task', 'start', 'with notes', '--next', 'keep this', '--files', 'A.cpp')
	cli(200, 'task', 'block', task_row('with notes')['id'][:8], '--on', 'user', '--why', 'which map?')
	cli(200, 'task', 'start', 'running when it died')
	done = cli(200, 'task', 'start', 'finished')[1].split()[1]
	cli(200, 'task', 'done', done, '--evidence', 'e')
	cancelled = cli(200, 'task', 'start', 'dropped')[1].split()[1]
	cli(200, 'task', 'cancel', cancelled, '--why', 'w')
	kill(200)
	feed = fresh_feed()
	check('a dead owner\'s task starts as left over', feed['tasksLeftOver'] == 2)
	updated = task_row('with notes')['updated']
	advance(AGE_OUT - 10)
	feed = fresh_feed()
	check('short of the limit it stays', task_row('with notes')['state'] == 'blocked' and feed['tasksLeftOver'] == 2)
	advance(20)
	feed = fresh_feed()
	row = task_row('with notes')
	check('past it the task closes itself', row['state'] == 'expired')
	check('so does one orphaned by its dying owner', task_row('running when it died')['state'] == 'expired')
	check('its row and notes stay', row['next_step'] == 'keep this' and row['files'] == 'A.cpp' and row['blocked_on'] == 'user' and row['why'] == 'which map?')
	check('and when it was last touched', row['updated'] == updated)
	check('it leaves the feed', feed['tasksLeftOver'] == 0 and feed['tasks'] == [])
	check('and the status list', 'with notes' not in cli(100, 'status', '--history', '0')[1])
	check('and the task list', 'with notes' not in cli(100, 'task', 'list')[1])
	check('--all still finds it', 'expired' in cli(100, 'task', 'list', '--all')[1])
	check('done and cancelled stay what they were', task_row('finished')['state'] == 'done' and task_row('dropped')['state'] == 'cancelled')
	lines = [line for line in journal_text().splitlines() if ' expire ' in line]
	check('it is journalled', len(lines) == 2 and 'no live owner' in lines[0] and 'was blocked' in ''.join(lines))
	advance(600)
	fresh_feed()
	check('once', len([line for line in journal_text().splitlines() if ' expire ' in line]) == 2)
	reset()
	cli(None, '--as', 'quiet', 'task', 'start', 'quiet agent')
	advance(AGE_OUT + 1)
	fresh_feed()
	check('an agent: owner gone quiet expires too', task_row('quiet agent')['state'] == 'expired')
	reset()
	cli(200, 'task', 'start', 'parked one')
	db = orch.connect()
	with orch.Tx(db):
		db.execute("UPDATE tasks SET state='parked'")
	run_hook(200, 'SessionEnd', reason='clear', session_id='s200')
	advance(AGE_OUT + 1)
	fresh_feed()
	check('a parked task of an ended session expires', task_row('parked one')['state'] == 'expired')


def test_a_live_owner_or_a_claim_keeps_a_task():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='s100')
	cli(100, 'task', 'start', 'waiting a long time')
	cli(100, 'task', 'block', task_row('waiting a long time')['id'][:8], '--on', 'user', '--why', 'w')
	for _ in range(3):
		advance(AGE_OUT)
		run_hook(100, 'Notification', message='keep alive', session_id='s100')
		fresh_feed()
	check('a live owner\'s task never expires', task_row('waiting a long time')['state'] == 'blocked')
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='same')
	cli(100, 'task', 'start', 'on a resumed session')
	cli(100, 'task', 'block', task_row('on a resumed session')['id'][:8], '--on', 'user', '--why', 'w')
	kill(100)
	fresh_feed()
	advance(AGE_OUT + 1)
	proc(400, 1, 'claude.exe')
	run_hook(400, 'UserPromptSubmit', prompt='continue', session_id='same')
	fresh_feed()
	check('nor one whose session runs under a new owner', task_row('on a resumed session')['state'] == 'blocked')
	reset()
	cli(200, 'task', 'start', 'claimed in time')
	kill(200)
	fresh_feed()
	advance(AGE_OUT - 10)
	cli(100, 'task', 'claim', task_row('claimed in time')['id'][:8])
	advance(20)
	fresh_feed()
	check('a claimed task does not expire', task_row('claimed in time')['state'] == 'in_progress' and task_row('claimed in time')['owner'] == owner(100))
	reset()
	cli(200, 'task', 'start', 'claimed late')
	kill(200)
	fresh_feed()
	advance(AGE_OUT + 1)
	fresh_feed()
	check('left too long it expires', task_row('claimed late')['state'] == 'expired')
	code, text = cli(100, 'task', 'claim', task_row('claimed late')['id'][:8])
	row = task_row('claimed late')
	check('and a claim brings it back', code == 0 and row['state'] == 'in_progress' and row['owner'] == owner(100) and row['updated'] == orch.now())
	feed = fresh_feed()
	check('into the feed', task_named(feed, 'claimed late')['ownerAlive'] is True)
	advance(AGE_OUT + 1)
	run_hook(100, 'Notification', message='keep alive', session_id='s100')
	fresh_feed()
	check('and its new live owner keeps it', task_row('claimed late')['state'] == 'in_progress')
	kill(100)
	fresh_feed()
	advance(AGE_OUT + 1)
	fresh_feed()
	check('until that owner goes too', task_row('claimed late')['state'] == 'expired')


def test_expired_tasks_stay_out_of_the_relaunch():
	real_cli, real_popen = orch._find_claude_cli, orch.subprocess.Popen
	launches = []

	class FakePopen(object):
		def __init__(self, *args, **kwargs):
			launches.append(args)

	orch._find_claude_cli = lambda: 'claude.exe'
	orch.subprocess.Popen = FakePopen
	try:
		reset()
		run_hook(200, 'UserPromptSubmit', prompt='y', session_id='s200')
		cli(200, 'task', 'start', 'would be relaunched')
		kill(200)
		fresh_feed()
		db = orch.connect()
		check('orphaned first', task_row('would be relaunched')['state'] == 'orphaned')
		advance(AGE_OUT + 1)
		fresh_feed()
		with orch.Tx(db):
			db.execute("UPDATE tasks SET updated=?", (orch.now(),))
		orch.resurrect(db, orch.SNAPSHOT())
		check('an expired task is not relaunched, however fresh', launches == [] and task_row('would be relaunched')['state'] == 'expired')
		proc(400, 1, 'claude.exe')
		run_hook(400, 'UserPromptSubmit', prompt='continue', session_id='s200')
		check('and a session that comes back later does not adopt it', task_row('would be relaunched')['state'] == 'expired')
		run_hook(400, 'SessionStart', session_id='s200')
		as_agent(400)
		check('nor is it offered in the session brief', 'would be relaunched' not in json.dumps(orch.session_brief(orch.connect(), orch.SNAPSHOT(), owner(400))))
		reset()
		run_hook(200, 'UserPromptSubmit', prompt='y', session_id='s200')
		cli(200, 'task', 'start', 'being relaunched')
		db = orch.connect()
		with orch.Tx(db):
			db.execute("UPDATE tasks SET state='resuming', resumed_at=?", (orch.now(),))
		kill(200)
		orch.expire_old_tasks(db, {}, orch.now() + AGE_OUT + 1)
		check('a task being relaunched does not expire', task_row('being relaunched')['state'] == 'resuming')
	finally:
		orch._find_claude_cli, orch.subprocess.Popen = real_cli, real_popen


def agent_row(name):
	return next((a for a in fresh_feed()['agents'] if a['owner'] == name), None)


def test_a_silent_owner_with_no_process_leaves_the_feed_and_can_come_back():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='s100')
	cli(None, '--as', 'bom-live', 'inbox', 'some-work')
	name = 'agent:bom-live'
	check('it starts in the feed', agent_row(name) is not None)
	advance(orch.AGE_OUT_SECONDS - 10)
	run_hook(100, 'Notification', message='keep alive', session_id='s100')
	row = agent_row(name)
	check('short of the limit it stays, as silent', row is not None and row['state'] == 'silent')
	advance(20)
	run_hook(100, 'Notification', message='keep alive', session_id='s100')
	check('past it it leaves the feed', agent_row(name) is None)
	db = orch.connect()
	check('marked dead', orch.get_owner(db, name)['dead_at'] != 0)
	check('it keeps its channel', name in json.loads(orch.meta_get(db, orch.CHANNEL_MEMBERS_KEY + 'some-work')))
	gone = [line for line in journal_text().splitlines() if ' gone ' in line]
	check('it is journalled', len(gone) == 1 and name in gone[0])
	fresh_feed()
	check('once', len([line for line in journal_text().splitlines() if ' gone ' in line]) == 1)
	code, text = cli(100, 'msg', '--to', 'some-work', 'are you there')
	check('a channel post still reaches it', code == 0 and name in text)
	code, text = cli(100, 'msg', '--to', name, 'direct too')
	check('and a direct message', code == 0 and name in text)
	code, text = cli(None, '--as', 'bom-live', 'inbox')
	check('it reads them when it comes back', 'are you there' in text and 'direct too' in text)
	row = agent_row(name)
	check('and is back in the feed', row is not None and row['working'] is True)
	check('not dead any more', orch.get_owner(orch.connect(), name)['dead_at'] == 0)
	check('that is journalled', len([line for line in journal_text().splitlines() if ' back ' in line]) == 1)
	fresh_feed()
	check('the next pass does not mark it again', agent_row(name) is not None and len([line for line in journal_text().splitlines() if ' gone ' in line]) == 1)
	reset()
	cli(None, '--as', 'gone-with-task', 'task', 'start', 'its task')
	advance(orch.AGE_OUT_SECONDS + 1)
	fresh_feed()
	check('its task expires with it', task_row('its task')['state'] == 'expired')
	reset()
	cli(None, '--owner', 'codex:no-pid', 'heartbeat')
	advance(orch.AGE_OUT_SECONDS + 1)
	check('any owner with no process ages out, not only --as', agent_row('codex:no-pid') is None)


def test_an_owner_with_a_process_is_not_aged_out():
	reset()
	cli(100, 'heartbeat')
	advance(orch.AGE_OUT_SECONDS * 3)
	row = agent_row(owner(100))
	check('a live process keeps its owner however quiet', row is not None and row['state'] == 'silent')
	check('and stays not dead', orch.get_owner(orch.connect(), owner(100))['dead_at'] == 0)
	kill(100)
	fresh_feed()
	dead_at = orch.get_owner(orch.connect(), owner(100))['dead_at']
	proc(100, 1, 'claude.exe', start=999)
	db = orch.connect()
	with orch.Tx(db):
		orch.ensure_owner(db, owner(100), 'claude', 100, 1000)
	check('a dead owner with a process is not revived by its id', orch.get_owner(db, owner(100))['dead_at'] == dead_at)


def test_feed_says_what_a_state_means():
	reset()
	run_hook(100, 'UserPromptSubmit', prompt='x', session_id='s100')
	run_hook(100, 'PreToolUse', tool_name='Read', tool_input={}, session_id='s100')
	check('a state in words', agent_row(owner(100))['stateText'] == 'running a tool')
	post(100, 'Read')
	proc(150, 100, 'bash.exe')
	run_hook(100, 'Stop', session_id='s100')
	row = agent_row(owner(100))
	check('background reads as a sentence', row['state'] == 'background' and row['stateText'] == 'running something in the background')
	check('every state has words', set(orch.AGENT_STATE_WORDS) >= set(orch.WORKING_STATES + ('waiting-user', 'idle', 'silent', 'hung', 'stale-background')))


EDITOR_CMD = 'UnrealEditor.exe C:/x/MyGame.uproject'


def editor_never_answers(test):
	"""The real editor_exec would reach for a real editor, and its wait loop runs on the fake clock, which throws these tests' times far forward."""
	def run():
		real = orch.editor_exec
		orch.editor_exec = lambda code, timeout=2.5: None
		try:
			test()
		finally:
			orch.editor_exec = real
	return run


def stops(calls):
	return calls.count(orch.PIE_STOP)


@editor_never_answers
def test_a_grant_waits_behind_a_pie_the_editor_reports():
	for kind in ('PIE', 'Editor', 'Build', 'Sync'):
		reset()
		proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
		write_report(60, True, since=orch.now() - 5, by_input=True)
		code, out = cli(100, 'acquire', '--kind', kind, '--purpose', 'x', '--wait', '0')
		check('%s waits behind a PIE the editor reports, with no probe at all' % kind, code == 3 and 'PIE is running in editor 60' in out, out)
		write_report(60, False)
		code, out = cli(100, 'acquire', '--wait', '0', '--id', orch.queued_grants(orch.connect())[0]['id'][:8])
		check('and gets it as soon as the PIE ends', code == 0 and 'GRANTED' in out, out)
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(60, True, since=orch.now())
	result = bash(100, BUILD)
	check('a lone build command is refused behind it too', denied(result) and 'PIE is running' in json.dumps(result), repr(result))
	code, out = cli(100, 'acquire', '--kind', 'Test', '--purpose', 'x', '--wait', '0')
	check('a shared Test grant does not touch the editor and is not held up', code == 0, out)
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	proc(61, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(61, True, since=orch.now())
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a PIE in any editor on the project counts', code == 3 and 'editor 61' in out, out)
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(60, False)
	db = orch.connect()
	with orch.Tx(db):
		orch.meta_set(db, 'pie_probe', json.dumps({'at': orch.now(), 'running': True, 'editor_pid': 60}))
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a fresh report of no PIE outranks a probe that says one is up', code == 0, out)


@editor_never_answers
def test_a_stale_report_falls_back_to_the_probe():
	def set_probe(running):
		db = orch.connect()
		with orch.Tx(db):
			orch.meta_set(db, 'pie_probe', json.dumps({'at': orch.now(), 'running': running, 'editor_pid': 60, 'reachable': True}))

	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(60, False, at=orch.now() - orch.EDITOR_REPORT_TTL - 1)
	set_probe(True)
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a stale "no" is no answer: the probe decides', code == 3 and 'PIE is running' in out, out)
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(60, False, at=orch.now() - orch.EDITOR_REPORT_TTL - 1)
	set_probe(False)
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('and with a probe of no PIE the lane is free', code == 0, out)
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(60, True, at=orch.now() - orch.EDITOR_REPORT_TTL - 1, since=orch.now() - 100, by_input=True)
	set_probe(False)
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a stale "up" still counts: a map load blocks the ticker that writes it', code == 3 and 'PIE is running' in out, out)
	write_report(60, True, at=orch.now() - orch.FROZEN_EDITOR_SECONDS - 1, since=orch.now() - 900, by_input=True)
	set_probe(False)
	code, out = cli(100, 'acquire', '--wait', '0', '--id', orch.queued_grants(orch.connect())[0]['id'][:8])
	check('until the editor would count as frozen: a hung editor must not shut the lane for good', code == 0, out)
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	write_report(61, True, since=orch.now())
	code, out = cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	check('a report from a process that is gone counts for nothing', code == 0, out)


@editor_never_answers
def test_a_pie_under_a_grant_is_not_an_occupant_until_it_ends():
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'x', '--wait', '0')
	write_report(60, True, since=orch.now() + 1, by_input=True)
	feed = fresh_feed()
	check('a PIE during a grant is no occupant while the grant holds', feed['occupants'] == [], repr(feed['occupants']))
	check('though the editor row says so', feed['editors'][0]['pie'] is True)
	check('and the holder keeps its commands', not denied(bash(100, BUILD)))
	post(100)
	advance(2)
	write_report(60, True, since=orch.now() - 1, by_input=True)
	cli(100, 'finish', '--evidence', 'built')
	code, out = cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	check('once it ends the next agent waits behind that PIE', code == 3 and 'PIE is running in editor 60' in out, out)


def test_the_report_saves_asking_the_editor():
	reset()
	with fake_editor(stopped_pie) as calls:
		write_report(50, True, since=orch.now(), by_input=True)
		orch.refresh_pie_probe(orch.connect(), orch.SNAPSHOT(), force=True)
		probe = json.loads(orch.meta_get(orch.connect(), 'pie_probe'))
		check('a fresh report answers without running Python in the user\'s PIE', calls == [] and probe['running'] is True and probe['editor_pid'] == 50, repr(calls))
		write_report(50, False, at=orch.now() - orch.EDITOR_REPORT_TTL - 1)
		orch.refresh_pie_probe(orch.connect(), orch.SNAPSHOT(), force=True)
		check('a stale one means asking', calls == [orch.PIE_PROBE], repr(calls))


def finish_with_report(kind='Editor', since_offset=1, by_input=False, keep=False, started_offset=None):
	"""An agent with a grant, a PIE that came up since_offset seconds after the grant, then finish. -> (exit code, output, editor calls)."""
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', kind, '--purpose', 'x', '--wait', '0')
		granted = active()[0]['acquired_at']
		if started_offset is not None:
			db = orch.connect()
			with orch.Tx(db):
				db.execute('UPDATE grants SET pie_started=1, started_at=?', (granted + started_offset,))
		advance(5)
		write_report(50, True, since=granted + since_offset, by_input=by_input)
		del calls[:]
		code, out = cli(100, 'finish', '--evidence', 'x', *(['--keep-pie'] if keep else []))
		return code, out, list(calls)


def test_finish_stops_only_a_pie_its_grant_started():
	code, out, calls = finish_with_report(since_offset=-30, by_input=True)
	check('a PIE up before the grant is left running', stops(calls) == 0 and 'left a PIE running: not started by this grant' in out, out)
	check('it finishes all the same', code == 0 and 'FINISHED' in out)
	check('and says so in the journal', 'pie-left' in journal_text())
	code, out, calls = finish_with_report(since_offset=-30, by_input=False)
	check('with no input either: it was still up first', stops(calls) == 0 and 'left a PIE running' in out, out)
	code, out, calls = finish_with_report(since_offset=1, by_input=True)
	check('a PIE that came up inside the grant with someone at the keyboard is theirs', stops(calls) == 0 and 'left a PIE running' in out, out)
	code, out, calls = finish_with_report(since_offset=1, by_input=False)
	check('a PIE that came up inside the grant with no input is the holder\'s', stops(calls) == 1 and 'stopped its PIE' in out, out)
	code, out, calls = finish_with_report(since_offset=2, by_input=True, started_offset=1)
	check('with someone at the keyboard, the holder\'s own start command still makes it its own', stops(calls) == 1, out)
	code, out, calls = finish_with_report(since_offset=1, by_input=True, started_offset=2)
	check('but not a PIE that was up before that command', stops(calls) == 0 and 'left a PIE running' in out, out)
	code, out, calls = finish_with_report(kind='Build', since_offset=1, by_input=False)
	check('a Build grant still stops nothing', stops(calls) == 0 and 'left' not in out, out)
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'Editor', '--purpose', 'x', '--wait', '0')
		write_report(50, False)
		del calls[:]
		code, out = cli(100, 'finish', '--evidence', 'x')
		check('the editor says no PIE: nothing runs in it and nothing is claimed', calls == [] and 'PIE' not in out, out)


def test_finish_with_nothing_to_go_on():
	reset()
	answer = ['ORCH_PIE=0']
	with fake_editor(lambda code: answer[0]) as calls:
		cli(100, 'acquire', '--kind', 'Editor', '--purpose', 'x', '--wait', '0')
		answer[0] = 'ORCH_PIE=1'
		del calls[:]
		code, out = cli(100, 'finish', '--evidence', 'x')
		check('no report and no start of its own: when unsure it stays and finish says so', orch.PIE_STOP not in calls and 'left a PIE running' in out, out)
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'Editor', '--purpose', 'x', '--wait', '0')
		del calls[:]
		code, out = cli(100, 'finish', '--evidence', 'x')
		check('no report and no PIE up: nothing is claimed', orch.PIE_STOP not in calls and 'PIE' not in out, out)
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'Editor', '--purpose', 'x', '--wait', '0')
		db = orch.connect()
		with orch.Tx(db):
			db.execute('UPDATE grants SET pie_started=1, started_at=acquired_at')
		del calls[:]
		code, out = cli(100, 'finish', '--evidence', 'x')
		check('no report but its own start command: it stops it', stops(calls) == 1 and 'stopped its PIE' in out, out)
	row = {'pie_started': 0, 'started_at': 0, 'acquired_at': 5, 'evidence': '{}'}
	check('a report from a plugin with no start time reads as nothing to go on', orch.pie_verdict(row, {'pie': True, 'at': 1}) == 'unknown')
	check('and says no PIE as well as any', orch.pie_verdict(row, {'pie': False, 'at': 1}) == 'none')


def test_a_reclaim_stops_only_a_pie_its_grant_started():
	gid = hold_lane_with_one_waiting()
	with fake_editor(stopped_pie) as calls:
		granted = active('Build')[0]['acquired_at']
		advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 30)
		write_report(50, True, since=granted - 30, by_input=True)
		del calls[:]
		orch.tick(orch.connect(), force=True)
		check('a reclaim leaves a PIE up before the grant', stops(calls) == 0 and lane_state(gid) == 'done', repr(calls))
		check('and journals it', 'left a PIE running after reclaiming grant ' + gid in journal_text())
	gid = hold_lane_with_one_waiting()
	with fake_editor(stopped_pie) as calls:
		granted = active('Build')[0]['acquired_at']
		advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 30)
		write_report(50, True, since=granted + 5, by_input=True)
		del calls[:]
		orch.tick(orch.connect(), force=True)
		check('and one someone at the keyboard started', stops(calls) == 0 and lane_state(gid) == 'done', repr(calls))
	gid = hold_lane_with_one_waiting()
	with fake_editor(stopped_pie) as calls:
		granted = active('Build')[0]['acquired_at']
		advance(orch.LANE_TURN + orch.LANE_TURN_GRACE + 30)
		write_report(50, True, since=granted + 5, by_input=False)
		del calls[:]
		orch.tick(orch.connect(), force=True)
		check('it stops the one its holder\'s harness started, with no start command on record', stops(calls) == 1 and 'stopped PIE: reclaimed grant ' + gid in journal_text(), repr(calls))
	gid = hold_lane_with_one_waiting()
	with fake_editor(lambda code: 'ORCH_PIE=1') as calls:
		del calls[:]
		turn_over()
		check('with nothing to go on it asks and leaves it', stops(calls) == 0 and orch.PIE_PROBE in calls and lane_state(gid) == 'done', repr(calls))
		check('and tells the owner how to stop it if it is theirs', 'release-pie' in json.dumps(post(100) or {}))
	gid = hold_lane_with_one_waiting()
	with fake_editor(lambda code: None) as calls:
		del calls[:]
		turn_over()
		check('no answer to that: the lane stays shut for the wait, as for a stop', lane_state(gid) == 'releasing')
		advance(orch.RECLAIM_PIE_WAIT)
		orch.tick(orch.connect(), force=True)
		check('then the lane comes back with nothing stopped', lane_state(gid) == 'done' and stops(calls) == 0)


def released_by(pid, since_offset, by_input=False, kind='Build'):
	reset()
	real_exec = orch.editor_exec
	proc(50, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:\\x\\MyGame.uproject')
	calls = []
	orch.editor_exec = lambda code, timeout=2.5: calls.append(code) or ('ORCH_STOPPED' if code == orch.PIE_STOP else 'ORCH_PIE=0')
	try:
		cli(pid, 'acquire', '--kind', kind, '--purpose', 'x', '--wait', '0')
		granted = active()[0]['acquired_at']
		advance(10)
		cli(pid, 'finish', '--evidence', 'x')
		advance(1)
		write_report(50, True, since=granted + since_offset, by_input=by_input)
		del calls[:]
		code, out = cli(pid, 'release-pie')
		return code, out, list(calls)
	finally:
		orch.editor_exec = real_exec


def test_release_pie_gets_the_same_care():
	code, out, calls = released_by(100, 5, by_input=False)
	check('release-pie stops a PIE that came up in the caller\'s grant', code == 0 and 'stopped the PIE' in out and stops(calls) == 1, out)
	code, out, calls = released_by(100, 5, by_input=True)
	check('not one someone at the keyboard started', code == 2 and 'not yours' in out and stops(calls) == 0, out)
	code, out, calls = released_by(100, -30)
	check('not one up before the grant', code == 2 and 'not yours' in out and stops(calls) == 0, out)
	code, out, calls = released_by(100, 10.5)
	check('not one that came up after its grant ended', code == 2 and 'not yours' in out and stops(calls) == 0, out)
	reset()
	real_exec = orch.editor_exec
	proc(50, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:\\x\\MyGame.uproject')
	answer = ['ORCH_PIE=0']
	orch.editor_exec = lambda code, timeout=2.5: answer[0] if code == orch.PIE_PROBE else 'ORCH_STOPPED'
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'x', '--wait', '0')
	cli(100, 'finish', '--evidence', 'x')
	answer[0] = 'ORCH_PIE=1'
	code, out = cli(100, 'release-pie')
	check('nothing to go on: it stays and it says who can stop it', code == 2 and '--master' in out and 'stopped' not in out, out)
	code, out = cli(300, 'release-pie', '--master', '--reason', 'checked: idle, not the user\'s')
	check('--master stays an explicit override', code == 0 and 'stopped the PIE' in out, out)
	reset()
	orch.editor_exec = lambda code, timeout=2.5: 'ORCH_PIE=0' if code == orch.PIE_PROBE else 'ORCH_STOPPED'
	proc(50, 1, 'unrealeditor.exe', cmd='UnrealEditor.exe C:\\x\\MyGame.uproject')
	cli(100, 'acquire', '--kind', 'Build', '--purpose', 'x', '--wait', '0')
	cli(100, 'finish', '--evidence', 'x')
	code, out = cli(100, 'release-pie')
	check('nothing to go on and no PIE up: a plain answer', code == 0 and 'no PIE was running' in out, out)
	orch.editor_exec = real_exec


def test_keep_pie_keeps_only_its_own():
	code, out, calls = finish_with_report(since_offset=-30, by_input=True, keep=True)
	check('--keep-pie on a PIE that is not the grant\'s hands nothing over', 'left a PIE running' in out and 'kept' not in out, out)
	check('no hand-off is recorded', json.loads(orch.meta_get(orch.connect(), 'pie_handoff', '{}') or '{}') == {})
	with fake_editor(stopped_pie) as calls:
		del calls[:]
		run_hook(100, 'UserPromptSubmit', prompt='hi', session_id='s')
		check('so that agent\'s next prompt stops nothing', stops(calls) == 0, repr(calls))
	code, out, calls = finish_with_report(since_offset=1, keep=True)
	check('its own is kept', 'kept for the user' in out and stops(calls) == 0, out)
	with fake_editor(stopped_pie) as calls:
		del calls[:]
		result = run_hook(100, 'UserPromptSubmit', prompt='looks good', session_id='s')
		check('and stops at its next prompt', stops(calls) == 1 and 'Stopped the PIE you left running' in json.dumps(result), repr(result))
	code, out, calls = finish_with_report(since_offset=1, keep=True)
	with fake_editor(stopped_pie) as calls:
		advance(3)
		write_report(50, True, since=orch.now(), by_input=True)
		del calls[:]
		result = run_hook(100, 'UserPromptSubmit', prompt='looks good', session_id='s')
		check('a PIE the user started after the hand-off is not the one handed over', stops(calls) == 0 and 'left a PIE running: it came up after' in journal_text(), repr(result))
	code, out, calls = finish_with_report(since_offset=1, keep=True)
	with fake_editor(stopped_pie) as calls:
		write_report(50, True)
		del calls[:]
		run_hook(100, 'UserPromptSubmit', prompt='looks good', session_id='s')
		check('a PIE with no start time is not assumed to be the one handed over', stops(calls) == 0 and 'nothing says it is the one handed over' in journal_text(), repr(calls))
	code, out, calls = finish_with_report(since_offset=1, keep=True)
	with fake_editor(stopped_pie) as calls:
		write_report(50, False)
		del calls[:]
		run_hook(100, 'UserPromptSubmit', prompt='looks good', session_id='s')
		check('with the PIE already over, nothing runs in the editor', calls == [], repr(calls))


def test_a_transfer_brings_the_pie_with_it():
	reset()
	with fake_editor(stopped_pie) as calls:
		run_hook(200, 'UserPromptSubmit', prompt='x', session_id='s200')
		cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
		came_up = orch.now() + 1
		advance(5)
		code, out = cli(100, 'transfer', '--id', active()[0]['id'][:8], '--to', owner(200))
		check('the transfer happens', code == 0, out)
		advance(5)
		write_report(50, True, since=came_up, by_input=False)
		del calls[:]
		code, out = cli(200, 'finish', '--evidence', 'x')
		check('a PIE from before a transfer comes with the grant', stops(calls) == 1 and 'stopped its PIE' in out, out)


def test_hold_stop_pie_stops_only_the_holders():
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'Build', '--purpose', 'x', '--wait', '0')
		granted = active()[0]['acquired_at']
		advance(5)
		write_report(50, True, since=granted + 1, by_input=True)
		del calls[:]
		cli(None, 'hold', 'on', '--stop-pie', '--source', 'cli')
		check('--stop-pie leaves the user\'s own PIE', stops(calls) == 0, repr(calls))
		cli(None, 'hold', 'off')
	reset()
	with fake_editor(stopped_pie) as calls:
		cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
		granted = active()[0]['acquired_at']
		advance(5)
		write_report(50, True, since=granted + 1, by_input=False)
		del calls[:]
		cli(None, 'hold', 'on', '--stop-pie', '--source', 'cli')
		check('and stops the holder\'s', stops(calls) == 1, repr(calls))
		cli(None, 'hold', 'off')


@editor_never_answers
def test_feed_says_whether_the_holder_owns_the_pie():
	reset()
	proc(60, 1, 'unrealeditor.exe', cmd=EDITOR_CMD)
	cli(100, 'acquire', '--kind', 'PIE', '--purpose', 'x', '--wait', '0')
	granted = active()[0]['acquired_at']
	check('no report, no start: not known to be its own', fresh_feed()['lane']['ownsPie'] is False)
	advance(5)
	write_report(60, True, since=granted + 1, by_input=True)
	check('the user\'s PIE during a grant is not the holder\'s', fresh_feed()['lane']['ownsPie'] is False)
	write_report(60, True, since=granted + 1, by_input=False)
	check('one it started is', fresh_feed()['lane']['ownsPie'] is True)
	cli(200, 'acquire', '--kind', 'PIE', '--purpose', 'y', '--wait', '0')
	check('a queue entry owns nothing', fresh_feed()['queue'][0]['ownsPie'] is False)


def main():
	try:
		test_classification()
		test_read_only_editor_queries()
		test_explicit_grant_and_stop_release()
		test_background_shell_keeps_grant()
		test_dead_owner_and_queue()
		test_head_grace()
		test_reorder()
		test_a_grant_never_reads_as_waiting()
		test_plan_line_on_every_lane_grant()
		test_lane_patch_note()
		test_lane_patch_note_fires_per_grant_and_per_extension()
		test_lane_patch_note_exemptions()
		test_until_granted_blocks_past_the_wait()
		test_a_waiter_that_ended_its_turn_keeps_its_grant()
		test_the_hold_stops_the_turn_clock_after_it_ends()
		test_release_pie()
		test_test_processes_cleanup()
		test_stop_hook()
		test_no_wait_while_holding()
		test_revert_protection_and_blame()
		test_resume_adoption()
		test_resurrect_skips_a_running_session()
		test_human_hold()
		test_hold_survives_holder_stopping()
		test_lane_turn_limits_only_a_hold_someone_waits_behind()
		test_lane_turn_gives_notice_to_a_long_hold()
		test_lane_turn_never_stops_a_running_command_before_the_hard_stop()
		test_lane_turn_reclaims_a_holder_that_ignores_it()
		test_lane_turn_exemptions()
		test_hookless_owner_stops_when_the_registry_says_idle()
		test_a_turn_ended_reclaim_stops_the_pie_before_the_next_grant()
		test_a_reclaim_waits_out_a_running_remote_python()
		test_a_reclaim_stops_the_pie_past_the_wait_whatever_runs()
		test_a_reclaim_retries_an_editor_that_does_not_answer()
		test_a_reclaim_leaves_the_editor_alone_during_the_hold()
		test_a_plain_finish_of_a_build_leaves_the_pie_alone()
		test_a_message_ends_a_foreground_wait_early()
		test_a_first_acquire_wakes_for_a_message_too()
		test_until_granted_does_not_wake_for_a_message()
		test_a_busy_editor_is_not_no_pie()
		test_a_stale_probe_never_shuts_the_lane_for_good()
		test_guard_follows_the_grant()
		test_guard_finds_a_chain_cut_off_from_its_agent()
		test_ue_python_asks_before_it_connects()
		test_ue_python_fails_open_on_everything_but_denied()
		test_ue_python_timeout_is_short_even_when_the_orchestrator_hangs()
		test_ue_python_against_the_real_guard()
		test_build_advice_when_nothing_changed()
		test_build_advice_silent_when_source_changed()
		test_build_advice_reads_the_log_strictly()
		test_build_advice_follows_generated_headers()
		test_build_advice_only_advises()
		test_build_advice_measured_on_a_big_tree()
		test_a_chained_p4_holds_the_mutex_only_while_p4_runs()
		test_a_short_gap_between_p4_commands_keeps_the_mutex()
		test_a_p4_under_another_agent_does_not_keep_the_mutex()
		test_a_background_p4_keeps_the_mutex_while_it_runs()
		test_the_idle_window_is_only_for_implicit_p4()
		test_a_restart_keeps_channels_and_mail()
		test_a_restart_with_a_channel_rejoined_by_hand()
		test_a_live_owner_keeps_its_mail()
		test_an_owner_with_no_session_id_moves_nothing()
		test_a_restart_of_a_session_with_no_hooks()
		test_msg_to_a_bare_agent_name()
		test_channels_still_work_beside_agent_names()
		test_hook_messages_teach_the_waiter_that_goes_by_exit_codes()
		test_a_no_op_reorder_changes_nothing()
		test_a_real_reorder_only_resets_the_moved_row()
		test_reorder_counts_what_queue_position_counts()
		test_reorder_refuses_an_ambiguous_prefix()
		test_a_dead_waiter_does_not_end_a_turn()
		test_a_transfer_starts_a_fresh_turn()
		test_p4_processes_are_tracked_by_verb()
		test_a_release_waits_out_a_running_p4_sync()
		test_a_plain_finish_waits_out_a_running_p4_sync()
		test_a_background_p4_sync_keeps_its_implicit_grant()
		test_a_registry_file_that_is_not_an_object()
		test_a_locked_database_does_not_end_the_waiter()
		test_inbox_survives_what_the_code_page_lacks()
		test_a_hold_taken_in_the_editor_ends_with_that_editor()
		test_feed_keeps_live_tasks_ahead_of_left_over_ones()
		test_a_resumed_session_keeps_its_old_tasks_live()
		test_titles_follow_one_rule()
		test_a_resumed_session_is_not_called_continue()
		test_a_title_ignores_what_a_person_did_not_type()
		test_a_session_with_no_hooks_gets_its_name_from_the_registry()
		test_feed_display_names()
		test_feed_activity_says_what_a_grant_is_doing()
		test_feed_lists_every_editor()
		test_feed_pie_state_is_never_a_guess()
		test_feed_says_who_is_on_each_editor()
		test_a_frozen_editor_shows_in_the_feed()
		test_feed_turn_clock()
		test_feed_summary_decides_the_chip()
		test_old_tasks_with_no_live_owner_expire()
		test_a_live_owner_or_a_claim_keeps_a_task()
		test_expired_tasks_stay_out_of_the_relaunch()
		test_a_silent_owner_with_no_process_leaves_the_feed_and_can_come_back()
		test_an_owner_with_a_process_is_not_aged_out()
		test_feed_says_what_a_state_means()
		test_a_grant_waits_behind_a_pie_the_editor_reports()
		test_a_stale_report_falls_back_to_the_probe()
		test_a_pie_under_a_grant_is_not_an_occupant_until_it_ends()
		test_the_report_saves_asking_the_editor()
		test_finish_stops_only_a_pie_its_grant_started()
		test_finish_with_nothing_to_go_on()
		test_a_reclaim_stops_only_a_pie_its_grant_started()
		test_release_pie_gets_the_same_care()
		test_keep_pie_keeps_only_its_own()
		test_a_transfer_brings_the_pie_with_it()
		test_hold_stop_pie_stops_only_the_holders()
		test_feed_says_whether_the_holder_owns_the_pie()
	finally:
		shutil.rmtree(STATE, ignore_errors=True)
	print('\n%d failure(s)%s' % (len(FAILURES), (': ' + ', '.join(FAILURES)) if FAILURES else ''))
	return 1 if FAILURES else 0


if __name__ == '__main__':
	sys.exit(main())
