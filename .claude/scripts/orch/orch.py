"""Agent Orchestrator - one coordinator for every Claude and Codex session on this workspace.

    .claude/scripts/orch.cmd status | acquire | finish | cancel | task ... | --help

Ownership is inferred from what the machine is really doing (the owning agent process,
its hook events, its live tool shells, the Unreal processes it launched) instead of
heartbeats the agent has to remember to send. A grant is reclaimed only once its owner
has provably stopped working, so nobody loses a slot mid-task. See orchestration.md.

The project is the single *.uproject in the project root (the folder that holds .claude/),
or the file ORCH_UPROJECT names; its stem names the editor target and the game executables.
"""
import argparse
import ctypes
import glob
import json
import os
import re
import sqlite3
import struct
import subprocess
import sys
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
STATE_DIR = os.environ.get('ORCH_STATE') or os.path.join(PROJECT, 'Saved', 'Orchestration')
DB_PATH = os.path.join(STATE_DIR, 'orch.db')
MIRROR_PATH = os.path.join(STATE_DIR, 'editor-session.json')
STATUS_PATH = os.path.join(STATE_DIR, 'orch-status.json')
EVENTS_PATH = os.path.join(STATE_DIR, 'orch-events.log')
HOOK_FIELDS_PATH = os.path.join(STATE_DIR, 'orch-hook-fields.log')
RESUME_DIR = os.path.join(STATE_DIR, 'resume')
# The test suite sets it for the copies of this file it runs: they then never kill a process, talk to an editor, or start the daemon or a session.
SANDBOX = bool(os.environ.get('ORCH_SANDBOX'))
UBT_LOG_DIR = os.path.join(os.environ.get('LOCALAPPDATA') or '', 'UnrealBuildTool')
CLAUDE_SESSIONS_DIR = os.path.join(os.environ.get('CLAUDE_CONFIG_DIR') or os.path.join(os.path.expanduser('~'), '.claude'), 'sessions')
ORCH_CMD = '.claude/scripts/orch.cmd'
INBOX_POLL_SECONDS = 2
# A channel is named after a piece of work, so several conversations between agents can run at once. A message on it reaches
# every agent that has used it but the sender; with nobody else there yet it waits in the channel's backlog.
CHANNEL_BACKLOG_PREFIX = 'channel:'
CHANNEL_MEMBERS_KEY = 'channel_members:'

STOP_GRACE = 90
RELEASE_PIE_LAST_GRANTS = 3
LANE_TURN = 20 * 60
LANE_TURN_NOTICE = 3 * 60
LANE_TURN_GRACE = 2 * 60
LANE_TURN_HARD_STOP = 10 * 60
PATCH_NOTE_EXTENSIONS = ('.cpp', '.h', '.py', '.ps1', '.sh', '.js')
PATCH_NOTE_NAMES_SHOWN = 3
INTURN_SILENCE = 30 * 60
CLI_SILENCE = 20 * 60
HUNG_TOOL = 45 * 60
USER_WAIT = 5 * 60
ORPHANED_HOLD_SECONDS = 3 * 60
HOLD_EDITOR_SEEN_SECONDS = 30
FROZEN_EDITOR_SECONDS = 5 * 60
EDITOR_REPORT_TTL = 30
EDITOR_HUNG_KEY = 'editor_hung'
FEED_TASKS = 20
FEED_LEFTOVER_TASKS = 20
TASK_AGE_OUT_DAYS = 14
AGE_OUT_SECONDS = TASK_AGE_OUT_DAYS * 86400
TITLE_MAX = 160
TITLE_PROMPT_MAX = 80
MASTER_AGENT_NAME = 'MASTER AGENT'
MASTER_AGENT_SESSION_NAMES = ('MASTER_AGENT', 'Master Agent')
NAMED_OWNER_PREFIX = 'agent:'
ASTRA_OWNER_PREFIX = NAMED_OWNER_PREFIX + 'astra-'
ASTRA_NAME_PREFIX = 'Astra: '
WRAPPED_PROMPT_PREFIXES = ('<', 'Another Claude session sent a message', '[Request interrupted', 'Caveat:', 'This session is being continued')
BACKGROUND_MAX = 60 * 60
HEAD_GRACE = 120
IMPLICIT_WAIT = 90
P4_WAIT = 120
P4_IDLE = 15
P4_EXE = 'p4.exe'
WAITED_OUT_KINDS = ('ubt', 'commandlet', 'p4-sync')
WAITER_DB_RETRY = 3
RECONCILE_INTERVAL = 5
PIE_PROBE_TTL = 20
PIE_PROBE_UNREACHABLE_TTL = 60
RECLAIM_PIE_WAIT = 60
HUMAN_IDLE_FOR_ORPHAN_PIE = 20 * 60
UNREGISTERED_BUILD_MAX = 60 * 60
RESUME_MAX_ATTEMPTS = 2
RESUME_CLAIM_WINDOW = 10 * 60
RESUME_TASK_MAX_AGE = 2 * 60 * 60
DAEMON_PERIOD = 10
FILETIME_TO_DOTNET_TICKS = 504911232000000000
DOTNET_TICKS_AT_UNIX_EPOCH = 621355968000000000
PROBE_COMMAND_PORT = 6786
DETACHED_FLAGS = 0x00000008 | 0x00000200 | 0x08000000
BREAKAWAY_FROM_JOB = 0x01000000

KIND_RESOURCES = {
	'PIE': {'lane': 'X'},
	'Build': {'lane': 'X'},
	'Editor': {'lane': 'X'},
	'Sync': {'lane': 'X'},
	'Test': {'lane': 'S'},
	'Deploy': {'deploy': 'X'},
	'P4': {'p4': 'X'},
}
LONG_KINDS = ('PIE', 'Build', 'Editor', 'Sync', 'Test', 'Deploy')
WORKING_STATES = ('busy', 'working', 'background')
SHOWN_WORKING_STATES = WORKING_STATES + ('waiting-user',)
ACTIVITY_COMMAND = {'Build': 'building', 'Editor': 'using the editor', 'Sync': 'syncing from p4', 'Test': 'testing', 'Deploy': 'deploying', 'P4': 'using p4'}
ACTIVITY_HOLDING = {'PIE': 'has the editor for PIE', 'Build': 'has the editor for a build', 'Editor': 'using the editor', 'Sync': 'has the editor for a p4 sync',
	'Test': 'has the editor for tests', 'Deploy': 'deploying', 'P4': 'using p4'}
AGENT_STATE_WORDS = {'busy': 'running a tool', 'working': 'working', 'background': 'running something in the background', 'waiting-user': 'waiting for its user',
	'idle': 'idle', 'silent': 'not heard from', 'hung': 'a tool looks stuck', 'stale-background': 'an old background job is still running'}
DELIBERATE_END_REASONS = ('clear', 'logout', 'prompt_input_exit')
USER_WAIT_TOOLS = ('AskUserQuestion', 'ExitPlanMode')
TOOL_SHELLS = ('bash.exe', 'sh.exe', 'cmd.exe', 'powershell.exe', 'pwsh.exe')
NEVER = re.compile(r'(?!)')
DEFAULT_DEPLOY_PATTERN = r'\bsteamcmd(\.exe)?\b.*\+run_app_build|\bbutler(\.exe)?\s+push\b|(^|[\\/\s"\'])deploy\.(cmd|bat|ps1|sh)\b'

now = time.time


# ---------------------------------------------------------------- the project

def find_uproject(root):
	"""ORCH_UPROJECT (absolute, or relative to the project root), else the single *.uproject in the root; '' when there is none or more than one."""
	override = os.environ.get('ORCH_UPROJECT')
	if override:
		return os.path.abspath(os.path.join(root, override))
	found = glob.glob(os.path.join(glob.escape(root), '*.uproject'))
	return os.path.abspath(found[0]) if len(found) == 1 else ''


def use_project(uproject):
	"""Everything that names the game comes from the project file: its stem is the name, `<Name>Editor` the editor target, `<Name>`,
	`<Name>Client` and `<Name>Server` the game executables. With no project file no editor or game process is this project's."""
	global UPROJECT, PROJECT_NAME, EDITOR_TARGET, UHT_TIMESTAMPS, FULL_BUILD_WORDS, RX_UPROJECT, RX_GAME_PROCESS, RX_GAME_EXE, RX_UE_WORD
	UPROJECT = uproject
	PROJECT_NAME = os.path.splitext(os.path.basename(uproject))[0] if uproject else ''
	EDITOR_TARGET = PROJECT_NAME + 'Editor' if PROJECT_NAME else ''
	UHT_TIMESTAMPS = ('Intermediate', 'Build', 'Win64', EDITOR_TARGET, EDITOR_TARGET + '.uhttimestamps')
	FULL_BUILD_WORDS = {EDITOR_TARGET.lower(), 'win64', 'development'}
	name = re.escape(PROJECT_NAME)
	if PROJECT_NAME:
		RX_UPROJECT = re.compile(r'(^|[\\/"\'\s])%s\.uproject(["\'\s]|$)' % name, re.I)
		RX_GAME_PROCESS = re.compile(r'%s(client|server)?(-win64-\w+)?\.exe$' % name, re.I)
		RX_GAME_EXE = re.compile(r'\b%s(Client|Server)?(-Win64-\w+)?\.exe' % name, re.I)
		RX_UE_WORD = re.compile(r'Unreal|LiveCodingConsole|' + name, re.I)
	else:
		RX_UPROJECT = RX_GAME_PROCESS = RX_GAME_EXE = NEVER
		RX_UE_WORD = re.compile(r'Unreal|LiveCodingConsole', re.I)


use_project(find_uproject(PROJECT))


def named_like_game(name):
	return bool(PROJECT_NAME) and name.startswith(PROJECT_NAME.lower())


def deploy_pattern():
	"""ORCH_DEPLOY_PATTERN, a regular expression, replaces the default; one that does not compile is ignored."""
	custom = os.environ.get('ORCH_DEPLOY_PATTERN')
	if custom:
		try:
			return re.compile(custom, re.I)
		except re.error:
			pass
	return re.compile(DEFAULT_DEPLOY_PATTERN, re.I)


def registry_engine_roots(association):
	"""A launcher install is HKLM SOFTWARE\\EpicGames\\Unreal Engine\\<version>; a registered source build is a value of HKCU Software\\Epic Games\\Unreal Engine\\Builds."""
	try:
		import winreg
	except ImportError:
		return []
	found = []
	for hive, key, value in ((winreg.HKEY_CURRENT_USER, 'Software\\Epic Games\\Unreal Engine\\Builds', association),
			(winreg.HKEY_LOCAL_MACHINE, 'SOFTWARE\\EpicGames\\Unreal Engine\\' + association, 'InstalledDirectory')):
		try:
			with winreg.OpenKey(hive, key) as handle:
				found.append(winreg.QueryValueEx(handle, value)[0])
		except OSError:
			pass
	return found


def engine_roots():
	"""Engine folders to look in, best first: UE_ROOT, then the engine the project's EngineAssociation names, then the launcher's default folder for it."""
	roots = [os.environ.get('UE_ROOT') or '']
	association = ''
	try:
		with open(UPROJECT, encoding='utf-8-sig') as f:
			association = json.load(f).get('EngineAssociation') or ''
	except (OSError, ValueError, AttributeError):
		pass
	if association and isinstance(association, str):
		roots += registry_engine_roots(association)
		if os.environ.get('ProgramFiles'):
			roots.append(os.path.join(os.environ['ProgramFiles'], 'Epic Games', 'UE_' + association))
	return [r for r in roots if r]


def iso(t):
	if not t:
		return ''
	return time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime(t))


def ago(t):
	if not t:
		return 'never'
	s = int(now() - t)
	if s < 90:
		return '%ds ago' % s
	if s < 5400:
		return '%dm ago' % (s // 60)
	return '%.1fh ago' % (s / 3600.0)


# ---------------------------------------------------------------- processes

class Proc(object):
	__slots__ = ('pid', 'ppid', 'name', 'start', 'cmd')

	def __init__(self, pid, ppid, name, start=None, cmd=None):
		self.pid = pid
		self.ppid = ppid
		self.name = name.lower()
		self.start = start
		self.cmd = cmd


if os.name == 'nt':
	from ctypes import wintypes as wt

	_k32 = ctypes.WinDLL('kernel32', use_last_error=True)
	_ntdll = ctypes.WinDLL('ntdll')
	_user32 = ctypes.WinDLL('user32')

	class _PROCESSENTRY32W(ctypes.Structure):
		_fields_ = [('dwSize', wt.DWORD), ('cntUsage', wt.DWORD), ('th32ProcessID', wt.DWORD),
			('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', wt.DWORD), ('cntThreads', wt.DWORD),
			('th32ParentProcessID', wt.DWORD), ('pcPriClassBase', ctypes.c_long), ('dwFlags', wt.DWORD),
			('szExeFile', ctypes.c_wchar * 260)]

	class _LASTINPUTINFO(ctypes.Structure):
		_fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]

	_k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
	_k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]
	_k32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
	_k32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
	_k32.OpenProcess.restype = wt.HANDLE
	_k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
	_k32.CloseHandle.argtypes = [wt.HANDLE]
	_k32.GetProcessTimes.argtypes = [wt.HANDLE] + [ctypes.POINTER(ctypes.c_ulonglong)] * 4
	_k32.CreateMutexW.restype = wt.HANDLE
	_k32.CreateMutexW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.LPCWSTR]
	_ntdll.NtQueryInformationProcess.restype = ctypes.c_long
	_ntdll.NtQueryInformationProcess.argtypes = [wt.HANDLE, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong)]
	_k32.GetTickCount.restype = wt.DWORD
	_user32.IsWindowVisible.argtypes = [wt.HWND]
	_user32.IsHungAppWindow.argtypes = [wt.HWND]
	_user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]


def _win_list():
	snap = _k32.CreateToolhelp32Snapshot(2, 0)
	out = []
	entry = _PROCESSENTRY32W()
	entry.dwSize = ctypes.sizeof(_PROCESSENTRY32W)
	try:
		ok = _k32.Process32FirstW(snap, ctypes.byref(entry))
		while ok:
			out.append(Proc(int(entry.th32ProcessID), int(entry.th32ParentProcessID), entry.szExeFile))
			ok = _k32.Process32NextW(snap, ctypes.byref(entry))
	finally:
		_k32.CloseHandle(snap)
	return out


def _win_start(pid):
	h = _k32.OpenProcess(0x1000, False, pid)
	if not h:
		return 0
	try:
		c, e, k, u = (ctypes.c_ulonglong(), ctypes.c_ulonglong(), ctypes.c_ulonglong(), ctypes.c_ulonglong())
		if not _k32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u)):
			return 0
		# .NET ticks truncated to microseconds, the precision the old PowerShell owner ids carry.
		ticks = c.value + FILETIME_TO_DOTNET_TICKS
		return ticks - ticks % 10
	finally:
		_k32.CloseHandle(h)


def _win_cmd(pid):
	h = _k32.OpenProcess(0x1000, False, pid)
	if not h:
		return ''
	try:
		size = ctypes.c_ulong(0)
		buf = ctypes.create_string_buffer(8192)
		status = _ntdll.NtQueryInformationProcess(h, 60, buf, 8192, ctypes.byref(size))
		if status != 0 and size.value > 8192:
			buf = ctypes.create_string_buffer(size.value)
			status = _ntdll.NtQueryInformationProcess(h, 60, buf, size.value, ctypes.byref(size))
		if status != 0:
			return ''
		length = struct.unpack_from('<H', buf.raw, 0)[0]
		pointer = struct.unpack_from('<Q', buf.raw, 8)[0]
		if not pointer or not length:
			return ''
		return ctypes.wstring_at(pointer, length // 2)
	except Exception:
		return ''
	finally:
		_k32.CloseHandle(h)


class Snapshot(object):
	def __init__(self, procs):
		self.procs = {p.pid: p for p in procs}
		self.children = {}
		for p in procs:
			self.children.setdefault(p.ppid, []).append(p)

	def start(self, pid):
		p = self.procs.get(pid)
		if not p:
			return 0
		if p.start is None:
			p.start = _win_start(pid) if os.name == 'nt' else 0
		return p.start

	def cmd(self, pid):
		p = self.procs.get(pid)
		if not p:
			return ''
		if p.cmd is None:
			p.cmd = _win_cmd(pid) if os.name == 'nt' else ''
		return p.cmd

	def alive(self, pid, ticks):
		if pid not in self.procs:
			return False
		return not ticks or self.start(pid) == ticks

	def ancestors(self, pid):
		chain = []
		seen = set()
		cur = self.procs.get(pid)
		while cur and cur.pid not in seen and len(chain) < 32:
			seen.add(cur.pid)
			chain.append(cur)
			parent = self.procs.get(cur.ppid)
			# A reused parent PID younger than its child is not the real parent.
			if parent and self.start(parent.pid) and self.start(cur.pid) and self.start(parent.pid) > self.start(cur.pid):
				break
			cur = parent
		return chain

	def tool_shells(self, pid):
		return [c for c in self.children.get(pid, []) if c.name in TOOL_SHELLS]


def take_snapshot():
	return Snapshot(_win_list())


SNAPSHOT = take_snapshot


def human_idle_seconds():
	if os.name != 'nt':
		return 0
	info = _LASTINPUTINFO()
	info.cbSize = ctypes.sizeof(_LASTINPUTINFO)
	if not _user32.GetLastInputInfo(ctypes.byref(info)):
		return 0
	return ((_k32.GetTickCount() - info.dwTime) & 0xFFFFFFFF) / 1000.0


def hung_window_pids():
	"""Processes with a visible window Windows reports as not answering. A moving log proves nothing: a frozen game thread's background threads keep logging."""
	if os.name != 'nt':
		return set()
	found = set()

	def look_at(window, _):
		if _user32.IsWindowVisible(window) and _user32.IsHungAppWindow(window):
			pid = wt.DWORD()
			_user32.GetWindowThreadProcessId(window, ctypes.byref(pid))
			found.add(int(pid.value))
		return True

	_user32.EnumWindows(ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)(look_at), 0)
	return found


def kill_tree(pid):
	if SANDBOX:
		return
	subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], capture_output=True)


def ue_process_kind(name, cmd):
	lower = (cmd or '').lower()
	if name in ('unrealeditor.exe', 'unrealeditor-cmd.exe'):
		if re.search(r'(^|\s)-(game|server)(\s|$)', lower):
			return 'game'
		if name == 'unrealeditor-cmd.exe' or '-run=' in lower:
			return 'commandlet'
		return 'editor'
	if RX_GAME_PROCESS.match(name):
		return 'game'
	if name in ('dotnet.exe', 'unrealbuildtool.exe') and 'unrealbuildtool' in lower:
		return 'ubt-single' if '-singlefile' in lower else 'ubt'
	if name == 'livecodingconsole.exe':
		return 'livecoding'
	if name == P4_EXE:
		verb, args = _p4_verb(cmd or '')
		return 'p4-sync' if verb in P4_CONTENT and not p4_is_harmless(verb, args) else None
	return None


UE_NAMES = ('unrealeditor.exe', 'unrealeditor-cmd.exe', 'dotnet.exe', 'unrealbuildtool.exe', 'livecodingconsole.exe')


def ue_processes(snap):
	out = []
	for p in list(snap.procs.values()):
		if p.name in UE_NAMES or p.name == P4_EXE or named_like_game(p.name):
			kind = ue_process_kind(p.name, snap.cmd(p.pid))
			if kind:
				out.append((p, kind))
	return out


# ---------------------------------------------------------------- owner identity

def find_agent_process(snap, pid=None):
	for p in snap.ancestors(pid or os.getpid()):
		if p.name in ('claude.exe', 'claude'):
			return 'claude', p
		if p.name.startswith('codex'):
			return 'codex', p
		if p.name == 'node.exe' and 'codex' in snap.cmd(p.pid).lower():
			return 'codex', p
	return None, None


def detached_agent(snap):
	"""The Claude that started a background chain, once the tool shell between them has exited and cut the process tree: the variable it set
	in that shell is all that is left. A human's terminal has none."""
	value = os.environ.get('CLAUDE_PID') or ''
	p = snap.procs.get(int(value)) if value.isdigit() else None
	return p if p and p.name in ('claude.exe', 'claude') else None


def owner_key(kind, snap, proc):
	return '%s:%d:%d' % (kind, proc.pid, snap.start(proc.pid))


def parse_owner_pid(owner):
	m = re.match(r'^(claude|codex):(\d+):(\d+)$', owner or '')
	if not m:
		return None, 0, 0
	return m.group(1), int(m.group(2)), int(m.group(3))


# ---------------------------------------------------------------- state

