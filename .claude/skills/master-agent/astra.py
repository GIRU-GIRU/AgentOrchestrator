"""Hands a bridge channel to a new Astra: a Codex window in the project whose first prompt is the channel's join line.

    python .claude/skills/master-agent/astra.py <channel>            open it and wait until it has joined
    python .claude/skills/master-agent/astra.py --waiting            list the channels nobody has joined
    python .claude/skills/master-agent/astra.py --again <channel>    a new window for a channel whose Astra has gone (a restart)
    python .claude/skills/master-agent/astra.py --resume <channel> <thread>  a window on that Astra's own thread, with its history, when its window has gone
    python .claude/skills/master-agent/astra.py --type <pid> <text>  wake an idle Astra window with a prompt
    python .claude/skills/master-agent/astra.py --keep <channel>     hold its Astra open: watch.py never closes it
    python .claude/skills/master-agent/astra.py --keep-off <channel> let watch.py close it again once its job looks done

watch.py calls this by itself, so the user never pastes a bridge line or a brief. Codex takes the prompt on its command line; only --type
types, and only into that one window's console.
"""
import ctypes
import glob
import json
import os
import re
import sqlite3
import subprocess
import sys
import time
import winreg
from ctypes import wintypes

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..'))
DB = os.path.join(ROOT, 'Saved', 'Orchestration', 'orch.db')
LAUNCHERS = os.path.join(ROOT, 'Saved', 'Orchestration', 'astra')
WINDOWS_TERMINAL = os.path.expandvars(r'%LOCALAPPDATA%\Microsoft\WindowsApps\wt.exe')
CODEX = os.path.expandvars(r'%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe')
MEMBERS_KEY = 'channel_members:'
BACKLOG_PREFIX = 'channel:'
# `msg --to astra-x` without the `agent:` prefix makes a channel of that name, and handing it over opened a window for nothing.
NOT_A_BRIDGE_PREFIX = 'astra-'
SESSION_PREFIXES = ('CLAUDE', 'CODEX')
JOIN_WAIT_SECONDS = 180
POLL_SECONDS = 3
KEY_EVENT = 1
VK_RETURN = 0x0D
ENTER_DELAY_SECONDS = 0.3
MEMORY = os.path.join(ROOT, 'Saved', 'Orchestration', 'master-watch.json')
CLOSED_LOG = os.path.join(LAUNCHERS, 'closed.log')
CODEX_SESSIONS = os.path.join(os.environ.get('CODEX_HOME') or os.path.expanduser(os.path.join('~', '.codex')), 'sessions')
KEEP_KEY = 'astraKeep'
KEEP_WRITE_TRIES = 3
KEEP_SETTLE_SECONDS = 0.5
OWNER_PREFIX = 'agent:astra-'
# A channel and a thread id end up in a .cmd file, so a title that is anything else leaves its card alone.
CHANNEL = r'[a-z0-9][a-z0-9_.-]*'
ASTRA_TITLE = re.compile('Astra: (%s)' % CHANNEL)
RESUME_COMMAND = re.compile(r'codex resume ([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})')
THREAD_ID = re.compile(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}')
CODEX_KIND = 'Codex'
CODEX_EXE = 'codex.exe'
DAEMON_MARK = 'app-server'
CLOSED_TASK_STATES = ('done', 'cancelled', 'expired')
TASK_COLUMNS = 'id, owner, title, state, blocked_on, why, next_step, handoff'
PARTNER_WORD = 'astra'
MIN_WORK_WORD_LETTERS = 4
TH32CS_SNAPPROCESS = 2
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_COMMAND_LINE_INFORMATION = 60
INVALID_HANDLE = wintypes.HANDLE(-1).value


def members(db):
	return {key[len(MEMBERS_KEY):]: json.loads(value) for key, value in db.execute('SELECT key, value FROM meta WHERE key LIKE ?', (MEMBERS_KEY + '%',))}


def has_backlog(db, channel):
	return db.execute('SELECT COUNT(*) FROM inbox WHERE owner=?', (BACKLOG_PREFIX + channel,)).fetchone()[0] > 0


