"""Records what the editor process is doing after it has closed its log and before it exits, for an editor that lingers on every restart.

    python .claude/skills/master-agent/linger_sample.py [--minutes 60]     run in the background; it only reads

Waits for `Log file closed` at the end of the editor log (Saved/Logs/<Name>.log, <Name> being the project's .uproject), then samples
threads and modules a few times, with stacks from a non-invasive cdb attach when cdb is installed, and writes
Saved/Orchestration/EditorLinger/<time>.txt. Takes no lane.
"""
import argparse
import glob
import json
import os
import subprocess
import sys
import time

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..'))


def editor_log_path():
	"""The project is ORCH_UPROJECT, else the single *.uproject in the project root, as the orchestrator finds it; None when neither says."""
	uproject = os.environ.get('ORCH_UPROJECT')
	if uproject:
		uproject = os.path.join(ROOT, uproject)
	else:
		found = glob.glob(os.path.join(glob.escape(ROOT), '*.uproject'))
		uproject = found[0] if len(found) == 1 else None
	if not uproject:
		return None
	name = os.path.splitext(os.path.basename(uproject))[0]
	return os.path.join(os.path.dirname(os.path.abspath(uproject)), 'Saved', 'Logs', name + '.log')


LOG = editor_log_path()
OUT = os.path.join(ROOT, 'Saved', 'Orchestration', 'EditorLinger')
EXIT_MARKERS = (b'Log file closed', b'RequestExitWithStatus')
TAIL_BYTES = 400
POLL_SECONDS = 1
SAMPLE_AT_SECONDS = (3, 20, 40)
GONE_WAIT_SECONDS = 180
CDB_PATHS = (r'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe', r'C:\Program Files\Windows Kits\10\Debuggers\x64\cdb.exe')
THREADS = r"""
$p = Get-Process -Id {pid} -ErrorAction SilentlyContinue
if (-not $p) {{ 'GONE'; exit }}
$modules = $p.Modules | Sort-Object {{ [int64]$_.BaseAddress }}
$rows = foreach ($t in $p.Threads) {{
	$start = [int64]$t.StartAddress
	$owner = ($modules | Where-Object {{ [int64]$_.BaseAddress -le $start -and $start -lt ([int64]$_.BaseAddress + $_.ModuleMemorySize) }} | Select-Object -First 1).ModuleName
	$why = if ($t.ThreadState -eq 'Wait') {{ $t.WaitReason }} else {{ '' }}
	'{{0,7}} {{1,-10}} {{2,-18}} cpu {{3,8:n2}} {{4}}' -f $t.Id, $t.ThreadState, $why, $t.TotalProcessorTime.TotalSeconds, $owner
}}
'memory ' + [int]($p.WorkingSet64 / 1MB) + ' MB, handles ' + $p.HandleCount + ', threads ' + $p.Threads.Count + ', cpu ' + $p.TotalProcessorTime.TotalSeconds
$rows | Sort-Object
Get-CimInstance Win32_Process -Filter "ParentProcessId={pid}" | ForEach-Object {{ 'child ' + $_.ProcessId + ' ' + $_.Name }}
Get-NetTCPConnection -OwningProcess {pid} -ErrorAction SilentlyContinue | ForEach-Object {{ 'tcp ' + $_.State + ' ' + $_.LocalPort + ' -> ' + $_.RemoteAddress + ':' + $_.RemotePort }}
"""


def powershell(script):
	done = subprocess.run(['powershell', '-NoProfile', '-Command', script], capture_output=True, text=True, encoding='utf-8', errors='replace')
	return done.stdout + done.stderr


def editor_pids():
	return [int(line) for line in powershell("Get-Process UnrealEditor -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }").split() if line.isdigit()]


def log_closed():
	if not LOG:
		return False
	try:
		with open(LOG, 'rb') as f:
			f.seek(0, os.SEEK_END)
			f.seek(max(0, f.tell() - TAIL_BYTES))
			tail = f.read()
			return any(marker in tail for marker in EXIT_MARKERS)
	except OSError:
		return False


def stacks(pid):
	cdb = next((path for path in CDB_PATHS if os.path.exists(path)), None)
	if not cdb:
		return 'cdb is not installed\n'
	done = subprocess.run([cdb, '-pv', '-p', str(pid), '-c', '~*k 80;qd'], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=120)
	return done.stdout


def main():
	parser = argparse.ArgumentParser()
	parser.add_argument('--minutes', type=int, default=60)
	args = parser.parse_args()
	if not LOG:
		print('NO PROJECT: no single *.uproject in %s and no ORCH_UPROJECT' % ROOT)
		return 1
	deadline = time.time() + args.minutes * 60
	was_closed = log_closed()
	while time.time() < deadline:
		is_closed = log_closed()
		if is_closed and not was_closed:
			break
		was_closed = is_closed
		time.sleep(POLL_SECONDS)
	else:
		print('NO RESTART in %d minutes' % args.minutes)
		return 1
	closed_at = time.time()
	pids = editor_pids()
	os.makedirs(OUT, exist_ok=True)
	path = os.path.join(OUT, time.strftime('%H%M%S', time.gmtime(closed_at)) + '.txt')
	with open(path, 'w', encoding='utf-8') as out:
		out.write('Log file closed seen %s UTC, editor pids %s\n' % (time.strftime('%H:%M:%S', time.gmtime(closed_at)), json.dumps(pids)))
		for at in SAMPLE_AT_SECONDS:
			time.sleep(max(0, closed_at + at - time.time()))
			for pid in pids:
				out.write('\n===== +%.0f s, pid %d\n%s\n' % (time.time() - closed_at, pid, powershell(THREADS.format(pid=pid))))
				out.flush()
			if at == SAMPLE_AT_SECONDS[1]:
				for pid in editor_pids():
					try:
						out.write('\n===== stacks +%.0f s, pid %d\n%s\n' % (time.time() - closed_at, pid, stacks(pid)))
					except (OSError, subprocess.SubprocessError) as error:
						out.write('stacks failed: %s\n' % error)
					out.flush()
		while time.time() - closed_at < GONE_WAIT_SECONDS and any(pid in editor_pids() for pid in pids):
			time.sleep(POLL_SECONDS)
		out.write('\nprocess gone at +%.0f s\n' % (time.time() - closed_at))
	print('SAMPLED %s, gone %.0f s after the log closed' % (path, time.time() - closed_at))
	return 0


if __name__ == '__main__':
	sys.exit(main())