SCHEMA = """
CREATE TABLE IF NOT EXISTS owners (
	owner TEXT PRIMARY KEY, kind TEXT, pid INTEGER DEFAULT 0, ticks INTEGER DEFAULT 0, session_id TEXT DEFAULT '',
	transcript TEXT DEFAULT '', in_turn INTEGER DEFAULT 1, last_active REAL DEFAULT 0, stopped_at REAL DEFAULT 0,
	open_tools INTEGER DEFAULT 0, open_since REAL DEFAULT 0, open_desc TEXT DEFAULT '', user_wait_since REAL DEFAULT 0,
	ended INTEGER DEFAULT 0, ended_reason TEXT DEFAULT '', last_shell_seen REAL DEFAULT 0,
	hooked INTEGER DEFAULT 0, dead_at REAL DEFAULT 0, created REAL DEFAULT 0);
CREATE TABLE IF NOT EXISTS grants (
	seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE, owner TEXT, kind TEXT, resources TEXT, purpose TEXT DEFAULT '',
	requirements TEXT DEFAULT '', minutes INTEGER DEFAULT 15, state TEXT, phase TEXT, implicit INTEGER DEFAULT 0,
	tool TEXT DEFAULT '', process_bound INTEGER DEFAULT 0, keep_processes INTEGER DEFAULT 0, queued_at REAL DEFAULT 0,
	acquired_at REAL DEFAULT 0, started_at REAL DEFAULT 0, updated_at REAL DEFAULT 0, released_at REAL DEFAULT 0,
	release_reason TEXT DEFAULT '', evidence TEXT DEFAULT '{}', claimable_since REAL DEFAULT 0, cleanup_by INTEGER DEFAULT 0,
	cleanup_at REAL DEFAULT 0, pie_started INTEGER DEFAULT 0, last_command TEXT DEFAULT '', editor_pid INTEGER DEFAULT 0,
	map TEXT DEFAULT '');
CREATE TABLE IF NOT EXISTS procs (
	pid INTEGER, start INTEGER, name TEXT, kind TEXT, cmd TEXT, owner TEXT DEFAULT '', grant_id TEXT DEFAULT '',
	first_seen REAL, gone_at REAL DEFAULT 0, handed_to_user INTEGER DEFAULT 0, PRIMARY KEY (pid, start));
CREATE TABLE IF NOT EXISTS inbox (id INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT, created REAL, text TEXT, delivered INTEGER DEFAULT 0);
CREATE TABLE IF NOT EXISTS tasks (
	id TEXT PRIMARY KEY, owner TEXT, session_id TEXT DEFAULT '', title TEXT, state TEXT, blocked_on TEXT DEFAULT '',
	why TEXT DEFAULT '', next_step TEXT DEFAULT '', handoff TEXT DEFAULT '', files TEXT DEFAULT '', evidence TEXT DEFAULT '',
	created REAL, updated REAL, resume_count INTEGER DEFAULT 0, resumed_at REAL DEFAULT 0, orphaned_at REAL DEFAULT 0);
CREATE TABLE IF NOT EXISTS files (path TEXT PRIMARY KEY, owner TEXT, at REAL);
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
"""
ADDED_COLUMNS = (
	('owners', 'title', "TEXT DEFAULT ''"),
	('owners', 'cwd', "TEXT DEFAULT ''"),
	('owners', 'title_fixed', 'INTEGER DEFAULT 0'),
)


def connect():
	os.makedirs(STATE_DIR, exist_ok=True)
	db = sqlite3.connect(DB_PATH, timeout=20, isolation_level=None)
	db.row_factory = sqlite3.Row
	db.execute('PRAGMA busy_timeout=20000')
	db.execute('PRAGMA journal_mode=WAL')
	db.executescript(SCHEMA)
	for table, column, decl in ADDED_COLUMNS:
		if column not in [r['name'] for r in db.execute('PRAGMA table_info(%s)' % table)]:
			try:
				db.execute('ALTER TABLE %s ADD COLUMN %s %s' % (table, column, decl))
			except sqlite3.OperationalError:
				pass
	return db


class Tx(object):
	def __init__(self, db):
		self.db = db

	def __enter__(self):
		self.db.execute('BEGIN IMMEDIATE')
		return self.db

	def __exit__(self, exc_type, exc, tb):
		self.db.execute('ROLLBACK' if exc_type else 'COMMIT')
		return False


def meta_get(db, key, default=None):
	row = db.execute('SELECT value FROM meta WHERE key=?', (key,)).fetchone()
	return row['value'] if row else default


def meta_set(db, key, value):
	db.execute('INSERT INTO meta(key, value) VALUES(?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value', (key, value))


def journal(owner, kind, text):
	try:
		with open(EVENTS_PATH, 'a', encoding='utf-8') as f:
			f.write('%s %-10s %-34s %s\n' % (iso(now()), kind, owner or '-', text.replace('\n', ' ')))
		if os.path.getsize(EVENTS_PATH) > 4 * 1024 * 1024:
			os.replace(EVENTS_PATH, EVENTS_PATH + '.1')
	except OSError:
		pass


def notify(db, owner, text):
	db.execute('INSERT INTO inbox(owner, created, text) VALUES(?, ?, ?)', (owner, now(), text))


def get_owner(db, owner):
	return db.execute('SELECT * FROM owners WHERE owner=?', (owner,)).fetchone()


def ensure_owner(db, owner, kind, pid=0, ticks=0):
	row = get_owner(db, owner)
	if not row:
		db.execute('INSERT INTO owners(owner, kind, pid, ticks, last_active, created) VALUES(?, ?, ?, ?, ?, ?)',
			(owner, kind, pid, ticks, now(), now()))
		row = get_owner(db, owner)
	elif row['dead_at'] and not row['pid']:
		db.execute('UPDATE owners SET dead_at=0, last_active=? WHERE owner=?', (now(), owner))
		journal(owner, 'back', 'an owner with no process is calling again')
		row = get_owner(db, owner)
	return row


def touch_owner(db, owner, **fields):
	fields.setdefault('last_active', now())
	cols = ', '.join('%s=?' % k for k in fields)
	db.execute('UPDATE owners SET %s WHERE owner=?' % cols, list(fields.values()) + [owner])


def _parse_iso(value):
	if not value:
		return 0
	m = re.match(r'(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(\.\d+)?', value)
	if not m:
		return 0
	import calendar
	base = calendar.timegm(tuple(int(m.group(i)) for i in range(1, 7)))
	return base + (float('0' + m.group(7)) if m.group(7) else 0)


def _ticks_to_epoch(ticks):
	if not ticks:
		return now()
	return (ticks - DOTNET_TICKS_AT_UNIX_EPOCH) / 1e7


# ---------------------------------------------------------------- liveness

def owner_state(o, snap, t=None):
	"""busy | working | background keep everything; any other state means the owner has stopped working."""
	t = t or now()
	if o is None:
		return 'unknown'
	if o['pid'] and not snap.alive(o['pid'], o['ticks']):
		return 'dead'
	if o['ended']:
		return 'ended'
	if o['user_wait_since'] and t - o['user_wait_since'] > USER_WAIT:
		return 'waiting-user'
	shells = snap.tool_shells(o['pid']) if o['pid'] else []
	if o['open_tools'] > 0 and o['open_since'] and t - o['open_since'] < HUNG_TOOL and o['kind'] == 'claude':
		return 'busy'
	if shells:
		if o['in_turn']:
			return 'busy'
		if t - max(o['stopped_at'], o['last_active']) < BACKGROUND_MAX:
			return 'background'
		return 'stale-background'
	if o['kind'] == 'claude' and o['hooked'] and not o['in_turn']:
		return 'working' if t - max(o['stopped_at'], o['last_active'], o['last_shell_seen']) < STOP_GRACE else 'idle'
	if o['kind'] == 'claude' and not o['hooked'] and o['pid']:
		idle_since = registry_idle_since(o['pid'])
		if idle_since:
			return 'working' if t - max(idle_since, o['last_active'], o['last_shell_seen']) < STOP_GRACE else 'idle'
	if o['open_tools'] > 0 and o['open_since'] and t - o['open_since'] >= HUNG_TOOL:
		return 'hung'
	limit = INTURN_SILENCE if o['kind'] == 'claude' else CLI_SILENCE
	last = max(o['last_active'], o['last_shell_seen'])
	return 'working' if t - last < limit else 'silent'


def registry_idle_since(pid):
	"""When a Claude with no hooks went idle, from Claude Code's own registry; 0 while it works or when the entry says nothing.
	A session started outside the project runs none of its hooks, so this is the only sign that its turn ended."""
	try:
		with open(os.path.join(CLAUDE_SESSIONS_DIR, '%d.json' % pid), encoding='utf-8') as f:
			entry = json.load(f)
	except (OSError, ValueError):
		return 0
	if not isinstance(entry, dict) or entry.get('pid') != pid or entry.get('status') != 'idle':
		return 0
	try:
		return float(entry.get('statusUpdatedAt') or 0) / 1000
	except (TypeError, ValueError):
		return 0


def queue_entry_alive(state):
	return state in WORKING_STATES or state == 'stale-background'


# ---------------------------------------------------------------- grants

def grant_resources(g):
	return json.loads(g['resources'] or '{}')


def covers(held, need):
	return all(held.get(r) == 'X' or held.get(r) == m for r, m in need.items())


def conflicts(a, b):
	for res, mode in a.items():
		other = b.get(res)
		if other and (mode == 'X' or other == 'X'):
			return True
	return False


def active_grants(db):
	return db.execute("SELECT * FROM grants WHERE state IN ('active','releasing') ORDER BY seq").fetchall()


def queued_grants(db):
	return db.execute("SELECT * FROM grants WHERE state='queued' ORDER BY seq").fetchall()


def lane_holder(db):
	for g in active_grants(db):
		if grant_resources(g).get('lane') == 'X':
			return g
	return None


# ---------------------------------------------------------------- human hold

def hold_get(db):
	return json.loads(meta_get(db, 'human_hold', '') or '{}')


def hold_active(db):
	return bool(hold_get(db).get('on'))


def hold_set(db, h):
	meta_set(db, 'human_hold', json.dumps(h))


def hold_reason(h):
	return 'the user (%s) has taken the editor%s - held for %s' % (h.get('by') or 'user',
		(': "%s"' % h['reason'][:120]) if h.get('reason') else '', ago(h.get('since')).replace(' ago', ''))


def hold_note_paused(db, h, owner):
	"""Everyone the hold turned away hears about the release."""
	if owner and owner not in (h.get('paused') or []):
		h['paused'] = (h.get('paused') or []) + [owner]
		hold_set(db, h)


def hold_pause_text(db, h, owner):
	held = [g for g in owner_long_grants(db, owner) if g['state'] == 'active' and grant_resources(g).get('lane')]
	keep = (' Your %s grant %s stays yours while you wait; do not finish it unless you are giving the work up.'
		% (held[0]['kind'], held[0]['id'][:8])) if held else ''
	return ('ORCHESTRATOR: PAUSED - %s.\nYour command was NOT run. While the user holds the editor no agent may use it, '
		'build, start PIE or test processes, or p4 sync.%s Wait with `%s hold wait` (it blocks up to 9 minutes and prints RELEASED '
		'when the user lets go; repeat it until then) and do independent work meanwhile: reading, searching, editing and '
		'-singlefile compiles still work. Only the user lifts the hold; never work around it.' % (hold_reason(h), keep, ORCH_CMD))


def caller_is_agent(snap):
	"""The editor's own button runs orch under UnrealEditor, which an agent may have launched; the editor wins."""
	for p in snap.ancestors(os.getpid()):
		if p.name in ('unrealeditor.exe', 'unrealeditor-cmd.exe'):
			return False
	kind, proc = find_agent_process(snap)
	return proc is not None


def unclaimed_pie_editor(db, snap, t):
	"""The editor a PIE no grant accounts for is up in, None if there is none. The editor's own report is live where the probe can be a minute old, and a PIE
	that came up during a grant is that grant's concern until it ends. A map load blocks the ticker that writes the report, so 'up' is believed for
	FROZEN_EDITOR_SECONDS, past which the editor is someone's emergency, not a PIE. With no fresh report, the probe decides."""
	if not lane_holder(db):
		for pid, _ in editor_rows(snap):
			report = read_editor_report(pid)
			if report and report['pie'] and t - report['at'] < FROZEN_EDITOR_SECONDS:
				return pid
	probe = json.loads(meta_get(db, 'pie_probe', '{}') or '{}')
	is_told_otherwise = editor_report(probe.get('editor_pid') or 0, t) is not None
	if probe.get('running') and t - probe.get('at', 0) < PIE_PROBE_TTL * 3 and not is_told_otherwise:
		return probe.get('editor_pid') or 0
	return None