def connect():
	return sqlite3.connect('file:%s?mode=ro' % DB.replace('\\', '/'), uri=True)


def waiting(db):
	"""Channels with a brief and only its poster on them, oldest member list order: [(channel, poster)]."""
	return [(channel, people[0]) for channel, people in members(db).items() if len(people) == 1 and has_backlog(db, channel) and not channel.startswith(NOT_A_BRIDGE_PREFIX)]


def is_configured(name):
	"""The user's own variable, as opposed to one a parent agent set for its children: a Claude's variables must not reach a Codex."""
	for hive, path in ((winreg.HKEY_CURRENT_USER, 'Environment'), (winreg.HKEY_LOCAL_MACHINE, r'SYSTEM\CurrentControlSet\Control\Session Manager\Environment')):
		try:
			with winreg.OpenKey(hive, path) as key:
				winreg.QueryValueEx(key, name)
				return True
		except OSError:
			continue
	return False


def clean_environment():
	return {name: value for name, value in os.environ.items() if not name.upper().startswith(SESSION_PREFIXES) or is_configured(name)}


def join_prompt(channel):
	"""Every Codex window shares the daemon's orchestrator owner and `inbox` drains by owner, so each Astra takes its own name or they read each other's mail."""
	name = 'astra-' + channel
	return 'Run .claude/scripts/orch.cmd --as %s inbox %s and do what it says. Put --as %s before the command word on every orch.cmd call in this job.' % (name, channel, name)


def start_in_terminal(channel, arguments):
	"""A launcher file, because a prompt with spaces does not survive wt's and cmd's quoting on one command line."""
	os.makedirs(LAUNCHERS, exist_ok=True)
	launcher = os.path.join(LAUNCHERS, channel + '.cmd')
	with open(launcher, 'w', encoding='ascii', newline='\r\n') as f:
		f.write('@echo off\n"%s" %s\n' % (CODEX, arguments))
	subprocess.Popen([WINDOWS_TERMINAL, '-w', 'new', 'new-tab', '--title', 'Astra: ' + channel, '-d', ROOT, 'cmd', '/c', launcher], cwd=ROOT, env=clean_environment())


def open_window(channel):
	start_in_terminal(channel, '"%s"' % join_prompt(channel))


def resume_window(channel, thread):
	start_in_terminal(channel, 'resume %s "%s"' % (thread, join_prompt(channel)))


def can_resume(thread):
	"""Codex names a rollout after its thread, so with no rollout there is nothing to resume."""
	return bool(thread) and bool(glob.glob(os.path.join(CODEX_SESSIONS, '*', '*', '*', 'rollout-*%s.jsonl' % thread)))


def resume_on_thread(channel, thread):
	"""Its own thread rather than a new one: `inbox` hands a message over once, so a new thread would read an empty inbox and never see its brief. A second window on a channel is what this must never open."""
	if channel.startswith(NOT_A_BRIDGE_PREFIX) or not re.fullmatch(CHANNEL, channel) or not THREAD_ID.fullmatch(thread):
		print('NOT A BRIDGE %s %s' % (channel, thread))
		return 2
	windows = windows_of(channel, thread, codex_processes())
	if windows:
		print('ALREADY RUNNING %s: %s' % (channel, windows))
		return 0
	if not can_resume(thread):
		return hand_over(channel, again=True)
	resume_window(channel, thread)
	print('RESUMED Astra for %s on thread %s' % (channel, thread))
	return 0


def channel_of(card):
	"""The channel of an Astra's card, None for every other card: only a Codex window titled this way is ever closed."""
	match = ASTRA_TITLE.fullmatch(card.get('name') or '')
	return match.group(1) if match and card.get('kind') == CODEX_KIND else None


def thread_of(card):
	match = RESUME_COMMAND.fullmatch(card.get('resume') or '')
	return match.group(1) if match else None