def occupants(db, snap, needs):
	"""Things holding the lane that no grant accounts for (a build or PIE started outside the protocol)."""
	found = []
	if needs.get('lane') != 'X':
		return found
	attributed = set()
	for row in db.execute("SELECT p.pid FROM procs p JOIN grants g ON g.id=p.grant_id WHERE g.state IN ('active','releasing') AND p.gone_at=0"):
		attributed.add(row['pid'])
	for p, kind in ue_processes(snap):
		if kind == 'ubt' and p.pid not in attributed:
			age = now() - _ticks_to_epoch(snap.start(p.pid))
			if age < UNREGISTERED_BUILD_MAX:
				found.append('an unregistered UnrealBuildTool (pid %d, running %ds) - it will finish on its own' % (p.pid, age))
	pie_editor = unclaimed_pie_editor(db, snap, now())
	if pie_editor is not None:
		found.append('PIE is running in editor %s with no grant (a human, or an agent outside the protocol); '
			'it is stopped automatically once nobody has touched the keyboard for %d min' % (pie_editor, HUMAN_IDLE_FOR_ORPHAN_PIE // 60))
	return found


def grantable(db, snap, need, owner, seq=None, t=None):
	"""(ok, reason). Strict FIFO among conflicting requests, except a head that leaves a free slot unclaimed."""
	t = t or now()
	if need.get('lane'):
		h = hold_get(db)
		if h.get('on'):
			return False, hold_reason(h)
	for g in active_grants(db):
		if g['owner'] == owner:
			continue
		if conflicts(need, grant_resources(g)):
			return False, 'held by ' + describe(db, snap, g)
	for q in queued_grants(db):
		if seq is not None and q['seq'] >= seq:
			break
		if q['owner'] == owner:
			continue
		if conflicts(need, grant_resources(q)):
			if q['claimable_since'] and t - q['claimable_since'] > HEAD_GRACE:
				continue
			return False, 'queued ahead of you: ' + describe(db, snap, q)
	occ = occupants(db, snap, need)
	if occ:
		return False, occ[0]
	return True, ''


def describe(db, snap, g, o=None):
	o = o if o is not None else get_owner(db, g['owner'])
	state = owner_state(o, snap) if o else 'unknown'
	since = g['acquired_at'] or g['queued_at']
	what = ('command: ' + (g['last_command'] or g['tool'])[:90]) if g['implicit'] else g['purpose'][:140]
	return '%s | %s | %s | owner=%s [%s] | %s | expected %d min | since %s' % (
		g['id'][:8], g['kind'], g['phase'], g['owner'], state, what, g['minutes'], ago(since))


def new_grant(db, owner, kind, purpose='', requirements='', minutes=15, state='queued', implicit=False, tool='', command=''):
	gid = uuid.uuid4().hex
	t = now()
	db.execute("""INSERT INTO grants(id, owner, kind, resources, purpose, requirements, minutes, state, phase, implicit,
		tool, queued_at, acquired_at, updated_at, last_command) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
		(gid, owner, kind, json.dumps(KIND_RESOURCES[kind]), purpose, requirements, minutes, state,
		'preparing' if state == 'active' else 'queued', 1 if implicit else 0, tool, t, t if state == 'active' else 0, t, command[:400]))
	return db.execute('SELECT * FROM grants WHERE id=?', (gid,)).fetchone()


def activate(db, g, how):
	db.execute("UPDATE grants SET state='active', phase='preparing', acquired_at=?, updated_at=?, claimable_since=0 WHERE id=?",
		(now(), now(), g['id']))
	journal(g['owner'], 'grant', '%s %s %s (%s)' % (g['id'][:8], g['kind'], (g['purpose'] or g['last_command'])[:80], how))
	return db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone()


def begin_release(db, g, reason, abnormal):
	ev = json.loads(g['evidence'] or '{}')
	ev['abnormal'] = bool(abnormal)
	db.execute("UPDATE grants SET state='releasing', release_reason=?, updated_at=?, evidence=? WHERE id=?",
		(reason, now(), json.dumps(ev), g['id']))
	if not g['implicit'] or abnormal:
		journal(g['owner'], 'release', '%s %s: %s' % (g['id'][:8], g['kind'], reason))


def lane_turn_waiters(db, g):
	return [q for q in queued_grants(db) if q['owner'] != g['owner'] and conflicts(grant_resources(q), grant_resources(g))]


def last_hold_inside(db, g):
	"""How long the user's last hold sat inside this grant: the turn clock stands still for it."""
	h = hold_get(db)
	released = h.get('released_at') or 0
	since = h.get('last_since') or 0
	return max(0, released - max(since, g['acquired_at'])) if released and since else 0


def lane_turn_due(db, g):
	"""When a holder's turn on the lane ends, 0 while it has no limit. With nobody waiting a hold has no limit; with someone
	waiting it has LANE_TURN from when it was granted, and always LANE_TURN_NOTICE from when the first of them queued."""
	if g['implicit'] or g['state'] != 'active' or grant_resources(g).get('lane') != 'X':
		return 0
	if meta_get(db, 'lane-turn', 'on') != 'on' or hold_active(db):
		return 0
	waiters = lane_turn_waiters(db, g)
	if not waiters:
		return 0
	return max(g['acquired_at'] + LANE_TURN, min(q['queued_at'] for q in waiters) + LANE_TURN_NOTICE) + last_hold_inside(db, g)


def lane_turn_overrun(db, g, t):
	"""Seconds a holder is past its turn on the lane, 0 while it has time."""
	due = lane_turn_due(db, g)
	return max(0, t - due) if due else 0


def lane_turn_ended(db, snap, g, t):
	"""A holder past its turn is refused its next lane command, so it can only finish. The lane comes back once it is past
	LANE_TURN_GRACE with no command running, and at LANE_TURN_HARD_STOP whatever it is doing; a running build is still waited out."""
	over = lane_turn_overrun(db, g, t)
	if not over:
		return False
	if over >= LANE_TURN_HARD_STOP:
		return True
	o = get_owner(db, g['owner'])
	if o is None:
		return True
	is_running = o['open_tools'] > 0 if o['hooked'] else bool(o['pid'] and snap.tool_shells(o['pid']))
	return over >= LANE_TURN_GRACE and not is_running


def lane_turn_text(db, g, t):
	return ('ORCHESTRATOR: your turn on the lane is over: %s grant %s has held it %d min and %d agent(s) are waiting.\nYour command was NOT run. '
		'Release it now: %s finish --id %s --evidence "<what is proven, PIE stopped>"\nFix your test script or code with the lane released, then '
		'`%s acquire --kind %s --purpose "..." --wait 540` to queue again for one prepared run.'
		% (g['kind'], g['id'][:8], (t - g['acquired_at']) // 60, len(lane_turn_waiters(db, g)), ORCH_CMD, g['id'][:8], ORCH_CMD, g['kind']))


def lane_plan_text(g):
	return ('PLAN: one prepared run, then finish --id %s. Write fixes and new scripts after you release, then queue again; '
		'every minute here is a minute everyone behind you waits.' % g['id'][:8])


def queued_label(db, q):
	o = get_owner(db, q['owner'])
	name = (o['title'] or '').strip()[:40] if o else ''
	return '%s (queued %s)' % (name or q['owner'], ago(q['queued_at']))


def lane_patch_note(db, owner, path):
	if not path.lower().endswith(PATCH_NOTE_EXTENSIONS) or hold_active(db):
		return ''
	for g in db.execute("SELECT * FROM grants WHERE owner=? AND state='active' AND implicit=0 ORDER BY seq", (owner,)).fetchall():
		evidence = json.loads(g['evidence'] or '{}')
		if evidence.get('patchNoted') or not grant_resources(g).get('lane'):
			continue
		waiters = lane_turn_waiters(db, g)
		if not waiters:
			continue
		evidence['patchNoted'] = True
		db.execute('UPDATE grants SET evidence=? WHERE id=?', (json.dumps(evidence), g['id']))
		journal(owner, 'patch-note', '%s %s: %d queued, %s' % (g['id'][:8], g['kind'], len(waiters), path))
		names = ', '.join(queued_label(db, q) for q in waiters[:PATCH_NOTE_NAMES_SHOWN])
		more = len(waiters) - PATCH_NOTE_NAMES_SHOWN
		return ('[orchestrator] You hold the lane (%s grant %s) and changed a script or C++ with %s%s behind you. A fix can wait: '
			'`%s finish --id %s --evidence "<what is proven>"`, write it after you release, then queue again. If you need the live run to '
			'investigate, carry on: this is a note, and it shows once per grant.'
			% (g['kind'], g['id'][:8], names, ' and %d more' % more if more > 0 else '', ORCH_CMD, g['id'][:8]))
	return ''


def finish_release(db, g_id, phase='finished'):
	db.execute("UPDATE grants SET state='done', phase=?, released_at=?, updated_at=? WHERE id=?", (phase, now(), now(), g_id))
	db.execute("""DELETE FROM grants WHERE state='done' AND id NOT IN
		(SELECT id FROM grants WHERE state='done' ORDER BY released_at DESC LIMIT 200)""")


# ---------------------------------------------------------------- reconcile

RECLAIM_CLEANUP_TEXT = ('Its PIE is stopped and the test processes it launched are killed before the lane moves on (a PIE stays up only while '
	'the user holds the editor), and a ue_python.py call from a background chain of yours is refused until you hold the lane again.')


def reconcile(db, snap, force=False):
	"""Reap what provably stopped working, attribute Unreal processes, and mark releases. Runs inside a Tx."""
	t = now()
	last = float(meta_get(db, 'reconciled_at', '0') or 0)
	if not force and t - last < RECONCILE_INTERVAL:
		return
	meta_set(db, 'reconciled_at', str(t))
	end_orphaned_hold(db, snap, t)

	states = {}
	for o in db.execute('SELECT * FROM owners WHERE dead_at=0').fetchall():
		st = owner_state(o, snap, t)
		states[o['owner']] = st
		if o['pid'] and st != 'dead' and snap.tool_shells(o['pid']):
			db.execute('UPDATE owners SET last_shell_seen=? WHERE owner=?', (t, o['owner']))
		if st == 'dead':
			db.execute('UPDATE owners SET dead_at=? WHERE owner=?', (t, o['owner']))
			if not o['ended']:
				n = db.execute("""UPDATE tasks SET state='orphaned', orphaned_at=? WHERE owner=? AND state IN ('in_progress','blocked')
					AND blocked_on!='user'""", (t, o['owner'])).rowcount
				if n:
					journal(o['owner'], 'orphan', '%d task(s) orphaned: the owner process vanished without SessionEnd' % n)
		elif not o['pid'] and st == 'silent' and t - max(o['last_active'], o['last_shell_seen']) >= AGE_OUT_SECONDS:
			# It has no process to die, so nothing else would ever mark it, and the name can come back: ensure_owner, with its channels and mail where they were.
			db.execute('UPDATE owners SET dead_at=? WHERE owner=?', (t, o['owner']))
			journal(o['owner'], 'gone', 'no process to check and silent for %d days' % TASK_AGE_OUT_DAYS)

	def state_of(owner):
		if owner not in states:
			states[owner] = owner_state(get_owner(db, owner), snap, t)
		return states[owner]

	attribute_processes(db, snap)

	for g in active_grants(db):
		if g['state'] != 'active':
			continue
		st = state_of(g['owner'])
		is_p4 = g['implicit'] and g['kind'] == 'P4'
		if is_p4 and p4_grant_is_idle(db, snap, g, t):
			begin_release(db, g, 'no p4 ran under its owner for %ds' % P4_IDLE, False)
			journal(g['owner'], 'release', '%s P4: no p4 ran under its owner for %ds' % (g['id'][:8], P4_IDLE))
			continue
		if g['process_bound']:
			if not is_p4 and not live_procs(db, snap, g['id'], kinds=('game',) + WAITED_OUT_KINDS) and t - g['acquired_at'] > 20:
				begin_release(db, g, 'the processes its command launched have exited', False)
				continue
		elif g['implicit'] and st != 'busy':
			begin_release(db, g, 'command grant outlived its tool call (owner %s)' % st, st not in WORKING_STATES)
			continue
		if st not in WORKING_STATES:
			begin_release(db, g, 'owner stopped working (%s)' % st, True)
			if st != 'dead' and not g['implicit']:
				notify(db, g['owner'], 'The orchestrator reclaimed your %s grant %s because you stopped working (%s). '
					'%s Acquire again when ready: %s acquire --kind %s --purpose "..." --wait 540'
					% (g['kind'], g['id'][:8], st, RECLAIM_CLEANUP_TEXT, ORCH_CMD, g['kind']))

	for q in queued_grants(db):
		st = state_of(q['owner'])
		if not queue_entry_alive(st):
			db.execute("UPDATE grants SET state='done', phase='abandoned', released_at=?, release_reason=? WHERE id=?",
				(t, 'queued owner stopped working (%s)' % st, q['id']))
			journal(q['owner'], 'abandon', '%s %s queue entry dropped (%s)' % (q['id'][:8], q['kind'], st))
	# After the sweep, so a waiter that died in this pass cannot end anyone's turn.
	for g in active_grants(db):
		if g['state'] == 'active' and lane_turn_ended(db, snap, g, t):
			begin_release(db, g, 'its turn on the lane ended: held %d min with agents waiting' % ((t - g['acquired_at']) // 60), True)
			notify(db, g['owner'], 'The orchestrator took the lane back from your %s grant %s: your turn ended and others were waiting. '
				'%s Fix your test script or code with the lane released, then queue again: '
				'%s acquire --kind %s --purpose "..." --wait 540' % (g['kind'], g['id'][:8], RECLAIM_CLEANUP_TEXT, ORCH_CMD, g['kind']))
	# A head that could take a free slot but does not must not block everyone behind it.
	for q in queued_grants(db):
		ok, _ = grantable(db, snap, grant_resources(q), q['owner'], q['seq'], t)
		if ok and not q['claimable_since']:
			db.execute('UPDATE grants SET claimable_since=? WHERE id=?', (t, q['id']))
		elif not ok and q['claimable_since']:
			db.execute('UPDATE grants SET claimable_since=0 WHERE id=?', (q['id'],))

	for task in db.execute("SELECT * FROM tasks WHERE state='resuming'").fetchall():
		if t - task['resumed_at'] > RESUME_CLAIM_WINDOW:
			db.execute("UPDATE tasks SET state='orphaned' WHERE id=?", (task['id'],))
			journal(task['owner'], 'resume', 'task %s: the relaunched session never reported in' % task['id'][:8])
	expire_old_tasks(db, states, t)


def expire_old_tasks(db, states, t):
	"""Nothing else ever closes a task whose session has gone, and they piled up in the panel. The row and its notes stay, and `task claim` brings one back.
	Almost every pass stops at the first query."""
	old = db.execute("SELECT * FROM tasks WHERE state NOT IN ('done','cancelled','expired','resuming') AND updated<?", (t - AGE_OUT_SECONDS,)).fetchall()
	if not old:
		return
	owners = {o['owner']: o for o in db.execute('SELECT * FROM owners').fetchall()}
	sessions = live_sessions(owners, states)
	for r in old:
		if not task_carrier(r, owners.get(r['owner']), states, sessions):
			db.execute("UPDATE tasks SET state='expired' WHERE id=?", (r['id'],))
			journal(r['owner'], 'expire', 'task %s was %s: no live owner and no update for %d days: %s' % (r['id'][:8], r['state'], TASK_AGE_OUT_DAYS, r['title'][:100]))


def p4_grant_is_idle(db, snap, g, t):
	"""The P4 mutex guards p4 itself, not the tool call it came in: otherwise a p4 edit chained with a long run holds every other agent's p4 for the
	whole run. A p4 later in that chain then runs unprotected. `updated_at` is when p4 last ran under the grant's owner."""
	o = get_owner(db, g['owner'])
	pid = o['pid'] if o else 0
	if pid and any(p.name == P4_EXE and any(a.pid == pid for a in snap.ancestors(p.pid)[1:]) for p in list(snap.procs.values())):
		db.execute('UPDATE grants SET updated_at=? WHERE id=?', (t, g['id']))
		return False
	return t - g['updated_at'] >= P4_IDLE


def attribute_processes(db, snap):
	t = now()
	known = {(r['pid'], r['start']) for r in db.execute('SELECT pid, start FROM procs WHERE gone_at=0')}
	seen = set()
	owners = {o['pid']: o['owner'] for o in db.execute('SELECT owner, pid FROM owners WHERE pid>0 AND dead_at=0').fetchall()}
	for p, kind in ue_processes(snap):
		key = (p.pid, snap.start(p.pid))
		seen.add(key)
		if key in known:
			continue
		owner = ''
		for a in snap.ancestors(p.pid)[1:]:
			if a.pid in owners:
				owner = owners[a.pid]
				break
		created = _ticks_to_epoch(key[1])
		grant_id = ''
		candidates = [g for g in active_grants(db) if g['state'] == 'active']
		if owner:
			mine = [g for g in candidates if g['owner'] == owner]
			if mine:
				grant_id = mine[-1]['id']
		# Detached launches lose their parent; whoever held the lane when it started owns it. Only an editor binary run as
		# -game/-server can be such a launch: a packaged client with no agent ancestor is the user's own game (a launcher
		# such as Steam starts it), and claiming it by timing would kill it at the end of an unrelated build grant.
		if not grant_id and kind != 'editor' and p.name in ('unrealeditor.exe', 'unrealeditor-cmd.exe'):
			for g in candidates:
				if g['acquired_at'] and g['acquired_at'] - 5 <= created and grant_resources(g).get('lane'):
					grant_id = g['id']
					owner = owner or g['owner']
		db.execute('INSERT OR IGNORE INTO procs(pid, start, name, kind, cmd, owner, grant_id, first_seen) VALUES(?,?,?,?,?,?,?,?)',
			(p.pid, key[1], p.name, kind, snap.cmd(p.pid)[:600], owner, grant_id, t))
	for r in db.execute('SELECT pid, start FROM procs WHERE gone_at=0').fetchall():
		if (r['pid'], r['start']) not in seen:
			db.execute('UPDATE procs SET gone_at=? WHERE pid=? AND start=?', (t, r['pid'], r['start']))
	db.execute('DELETE FROM procs WHERE gone_at>0 AND gone_at<?', (t - 86400,))


def live_procs(db, snap, grant_id, kinds=None):
	out = []
	for r in db.execute('SELECT * FROM procs WHERE grant_id=? AND gone_at=0 AND handed_to_user=0', (grant_id,)).fetchall():
		if snap.alive(r['pid'], r['start']) and (kinds is None or r['kind'] in kinds):
			out.append(r)
	return out


def run_cleanups(db, snap):
	"""Second half of a release, outside the transaction: kill leftover test processes, stop orphaned PIE, wait out builds."""
	for g in db.execute("SELECT * FROM grants WHERE state='releasing'").fetchall():
		with Tx(db):
			g = db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone()
			if not g or g['state'] != 'releasing':
				continue
			if g['cleanup_by'] and g['cleanup_by'] != os.getpid() and now() - g['cleanup_at'] < 60:
				continue
			db.execute('UPDATE grants SET cleanup_by=?, cleanup_at=? WHERE id=?', (os.getpid(), now(), g['id']))
		evidence = json.loads(g['evidence'] or '{}')
		notes = []
		if not g['keep_processes']:
			for r in live_procs(db, snap, g['id'], kinds=('game',)):
				kill_tree(r['pid'])
				notes.append('killed %s pid %d' % (r['name'], r['pid']))
		pending = live_procs(db, snap, g['id'], kinds=WAITED_OUT_KINDS)
		is_stop_due = evidence.get('abnormal') and grant_resources(g).get('lane') == 'X' and not pending and not g['implicit']
		is_stop_settled = not is_stop_due or stop_reclaimed_pie(db, snap, g, notes)
		with Tx(db):
			if pending or not is_stop_settled:
				db.execute('UPDATE grants SET cleanup_by=0, cleanup_at=? WHERE id=?', (now(), g['id']))
			else:
				finish_release(db, g['id'], 'reclaimed' if evidence.get('abnormal') else 'finished')
		if notes:
			journal(g['owner'], 'cleanup', '%s: %s' % (g['id'][:8], '; '.join(notes)))


def settle(db, snap):
	if db.execute("SELECT COUNT(*) c FROM grants WHERE state='releasing'").fetchone()['c']:
		run_cleanups(db, snap)


# ---------------------------------------------------------------- editor probe

def editor_rows(snap):
	"""Every editor on this project as (pid, rendering), the main one first: the oldest with rendering on."""
	rows = [(p.pid, '-nullrhi' not in snap.cmd(p.pid).lower()) for p, kind in ue_processes(snap) if kind == 'editor' and RX_UPROJECT.search(snap.cmd(p.pid))]
	return sorted(rows, key=lambda r: (not r[1], snap.start(r[0]), r[0]))


def editor_pids(snap):
	return [pid for pid, _ in editor_rows(snap)]


def refresh_editor_watch(db, snap, t):
	"""A long load also stops answering for a while, so the feed calls an editor frozen only past FROZEN_EDITOR_SECONDS. Only the daemon runs this, so
	it costs a hook nothing, and a -nullrhi editor has no window to report."""
	pids = editor_pids(snap)
	hung = hung_window_pids() if pids else ()
	before = json.loads(meta_get(db, EDITOR_HUNG_KEY, '{}') or '{}')
	after = {str(pid): before.get(str(pid), t) for pid in pids if pid in hung}
	if after != before:
		with Tx(db):
			meta_set(db, EDITOR_HUNG_KEY, json.dumps(after))


def remote_python_busy(snap):
	for p in snap.procs.values():
		if p.name in ('python.exe', 'pythonw.exe', 'py.exe') and 'ue_python' in snap.cmd(p.pid).lower():
			return True
	return False


def editor_exec(code, timeout=2.5):
	"""Run code in this project's editor over remote execution on a private command port. -> output text, or None."""
	if SANDBOX:
		return None
	plugin = None
	for root in engine_roots():
		cand = os.path.join(root, 'Engine', 'Plugins', 'Experimental', 'PythonScriptPlugin', 'Content', 'Python')
		if os.path.isfile(os.path.join(cand, 'remote_execution.py')):
			plugin = cand
			break
	if not plugin:
		return None
	if plugin not in sys.path:
		sys.path.insert(0, plugin)
	try:
		import remote_execution as rex
	except Exception:
		return None
	cfg = rex.RemoteExecutionConfig()
	cfg.command_endpoint = ('127.0.0.1', PROBE_COMMAND_PORT)
	conn = rex.RemoteExecution(cfg)
	try:
		conn.start()
		deadline = time.time() + timeout
		node = None
		target = os.path.normcase(PROJECT).rstrip('\\/')
		while time.time() < deadline and not node:
			for n in conn.remote_nodes:
				root = os.path.normcase(os.path.abspath(n.get('project_root') or '')).rstrip('\\/')
				if root == target:
					node = n
			if not node:
				time.sleep(0.2)
		if not node:
			return None
		conn.open_command_connection(node['node_id'])
		result = conn.run_command(code, unattended=True, exec_mode=rex.MODE_EXEC_FILE)
		return '\n'.join(o.get('output', '') for o in result.get('output') or [])
	except Exception:
		return None
	finally:
		try:
			conn.stop()
		except Exception:
			pass


PIE_PROBE = ("import unreal\n"
	"s = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)\n"
	"print('ORCH_PIE=%d' % (1 if s.is_in_play_in_editor() else 0))\n")
PIE_STOP = ("import unreal\n"
	"s = unreal.get_editor_subsystem(unreal.LevelEditorSubsystem)\n"
	"if s.is_in_play_in_editor():\n"
	"\ts.editor_request_end_play()\n"
	"\tprint('ORCH_STOPPED')\n")


def refresh_pie_probe(db, snap, force=False):
	"""Only called while nobody holds the lane, so the probe never talks over an agent's own editor session."""
	probe = json.loads(meta_get(db, 'pie_probe', '{}') or '{}')
	ttl = PIE_PROBE_TTL if probe.get('reachable', True) else PIE_PROBE_UNREACHABLE_TTL
	# The probe runs Python inside the editor; while the user holds it, nothing of ours runs there.
	if hold_active(db) or (not force and now() - probe.get('at', 0) < ttl):
		return probe
	pids = editor_pids(snap)
	report = editor_report(pids[0], now()) if pids else None
	if report:
		result = {'at': now(), 'running': report['pie'], 'editor_pid': pids[0], 'reachable': True}
		with Tx(db):
			meta_set(db, 'pie_probe', json.dumps(result))
		return result
	# Not asking is no answer: the last one stands, and `at` stays the time of that answer, so occupants() still ages it out.
	if pids and remote_python_busy(snap):
		return probe
	result = {'at': now(), 'running': False, 'editor_pid': pids[0] if pids else 0}
	if pids:
		out = editor_exec(PIE_PROBE)
		result['running'] = bool(out and 'ORCH_PIE=1' in out)
		result['reachable'] = out is not None
	with Tx(db):
		meta_set(db, 'pie_probe', json.dumps(result))
	return result


def stop_orphaned_pie(db, snap, why):
	pids = editor_pids(snap)
	if not pids or remote_python_busy(snap) or hold_active(db):
		return ''
	out = editor_exec(PIE_STOP)
	with Tx(db):
		meta_set(db, 'pie_probe', json.dumps({'at': now(), 'running': False, 'editor_pid': pids[0]}))
	if out and 'ORCH_STOPPED' in out:
		journal('', 'pie-stop', 'stopped PIE: %s' % why)
		return 'stopped PIE'
	return ''


def settle_unregistered_pie(db, snap):
	"""An unregistered PIE is protected while a human is at the machine; with nobody at the keyboard it is an orphan."""
	probe = json.loads(meta_get(db, 'pie_probe', '{}') or '{}')
	if not probe.get('running') or lane_holder(db):
		return
	if human_idle_seconds() > HUMAN_IDLE_FOR_ORPHAN_PIE:
		stop_orphaned_pie(db, snap, 'unregistered PIE, no grant, no keyboard/mouse input for %d min' % (HUMAN_IDLE_FOR_ORPHAN_PIE // 60))


def stop_pie_now(db, snap, owner, why):
	"""Stop the editor's PIE for an agent that is done with it. -> True if a PIE was stopped."""
	if not editor_pids(snap) or hold_active(db):
		return False
	out = editor_exec(PIE_STOP)
	with Tx(db):
		meta_set(db, 'pie_probe', json.dumps({'at': now(), 'running': False, 'editor_pid': editor_pids(snap)[0]}))
	if out and 'ORCH_STOPPED' in out:
		journal(owner, 'pie-stop', 'stopped PIE: %s' % why)
		return True
	return False


def main_editor_report(snap, t):
	main = next((pid for pid, rendering in editor_rows(snap) if rendering), 0)
	return editor_report(main, t) if main else None


def pie_verdict(g, report):
	"""Whether a PIE on the editor is this grant's to stop, as far as anything can say. 'none': the editor says none is up. 'ours': it came up inside the grant and the
	grant's owner started it. 'not ours': it was up before the grant, or someone at the keyboard started it and the holder did not. 'unknown': nothing can say.
	A harness starts PIE from a script, so `pie_started` is usually 0 and the plugin's report is the evidence: no input in that editor just before a PIE
	came up, while this agent held the lane, means the agent started it."""
	if report and not report['pie']:
		return 'none'
	since = report.get('pieSince') if report else 0
	if not since or not isinstance(since, (int, float)):
		return 'ours' if g['pie_started'] else 'unknown'
	if since < (json.loads(g['evidence'] or '{}').get('heldSince') or g['acquired_at']):
		return 'not ours'
	is_holders_start = g['pie_started'] and since >= g['started_at']
	return 'not ours' if report.get('pieByInput') and not is_holders_start else 'ours'


def editor_says_pie_is_up(db, snap):
	"""Only when nothing else can say: this runs Python in the editor."""
	if not editor_pids(snap) or hold_active(db):
		return False
	return 'ORCH_PIE=1' in (editor_exec(PIE_PROBE) or '')


def stop_reclaimed_pie(db, snap, g, notes):
	"""Taking a lane grant back stops a PIE that grant started, whatever its kind: its work, or a chain of its owner's, may have left one. Any other PIE stays.
	-> False to try again at the next settle. The grant stays `releasing` until this is True, which keeps the lane shut, so the stop can never
	reach the next holder's PIE. A remote python still running may be about to start a PIE, and an editor that does not answer may only be
	hitching, so both get RECLAIM_PIE_WAIT."""
	if not editor_pids(snap) or hold_active(db):
		return True
	is_out_of_time = now() - g['updated_at'] >= RECLAIM_PIE_WAIT
	if remote_python_busy(snap) and not is_out_of_time:
		return False
	verdict = pie_verdict(g, main_editor_report(snap, now()))
	if verdict == 'none':
		return True
	if verdict == 'not ours':
		journal(g['owner'], 'pie-left', 'left a PIE running after reclaiming grant %s: not started by it' % g['id'][:8])
		return True
	out = editor_exec(PIE_STOP if verdict == 'ours' else PIE_PROBE)
	if verdict == 'unknown' and out is not None:
		if 'ORCH_PIE=1' in out:
			journal(g['owner'], 'pie-left', 'left a PIE running after reclaiming grant %s: nothing says it started it' % g['id'][:8])
			with Tx(db):
				notify(db, g['owner'], 'A PIE is up and nothing says your %s grant %s started it, so it was left running. If it is yours, stop it with `%s release-pie`.'
					% (g['kind'], g['id'][:8], ORCH_CMD))
		return True
	if out is None:
		if not is_out_of_time:
			return False
		journal(g['owner'], 'pie-stop', 'could not stop PIE: no answer from the editor for %ds after reclaiming grant %s' % (RECLAIM_PIE_WAIT, g['id'][:8]))
		with Tx(db):
			notify(db, g['owner'], 'The editor did not answer, so the orchestrator could not stop a PIE your %s grant %s may have left up. '
				'If it is yours, stop it with `%s release-pie`.' % (g['kind'], g['id'][:8], ORCH_CMD))
		return True
	with Tx(db):
		meta_set(db, 'pie_probe', json.dumps({'at': now(), 'running': False, 'editor_pid': editor_pids(snap)[0]}))
	if 'ORCH_STOPPED' in out:
		journal(g['owner'], 'pie-stop', 'stopped PIE: reclaimed grant %s' % g['id'][:8])
		notes.append('stopped PIE')
	return True


def release_handed_pie(db, snap, owner, why):
	"""A PIE an agent left running for the user (finish --keep-pie) is that agent's last claim on the editor."""
	handoff = json.loads(meta_get(db, 'pie_handoff', '{}') or '{}')
	if handoff.get('owner') != owner:
		return False
	with Tx(db):
		meta_set(db, 'pie_handoff', '{}')
	if lane_holder(db):
		return False
	report = main_editor_report(snap, now())
	if not report or not report['pie']:
		return False
	since = report.get('pieSince') or 0
	if not since or since > handoff.get('at', 0):
		journal(owner, 'pie-left', 'left a PIE running: it came up after the one handed over' if since else 'left a PIE running: nothing says it is the one handed over')
		return False
	return stop_pie_now(db, snap, owner, why)


# ---------------------------------------------------------------- is a build needed

UBT_LOG_HEAD_BYTES = 32 * 1024
UBT_LOG_TAIL_BYTES = 4 * 1024
FULL_BUILD_FLAGS = {'-waitmutex', '-frommsbuild', '-architecture=x64'}
SOURCE_EXTENSIONS = {'.h', '.hpp', '.hh', '.c', '.cc', '.cpp', '.cxx', '.inl', '.inc', '.ipp', '.cs', '.usf', '.ush', '.ispc', '.rc', '.def',
	'.lib', '.dll', '.uplugin'}
SOURCE_FOLDER_NAMES = {'source', 'shaders'}
SOURCE_WALK_SKIP = {'binaries', 'intermediate', 'content', 'saved', 'deriveddatacache', '__pycache__', '.vs', '.git'}
PLUGIN_SOURCE_DEPTH = 3
RX_UBT_STARTED = re.compile(r'Log started at [^\n]*\((\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)Z\)')
RX_UBT_COMMAND = re.compile(r'^Command line: ([^\r\n]*)', re.M)
RX_UBT_RESULT = re.compile(r'^Result: ([^\r\n]*)', re.M)
RX_UBT_TOTAL = re.compile(r'^Total execution time: (\d+(?:\.\d+)?) seconds', re.M)


def is_full_build_command(line):
	"""A plain full build of this project's editor target. An unknown flag is not one: this only ever answers towards silence."""
	tokens = [t.strip('"') for t in re.findall(r'"[^"]*"|\S+', line)]
	if not tokens or not re.search(r'unrealbuildtool\.(dll|exe)$', tokens[0], re.I):
		return False
	words, project = set(), ''
	for token in tokens[1:]:
		lower = token.lower()
		if lower.startswith('-project='):
			project = token[len('-project='):]
		elif lower.startswith('-'):
			if lower not in FULL_BUILD_FLAGS:
				return False
		else:
			words.add(lower)
	return bool(UPROJECT) and words == FULL_BUILD_WORDS and bool(project) and os.path.normcase(os.path.abspath(project)) == os.path.normcase(UPROJECT)


def newest_full_build_log():
	"""-> (start, path) of the newest full build UBT's logs still hold. A -singlefile run rotates Log.txt into Log-backup-*, so look there too."""
	newest = None
	for path in [os.path.join(UBT_LOG_DIR, 'Log.txt')] + glob.glob(os.path.join(glob.escape(UBT_LOG_DIR), 'Log-backup-*.txt')):
		with open(path, 'rb') as f:
			head = f.read(UBT_LOG_HEAD_BYTES).decode('utf-8', 'replace')
		started, command = RX_UBT_STARTED.search(head), RX_UBT_COMMAND.search(head)
		if started and command and is_full_build_command(command.group(1)):
			at = _parse_iso(started.group(1))
			if not newest or at > newest[0]:
				newest = (at, path)
	return newest


def source_changed_since(started):
	"""True at the first source file or source folder newer than `started`. A folder's time moves when an entry is added, removed or renamed,
	the only sign of a deleted file or of one moved in with an old time; it is read with a stat of its own, because a listing of the parent
	holds a stale copy of a folder's time. Plugins nest (Vendor/SomePlugin/Source), so their folders are
	searched PLUGIN_SOURCE_DEPTH down for Source and Shaders."""
	if os.path.getmtime(UPROJECT) > started:
		return True
	# Source is popped first: that is where a change is most likely, and the first one ends the walk.
	stack = [(os.path.join(PROJECT, name), name != 'Plugins', 0) for name in ('Plugins', 'Shaders', 'Source')]
	for path, is_source, _ in stack:
		if is_source and os.path.getmtime(path) > started:
			return True
	while stack:
		path, is_source, depth = stack.pop()
		with os.scandir(path) as entries:
			for entry in entries:
				name = entry.name.lower()
				if entry.is_dir(follow_symlinks=False):
					if name in SOURCE_WALK_SKIP:
						continue
					is_inner_source = is_source or name in SOURCE_FOLDER_NAMES
					if is_inner_source and os.path.getmtime(entry.path) > started:
						return True
					if is_inner_source or depth < PLUGIN_SOURCE_DEPTH:
						stack.append((entry.path, is_inner_source, depth + 1))
				elif os.path.splitext(name)[1] in SOURCE_EXTENSIONS and entry.stat(follow_symlinks=False).st_mtime > started:
					return True
	return False


def covering_build_start():
	"""The start of the last successful full build if it covers the tree, else None. UHT's timestamp file newer than that build's end (a
	-singlefile run with the editor open rewrites it) or gone means a later build may compile against stale generated headers."""
	newest = newest_full_build_log()
	if not newest:
		return None
	started, path = newest
	with open(path, 'rb') as f:
		f.seek(0, os.SEEK_END)
		f.seek(max(0, f.tell() - UBT_LOG_TAIL_BYTES))
		tail = f.read().decode('utf-8', 'replace')
	results, totals = RX_UBT_RESULT.findall(tail), RX_UBT_TOTAL.findall(tail)
	if not results or results[-1].strip() != 'Succeeded' or not totals:
		return None
	if os.path.getmtime(os.path.join(PROJECT, *UHT_TIMESTAMPS)) > started + float(totals[-1]):
		return None
	return None if source_changed_since(started) else started


def build_not_needed_line():
	"""Advice beside a Build grant, never a refusal: all agents share one source tree, so one full build compiles everyone's saved edits. It
	fails towards silence, because a false "not needed" would ship old code in someone's test: whatever the log cannot prove says nothing."""
	try:
		started = covering_build_start()
	except Exception:
		return ''
	if not started:
		return ''
	return ('BUILD NOT NEEDED: no source changed since the full %s build that started %s. Skip the build; keep or cancel this grant as you need.'
		% (EDITOR_TARGET, time.strftime('%H:%M:%S', time.gmtime(started))))


def print_build_advice(g):
	if g['kind'] == 'Build':
		line = build_not_needed_line()
		if line:
			print(line)


# ---------------------------------------------------------------- command classification

READERS = {
	'grep', 'rg', 'egrep', 'fgrep', 'cat', 'head', 'tail', 'less', 'more', 'ls', 'dir', 'echo', 'printf', 'find',
	'findstr', 'sed', 'awk', 'wc', 'sort', 'uniq', 'cut', 'tr', 'diff', 'file', 'stat', 'select-string', 'sls',
	'get-content', 'gc', 'type', 'get-childitem', 'gci', 'test-path', 'get-item', 'resolve-path', 'write-output',
	'write-host', 'git', 'jq', 'xxd', 'strings', 'basename', 'dirname', 'realpath', 'readlink', 'which', 'where',
	'get-command', 'tasklist', 'get-process', 'get-ciminstance', 'gwmi', 'get-wmiobject', 'wmic', 'measure-object',
	'select-object', 'format-table', 'ft', 'out-string', 'convertto-json', 'convertfrom-json', 'tee', 'column', 'nl',
	'split-path', 'join-path', 'md5sum', 'sha256sum', 'certutil', 'get-filehash', 'where-object', '?',
}
DATA_WRITERS = {'cat', 'tee'}
LEADING_NOISE = {'then', 'do', 'else', 'elif', 'time', 'exec', 'env', 'sudo', 'if', 'while', 'until', '!', 'try', 'foreach', '&', '.', 'call', 'start'}
P4_OPTS_WITH_ARG = {'-c', '-u', '-p', '-P', '-H', '-d', '-C', '-Q', '-L', '-x', '-z', '-r', '-v', '-I'}
P4_CONTENT = {'sync', 'revert', 'unshelve', 'resolve', 'integrate', 'integ', 'merge', 'copy', 'move', 'rename', 'delete', 'undo', 'clean', 'unzip'}
P4_META = {'submit', 'reconcile', 'shelve', 'reopen', 'change', 'changelist', 'edit', 'add', 'lock', 'unlock', 'fix', 'flush', 'label', 'tag'}

RX_BUILD = re.compile(r'\bBuild\.(bat|sh)\b|RunUAT|RunUBT|UnrealBuildTool|UnrealHeaderTool', re.I)
RX_LIVECODE = re.compile(r'(?<!No)LiveCoding(?!Console)|SendKeys', re.I)
RX_EDITOR_EXE = re.compile(r'UnrealEditor(-Cmd)?\.exe|UnrealEditor-Cmd\b', re.I)
RX_EDITOR_RPC = re.compile(r'ue_python\.py|(localhost|127\.0\.0\.1):8000/mcp|remote_execution', re.I)
RX_UE_PYTHON_SCRIPT = re.compile(r'ue_python\.py["\']?\s+((?:--\S+\s+)*)["\']?([^\s"\']+\.py)\b', re.I)
READ_ONLY_MARKER = 'ORCH: read-only'
READ_ONLY_HEADER_BYTES = 2000
RX_PIE_START = re.compile(r'StartPIE|StartPlaySession|RequestPlaySession|editor_play_simulate|editor_request_begin_play|play_in_editor', re.I)
RX_PIE_STOP = re.compile(r'StopPIE|StopPlaySession|RequestEndPlayMap|editor_end_play|editor_request_end_play', re.I)
RX_DEPLOY = deploy_pattern()
RX_KILL = re.compile(r'\b(Stop-Process|taskkill|kill|spps)\b', re.I)
RX_HEREDOC = re.compile(r"<<-?\s*(['\"]?)([A-Za-z_][A-Za-z0-9_]*)\1")
RX_SEGMENT_SPLIT = re.compile(r'\n|;|&&|\|\||\||\{|\}')

RANK = {None: 0, 'inspect': 1, 'free': 2, 'p4': 3, 'test': 4, 'deploy': 5, 'editor': 6, 'sync': 6, 'build': 7}


def _first_word(segment):
	for w in re.findall(r'[^\s(){}]+', segment):
		lw = w.strip('&"\'').lower()
		if not lw or lw in LEADING_NOISE or re.match(r'^\$?[a-z_][a-z0-9_]*=', lw):
			continue
		base = re.split(r'[\\/]', lw)[-1]
		if base.endswith('.exe'):
			base = base[:-4]
		return base
	return ''


def strip_data_heredocs(cmd):
	"""Heredoc bodies fed to cat/tee are file contents, not commands."""
	lines = cmd.split('\n')
	out = []
	i = 0
	while i < len(lines):
		line = lines[i]
		out.append(line)
		m = RX_HEREDOC.search(line)
		if m:
			delim = m.group(2)
			segment = RX_SEGMENT_SPLIT.split(line[:m.start()])[-1]
			is_data = _first_word(segment) in DATA_WRITERS
			j = i + 1
			while j < len(lines) and lines[j].strip() != delim:
				if not is_data:
					out.append(lines[j])
				j += 1
			i = j + 1
			continue
		i += 1
	return '\n'.join(out)


def _read_only_rpc(segment):
	"""A ue_python script whose header declares READ_ONLY_MARKER only reads editor state, so it may run beside any PIE,
	including a human's. The script must be named by a literal path (a shell variable cannot be checked)."""
	if re.search(r'(localhost|127\.0\.0\.1):8000/mcp|remote_execution', segment, re.I):
		return False
	m = RX_UE_PYTHON_SCRIPT.search(segment)
	if not m or m.group(1).strip():
		return False
	path = m.group(2)
	if '$' in path or '%' in path:
		return False
	drive = re.match(r'^/([a-zA-Z])/(.*)$', path)
	if drive:
		path = drive.group(1) + ':/' + drive.group(2)
	if not os.path.isabs(path):
		path = os.path.join(PROJECT, path)
	try:
		with open(path, encoding='utf-8', errors='replace') as f:
			return READ_ONLY_MARKER in f.read(READ_ONLY_HEADER_BYTES)
	except OSError:
		return False


def _p4_verb(segment):
	tokens = re.findall(r'"[^"]*"|\'[^\']*\'|\S+', segment)
	idx = None
	for k, tok in enumerate(tokens):
		if re.split(r'[\\/]', tok.strip('"\'&').lower())[-1] in ('p4', 'p4.exe'):
			idx = k
			break
	if idx is None:
		return None, []
	k = idx + 1
	while k < len(tokens) and tokens[k].startswith('-'):
		k += 2 if tokens[k] in P4_OPTS_WITH_ARG else 1
	if k >= len(tokens):
		return None, []
	return tokens[k].lower(), tokens[k + 1:]


def p4_is_harmless(verb, args):
	return any(a in ('-k', '-n') for a in args) or (verb == 'revert' and '-a' in args)


def classify_command(cmd, snap=None):
	"""-> (category, kind, flags). category: build | editor | sync | deploy | test | p4 | free | inspect | None."""
	best = (None, None, {})
	for segment in RX_SEGMENT_SPLIT.split(strip_data_heredocs(cmd or '')):
		if not segment.strip():
			continue
		first = _first_word(segment)
		result = (None, None, {})
		if first == 'p4':
			verb, args = _p4_verb(segment)
			if verb in P4_CONTENT:
				if p4_is_harmless(verb, args):
					result = ('p4', 'P4', {'p4verb': verb})
				else:
					result = ('sync', 'Sync', {'p4verb': verb, 'p4args': args})
			elif verb in P4_META:
				result = ('p4', 'P4', {'p4verb': verb})
		elif first in READERS:
			continue
		else:
			if RX_BUILD.search(segment):
				result = ('free', None, {}) if re.search(r'-singlefile', segment, re.I) else ('build', 'Build', {})
			elif RX_LIVECODE.search(segment):
				result = ('build', 'Build', {'livecoding': True})
			elif RX_KILL.search(segment) and (RX_UE_WORD.search(segment) or _kills_ue_pid(segment, snap)):
				result = ('editor', 'Editor', {'kill': True})
			elif RX_EDITOR_EXE.search(segment):
				if re.search(r'(^|[\s\'"])-(game|server)\b', segment, re.I):
					result = ('test', 'Test', {})
				else:
					result = ('editor', 'Editor', {})
			elif RX_GAME_EXE.search(segment):
				result = ('test', 'Test', {})
			elif RX_DEPLOY.search(segment):
				result = ('deploy', 'Deploy', {})
			elif RX_EDITOR_RPC.search(segment) or RX_PIE_START.search(segment) or RX_PIE_STOP.search(segment):
				if not RX_PIE_START.search(segment) and not RX_PIE_STOP.search(segment) and _read_only_rpc(segment):
					result = ('inspect', None, {'read_only': True})
				else:
					result = ('editor', 'Editor', {})
			if result[0] in ('editor', 'build'):
				if RX_PIE_START.search(segment):
					result[2]['pie_start'] = True
				if RX_PIE_STOP.search(segment):
					result[2]['pie_stop'] = True
		if RANK[result[0]] > RANK[best[0]]:
			best = result
		elif result[0] and result[0] == best[0]:
			best[2].update(result[2])
	return best


def _kills_ue_pid(segment, snap):
	if not snap:
		return False
	for num in re.findall(r'(?:-Id|/PID|-p|\bkill(?:\s+-\d+)?)\s+(\d+)', segment, re.I):
		p = snap.procs.get(int(num))
		if p and (p.name in UE_NAMES or named_like_game(p.name)) and ue_process_kind(p.name, snap.cmd(p.pid)):
			return True
	return False


def classify_tool(tool, tool_input, snap=None):
	if tool.startswith('mcp__unreal'):
		blob = json.dumps(tool_input or {})
		if 'IsPIERunning' in blob or not tool.endswith('call_tool'):
			return ('inspect', None, {})
		flags = {}
		if RX_PIE_START.search(blob):
			flags['pie_start'] = True
		if RX_PIE_STOP.search(blob):
			flags['pie_stop'] = True
		return ('editor', 'Editor', flags)
	if tool in ('Bash', 'PowerShell'):
		return classify_command((tool_input or {}).get('command') or '', snap)
	return (None, None, {})


# ---------------------------------------------------------------- acquisition

def owner_long_grants(db, owner):
	return [g for g in db.execute("SELECT * FROM grants WHERE owner=? AND state IN ('active','releasing','queued') ORDER BY seq", (owner,)).fetchall()
		if g['kind'] in LONG_KINDS and not g['implicit']]


def request(db, snap, owner, kind, purpose, requirements='', minutes=15, grant_id=None, keep=False):
	"""Create or reuse this owner's grant. Waiting while holding a different grant is refused: that is how deadlocks form."""
	need = KIND_RESOURCES[kind]
	mine = [g for g in owner_long_grants(db, owner) if g['state'] != 'releasing']
	if grant_id:
		mine = [g for g in mine if g['id'].startswith(grant_id)] or mine
	for g in mine:
		held = grant_resources(g)
		if covers(held, need):
			return g, ''
		if g['state'] == 'queued':
			db.execute('UPDATE grants SET kind=?, resources=?, purpose=? WHERE id=?', (kind, json.dumps(need), purpose or g['purpose'], g['id']))
			return db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone(), ''
		ok, why = grantable(db, snap, need, owner)
		if not ok:
			return None, ('you hold %s grant %s and are asking for %s, which is not free (%s). Waiting while holding a grant '
				'is refused because that is how deadlocks form: finish %s first, or carry on with it.' % (g['kind'], g['id'][:8], kind, why, g['id'][:8]))
		merged = dict(held)
		for r, m in need.items():
			if merged.get(r) != 'X':
				merged[r] = m
		db.execute('UPDATE grants SET resources=?, kind=? WHERE id=?', (json.dumps(merged), kind if need.get('lane') == 'X' else g['kind'], g['id']))
		return db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone(), ''
	g = new_grant(db, owner, kind, purpose, requirements, minutes)
	if keep:
		db.execute('UPDATE grants SET keep_processes=1 WHERE id=?', (g['id'],))
	journal(owner, 'queue', '%s %s %s' % (g['id'][:8], kind, purpose[:100]))
	return g, ''


def try_activate(db, snap, g):
	if g['state'] == 'active':
		return g, ''
	ok, why = grantable(db, snap, grant_resources(g), g['owner'], g['seq'])
	if not ok:
		return None, why
	return activate(db, g, 'acquired'), ''


def queue_position(db, g):
	pos = 1
	for q in queued_grants(db):
		if q['id'] == g['id']:
			return pos
		if conflicts(grant_resources(q), grant_resources(g)):
			pos += 1
	return pos


# ---------------------------------------------------------------- tasks & resurrection

def resurrect(db, snap):
	if SANDBOX or meta_get(db, 'resurrect', 'on') != 'on':
		return
	if db.execute("SELECT COUNT(*) c FROM tasks WHERE state='resuming'").fetchone()['c']:
		return
	# A session already running again (the user restored it, e.g. from Agent Manager after a restart) is never relaunched:
	# that was the same agent running twice. It takes its task back on its next hook (adopt_resumed_session).
	running = running_sessions(snap)
	candidates = db.execute("""SELECT * FROM tasks WHERE state='orphaned' AND resume_count<? AND session_id!='' AND updated>?
		ORDER BY updated DESC""", (RESUME_MAX_ATTEMPTS, now() - RESUME_TASK_MAX_AGE)).fetchall()
	task = next((t for t in candidates if t['session_id'] not in running), None)
	if not task:
		return
	claude = _find_claude_cli()
	if not claude:
		return
	os.makedirs(RESUME_DIR, exist_ok=True)
	log_path = os.path.join(RESUME_DIR, '%s-%d.log' % (task['id'][:8], task['resume_count'] + 1))
	tid = task['id'][:8]
	prompt = ('The orchestrator restarted you: your previous process died in the middle of task %s ("%s"). '
		'Next step you recorded: %s. Handoff notes: %s. Files: %s. '
		'Check the workspace state first (`%s status`, `%s task list`), then carry the task through to verified completion '
		'and close it with `%s task done %s --evidence "..."`. If it now needs a decision from the user, record '
		'`%s task block %s --on user --why "..."` and stop.') % (
		tid, task['title'], task['next_step'] or '(none recorded)', task['handoff'] or '(none)', task['files'] or '(none)',
		ORCH_CMD, ORCH_CMD, ORCH_CMD, tid, ORCH_CMD, tid)
	args = [claude, '-p', '--resume', task['session_id'], '--permission-mode', 'auto', prompt]
	try:
		with open(log_path, 'w', encoding='utf-8') as log:
			subprocess.Popen(args, cwd=PROJECT, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, creationflags=DETACHED_FLAGS)
	except OSError as exc:
		journal(task['owner'], 'resume', 'task %s: launch failed: %s' % (tid, exc))
		return
	with Tx(db):
		db.execute("UPDATE tasks SET state='resuming', resume_count=resume_count+1, resumed_at=? WHERE id=?", (now(), task['id']))
	journal(task['owner'], 'resume', 'task %s: relaunched session %s headless (log %s)' % (tid, task['session_id'], log_path))


def _find_claude_cli():
	local = os.path.expanduser(os.path.join('~', '.local', 'bin', 'claude.exe'))
	if os.path.exists(local):
		return local
	from shutil import which
	return which('claude')


def running_sessions(snap):
	"""Session ids a live Claude process is running, from Claude Code's own registry (~/.claude/sessions/<pid>.json),
	which a process writes when it starts, before its first hook."""
	ids = set()
	try:
		names = os.listdir(CLAUDE_SESSIONS_DIR)
	except OSError:
		return ids
	for name in names:
		if not name.endswith('.json'):
			continue
		try:
			with open(os.path.join(CLAUDE_SESSIONS_DIR, name), encoding='utf-8') as f:
				entry = json.load(f)
		except (OSError, ValueError):
			continue
		if not isinstance(entry, dict):
			continue
		pid, session_id = entry.get('pid'), entry.get('sessionId')
		if isinstance(pid, int) and session_id and pid in snap.procs:
			ids.add(session_id)
	return ids


def adopt_resumed_session(db, snap, owner, session_id):
	if not session_id:
		return
	n = db.execute("UPDATE tasks SET owner=?, state='in_progress' WHERE session_id=? AND state IN ('resuming','orphaned') AND owner!=?",
		(owner, session_id, owner)).rowcount
	if n:
		journal(owner, 'resume', 'adopted %d task(s) of session %s' % (n, session_id))
	for old in db.execute('SELECT owner, pid, ticks FROM owners WHERE session_id=? AND owner!=?', (session_id, owner)).fetchall():
		if not snap.alive(old['pid'], old['ticks']):
			adopt_dead_owner_mail(db, old['owner'], owner)


def adopt_dead_owner_mail(db, dead, owner):
	"""An owner id holds a process id, so a restart strands a resumed session's channels and mail on the dead one. Called on every hook, so
	what still reaches the dead id later (a reply to its address) follows too."""
	messages = db.execute('UPDATE inbox SET owner=? WHERE owner=? AND delivered!=1', (owner, dead)).rowcount
	channels = 0
	for row in db.execute('SELECT key, value FROM meta WHERE key LIKE ?', (CHANNEL_MEMBERS_KEY + '%',)).fetchall():
		members = json.loads(row['value'] or '[]')
		if dead in members:
			meta_set(db, row['key'], json.dumps(list(dict.fromkeys(owner if m == dead else m for m in members))))
			channels += 1
	if messages or channels:
		journal(owner, 'resume', 'took over %d message(s) and %d channel(s) from %s' % (messages, channels, dead))


def registry_session_id(pid):
	"""A Claude with no hooks learns its session id only from Claude Code's own registry."""
	try:
		with open(os.path.join(CLAUDE_SESSIONS_DIR, '%d.json' % pid), encoding='utf-8') as f:
			entry = json.load(f)
	except (OSError, ValueError):
		return ''
	if not isinstance(entry, dict) or entry.get('pid') != pid or not isinstance(entry.get('sessionId'), str):
		return ''
	return entry['sessionId']


def adopt_hookless_session(db, snap, row):
	kind, pid, _ = parse_owner_pid(row['owner'])
	if kind != 'claude' or row['hooked']:
		return
	session_id = row['session_id'] or registry_session_id(pid)
	if session_id and not row['session_id']:
		db.execute('UPDATE owners SET session_id=? WHERE owner=?', (session_id, row['owner']))
	adopt_resumed_session(db, snap, row['owner'], session_id)


# ---------------------------------------------------------------- mirror & daemon

def write_mirror(db, snap):
	def entry(g):
		o = get_owner(db, g['owner'])
		ev = json.loads(g['evidence'] or '{}')
		return {'id': g['id'], 'owner': g['owner'], 'kind': g['kind'], 'phase': g['phase'], 'state': g['state'],
			'purpose': g['purpose'] or g['last_command'], 'requirements': g['requirements'], 'minutes': g['minutes'],
			'queuedAt': iso(g['queued_at']), 'acquiredAt': iso(g['acquired_at']), 'startedAt': iso(g['started_at']),
			'updatedAt': iso(g['updated_at']), 'liveness': owner_state(o, snap) if o else 'unknown', 'implicit': bool(g['implicit']),
			'codeEvidence': ev.get('codeEvidence', ''), 'blueprintEvidence': ev.get('blueprintEvidence', ''),
			'idleEvidence': ev.get('idleEvidence', ''), 'editorPid': g['editor_pid'], 'map': g['map']}
	holder = lane_holder(db)
	state = {
		'version': 1, 'orchestrator': 'read-only mirror of Saved/Orchestration/orch.db; change state only through %s' % ORCH_CMD,
		'active': entry(holder) if holder else None,
		'grants': [entry(g) for g in active_grants(db)],
		'queue': [entry(q) for q in queued_grants(db)],
		'history': [dict(entry(g), releaseReason=g['release_reason'], releasedAt=iso(g['released_at']))
			for g in db.execute("SELECT * FROM grants WHERE state='done' AND implicit=0 ORDER BY released_at DESC LIMIT 30")],
	}
	body = json.dumps(state, indent=2)
	write_status(db, snap)
	try:
		with open(MIRROR_PATH, 'r', encoding='utf-8-sig') as f:
			if f.read() == body:
				return
	except OSError:
		pass
	replace_file(MIRROR_PATH, body)


def replace_file(path, body):
	"""A reader holding the file open makes the replace fail; the next write catches up."""
	tmp = path + '.%d.tmp' % os.getpid()
	try:
		with open(tmp, 'w', encoding='utf-8') as f:
			f.write(body)
		os.replace(tmp, path)
	except OSError:
		try:
			os.remove(tmp)
		except OSError:
			pass


def display_name(owner, o):
	"""What a person calls this agent; every reader of the feed shows this one name."""
	if owner.startswith(ASTRA_OWNER_PREFIX):
		return ASTRA_NAME_PREFIX + owner[len(ASTRA_OWNER_PREFIX):]
	title = ((o['title'] if o else '') or '').strip()
	if title in MASTER_AGENT_SESSION_NAMES:
		return MASTER_AGENT_NAME
	if title:
		return title
	if owner.startswith(NAMED_OWNER_PREFIX):
		return owner[len(NAMED_OWNER_PREFIX):]
	kind, pid, _ = parse_owner_pid(owner)
	return '%s %d' % (kind.capitalize(), pid) if kind else owner


def owner_is_alive(o, state):
	"""An `agent:` owner has no process to check, so one quiet past CLI_SILENCE counts as gone; its next call brings it back."""
	if state in ('dead', 'ended', 'unknown'):
		return False
	return bool(o['pid']) or state != 'silent'


def live_sessions(owners, states):
	return {o['session_id']: k for k, o in owners.items() if o['session_id'] and owner_is_alive(o, states.get(k, 'unknown'))}


def task_carrier(task, o, states, sessions):
	"""The live owner a task is with, None if nobody. A resumed session runs under a new owner id and a `blocked` task stays on the old one, so the session decides."""
	if o and owner_is_alive(o, states.get(task['owner'], 'unknown')):
		return task['owner']
	return sessions.get(task['session_id'] or (o['session_id'] if o else ''))


def read_editor_report(pid):
	"""What an editor's own plugin last wrote about its PIE (FOrchestratorStatus::ReportPieState), however old: pie, at, pieSince, pieByInput."""
	try:
		with open(os.path.join(STATE_DIR, 'editors', '%d.json' % pid), encoding='utf-8-sig') as f:
			report = json.load(f)
	except (OSError, ValueError):
		return None
	if not isinstance(report, dict) or report.get('pid') != pid or not isinstance(report.get('pie'), bool) or not isinstance(report.get('at'), (int, float)):
		return None
	return report


def editor_report(pid, t):
	"""The report while the plugin keeps writing it. It answers while an agent holds the lane, when the probe must not talk over the agent's session,
	and a frozen editor stops writing it."""
	report = read_editor_report(pid)
	return report if report and t - report['at'] < EDITOR_REPORT_TTL else None


def editor_pie_state(pid, probe, t):
	"""True or False where something can tell, None where nothing can, never a guess: a -nullrhi editor runs without the plugin and on its own multicast group."""
	report = editor_report(pid, t)
	if report:
		return report['pie']
	is_answer = probe.get('editor_pid') == pid and probe.get('reachable', True) and t - probe.get('at', 0) < PIE_PROBE_TTL * 3
	return bool(probe.get('running')) if is_answer else None


def editor_user(db, snap, pid, is_main, h, holder):
	"""Who is on an editor, '' when nothing says. The lane is the main editor."""
	if is_main:
		if h.get('on'):
			return 'user'
		return holder['owner'] if holder else ''
	row = db.execute('SELECT owner FROM procs WHERE pid=? AND gone_at=0', (pid,)).fetchone()
	if row and row['owner']:
		return row['owner']
	# A detached launch loses its parent; whoever held the lane when it started owns it, the rule attribute_processes applies.
	return holder['owner'] if holder and holder['acquired_at'] - 5 <= _ticks_to_epoch(snap.start(pid)) else ''


def grant_activity(db, snap, g, o, ue, pie):
	"""What a grant is doing, in plain words, from what the machine shows. `phase` only records the PIE protocol, and 'preparing' read as waiting for the editor."""
	if g['state'] == 'queued':
		return 'queued'
	if g['state'] == 'releasing':
		return 'finishing up'
	pid = o['pid'] if o else 0
	kinds = {r['kind'] for r in live_procs(db, snap, g['id'])}
	if pid:
		kinds.update(kind for p, kind in ue if any(a.pid == pid for a in snap.ancestors(p.pid)[1:]))
	is_lane = grant_resources(g).get('lane') == 'X'
	if 'ubt' in kinds or (is_lane and g['kind'] == 'Build' and any(kind == 'ubt' for _, kind in ue)):
		return 'building'
	if 'p4-sync' in kinds:
		return 'syncing from p4'
	if 'commandlet' in kinds:
		return 'running a commandlet'
	if 'game' in kinds:
		return 'testing'
	if is_lane and (pie or (pie is None and g['pie_started'])):
		return 'in PIE'
	if is_lane and (owner_runs_remote_python(snap, pid) or (o and o['open_tools'] and (o['open_desc'] or '').startswith('mcp__unreal'))):
		return 'using the editor'
	return (ACTIVITY_COMMAND if g['implicit'] else ACTIVITY_HOLDING).get(g['kind'], g['kind'])


def owner_runs_remote_python(snap, pid):
	if not pid:
		return False
	return any(p.name in ('python.exe', 'pythonw.exe', 'py.exe') and 'ue_python' in snap.cmd(p.pid).lower()
		and any(a.pid == pid for a in snap.ancestors(p.pid)[1:]) for p in list(snap.procs.values()))


def feed_summary(h, lane, queue, occ, editors, agents):
	"""The one line and the dot the toolbar shows. An occupant with nobody queued behind it is the user's own PIE or build: no agent is waiting, so it stays green.
	The plugin ages the hold's line itself and says which window a PIE is in, the two things this file cannot know."""
	lane_queue = [q for q in queue if q['lane']]
	names = ([lane['displayName']] if lane else []) + [q['displayName'] for q in lane_queue]
	pauses = sorted(set(names), key=names.index)
	pie_pid = next((e['pid'] for e in editors if e['pie']), 0)
	if h.get('on'):
		state, dot, text = 'hold', 'blue', 'You have the editor'
	elif lane:
		state, dot, text = 'lane', 'orange', '%s - %s' % (lane['displayName'], lane['activity'])
	elif lane_queue:
		state, dot = 'waiting', 'orange'
		text = '%d %s waiting for the editor' % (len(lane_queue), 'agent' if len(lane_queue) == 1 else 'agents')
	elif pie_pid:
		state, dot, text = 'pie', 'green', 'Your PIE - no agent waiting'
	elif occ:
		state, dot, text = 'occupied', 'green', 'Editor busy outside the orchestrator - no agent waiting'
	else:
		working = sum(1 for a in agents if a['working'])
		state, dot = 'free', 'green'
		text = 'Editor free - %d %s working' % (working, 'agent' if working == 1 else 'agents')
	if any(e['frozen'] for e in editors):
		dot = 'red'
	return {'state': state, 'dot': dot, 'text': text, 'piePid': pie_pid, 'takePauses': [] if h.get('on') else pauses}


def write_status(db, snap):
	"""The feed the editor's orchestrator panel polls (Plugins/AgentOrchestrator). Times are Unix seconds; the panel ages them."""
	t = now()
	owners = {o['owner']: o for o in db.execute('SELECT * FROM owners WHERE dead_at=0').fetchall()}
	states = {k: owner_state(o, snap, t) for k, o in owners.items()}
	h = hold_get(db)
	grants = active_grants(db)
	queue = queued_grants(db)
	holder = lane_holder(db)
	probe = json.loads(meta_get(db, 'pie_probe', '{}') or '{}')
	hung = json.loads(meta_get(db, EDITOR_HUNG_KEY, '{}') or '{}')
	ue = ue_processes(snap)
	rows = editor_rows(snap)
	main_pid = next((pid for pid, rendering in rows if rendering), 0)
	editors = []
	for pid, rendering in rows:
		user = editor_user(db, snap, pid, pid == main_pid, h, holder)
		since = hung.get(str(pid), 0)
		editors.append({'pid': pid, 'role': 'main' if pid == main_pid else 'second' if rendering else 'headless', 'rendering': rendering,
			'pie': editor_pie_state(pid, probe, t), 'onIt': user, 'onItName': 'you' if user == 'user' else display_name(user, owners.get(user) or get_owner(db, user)) if user else '',
			'notRespondingSince': since, 'frozen': bool(since) and t - since >= FROZEN_EDITOR_SECONDS})
	main_pie = next((e['pie'] for e in editors if e['pid'] == main_pid), None)
	main_report = editor_report(main_pid, t) if main_pid else None

	def grant(g):
		o = owners.get(g['owner']) or get_owner(db, g['owner'])
		due = lane_turn_due(db, g)
		is_lane = grant_resources(g).get('lane') == 'X'
		return {'id': g['id'], 'owner': g['owner'], 'title': (o['title'] if o else '') or '', 'displayName': display_name(g['owner'], o), 'sessionId': (o['session_id'] if o else '') or '',
			'liveness': states.get(g['owner']) or (owner_state(o, snap, t) if o else 'unknown'), 'kind': g['kind'], 'state': g['state'],
			'phase': g['phase'], 'activity': grant_activity(db, snap, g, o, ue, main_pie), 'purpose': g['purpose'] or '', 'lastCommand': g['last_command'] or '', 'implicit': bool(g['implicit']),
			'minutes': g['minutes'], 'queuedAt': g['queued_at'], 'acquiredAt': g['acquired_at'], 'startedAt': g['started_at'],
			'pieStarted': bool(g['pie_started']), 'lane': grant_resources(g).get('lane') or '', 'releaseReason': g['release_reason'] or '',
			'editorPid': (g['editor_pid'] if any(g['editor_pid'] == pid for pid, _ in rows) else main_pid) if is_lane else 0,
			'turnDueAt': due, 'turnBackAt': due + LANE_TURN_HARD_STOP if due else 0,
			'ownsPie': is_lane and g['state'] == 'active' and pie_verdict(g, main_report) == 'ours'}

	agents = []
	for key, o in owners.items():
		st = states[key]
		if st in ('dead', 'ended'):
			continue
		mine = [g['id'] for g in grants if g['owner'] == key] + [q['id'] for q in queue if q['owner'] == key]
		agents.append({'owner': key, 'kind': o['kind'], 'pid': o['pid'], 'sessionId': o['session_id'] or '', 'title': o['title'] or '', 'displayName': display_name(key, o),
			'cwd': o['cwd'] or '', 'transcript': o['transcript'] or '', 'state': st, 'stateText': AGENT_STATE_WORDS.get(st, st), 'working': st in SHOWN_WORKING_STATES, 'inTurn': bool(o['in_turn']),
			'lastActive': max(o['last_active'], o['last_shell_seen']), 'openTool': o['open_desc'] if o['open_tools'] else '',
			'openSince': o['open_since'] if o['open_tools'] else 0, 'waitingOnUser': bool(o['user_wait_since']), 'grants': mine,
			'holdsLane': bool(holder and holder['owner'] == key), 'paused': key in (h.get('paused') or []) and bool(h.get('on'))})
	agents.sort(key=lambda a: (not a['holdsLane'], -a['lastActive']))
	gone = {o['owner']: o for o in db.execute('SELECT * FROM owners WHERE dead_at!=0').fetchall()}
	sessions = live_sessions(owners, states)
	live_tasks, left_over = [], []
	for r in db.execute("SELECT * FROM tasks WHERE state NOT IN ('done','cancelled','expired') ORDER BY updated DESC").fetchall():
		o = owners.get(r['owner']) or gone.get(r['owner'])
		carrier = task_carrier(r, o, states, sessions)
		is_alive = bool(carrier) or r['state'] == 'resuming'
		(live_tasks if is_alive else left_over).append({'id': r['id'], 'owner': r['owner'], 'ownerName': display_name(carrier or r['owner'], owners.get(carrier) or o),
			'ownerAlive': is_alive, 'title': r['title'] or '', 'state': r['state'], 'nextStep': r['next_step'] or '',
			'blockedOn': r['blocked_on'] or '', 'why': r['why'] or '', 'updated': r['updated']})
	daemon_pid = int(meta_get(db, 'daemon_pid', '0') or 0)
	lane = grant(holder) if holder else None
	queue_entries = [dict(grant(q), position=queue_position(db, q)) for q in queue]
	occ = occupants(db, snap, {'lane': 'X'})
	state = {
		'version': 1, 'generatedAt': t, 'writerPid': os.getpid(),
		'daemon': {'pid': daemon_pid, 'alive': bool(daemon_pid and snap.alive(daemon_pid, int(meta_get(db, 'daemon_ticks', '0') or 0)))},
		'hold': {'on': bool(h.get('on')), 'by': h.get('by') or '', 'reason': h.get('reason') or '', 'since': h.get('since') or 0,
			'source': h.get('source') or '', 'paused': h.get('paused') or [], 'releasedAt': h.get('released_at') or 0,
			'pausedNames': [display_name(p, owners.get(p) or get_owner(db, p)) for p in h.get('paused') or []]},
		'lane': lane,
		'grants': [grant(g) for g in grants],
		'queue': queue_entries,
		'occupants': occ,
		'pie': {'running': bool(probe.get('running')), 'probedAt': probe.get('at') or 0},
		'agents': agents,
		'tasks': live_tasks[:FEED_TASKS] + left_over[:FEED_LEFTOVER_TASKS],
		'tasksLeftOver': len(left_over),
		'editors': editors,
		'summary': feed_summary(h, lane, queue_entries, occ, editors, agents),
	}
	replace_file(STATUS_PATH, json.dumps(state, indent=1))


def tick(db, snap=None, force=False):
	snap = snap or SNAPSHOT()
	with Tx(db):
		reconcile(db, snap, force)
	settle(db, snap)
	write_mirror(db, snap)
	return snap


def ensure_daemon(db, snap):
	if SANDBOX or os.environ.get('ORCH_NO_DAEMON'):
		return
	pid = int(meta_get(db, 'daemon_pid', '0') or 0)
	ticks = int(meta_get(db, 'daemon_ticks', '0') or 0)
	if pid and snap.alive(pid, ticks):
		return
	exe = sys.executable
	pyw = os.path.join(os.path.dirname(exe), 'pythonw.exe')
	if os.path.exists(pyw):
		exe = pyw
	for extra in (BREAKAWAY_FROM_JOB, 0):
		try:
			subprocess.Popen([exe, os.path.abspath(__file__), 'daemon'], cwd=PROJECT, stdin=subprocess.DEVNULL,
				stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=DETACHED_FLAGS | extra, close_fds=True)
			return
		except OSError:
			continue


def daemon():
	mutex = None
	if os.name == 'nt':
		import hashlib
		name = 'Local\\AgentOrchDaemon-' + hashlib.sha1(os.path.normcase(STATE_DIR).encode('utf-8')).hexdigest()[:16]
		mutex = _k32.CreateMutexW(None, False, name)
		if ctypes.get_last_error() == 183:
			return 0
	code_mtime = os.path.getmtime(os.path.abspath(__file__))
	db = connect()
	snap = SNAPSHOT()
	with Tx(db):
		meta_set(db, 'daemon_pid', str(os.getpid()))
		meta_set(db, 'daemon_ticks', str(snap.start(os.getpid())))
	journal('', 'daemon', 'started pid %d' % os.getpid())
	titles = {}
	while True:
		try:
			refresh_titles(db, snap, titles)
			refresh_editor_watch(db, snap, now())
			snap = tick(db, force=True)
			if not lane_holder(db) and editor_pids(snap):
				refresh_pie_probe(db, snap)
				settle_unregistered_pie(db, SNAPSHOT())
			resurrect(db, snap)
		except Exception as exc:
			journal('', 'daemon', 'error: %r' % (exc,))
		time.sleep(DAEMON_PERIOD)
		try:
			if os.path.getmtime(os.path.abspath(__file__)) != code_mtime:
				journal('', 'daemon', 'code changed; exiting so the next hook starts the new version')
				return 0
		except OSError:
			pass
	return mutex


# ---------------------------------------------------------------- hooks

def hook_output(obj):
	if obj:
		sys.stdout.write(json.dumps(obj))
	return 0


def pre_deny(reason):
	return {'hookSpecificOutput': {'hookEventName': 'PreToolUse', 'permissionDecision': 'deny', 'permissionDecisionReason': reason}}


def context(event, text):
	return {'hookSpecificOutput': {'hookEventName': event, 'additionalContext': text}}


def drain_inbox(db, owner):
	rows = db.execute('SELECT * FROM inbox WHERE owner=? AND delivered!=1 ORDER BY id', (owner,)).fetchall()
	if rows:
		db.execute('UPDATE inbox SET delivered=1 WHERE owner=? AND delivered!=1', (owner,))
	return ['[orchestrator] ' + r['text'] for r in rows]


def messages_for_waiter(db, owner):
	"""Wakes a foreground acquire once per message (delivered=2). The next hook or `inbox` still delivers it whole: most waiters pipe their
	output through tail or head, so what is printed here may never be read."""
	rows = db.execute('SELECT * FROM inbox WHERE owner=? AND delivered=0 ORDER BY id', (owner,)).fetchall()
	if rows:
		db.execute('UPDATE inbox SET delivered=2 WHERE owner=? AND delivered=0', (owner,))
	return ['[orchestrator] ' + r['text'].encode('ascii', 'replace').decode('ascii') for r in rows]


def resolve_recipient(db, target):
	if get_owner(db, target):
		return target
	wanted = (target or '').strip().lower()
	named = [r for r in db.execute('SELECT owner, title, ended FROM owners ORDER BY last_active DESC').fetchall()
		if (r['title'] or '').strip().lower() == wanted]
	live = [r for r in named if not r['ended']]
	pick = live or named
	return pick[0]['owner'] if pick else None


def named_agent(db, work, sender):
	"""`msg --to astra-x` is the agent `--as astra-x`, unless that agent is on a channel of the name: then the channel is real and reaches it too."""
	agent = 'agent:' + work
	if agent == sender or not get_owner(db, agent) or agent in json.loads(meta_get(db, CHANNEL_MEMBERS_KEY + work, '[]') or '[]'):
		return None
	return agent


def join_channel(db, work, owner):
	members = json.loads(meta_get(db, CHANNEL_MEMBERS_KEY + work, '[]') or '[]')
	joined = owner not in members
	if joined:
		members.append(owner)
		meta_set(db, CHANNEL_MEMBERS_KEY + work, json.dumps(members))
	return members, joined


def drain_channel_backlog(db, work, reader):
	rows = db.execute('SELECT * FROM inbox WHERE owner=? AND delivered=0 ORDER BY id', (CHANNEL_BACKLOG_PREFIX + work,)).fetchall()
	theirs = [r for r in rows if not r['text'].split('\n', 1)[0].endswith('[%s]:' % reader)]
	for r in theirs:
		db.execute('UPDATE inbox SET delivered=1 WHERE id=?', (r['id'],))
	return ['[orchestrator] ' + r['text'] for r in theirs]


# Agents talk to each other through the inbox: a Claude session's hooks show its messages on its next tool call, a client
# without hooks (Codex) reads them with `inbox`, and `inbox --wait` blocks until one arrives, for a back-and-forth.
def cmd_msg(db, snap, args, owner):
	text = args.text or ''
	if args.file:
		with open(args.file, encoding='utf-8-sig') as f:
			text = f.read()
	if not text.strip():
		print('ORCHESTRATOR: msg needs some text, or --file <path>.', file=sys.stderr)
		return 2
	with Tx(db):
		me = get_owner(db, owner)
		name = (me['title'] or '').strip() if me else ''
		sender = '%s [%s]' % (name[:40], owner)
		work = args.to.strip().lower()
		target = resolve_recipient(db, args.to) or named_agent(db, work, owner)
		if target:
			notify(db, target, 'Message from %s:\n%s\n(reply with: %s msg --to %s "...")' % (sender, text.strip(), ORCH_CMD, owner))
		elif parse_owner_pid(args.to)[0] or work.startswith('agent:'):
			print('ORCHESTRATOR: no agent %s.' % args.to, file=sys.stderr)
			return 2
		else:
			members, joined = join_channel(db, work, owner)
			others = [m for m in members if m != owner]
			body = 'Message on %s from %s:\n%s\n(reply with: %s msg --to %s "...", wait with: %s inbox %s --wait 540)' % (
				work, sender, text.strip(), ORCH_CMD, work, ORCH_CMD, work)
			for member in others or [CHANNEL_BACKLOG_PREFIX + work]:
				notify(db, member, body)
			target = ', '.join(others) if others else 'the %s backlog%s' % (work, ': a new channel, no agent has that name' if joined else '')
	journal(owner, 'msg', '-> %s (%d chars)' % (target, len(text)))
	print('SENT to %s' % target)
	return 0


def log_hook_fields(event, payload):
	try:
		if os.path.exists(HOOK_FIELDS_PATH) and os.path.getsize(HOOK_FIELDS_PATH) > 256 * 1024:
			return
		with open(HOOK_FIELDS_PATH, 'a', encoding='utf-8') as f:
			f.write('%s %s %s\n' % (iso(now()), event, ','.join(sorted(payload.keys()))))
	except OSError:
		pass


def human_prompt(entry):
	"""The prompt a transcript's user line holds, '' for anything a person did not type: Agent Manager's IsHumanPrompt, so both call a session the same."""
	origin = entry.get('origin')
	is_from_elsewhere = isinstance(origin, dict) and origin.get('kind') not in (None, 'user', 'human')
	if entry.get('type') != 'user' or entry.get('isSidechain') or entry.get('isMeta') or 'toolUseResult' in entry or is_from_elsewhere:
		return ''
	message = entry.get('message')
	content = message.get('content') if isinstance(message, dict) else None
	if isinstance(content, list):
		blocks = [b for b in content if isinstance(b, dict)]
		if any(b.get('type') == 'tool_result' for b in blocks):
			return ''
		content = next((b.get('text') for b in blocks if b.get('type') == 'text'), '')
	if not isinstance(content, str) or content.lstrip().startswith(WRAPPED_PROMPT_PREFIXES):
		return ''
	return ' '.join(content.split())[:TITLE_PROMPT_MAX]


def read_title_line(line, titles):
	is_title = b'"custom-title"' in line or b'"agent-name"' in line or b'"ai-title"' in line
	if not is_title and (titles['prompt'] or b'"user"' not in line):
		return
	try:
		entry = json.loads(line.decode('utf-8', 'replace'))
	except ValueError:
		return
	if not isinstance(entry, dict):
		return
	kind = entry.get('type')
	field = {'custom-title': 'customTitle', 'agent-name': 'agentName', 'ai-title': 'aiTitle'}.get(kind) if isinstance(kind, str) else None
	if field:
		value = entry.get(field)
		if isinstance(value, str) and value.strip():
			titles['ai' if kind == 'ai-title' else 'name'] = value.strip()
	elif not titles['prompt']:
		titles['prompt'] = human_prompt(entry)


def transcript_titles(path, seen=None):
	"""A session's names as its transcript records them, reading only what was added since `seen`. A resumed session keeps its transcript, so its first
	prompt is here too, where a prompt from the hook would call it 'continue'."""
	titles = seen or {'offset': 0, 'name': '', 'ai': '', 'prompt': ''}
	if not path or not isinstance(path, str):
		return titles
	try:
		with open(path, 'rb') as f:
			f.seek(0, os.SEEK_END)
			size = f.tell()
			if size < titles['offset']:
				titles = {'offset': 0, 'name': '', 'ai': '', 'prompt': ''}
			if size == titles['offset']:
				return titles
			f.seek(titles['offset'])
			for line in f:
				if not line.endswith(b'\n'):
					break
				titles['offset'] += len(line)
				read_title_line(line, titles)
	except OSError:
		pass
	return titles


def registry_entry(pid):
	try:
		with open(os.path.join(CLAUDE_SESSIONS_DIR, '%d.json' % pid), encoding='utf-8') as f:
			entry = json.load(f)
	except (OSError, ValueError):
		return {}
	return entry if isinstance(entry, dict) and entry.get('pid') == pid else {}


def registry_user_name(entry):
	"""Claude derives a name (myproject-8b) for every session; only one the user gave means anything."""
	name = entry.get('name') if entry.get('nameSource') == 'user' else ''
	return name.strip() if isinstance(name, str) else ''


def registry_transcript(entry):
	"""A Claude with no hooks never says where its transcript is; the registry holds what its path is made from."""
	cwd, session = entry.get('cwd'), entry.get('sessionId')
	if not cwd or not session or not isinstance(cwd, str) or not isinstance(session, str):
		return ''
	return os.path.join(os.path.dirname(CLAUDE_SESSIONS_DIR), 'projects', re.sub(r'[^A-Za-z0-9]', '-', cwd), session + '.jsonl')


def refresh_titles(db, snap, seen):
	"""Keeps owners.title on one rule, Agent Manager's (ClaudeSource.SessionName): the user's own rename, then the generated ai-title, then the first prompt.
	A title an agent gave itself (`orch.cmd name`, a hook's session_title) yields only to the user's rename. Runs in the daemon, so a hook never reads a transcript for it."""
	for o in db.execute("SELECT * FROM owners WHERE dead_at=0 AND kind='claude' AND pid>0").fetchall():
		if not snap.alive(o['pid'], o['ticks']):
			continue
		entry = registry_entry(o['pid'])
		path = o['transcript'] or registry_transcript(entry)
		seen[path] = titles = transcript_titles(path, seen.get(path))
		named = registry_user_name(entry) or titles['name']
		title = (named or ('' if o['title_fixed'] else titles['ai'] or titles['prompt']) or o['title'] or '')[:TITLE_MAX]
		cwd = o['cwd'] or (entry.get('cwd') if isinstance(entry.get('cwd'), str) else '') or ''
		if title != (o['title'] or '') or path != (o['transcript'] or '') or cwd != (o['cwd'] or ''):
			with Tx(db):
				db.execute('UPDATE owners SET title=?, transcript=?, cwd=? WHERE owner=?', (title, path, cwd, o['owner']))


def hook(event, payload):
	log_hook_fields(event, payload)
	snap = SNAPSHOT()
	kind, proc = find_agent_process(snap)
	if not proc:
		return 0
	owner = owner_key(kind, snap, proc)
	tool = payload.get('tool_name') or ''
	tin = payload.get('tool_input') or {}
	category, grant_kind, flags = (None, None, {})
	if event == 'PreToolUse':
		category, grant_kind, flags = classify_tool(tool, tin, snap)

	db = connect()
	t = now()
	with Tx(db):
		ensure_owner(db, owner, kind, proc.pid, snap.start(proc.pid))
		sid = payload.get('session_id') or ''
		touch_owner(db, owner, hooked=1, session_id=sid, transcript=payload.get('transcript_path') or '')
		if event in ('PreToolUse', 'UserPromptSubmit', 'SessionStart'):
			adopt_resumed_session(db, snap, owner, sid)
		o = get_owner(db, owner)
		# Only UserPromptSubmit and SessionStart carry the session title; this prompt stands in until the transcript has one, and refresh_titles keeps it current.
		session_title = (payload.get('session_title') or '').strip()
		title = session_title
		if not title and not o['title']:
			titles = transcript_titles(payload.get('transcript_path'))
			title = titles['name'] or titles['ai'] or titles['prompt']
		if not title and event == 'UserPromptSubmit' and not o['title']:
			title = ' '.join((payload.get('prompt') or '').split())[:TITLE_PROMPT_MAX]
		if title and title != o['title']:
			db.execute('UPDATE owners SET title=?, title_fixed=? WHERE owner=?', (title[:TITLE_MAX], 1 if session_title else o['title_fixed'], owner))
		if payload.get('cwd') and payload['cwd'] != o['cwd']:
			db.execute('UPDATE owners SET cwd=? WHERE owner=?', (payload['cwd'], owner))
		if event == 'PreToolUse':
			touch_owner(db, owner, in_turn=1, open_tools=o['open_tools'] + 1, open_since=o['open_since'] if o['open_tools'] else t,
				open_desc=tool, user_wait_since=t if tool in USER_WAIT_TOOLS else o['user_wait_since'])
		elif event in ('PostToolUse', 'PostToolUseFailure'):
			touch_owner(db, owner, in_turn=1, open_tools=max(0, o['open_tools'] - 1), user_wait_since=0,
				open_since=t if o['open_tools'] > 1 else 0)
		elif event == 'UserPromptSubmit':
			touch_owner(db, owner, in_turn=1, open_tools=0, open_since=0, user_wait_since=0, ended=0)
		elif event == 'Stop':
			touch_owner(db, owner, in_turn=0, stopped_at=t, open_tools=0, open_since=0)
		elif event == 'Notification':
			ntype = '%s %s' % (payload.get('notification_type') or '', payload.get('message') or '')
			if 'permission' in ntype.lower():
				touch_owner(db, owner, user_wait_since=o['user_wait_since'] or t)
		elif event == 'SessionEnd':
			reason = payload.get('reason') or 'other'
			touch_owner(db, owner, ended=1, ended_reason=reason, in_turn=0, stopped_at=t)
			if reason in DELIBERATE_END_REASONS:
				db.execute("UPDATE tasks SET state='parked' WHERE owner=? AND state IN ('in_progress','blocked')", (owner,))
			else:
				db.execute("UPDATE tasks SET state='orphaned', orphaned_at=? WHERE owner=? AND state IN ('in_progress','blocked') AND blocked_on!='user'", (t, owner))
		elif event == 'SessionStart':
			touch_owner(db, owner, in_turn=1, ended=0)
		reconcile(db, snap, force=event in ('Stop', 'SessionEnd'))

	result = None
	try:
		if event == 'PreToolUse':
			result = hook_pre(db, snap, owner, tool, tin, category, grant_kind, flags)
			if result:
				with Tx(db):
					o = get_owner(db, owner)
					touch_owner(db, owner, open_tools=max(0, o['open_tools'] - 1))
		elif event in ('PostToolUse', 'PostToolUseFailure'):
			result = hook_post(db, snap, owner, tool, tin, payload)
		elif event == 'Stop':
			result = hook_stop(db, snap, owner, payload)
		elif event == 'SessionStart':
			result = context('SessionStart', session_brief(db, snap, owner))
		elif event == 'UserPromptSubmit':
			with Tx(db):
				msgs = drain_inbox(db, owner)
			# The user answering is the end of looking at what was staged for them.
			if release_handed_pie(db, snap, owner, 'the user sent the next prompt to the agent that staged it'):
				msgs.append('[orchestrator] Stopped the PIE you left running for the user (finish --keep-pie): they replied, so '
					'the editor is released. Stage it again (acquire, then finish --keep-pie) if they still need it.')
			if msgs:
				result = context('UserPromptSubmit', '\n'.join(msgs))
	finally:
		if event != 'PreToolUse':
			settle(db, snap)
		write_mirror(db, snap)
		ensure_daemon(db, snap)
	return hook_output(result)


def hook_pre(db, snap, owner, tool, tin, category, kind, flags):
	if category in (None, 'inspect', 'free'):
		return None
	command = (tin.get('command') or json.dumps(tin))[:400]
	if category == 'sync' and flags.get('p4verb') == 'revert':
		clash = revert_clash(db, snap, owner, flags.get('p4args') or [])
		if clash:
			return pre_deny('ORCHESTRATOR: refusing p4 revert - %s. Revert only files you changed yourself, by explicit path.' % clash)
	need = KIND_RESOURCES[kind]
	deadline = now() + (P4_WAIT if kind == 'P4' else IMPLICIT_WAIT)
	while True:
		with Tx(db):
			reconcile(db, snap, force=True)
		settle(db, snap)
		if need.get('lane') == 'X' and not lane_holder(db):
			refresh_pie_probe(db, snap)
		with Tx(db):
			decision = _pre_decide(db, snap, owner, tool, tin, need, kind, command, flags)
		if decision[0] == 'allow':
			return None
		if decision[0] == 'wait' and now() < deadline:
			time.sleep(2)
			snap = SNAPSHOT()
			continue
		return pre_deny(decision[1])


def _pre_decide(db, snap, owner, tool, tin, need, kind, command, flags):
	if need.get('lane'):
		h = hold_get(db)
		if h.get('on'):
			hold_note_paused(db, h, owner)
			return ('wait', hold_pause_text(db, h, owner))
	for g in db.execute("SELECT * FROM grants WHERE owner=? AND state='active' ORDER BY seq", (owner,)).fetchall():
		if covers(grant_resources(g), need):
			if lane_turn_overrun(db, g, now()):
				return ('deny', lane_turn_text(db, g, now()))
			if flags.get('pie_start'):
				db.execute("""UPDATE grants SET pie_started=1, started_at=?,
					phase=CASE WHEN phase IN ('preparing','ready') THEN 'running' ELSE phase END WHERE id=?""", (now(), g['id']))
			db.execute('UPDATE grants SET last_command=?, updated_at=? WHERE id=?', (command, now(), g['id']))
			return ('allow', '')
	for q in db.execute("SELECT * FROM grants WHERE owner=? AND state='queued' ORDER BY seq", (owner,)).fetchall():
		if covers(grant_resources(q), need):
			g, why = try_activate(db, snap, q)
			if g:
				return ('allow', '')
			return ('deny', 'ORCHESTRATOR: you are queued (#%d) for %s but it is not free yet: %s.\nYour command was NOT run. '
				'Run `%s acquire --id %s --until-granted` in the background and go by its exit code (0 granted, 2 dropped); do other work meanwhile.'
				% (queue_position(db, q), q['kind'], why, ORCH_CMD, q['id'][:8]))
	ok, why = grantable(db, snap, need, owner)
	if ok:
		g = new_grant(db, owner, kind, state='active', implicit=True, tool=tool, command=command)
		bound = kind == 'Test' or bool(tin.get('run_in_background'))
		db.execute('UPDATE grants SET process_bound=?, pie_started=? WHERE id=?', (1 if bound else 0, 1 if flags.get('pie_start') else 0, g['id']))
		journal(owner, 'implicit', '%s %s: %s' % (g['id'][:8], kind, command[:120]))
		return ('allow', '')
	holder = None
	for g in active_grants(db):
		if g['owner'] != owner and conflicts(need, grant_resources(g)):
			holder = g
	if kind == 'P4' or (holder is not None and (holder['implicit'] or holder['state'] == 'releasing')):
		return ('wait', why)
	return ('deny', 'ORCHESTRATOR: %s is not free - %s.\nYour command was NOT run. Queue for it and do other work while you wait:\n'
		'  %s acquire --kind %s --purpose "<agent/task: why>" --minutes <n> --wait 540\n'
		'Exit 0 is granted. Exit 3 is queued and prints the id: run `%s acquire --id <id> --until-granted` in the background and go by its exit '
		'code (0 granted, 2 dropped), never by words in the output. Reading, searching, editing and '
		'-singlefile compiles never need a grant.' % ('the editor/build lane' if need.get('lane') else kind, why, ORCH_CMD, kind, ORCH_CMD))


def revert_clash(db, snap, owner, args):
	others = {}
	for row in db.execute('SELECT * FROM files WHERE owner!=? AND at>?', (owner, now() - 6 * 3600)).fetchall():
		o = get_owner(db, row['owner'])
		if o and queue_entry_alive(owner_state(o, snap)):
			others[os.path.normcase(os.path.abspath(row['path']))] = row['owner']
	if not others:
		return ''
	paths = [a.strip('"\'') for a in args if not a.startswith('-')]
	if not paths or any('...' in p or '*' in p or p.startswith('//') for p in paths):
		return 'a wildcard/depot-wide revert while %d file(s) are being edited by live sessions (%s)' % (
			len(others), ', '.join(sorted(set(others.values())))[:200])
	for p in paths:
		full = os.path.normcase(os.path.abspath(os.path.join(PROJECT, p)))
		if full in others:
			return '%s is being edited by live session %s' % (p, others[full])
	return ''


RX_COMPILE_ERROR = re.compile(r'([A-Za-z]:[\\/][^()\r\n:"]+?\.(?:h|hpp|cpp|inl|cs))\((\d+)(?:,\d+)?\)\s*:\s*(?:fatal\s+)?error', re.I)


def hook_post(db, snap, owner, tool, tin, payload):
	notes = []
	with Tx(db):
		if tool in ('Edit', 'Write', 'NotebookEdit', 'MultiEdit'):
			path = tin.get('file_path') or tin.get('notebook_path')
			if path:
				db.execute('INSERT INTO files(path, owner, at) VALUES(?,?,?) ON CONFLICT(path) DO UPDATE SET owner=excluded.owner, at=excluded.at',
					(os.path.normcase(os.path.abspath(path)), owner, now()))
				note = lane_patch_note(db, owner, path)
				if note:
					notes.append(note)
		for g in db.execute("SELECT * FROM grants WHERE owner=? AND state='active' AND implicit=1 AND process_bound=0", (owner,)).fetchall():
			if not g['tool'] or g['tool'] == tool:
				begin_release(db, g, 'command finished', False)
				finish_release(db, g['id'])
		if tool in ('Bash', 'PowerShell'):
			out = json.dumps(payload.get('tool_response') or '')[:400000].replace('\\\\', '\\')
			blamed = {}
			for m in RX_COMPILE_ERROR.finditer(out):
				full = os.path.normcase(os.path.abspath(m.group(1)))
				row = db.execute('SELECT * FROM files WHERE path=?', (full,)).fetchone()
				if row and row['owner'] != owner and now() - row['at'] < 12 * 3600:
					blamed.setdefault(row['owner'], set()).add(os.path.relpath(full, os.path.normcase(PROJECT)))
			for other, files in blamed.items():
				o = get_owner(db, other)
				st = owner_state(o, snap) if o else 'unknown'
				notes.append('Compile errors in %s come from files last edited by session %s [%s], not by you. Do not fix or revert '
					'them; that session has been told. Rebuild once they compile again, or carry on with other work.' % (', '.join(sorted(files)), other, st))
				notify(db, other, 'Another session\'s build failed on files you are editing: %s. Get them compiling again promptly - '
					'every build on this workspace compiles them.' % ', '.join(sorted(files)))
				journal(owner, 'blame', 'build errors in %s -> %s' % (', '.join(sorted(files)), other))
		notes += drain_inbox(db, owner)
	if notes:
		return context('PostToolUse', '\n'.join(notes))
	return None


def hook_stop(db, snap, owner, payload):
	if payload.get('stop_hook_active'):
		return None
	reasons = []
	o = get_owner(db, owner)
	background = bool(o and o['pid'] and snap.tool_shells(o['pid']))
	h = hold_get(db)
	for g in owner_long_grants(db, owner):
		if g['state'] == 'active':
			reasons.append('You still hold the %s grant %s (%s, "%s"). If you are done, run `%s finish --id %s --evidence "<what is idle>"`. '
				'If work remains, continue it now. If you must wait for the user you may stop: %ds after the turn ends the '
				'orchestrator reclaims the grant and stops the PIE / test processes it launched.' % (g['kind'], g['id'][:8], g['phase'],
				g['purpose'][:80], ORCH_CMD, g['id'][:8], STOP_GRACE))
			if h.get('on') and grant_resources(g).get('lane'):
				reasons.append('The editor is paused for the user (%s). To keep your grant, wait it out with `%s hold wait` '
					'(repeat until RELEASED); stopping now gives the grant up.' % (hold_reason(h), ORCH_CMD))
		elif g['state'] == 'queued' and not background:
			reasons.append('You are queued (#%d) for %s (%s). Ending the turn drops you from the queue in %ds. Keep your place with '
				'`%s acquire --id %s --until-granted` in the background (exit 0 granted, 2 dropped; then do the work yourself), or `%s cancel --id %s` if you no longer need it.'
				% (queue_position(db, g), g['kind'], g['id'][:8], STOP_GRACE, ORCH_CMD, g['id'][:8], ORCH_CMD, g['id'][:8]))
	for task in db.execute("SELECT * FROM tasks WHERE owner=? AND state='in_progress'", (owner,)).fetchall():
		tid = task['id'][:8]
		reasons.append('Task %s "%s" is still in progress (next: %s). Finish and verify it, or record why you are stopping: '
			'`%s task done %s --evidence ...`, `%s task block %s --on user --why ...`, or `%s task handoff %s --note ...`.'
			% (tid, task['title'][:80], task['next_step'][:120] or '?', ORCH_CMD, tid, ORCH_CMD, tid, ORCH_CMD, tid))
	if not reasons:
		return None
	return {'decision': 'block', 'reason': '[orchestrator] Before you stop:\n- ' + '\n- '.join(reasons)}


def session_brief(db, snap, owner):
	lines = ['[orchestrator] Shared editor/build/PIE/p4 access is coordinated by `%s` (.claude/scripts/orchestration.md). '
		'Single lane commands auto-acquire when free; for multi-step editor work run `%s acquire --kind PIE|Build|Editor|Test|Sync|Deploy '
		'--purpose "..." --wait 540`; on exit 3 run `%s acquire --id <id> --until-granted` in the background and go by its exit code (0 granted, '
		'2 dropped); `%s finish --id <id>` when idle. Track multi-turn work with '
		'`%s task start "..."`.' % ((ORCH_CMD,) * 5)]
	holder = lane_holder(db)
	lines.append('Lane: %s; queued: %d.' % (describe(db, snap, holder) if holder else 'free', len(queued_grants(db))))
	h = hold_get(db)
	if h.get('on'):
		lines.append('HOLD: %s. Lane commands wait until the user releases it (`%s hold wait`).' % (hold_reason(h), ORCH_CMD))
	for task in db.execute("SELECT * FROM tasks WHERE owner=? AND state IN ('in_progress','blocked')", (owner,)).fetchall():
		lines.append('Your task %s: %s [%s] next: %s' % (task['id'][:8], task['title'][:100], task['state'], task['next_step'][:120]))
	for task in db.execute("SELECT * FROM tasks WHERE state IN ('orphaned','open','parked') ORDER BY updated DESC LIMIT 5").fetchall():
		lines.append('Unowned task %s (%s): %s - claim it with `%s task claim %s` only if the user asks you to pick it up.'
			% (task['id'][:8], task['state'], task['title'][:100], ORCH_CMD, task['id'][:8]))
	with Tx(db):
		lines += drain_inbox(db, owner)
	return '\n'.join(lines)


# ---------------------------------------------------------------- CLI

def cli_owner(args, snap):
	# Any model or client can take part: one the orchestrator does not recognise names itself.
	if getattr(args, 'as_name', None):
		return 'agent:' + args.as_name.strip().lower(), 'other', args.owner_pid or 0
	kind, proc = find_agent_process(snap)
	if args.owner:
		pid = args.owner_pid or (proc.pid if proc else 0)
		return args.owner, (kind or 'other'), pid
	if proc:
		return owner_key(kind, snap, proc), kind, proc.pid
	env = os.environ.get('ORCH_OWNER')
	if env:
		return env, 'other', args.owner_pid or 0
	detached = detached_agent(snap) if args.action == 'guard' else None
	if detached:
		args.detached = True
		return owner_key('claude', snap, detached), 'claude', detached.pid
	raise SystemExit('ORCHESTRATOR: cannot tell which agent you are. Pass --as <your name>, or --owner <stable-unique-id> '
		'(e.g. codex:<session>) and --owner-pid <your agent process id> if you know it.')


def find_grant(db, owner, gid, states):
	marks = ','.join('?' * len(states))
	rows = db.execute('SELECT * FROM grants WHERE state IN (%s) ORDER BY seq' % marks, states).fetchall()
	if gid:
		rows = [g for g in rows if g['id'].startswith(gid)]
	else:
		rows = [g for g in rows if g['owner'] == owner and not g['implicit']]
	return rows[-1] if rows else None


def cmd_status(db, snap, args):
	tick(db, snap)
	if args.probe and not lane_holder(db):
		refresh_pie_probe(db, snap, force=True)
	if args.json:
		with open(MIRROR_PATH, 'r', encoding='utf-8') as f:
			print(f.read())
		return 0
	print('ORCHESTRATOR status (%s)' % iso(now()))
	h = hold_get(db)
	if h.get('on'):
		print('  HOLD     %s - lane commands wait; `%s hold off` releases it' % (hold_reason(h), ORCH_CMD))
	grants = active_grants(db)
	if not grants:
		print('  grants: none - the lane is free')
	for g in grants:
		print('  GRANT    ' + describe(db, snap, g) + (('  [releasing: %s]' % g['release_reason']) if g['state'] == 'releasing' else ''))
	for q in queued_grants(db):
		print('  QUEUED   #%d %s' % (queue_position(db, q), describe(db, snap, q)))
	for item in occupants(db, snap, {'lane': 'X'}):
		print('  OCCUPANT ' + item)
	for p, kind in ue_processes(snap):
		row = db.execute('SELECT owner, grant_id FROM procs WHERE pid=? AND gone_at=0', (p.pid,)).fetchone()
		print('  PROCESS  %-6d %-24s %-10s %s' % (p.pid, p.name, kind,
			('owner=%s grant=%s' % (row['owner'], row['grant_id'][:8])) if row and row['owner'] else ''))
	probe = json.loads(meta_get(db, 'pie_probe', '{}') or '{}')
	if probe.get('at'):
		print('  PIE      %s (probed %s)' % ('RUNNING' if probe.get('running') else 'not running', ago(probe.get('at'))))
	for o in db.execute('SELECT * FROM owners WHERE dead_at=0 ORDER BY last_active DESC').fetchall():
		st = owner_state(o, snap)
		if st == 'dead':
			continue
		print('  AGENT    %-36s %-16s active %s%s' % (o['owner'], st, ago(max(o['last_active'], o['last_shell_seen'])),
			(' | open tool: %s' % o['open_desc']) if o['open_tools'] else ''))
	for task in db.execute("SELECT * FROM tasks WHERE state NOT IN ('done','cancelled','expired') ORDER BY updated DESC LIMIT 15").fetchall():
		print('  TASK     %s %-11s %-36s %s%s' % (task['id'][:8], task['state'], task['owner'], task['title'][:80],
			(' | blocked on %s: %s' % (task['blocked_on'], task['why'][:80])) if task['blocked_on'] else ''))
	for g in db.execute("SELECT * FROM grants WHERE state='done' AND implicit=0 ORDER BY released_at DESC LIMIT %d" % max(0, args.history)).fetchall():
		print('  DONE     %s %-6s %-9s %-9s %s | %s' % (g['id'][:8], g['kind'], g['phase'], ago(g['released_at']), g['owner'],
			(g['release_reason'] or g['purpose'])[:110]))
	print('  daemon pid %s | resurrection %s | journal %s' % (meta_get(db, 'daemon_pid', '-'), meta_get(db, 'resurrect', 'on'), EVENTS_PATH))
	return 0


def cmd_acquire(db, snap, args, owner, wait_default):
	wait = args.wait if args.wait is not None else wait_default
	deadline = float('inf') if args.until_granted else now() + max(0, wait)
	with Tx(db):
		touch_owner(db, owner)
		reconcile(db, snap)
		existing = find_grant(db, owner, args.id, ('queued', 'active')) if args.id else None
		if existing and existing['owner'] != owner:
			print('ORCHESTRATOR: grant %s belongs to %s.' % (args.id, existing['owner']), file=sys.stderr)
			return 2
		if args.id and not existing:
			print('ORCHESTRATOR: you have no queued/active grant %s (it was dropped because you stopped working, or it finished). '
				'Run acquire again without --id.' % args.id, file=sys.stderr)
			return 2
		if not existing and not args.purpose:
			print('ORCHESTRATOR: --purpose "<agent/task: why>" is required for a new request.', file=sys.stderr)
			return 2
		kind = existing['kind'] if existing and not args.kind_given else args.kind
		g, err = request(db, snap, owner, kind, args.purpose or (existing['purpose'] if existing else ''), args.requirements,
			args.minutes, existing['id'] if existing else None, args.keep_processes)
		if err:
			print('ORCHESTRATOR: ' + err, file=sys.stderr)
			return 2
	while True:
		with Tx(db):
			reconcile(db, snap, force=True)
		settle(db, snap)
		if grant_resources(g).get('lane') == 'X' and not lane_holder(db):
			refresh_pie_probe(db, snap)
		with Tx(db):
			touch_owner(db, owner)
			g = db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone()
			if g['state'] not in ('queued', 'active'):
				print('ORCHESTRATOR: your request %s was dropped (%s). Acquire again.' % (g['id'][:8], g['release_reason']), file=sys.stderr)
				return 2
			got, why = try_activate(db, snap, g)
			pos = 0 if got else queue_position(db, g)
			# --until-granted runs in the background and promises exit 0 or 2; the hooks deliver its owner's messages.
			msgs = [] if got or args.until_granted else messages_for_waiter(db, owner)
		if got:
			print('GRANTED %s' % describe(db, snap, got))
			print('id=%s  finish with: %s finish --id %s --evidence "<PIE stopped, nothing in flight>"' % (got['id'], ORCH_CMD, got['id'][:8]))
			if grant_resources(got).get('lane') == 'X':
				print('TURN: %d min on the lane once another agent queues, then your next lane command is refused.' % (LANE_TURN // 60))
			if grant_resources(got).get('lane'):
				print(lane_plan_text(got))
			h = hold_get(db)
			if h.get('on') and grant_resources(got).get('lane'):
				print('PAUSED: %s. The grant is yours, but its commands wait until the user releases the editor (`%s hold wait`).'
					% (hold_reason(h), ORCH_CMD))
			print_build_advice(got)
			write_mirror(db, snap)
			return 0
		if msgs or now() >= deadline:
			for msg in msgs:
				print(msg)
			print('WAITING #%d %s' % (pos, describe(db, snap, g)))
			print('blocked by: %s' % why)
			print('id=%s  re-run `%s acquire --id %s --wait 540` to keep your place; do independent work in between.' % (g['id'], ORCH_CMD, g['id'][:8]))
			print('To be woken once instead of polling, run `%s acquire --id %s --until-granted` in the background: exit 0 granted, 2 dropped.' % (ORCH_CMD, g['id'][:8]))
			print_build_advice(g)
			write_mirror(db, snap)
			return 3
		time.sleep(3)
		snap = SNAPSHOT()


def cmd_finish(db, snap, args, owner, cancel=False):
	with Tx(db):
		touch_owner(db, owner)
		g = find_grant(db, owner, args.id, ('queued',) if cancel else ('active', 'queued'))
		if not g:
			print('ORCHESTRATOR: no matching grant of yours%s (already finished or reclaimed?).' % (' ' + args.id if args.id else ''), file=sys.stderr)
			return 2
		if g['owner'] != owner:
			print('ORCHESTRATOR: grant %s belongs to %s. Only its owner finishes it; a stopped owner is reclaimed automatically.' % (g['id'][:8], g['owner']), file=sys.stderr)
			return 2
		ev = json.loads(g['evidence'] or '{}')
		if args.evidence:
			ev['idleEvidence'] = args.evidence
		db.execute('UPDATE grants SET evidence=?, keep_processes=? WHERE id=?', (json.dumps(ev), 1 if args.keep_processes or g['keep_processes'] else 0, g['id']))
		if g['state'] == 'queued':
			db.execute("UPDATE grants SET state='done', phase='cancelled', released_at=?, release_reason='cancelled by owner' WHERE id=?", (now(), g['id']))
			journal(owner, 'cancel', g['id'][:8])
			print('CANCELLED %s' % g['id'])
			write_mirror(db, snap)
			return 0
		if args.keep_processes:
			db.execute('UPDATE procs SET handed_to_user=1 WHERE grant_id=? AND gone_at=0', (g['id'],))
		# Only the kinds that run PIE: a Build or Sync grant must not end a PIE the user started meanwhile.
		holds_lane = grant_resources(g).get('lane') == 'X' and g['kind'] in ('PIE', 'Editor')
		verdict = pie_verdict(g, main_editor_report(snap, now())) if holds_lane else ''
		# Keeping a PIE that is not the grant's would hand it to this agent, and its next prompt would stop the user's game.
		is_kept = args.keep_pie and verdict in ('ours', 'unknown')
		if is_kept:
			meta_set(db, 'pie_handoff', json.dumps({'owner': owner, 'at': now(), 'grant': g['id']}))
		g = db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone()
		begin_release(db, g, 'finished by owner' + (': ' + args.evidence[:200] if args.evidence else ''), False)
	# Done means nothing held: a PIE this grant started is an occupant that blocks every other agent's lane. Any other PIE is not its to stop.
	stopped = verdict == 'ours' and not args.keep_pie and stop_pie_now(db, snap, owner, 'finish by its grant owner')
	is_left = verdict == 'not ours' or (verdict == 'unknown' and not args.keep_pie and editor_says_pie_is_up(db, snap))
	if is_left:
		journal(owner, 'pie-left', 'finish of %s left a PIE running: not started by it' % g['id'][:8])
	settle(db, snap)
	print('FINISHED %s%s' % (g['id'], ' (stopped its PIE)' if stopped else ' (left a PIE running: not started by this grant)' if is_left
		else ' (PIE kept for the user until your next prompt)' if is_kept else ''))
	write_mirror(db, snap)
	return 0


def pie_left_by(mine, handoff, report):
	"""The verdict on a running PIE for an agent asking to stop it: its grants, each only up to when it ended, and what it handed over."""
	if report and not report['pie']:
		return 'none'
	since = (report.get('pieSince') or 0) if report else 0
	verdicts = ['not ours' if since and since > g['released_at'] else pie_verdict(g, report) for g in mine]
	if handoff:
		verdicts.append(('ours' if since <= handoff.get('at', 0) else 'not ours') if since else 'unknown')
	return 'ours' if 'ours' in verdicts else 'unknown' if 'unknown' in verdicts else 'not ours'


def cmd_release_pie(db, snap, owner, args):
	"""The PIE is the caller's when they handed it over, or when one of the last few grants on the lane was theirs: a later agent's
	finish --keep-pie must not take it away. --master is the Master Agent's override, for a PIE it has checked is idle and not the user's."""
	if args.master:
		if not args.reason:
			print('ORCHESTRATOR: release-pie --master needs --reason.', file=sys.stderr)
			return 2
		if lane_holder(db) and not args.past_holder:
			print('ORCHESTRATOR: the lane is held by %s, and that PIE may be its own. Read what is running now; --past-holder only if it is not.' % describe(db, snap, lane_holder(db)), file=sys.stderr)
			return 2
		why = 'Master Agent override: %s' % args.reason
	else:
		if lane_holder(db):
			print('ORCHESTRATOR: the lane is held by %s; that PIE is theirs.' % describe(db, snap, lane_holder(db)), file=sys.stderr)
			return 2
		handoff = json.loads(meta_get(db, 'pie_handoff', '{}') or '{}')
		recent = []
		for row in db.execute("SELECT * FROM grants WHERE state='done' AND released_at>0 ORDER BY released_at DESC LIMIT 20"):
			if grant_resources(row).get('lane') == 'X' and len(recent) < RELEASE_PIE_LAST_GRANTS:
				recent.append(row)
		mine = [row for row in recent if row['owner'] == owner]
		if handoff.get('owner') != owner and not mine:
			print('ORCHESTRATOR: you did not leave the running PIE behind; only its agent or the user stops it.', file=sys.stderr)
			return 2
		verdict = pie_left_by(mine, handoff if handoff.get('owner') == owner else None, main_editor_report(snap, now()))
		if verdict == 'not ours':
			print('ORCHESTRATOR: that PIE is not yours: it was up before your grant or someone at the keyboard started it, so it stays up. '
				'The Master Agent can override with --master.', file=sys.stderr)
			return 2
		if verdict == 'unknown' and editor_says_pie_is_up(db, snap):
			print('ORCHESTRATOR: nothing says whose that PIE is, so it stays up. The Master Agent can override with --master.', file=sys.stderr)
			return 2
		why = 'release-pie by the agent that left it'
	with Tx(db):
		meta_set(db, 'pie_handoff', '{}')
	if (args.master or verdict == 'ours') and stop_pie_now(db, snap, owner, why):
		print('RELEASED: stopped the PIE')
	else:
		print('RELEASED: no PIE was running (or the user holds the editor)')
	settle(db, snap)
	write_mirror(db, snap)
	return 0


def cmd_phase(db, snap, args, owner, action):
	with Tx(db):
		touch_owner(db, owner)
		g = find_grant(db, owner, args.id, ('active',))
		if not g or g['owner'] != owner:
			print('ORCHESTRATOR: no active grant of yours%s.' % (' ' + args.id if args.id else ''), file=sys.stderr)
			return 2
		ev = json.loads(g['evidence'] or '{}')
		for attr, key in (('code_evidence', 'codeEvidence'), ('blueprint_evidence', 'blueprintEvidence'),
				('schedule_evidence', 'scheduleEvidence'), ('evidence', 'idleEvidence')):
			if getattr(args, attr, None):
				ev[key] = getattr(args, attr)
		phase = {'ready': 'ready', 'begin': 'running', 'prepare': 'preparing'}[action]
		db.execute('UPDATE grants SET phase=?, evidence=?, updated_at=?, editor_pid=?, map=?, started_at=?, pie_started=? WHERE id=?',
			(phase, json.dumps(ev), now(), args.editor_pid or g['editor_pid'], args.map or g['map'],
			now() if action == 'begin' else g['started_at'], 1 if action == 'begin' else g['pie_started'], g['id']))
		g = db.execute('SELECT * FROM grants WHERE id=?', (g['id'],)).fetchone()
	print(describe(db, snap, g))
	write_mirror(db, snap)
	return 0


def cmd_guard(db, snap, args, owner):
	with Tx(db):
		# A chain cut off from its agent is not the agent working: it must not keep a grant alive past the agent's turn.
		if not getattr(args, 'detached', False):
			touch_owner(db, owner)
		reconcile(db, snap)
		if args.operation == 'Inspect':
			print('ALLOWED: read-only inspection.')
			return 0
		h = hold_get(db)
		if h.get('on'):
			hold_note_paused(db, h, owner)
			print('DENIED: %s. Wait with `%s hold wait`.' % (hold_reason(h), ORCH_CMD), file=sys.stderr)
			return 2
		lane = [g for g in active_grants(db) if g['owner'] == owner and g['state'] == 'active' and grant_resources(g).get('lane') == 'X']
		if not lane:
			holder = lane_holder(db)
			print('DENIED: you hold no editor/build lane grant%s. Run `%s acquire --kind %s --purpose "..." --wait 540` first.'
				% ((' (held by ' + describe(db, snap, holder) + ')') if holder else '', ORCH_CMD, 'Build' if args.operation == 'Compile' else 'PIE'), file=sys.stderr)
			return 2
		g = lane[-1]
		if args.operation == 'Compile' and g['phase'] == 'running':
			print('DENIED: your PIE is running. Stop it, then `%s prepare --id %s --evidence "PIE stopped"` before compiling.' % (ORCH_CMD, g['id'][:8]), file=sys.stderr)
			return 2
		if args.operation == 'PieStart':
			db.execute("UPDATE grants SET phase='running', pie_started=1, started_at=? WHERE id=?", (now(), g['id']))
		print('ALLOWED: ' + describe(db, snap, g))
	return 0


def cmd_transfer(db, snap, args, owner):
	with Tx(db):
		touch_owner(db, owner)
		g = find_grant(db, owner, args.id, ('active', 'queued'))
		if not g or g['owner'] != owner:
			print('ORCHESTRATOR: you can only transfer your own grant.', file=sys.stderr)
			return 2
		target = get_owner(db, args.to)
		if not target or owner_state(target, snap) not in WORKING_STATES:
			print('ORCHESTRATOR: %s is not a live, working agent.' % args.to, file=sys.stderr)
			return 2
		# A PIE that came up under the previous holder comes with the grant, though the new holder's turn starts now.
		ev = json.loads(g['evidence'] or '{}')
		if g['state'] == 'active':
			ev.setdefault('heldSince', g['acquired_at'])
		db.execute('UPDATE grants SET owner=?, updated_at=?, acquired_at=?, evidence=? WHERE id=?', (args.to, now(), now() if g['state'] == 'active' else g['acquired_at'], json.dumps(ev), g['id']))
		notify(db, args.to, '%s handed you its %s grant %s ("%s"). It is yours now: use it and finish it.' % (owner, g['kind'], g['id'][:8], g['purpose'][:100]))
		journal(owner, 'transfer', '%s -> %s' % (g['id'][:8], args.to))
	print('TRANSFERRED %s to %s' % (g['id'], args.to))
	return 0


def cmd_reorder(db, snap, args, owner):
	"""The rows swap their seq values, so every reader's ORDER BY seq sees the new order and the schema stays as it is. Only the rows that
	conflict with the moved one take part: they are what queue_position counts, so --to means the place the entry then shows."""
	with Tx(db):
		touch_owner(db, owner)
		rows = queued_grants(db)
		matches = [q for q in rows if q['id'].startswith(args.id)]
		if not matches:
			print('ORCHESTRATOR: no queued grant %s.' % args.id, file=sys.stderr)
			return 2
		if len(matches) > 1:
			print('ORCHESTRATOR: %s matches %d queued grants; give more of the id.' % (args.id, len(matches)), file=sys.stderr)
			return 2
		moved = matches[0]
		peers = [q for q in rows if q['id'] == moved['id'] or conflicts(grant_resources(q), grant_resources(moved))]
		position = max(1, min(args.to, len(peers)))
		order = [q for q in peers if q['id'] != moved['id']]
		order.insert(position - 1, moved)
		if [q['id'] for q in order] == [q['id'] for q in peers]:
			print('MOVED %s to #%d (it was already there)' % (moved['id'], position))
			return 0
		seqs = [q['seq'] for q in peers]
		for q in peers:
			db.execute('UPDATE grants SET seq=? WHERE id=?', (-q['seq'], q['id']))
		for q, seq in zip(order, seqs):
			db.execute('UPDATE grants SET seq=? WHERE id=?', (seq, q['id']))
		db.execute('UPDATE grants SET claimable_since=0 WHERE id=?', (moved['id'],))
		journal(owner, 'reorder', '%s %s to #%d: %s' % (moved['id'][:8], moved['kind'], position, args.reason))
	print('MOVED %s to #%d' % (moved['id'], position))
	return 0


def cmd_recover(db, snap, args, owner):
	with Tx(db):
		touch_owner(db, owner)
		reconcile(db, snap, force=True)
		g = find_grant(db, owner, args.id, ('active', 'queued'))
		if not g:
			print('Nothing to recover: that grant is already released or being released.')
			return 0
		o = get_owner(db, g['owner'])
		st = owner_state(o, snap) if o else 'unknown'
		if g['owner'] != owner and st in WORKING_STATES and not args.handoff_confirmed:
			print('REFUSED: the owner %s is still working (%s). Grants are reclaimed automatically once their owner stops; '
				'ask the owner to hand it over with `%s transfer` if you need it sooner.' % (g['owner'], st, ORCH_CMD), file=sys.stderr)
			return 2
		begin_release(db, g, 'recovered by %s: %s' % (owner, args.reason or st), True)
	settle(db, snap)
	print('RECOVERED %s' % g['id'])
	return 0


def print_hold(h):
	if h.get('on'):
		print('HELD %s (since %s, %s)%s' % (hold_reason(h), iso(h.get('since')), h.get('source') or 'cli',
			(' | paused agents: ' + ', '.join(h['paused'])) if h.get('paused') else ''))
	else:
		print('RELEASED - agents may use the editor%s' % ((' (last hold released %s)' % ago(h['released_at'])) if h.get('released_at') else ''))


def release_hold(db, snap, h, why):
	paused = h.get('paused') or []
	released = {'on': False, 'by': h.get('by'), 'released_at': now(), 'last_since': h.get('since')}
	hold_set(db, released)
	journal('user', 'hold', 'off%s (%d agent(s) were paused)' % (why, len(paused)))
	for o in paused:
		row = get_owner(db, o)
		if row and queue_entry_alive(owner_state(row, snap)):
			mine = [g for g in owner_long_grants(db, o) if g['state'] == 'active' and grant_resources(g).get('lane')]
			notify(db, o, 'The user released the editor; you may use it again.%s' % (
				(' Your %s grant %s is still yours - carry on.' % (mine[0]['kind'], mine[0]['id'][:8])) if mine else
				' Acquire as usual when you need it.'))
	return released


def end_orphaned_hold(db, snap, t):
	"""A hold taken in the editor's toolbar can only be seen and released there, so it ends with that editor. A restart is not its end."""
	h = hold_get(db)
	if not h.get('on') or h.get('source') != 'editor':
		return
	if editor_pids(snap):
		if t - h.get('editor_seen', 0) >= HOLD_EDITOR_SEEN_SECONDS:
			h['editor_seen'] = t
			hold_set(db, h)
	elif t - max(h.get('editor_seen', 0), h.get('since', 0)) >= ORPHANED_HOLD_SECONDS:
		release_hold(db, snap, h, ': the editor it was taken in is gone')


def cmd_hold(db, snap, args):
	"""The user's own claim on the editor. Agents are paused at their next lane command, never preempted mid-command."""
	action = args.hold_action
	if action in ('on', 'off') and caller_is_agent(snap) and not args.user_asked:
		print('ORCHESTRATOR: only the user takes or releases the editor hold (the editor\'s Agent Orchestrator panel, or '
			'`%s hold on|off` in their own terminal). An agent may pass --user-asked only when the user asked for it in chat.' % ORCH_CMD, file=sys.stderr)
		return 2
	if action == 'status':
		tick(db, snap)
		h = hold_get(db)
		if args.json:
			print(json.dumps(h, indent=1))
		else:
			print_hold(h)
		return 0
	if action == 'wait':
		owner = None
		try:
			owner = cli_owner(args, snap)[0]
		except SystemExit:
			pass
		deadline = now() + max(0, args.wait)
		while True:
			with Tx(db):
				if owner and get_owner(db, owner):
					touch_owner(db, owner)
				h = hold_get(db)
				if h.get('on') and owner:
					hold_note_paused(db, h, owner)
			if not h.get('on'):
				print('RELEASED - the user let go of the editor; carry on (an active grant of yours is still yours).')
				return 0
			if now() >= deadline:
				print('STILL HELD - %s. Run `%s hold wait` again; do independent work in between.' % (hold_reason(h), ORCH_CMD))
				return 3
			time.sleep(3)
	stopped = ''
	if action == 'on' and args.stop_pie and not hold_active(db) and lane_holder(db) and pie_verdict(lane_holder(db), main_editor_report(snap, now())) == 'ours':
		stopped = stop_orphaned_pie(db, snap, 'the user took the editor')
		if stopped:
			print(stopped)
	with Tx(db):
		reconcile(db, snap, force=True)
		h = hold_get(db)
		t = now()
		if action == 'on':
			fresh = not h.get('on')
			if fresh:
				h = {'on': True, 'since': t, 'paused': []}
			h['by'] = args.by or h.get('by') or os.environ.get('USERNAME') or os.environ.get('USER') or 'user'
			h['reason'] = args.reason if args.reason is not None else h.get('reason', '')
			h['source'] = args.source or h.get('source') or 'cli'
			holder = lane_holder(db)
			affected = [g['owner'] for g in active_grants(db) + queued_grants(db) if grant_resources(g).get('lane')]
			for o in affected:
				if o not in h['paused']:
					h['paused'].append(o)
			hold_set(db, h)
			if fresh:
				journal('user', 'hold', 'on by %s (%s)%s' % (h['by'], h['source'], (': ' + h['reason']) if h['reason'] else ''))
				stopped_note = ''
				if holder and (args.stopped_pie or stopped) and not holder['implicit']:
					db.execute("UPDATE grants SET phase='preparing', pie_started=0, updated_at=? WHERE id=?", (t, holder['id']))
					stopped_note = ' Your PIE was stopped so the user has the editor to themselves; start it again after the release.'
				for o in set(affected):
					notify(db, o, 'The user has taken the editor (%s). Your editor, PIE, build, test and p4 sync commands now wait until '
						'they release it. Keep any grant you hold and wait with `%s hold wait` (repeat until RELEASED).%s'
						% (hold_reason(h), ORCH_CMD, stopped_note if holder and o == holder['owner'] else ''))
		else:
			if not h.get('on'):
				print_hold(h)
				return 0
			h = release_hold(db, snap, h, '')
	write_mirror(db, snap)
	print_hold(h)
	return 0


def cmd_task(db, snap, args, owner):
	t = now()
	o = get_owner(db, owner)
	sid = o['session_id'] if o and o['hooked'] else ''
	with Tx(db):
		touch_owner(db, owner)
		if args.task_action == 'start':
			tid = uuid.uuid4().hex
			db.execute("""INSERT INTO tasks(id, owner, session_id, title, state, next_step, files, created, updated)
				VALUES(?,?,?,?,'in_progress',?,?,?,?)""", (tid, owner, sid, args.title, args.next or '', args.files or '', t, t))
			journal(owner, 'task', 'start %s %s' % (tid[:8], args.title[:100]))
			print('TASK %s started' % tid[:8])
			return 0
		if args.task_action == 'list':
			where = '1=1' if args.all else "state NOT IN ('done','cancelled','expired')"
			rows = db.execute('SELECT * FROM tasks WHERE %s ORDER BY updated DESC LIMIT 40' % where).fetchall()
			for r in rows:
				print('%s %-11s %-36s %s\n    next: %s%s%s' % (r['id'][:8], r['state'], r['owner'], r['title'], r['next_step'] or '-',
					('\n    blocked on %s: %s' % (r['blocked_on'], r['why'])) if r['blocked_on'] else '',
					('\n    handoff: ' + r['handoff']) if r['handoff'] else ''))
			if not rows:
				print('no open tasks')
			return 0
		if not args.id:
			print('ORCHESTRATOR: task %s needs a task id.' % args.task_action, file=sys.stderr)
			return 2
		rows = [r for r in db.execute('SELECT * FROM tasks').fetchall() if r['id'].startswith(args.id)]
		if len(rows) != 1:
			print('ORCHESTRATOR: task id %s matches %d tasks.' % (args.id, len(rows)), file=sys.stderr)
			return 2
		task = rows[0]
		if args.task_action == 'claim':
			db.execute("UPDATE tasks SET owner=?, session_id=?, state='in_progress', blocked_on='', why='', updated=? WHERE id=?", (owner, sid, t, task['id']))
		elif task['owner'] != owner:
			print('ORCHESTRATOR: task %s belongs to %s; claim it first.' % (task['id'][:8], task['owner']), file=sys.stderr)
			return 2
		elif args.task_action == 'update':
			db.execute("""UPDATE tasks SET next_step=COALESCE(?, next_step), files=COALESCE(?, files), state='in_progress',
				blocked_on='', why='', updated=? WHERE id=?""", (args.next, args.files, t, task['id']))
		elif args.task_action == 'block':
			db.execute("UPDATE tasks SET state='blocked', blocked_on=?, why=?, updated=? WHERE id=?", (args.on, args.why, t, task['id']))
		elif args.task_action == 'done':
			db.execute("UPDATE tasks SET state='done', evidence=?, updated=? WHERE id=?", (args.evidence, t, task['id']))
		elif args.task_action == 'cancel':
			db.execute("UPDATE tasks SET state='cancelled', why=?, updated=? WHERE id=?", (args.why, t, task['id']))
		elif args.task_action == 'handoff':
			target = args.to or ''
			db.execute('UPDATE tasks SET state=?, owner=?, handoff=?, updated=? WHERE id=?',
				('in_progress' if target else 'open', target or task['owner'], args.note, t, task['id']))
			if target:
				notify(db, target, 'Task %s "%s" was handed to you by %s: %s' % (task['id'][:8], task['title'][:100], owner, args.note))
		journal(owner, 'task', '%s %s' % (args.task_action, task['id'][:8]))
	print('TASK %s %s' % (task['id'][:8], args.task_action))
	return 0


def build_parser():
	p = argparse.ArgumentParser(prog='orch', description='Agent Orchestrator (see .claude/scripts/orchestration.md)')
	p.add_argument('--owner', default=None, help='stable unique id when not running under claude.exe/codex (e.g. codex:<session>)')
	p.add_argument('--owner-pid', type=int, default=0, help='your persistent agent process id, when --owner is used')
	p.add_argument('--as', dest='as_name', default=None, help='any agent the orchestrator cannot identify: act as agent:<name>')
	sub = p.add_subparsers(dest='action')
	s = sub.add_parser('status', help='who holds what, who is waiting, what is running')
	s.add_argument('--json', action='store_true')
	s.add_argument('--probe', action='store_true', help='ask the editor whether PIE is running (only when the lane is free)')
	s.add_argument('--history', type=int, default=5)
	for name, helptext in (('acquire', 'queue and wait for a grant (default --wait 540)'), ('queue', 'queue without waiting')):
		a = sub.add_parser(name, help=helptext)
		a.add_argument('--kind', default=None, choices=list(LONG_KINDS))
		a.add_argument('--purpose', default='')
		a.add_argument('--requirements', default='')
		a.add_argument('--minutes', type=int, default=15)
		a.add_argument('--wait', type=int, default=None, help='seconds to block (default 540 for acquire, 0 for queue)')
		a.add_argument('--id', default=None, help='keep waiting on this existing request')
		a.add_argument('--keep-processes', action='store_true', help='leave launched test processes running for the user afterwards')
		a.add_argument('--until-granted', action='store_true', help='block until granted or dropped, whatever --wait says: run it in the background and it wakes you once')
	for name in ('finish', 'cancel'):
		f = sub.add_parser(name)
		f.add_argument('--id', default=None)
		f.add_argument('--evidence', default='')
		f.add_argument('--keep-processes', action='store_true')
		f.add_argument('--keep-pie', action='store_true', help='leave the PIE running for the user; it is stopped at their next prompt to you')
	rp = sub.add_parser('release-pie', help='stop the PIE you left running for the user (finish --keep-pie)')
	rp.add_argument('--master', action='store_true', help="the Master Agent's override: stop a PIE that blocks the lane")
	rp.add_argument('--past-holder', action='store_true', help='with --master: the lane holder is blocked by that PIE, checked just now')
	rp.add_argument('--reason', default='', help='why, for the journal; --master needs it')
	for name in ('ready', 'begin', 'prepare', 'heartbeat'):
		r = sub.add_parser(name)
		r.add_argument('--id', default=None)
		r.add_argument('--code-evidence', default='')
		r.add_argument('--blueprint-evidence', default='')
		r.add_argument('--schedule-evidence', default='')
		r.add_argument('--evidence', default='')
		r.add_argument('--editor-pid', type=int, default=0)
		r.add_argument('--map', default='')
	g = sub.add_parser('guard')
	g.add_argument('--operation', default='Work', choices=['Work', 'Compile', 'PieStart', 'PieStop', 'Inspect'])
	t = sub.add_parser('transfer', help='hand your grant to another live agent')
	t.add_argument('--id', default=None)
	t.add_argument('--to', required=True)
	ro = sub.add_parser('reorder', help="the Master Agent's queue control: move a queued grant to a position")
	ro.add_argument('--id', required=True)
	ro.add_argument('--to', type=int, required=True, help='1 is next in line')
	ro.add_argument('--reason', required=True)
	r = sub.add_parser('recover', help='release a grant whose owner has stopped working (normally automatic)')
	r.add_argument('--id', required=True)
	r.add_argument('--reason', default='')
	r.add_argument('--handoff-confirmed', action='store_true')
	k = sub.add_parser('task', help='multi-turn task ledger')
	k.add_argument('task_action', choices=['start', 'update', 'block', 'done', 'cancel', 'handoff', 'claim', 'list'])
	k.add_argument('id_or_title', nargs='?', default=None)
	k.add_argument('--next', default=None)
	k.add_argument('--files', default=None)
	k.add_argument('--on', default='user')
	k.add_argument('--why', default='')
	k.add_argument('--evidence', default='')
	k.add_argument('--note', default='')
	k.add_argument('--to', default='')
	k.add_argument('--all', action='store_true')
	hd = sub.add_parser('hold', help='the user takes (on) or releases (off) the editor; agents pause at their next lane command')
	hd.add_argument('hold_action', nargs='?', default='status', choices=['on', 'off', 'wait', 'status'])
	hd.add_argument('--by', default=None)
	hd.add_argument('--reason', default=None)
	hd.add_argument('--source', default=None, help='who flipped it: cli | editor')
	hd.add_argument('--stop-pie', action='store_true', help='stop the lane holder\'s PIE before taking the editor')
	hd.add_argument('--stopped-pie', action='store_true', help='the caller already stopped the lane holder\'s PIE')
	hd.add_argument('--user-asked', action='store_true', help='an agent flipping the hold because the user asked it to in chat')
	hd.add_argument('--wait', type=int, default=540, help='hold wait: seconds to block')
	hd.add_argument('--json', action='store_true')
	ib = sub.add_parser('inbox', help='read your messages; `inbox <work>` also joins that channel and reads what waits on it; --wait N '
		'blocks up to N seconds for one')
	ib.add_argument('channel', nargs='?', default=None)
	ib.add_argument('--wait', type=int, default=0)
	ms = sub.add_parser('msg', help='message another agent: --to <name or owner id> "text" (or --file <path>)')
	ms.add_argument('--to', required=True)
	ms.add_argument('--file', default=None)
	ms.add_argument('text', nargs='?', default='')
	nm = sub.add_parser('name', help='name yourself, so other agents can message you by name')
	nm.add_argument('agent_name')
	sub.add_parser('reap', help='reconcile now')
	c = sub.add_parser('config')
	c.add_argument('key', choices=['resurrect', 'lane-turn'])
	c.add_argument('value', choices=['on', 'off'])
	h = sub.add_parser('hook')
	h.add_argument('event')
	h.add_argument('--payload-file', default=None)
	sub.add_parser('daemon')
	return p


def run_hook(args):
	try:
		raw = open(args.payload_file, 'r', encoding='utf-8-sig').read() if args.payload_file else sys.stdin.buffer.read().decode('utf-8', 'replace')
		payload = json.loads(raw) if raw.strip() else {}
	except (OSError, ValueError):
		payload = {}
	try:
		return hook(args.event, payload)
	except Exception as exc:
		journal('', 'hook-error', '%s: %r' % (args.event, exc))
		if args.event == 'PreToolUse':
			cat = classify_tool(payload.get('tool_name') or '', payload.get('tool_input') or {})[0]
			if cat not in (None, 'inspect', 'free'):
				return hook_output(pre_deny('ORCHESTRATOR internal error (%r). The command touches shared editor/build state, so it was '
					'not run. Retry once; if it repeats, run `%s status` and report the error.' % (exc, ORCH_CMD)))
		return 0


def main(argv=None):
	if hasattr(sys.stdout, 'reconfigure'):
		sys.stdout.reconfigure(errors='backslashreplace')
	parser = build_parser()
	args = parser.parse_args(sys.argv[1:] if argv is None else argv)
	if args.action is None:
		parser.print_help()
		return 0
	if args.action == 'hook':
		return run_hook(args)
	if args.action == 'daemon':
		daemon()
		return 0
	if args.action in ('acquire', 'queue'):
		args.kind_given = args.kind is not None
		args.kind = args.kind or 'PIE'
	while True:
		try:
			return run_command(args)
		except sqlite3.OperationalError as exc:
			if not getattr(args, 'until_granted', False) or 'locked' not in str(exc):
				raise
			journal('', 'queue', 'a waiter retries: %s' % exc)
		time.sleep(WAITER_DB_RETRY)


def run_command(args):
	db = connect()
	snap = SNAPSHOT()
	ensure_daemon(db, snap)
	if args.action == 'status':
		return cmd_status(db, snap, args)
	if args.action == 'reap':
		tick(db, snap, force=True)
		return cmd_status(db, SNAPSHOT(), argparse.Namespace(json=False, probe=False, history=5))
	if args.action == 'config':
		with Tx(db):
			meta_set(db, args.key, args.value)
		print('%s=%s' % (args.key, args.value))
		return 0
	if args.action == 'hold':
		return cmd_hold(db, snap, args)
	owner, okind, opid = cli_owner(args, snap)
	with Tx(db):
		adopt_hookless_session(db, snap, ensure_owner(db, owner, okind, opid, snap.start(opid) if opid else 0))
	try:
		if args.action in ('acquire', 'queue'):
			return cmd_acquire(db, snap, args, owner, 540 if args.action == 'acquire' else 0)
		if args.action in ('finish', 'cancel'):
			return cmd_finish(db, snap, args, owner, cancel=args.action == 'cancel')
		if args.action == 'release-pie':
			return cmd_release_pie(db, snap, owner, args)
		if args.action == 'heartbeat':
			with Tx(db):
				touch_owner(db, owner)
			print('OK')
			return 0
		if args.action in ('ready', 'begin', 'prepare'):
			return cmd_phase(db, snap, args, owner, args.action)
		if args.action == 'guard':
			return cmd_guard(db, snap, args, owner)
		if args.action == 'transfer':
			return cmd_transfer(db, snap, args, owner)
		if args.action == 'reorder':
			return cmd_reorder(db, snap, args, owner)
		if args.action == 'recover':
			return cmd_recover(db, snap, args, owner)
		if args.action == 'task':
			if args.task_action == 'start':
				args.title = args.id_or_title or ''
				if not args.title:
					print('ORCHESTRATOR: task start needs a title.', file=sys.stderr)
					return 2
			else:
				args.id = args.id_or_title
			return cmd_task(db, snap, args, owner)
		if args.action == 'inbox':
			work = (args.channel or '').strip().lower()
			if work:
				with Tx(db):
					joined = join_channel(db, work, owner)[1]
				if joined:
					print('[orchestrator] You are on the %s channel (the AI bridge: .claude/skills/ai-bridge/SKILL.md). Every message on '
						'it reaches you here. Reply: %s msg --to %s "...". Wait for the next one: %s inbox %s --wait 540. If the '
						'orchestrator cannot tell who you are, put --as <your name> before the command word on every call.' % (
						work, ORCH_CMD, work, ORCH_CMD, work))
			deadline = now() + max(args.wait, 0)
			while True:
				with Tx(db):
					msgs = drain_inbox(db, owner) + (drain_channel_backlog(db, work, owner) if work else [])
				if msgs or now() >= deadline:
					break
				time.sleep(INBOX_POLL_SECONDS)
			print('\n'.join(msgs) or 'inbox empty')
			return 0
		if args.action == 'msg':
			return cmd_msg(db, snap, args, owner)
		if args.action == 'name':
			with Tx(db):
				touch_owner(db, owner, title=args.agent_name.strip(), title_fixed=1)
			print('NAMED %s as %s' % (owner, args.agent_name.strip()))
			return 0
	finally:
		ensure_daemon(db, SNAPSHOT())
	return 0


if __name__ == '__main__':
	sys.exit(main())