def undelivered(db, channel):
	"""What is waiting for this Astra. `delivered=2` only woke a waiter, so it still counts; a backlog post of its own is never handed back to it."""
	owner = OWNER_PREFIX + channel
	rows = db.execute('SELECT text FROM inbox WHERE owner IN (?, ?) AND delivered!=1 ORDER BY id', (owner, BACKLOG_PREFIX + channel)).fetchall()
	return [text for text, in rows if not text.split('\n', 1)[0].endswith('[%s]:' % owner)]


def channel_post_marker(channel):
	return 'Message on %s from ' % channel


def last_post_at(db, channel):
	"""The newest post on the channel, whoever it went to."""
	owner = OWNER_PREFIX + channel
	row = db.execute('SELECT MAX(created) FROM inbox WHERE owner IN (?, ?) OR text LIKE ?', (owner, BACKLOG_PREFIX + channel, channel_post_marker(channel) + '%')).fetchone()
	return row[0] if row else None


def unread_by(db, channel, partner):
	"""Posts on the channel its partner has not read yet."""
	row = db.execute('SELECT COUNT(*) FROM inbox WHERE owner=? AND delivered!=1 AND text LIKE ?', (partner, channel_post_marker(channel) + '%')).fetchone()
	return row[0]


def open_tasks(db):
	marks = ','.join('?' * len(CLOSED_TASK_STATES))
	rows = db.execute('SELECT %s FROM tasks WHERE state NOT IN (%s)' % (TASK_COLUMNS, marks), CLOSED_TASK_STATES).fetchall()
	return [dict(zip(TASK_COLUMNS.split(', '), row)) for row in rows]


def task_text(task):
	return ' '.join(str(task.get(field) or '') for field in ('title', 'blocked_on', 'why', 'next_step', 'handoff'))


def mentions_work(text, channel):
	"""Loose on purpose: naming the work in other words (one word of the channel, or just "Astra") must count, and a false match only leaves a window open."""
	lowered = (text or '').lower()
	words = [word for word in re.split('[^a-z0-9]+', channel) if len(word) >= MIN_WORK_WORD_LETTERS]
	return PARTNER_WORD in lowered or channel in lowered or any(word in lowered for word in words)


def own_task(tasks, channel):
	"""Its open task: under its own name, or under a Codex owner from before it took one (or from before a restart) when the task names the channel."""
	owner = OWNER_PREFIX + channel
	for task in tasks:
		is_named_for_it = task['owner'].startswith('codex:') and channel in task_text(task).lower()
		if task['owner'] == owner or is_named_for_it:
			return task
	return None


class ProcessEntry(ctypes.Structure):
	_fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD), ('th32ProcessID', wintypes.DWORD), ('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', wintypes.DWORD), ('cntThreads', wintypes.DWORD), ('th32ParentProcessID', wintypes.DWORD), ('pcPriClassBase', wintypes.LONG), ('dwFlags', wintypes.DWORD), ('szExeFile', wintypes.WCHAR * wintypes.MAX_PATH)]


class UnicodeString(ctypes.Structure):
	_fields_ = [('Length', wintypes.USHORT), ('MaximumLength', wintypes.USHORT), ('Buffer', wintypes.LPWSTR)]


def command_line(kernel, ntdll, pid):
	handle = kernel.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
	if not handle:
		return None
	try:
		size = wintypes.ULONG(0)
		ntdll.NtQueryInformationProcess(handle, PROCESS_COMMAND_LINE_INFORMATION, None, 0, ctypes.byref(size))
		if not size.value:
			return None
		buffer = ctypes.create_string_buffer(size.value)
		if ntdll.NtQueryInformationProcess(handle, PROCESS_COMMAND_LINE_INFORMATION, buffer, size, ctypes.byref(size)) != 0:
			return None
		text = ctypes.cast(buffer, ctypes.POINTER(UnicodeString)).contents
		return ctypes.wstring_at(text.Buffer, text.Length // 2)
	finally:
		kernel.CloseHandle(handle)


def codex_processes():
	"""[(pid, command line)] of every codex.exe, the daemon's too; None for a command line that could not be read."""
	kernel = ctypes.WinDLL('kernel32', use_last_error=True)
	ntdll = ctypes.WinDLL('ntdll')
	kernel.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
	kernel.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
	kernel.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
	kernel.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(ProcessEntry)]
	kernel.OpenProcess.restype = wintypes.HANDLE
	kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
	kernel.CloseHandle.argtypes = [wintypes.HANDLE]
	ntdll.NtQueryInformationProcess.argtypes = [wintypes.HANDLE, wintypes.ULONG, wintypes.LPVOID, wintypes.ULONG, ctypes.POINTER(wintypes.ULONG)]
	snapshot = kernel.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
	if snapshot == INVALID_HANDLE:
		raise ctypes.WinError(ctypes.get_last_error())
	try:
		found = []
		entry = ProcessEntry()
		entry.dwSize = ctypes.sizeof(ProcessEntry)
		is_more = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
		while is_more:
			if entry.szExeFile.lower() == CODEX_EXE:
				found.append((entry.th32ProcessID, command_line(kernel, ntdll, entry.th32ProcessID)))
			is_more = kernel.Process32NextW(snapshot, ctypes.byref(entry))
		return found
	finally:
		kernel.CloseHandle(snapshot)


def windows_of(channel, thread, processes):
	"""The windows running this Astra, read off their command lines. A card's pid cannot say: Codex records no link from a thread to its window, and
	windows opened in the same minute can all get one window's pid, so typing into it would close another Astra."""
	named = re.compile(r'--as astra-%s(?=[\s"]|$)' % re.escape(channel))
	resuming = re.compile(r'\bresume\s+%s(?=[\s"]|$)' % re.escape(thread)) if thread else None
	return [pid for pid, command in processes if command and DAEMON_MARK not in command and (named.search(command) or (resuming and resuming.search(command)))]


def note(line):
	"""The close log: what the watcher did without waking anyone."""
	try:
		os.makedirs(LAUNCHERS, exist_ok=True)
		with open(CLOSED_LOG, 'a', encoding='utf-8') as f:
			f.write('%s %s\n' % (time.strftime('%Y-%m-%d %H:%M:%S'), line))
	except OSError:
		pass


def read_memory():
	"""{} with no file yet, None when it cannot be read, so a keep never writes over a watcher's memory it failed to read."""
	try:
		with open(MEMORY, encoding='utf-8') as f:
			return json.load(f)
	except FileNotFoundError:
		return {}
	except (OSError, ValueError):
		return None


def kept():
	return (read_memory() or {}).get(KEEP_KEY) or []


def set_kept(channel, is_kept):
	"""The memory file is the truth for this list, and the watcher reads it back before each save, so a keep cannot be lost to a save unless it lands inside
	that read and write. Reading it back here covers that too."""
	for _ in range(KEEP_WRITE_TRIES):
		memory = read_memory()
		if memory is None:
			time.sleep(KEEP_SETTLE_SECONDS)
			continue
		names = set(memory.get(KEEP_KEY) or [])
		if is_kept:
			names.add(channel)
		else:
			names.discard(channel)
		memory[KEEP_KEY] = sorted(names)
		temp = '%s.%d.tmp' % (MEMORY, os.getpid())
		try:
			with open(temp, 'w', encoding='utf-8') as f:
				json.dump(memory, f)
			os.replace(temp, MEMORY)
		except OSError:
			pass
		time.sleep(KEEP_SETTLE_SECONDS)
		if (channel in kept()) == is_kept:
			return True
	return False


def hand_over(channel, wait=True, again=False):
	if channel.startswith(NOT_A_BRIDGE_PREFIX):
		print('NOT A BRIDGE %s: its Astra would be %s%s. Message an Astra as agent:astra-<work>' % (channel, NOT_A_BRIDGE_PREFIX, channel))
		return 2
	db = connect()
	people = members(db).get(channel)
	if people is None or (not again and not has_backlog(db, channel)):
		print('NO SUCH CHANNEL %s: nothing has been posted on it' % channel)
		return 2
	if not again and len(people) > 1:
		print('ALREADY JOINED %s: %s' % (channel, ', '.join(people)))
		return 0
	open_window(channel)
	print('OPENED Astra for %s' % channel, flush=True)
	if not wait or again:
		return 0
	deadline = time.time() + JOIN_WAIT_SECONDS
	while time.time() < deadline:
		time.sleep(POLL_SECONDS)
		now = members(connect()).get(channel, [])
		if len(now) > len(people):
			print('JOINED %s: %s' % (channel, ', '.join(now)))
			return 0
	print('NOT JOINED %s after %d s: look at the Astra window' % (channel, JOIN_WAIT_SECONDS))
	return 1


class KeyEvent(ctypes.Structure):
	_fields_ = [('bKeyDown', wintypes.BOOL), ('wRepeatCount', wintypes.WORD), ('wVirtualKeyCode', wintypes.WORD), ('wVirtualScanCode', wintypes.WORD), ('UnicodeChar', wintypes.WCHAR), ('dwControlKeyState', wintypes.DWORD)]


class InputRecord(ctypes.Structure):
	_fields_ = [('EventType', wintypes.WORD), ('_pad', wintypes.WORD), ('KeyEvent', KeyEvent)]


def key(is_down, character, virtual_key=0):
	return InputRecord(KEY_EVENT, 0, KeyEvent(is_down, 1, virtual_key, 0, character, 0))


def type_into(pid, text):
	"""Wakes an idle Astra: the same route as Agent Manager's Send (ConsoleInput.cs), one process's console and never the desktop."""
	kernel = ctypes.WinDLL('kernel32', use_last_error=True)
	kernel.CreateFileW.restype = wintypes.HANDLE
	kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
	kernel.WriteConsoleInputW.argtypes = [wintypes.HANDLE, ctypes.POINTER(InputRecord), wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
	kernel.CloseHandle.argtypes = [wintypes.HANDLE]
	kernel.FreeConsole()
	if not kernel.AttachConsole(pid):
		print('NOT TYPED: cannot attach to the console of pid %d (error %d)' % (pid, ctypes.get_last_error()))
		return 1
	try:
		console = kernel.CreateFileW('CONIN$', 0xC0000000, 3, None, 3, 0, None)
		written = wintypes.DWORD()
		line = ' '.join(text.split())
		keys = [record for c in line for record in (key(True, c), key(False, c))]
		kernel.WriteConsoleInputW(console, (InputRecord * len(keys))(*keys), len(keys), ctypes.byref(written))
		time.sleep(ENTER_DELAY_SECONDS)
		enter = [key(True, '\r', VK_RETURN), key(False, '\r', VK_RETURN)]
		is_sent = kernel.WriteConsoleInputW(console, (InputRecord * 2)(*enter), 2, ctypes.byref(written))
		kernel.CloseHandle(console)
	finally:
		kernel.FreeConsole()
	print('TYPED into pid %d' % pid if is_sent else 'NOT TYPED: Enter failed')
	return 0 if is_sent else 1


def main():
	if len(sys.argv) >= 3 and sys.argv[1] == '--again':
		return hand_over(sys.argv[2], again=True)
	if len(sys.argv) == 4 and sys.argv[1] == '--resume':
		return resume_on_thread(sys.argv[2], sys.argv[3])
	if len(sys.argv) >= 4 and sys.argv[1] == '--type':
		return type_into(int(sys.argv[2]), ' '.join(sys.argv[3:]))
	if len(sys.argv) == 3 and sys.argv[1] in ('--keep', '--keep-off'):
		channel = sys.argv[2].lower()
		is_kept = sys.argv[1] == '--keep'
		if not re.fullmatch(CHANNEL, channel) or not set_kept(channel, is_kept):
			print('NOT CHANGED: %s' % channel)
			return 1
		print('%s %s; kept now: %s' % ('KEPT' if is_kept else 'NOT KEPT', channel, ', '.join(kept()) or 'none'))
		return 0
	if len(sys.argv) != 2:
		print(__doc__)
		return 2
	if sys.argv[1] == '--waiting':
		for channel, poster in waiting(connect()):
			print(channel, poster)
		return 0
	return hand_over(sys.argv[1])


if __name__ == '__main__':
	sys.exit(main())
