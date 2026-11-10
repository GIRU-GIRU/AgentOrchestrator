"""Waits, however long it takes, until the Master Agent has something to look at, prints it and exits.

    python .claude/skills/master-agent/watch.py     (run_in_background: the exit is the wake-up; a quiet day costs nothing)
    python .claude/skills/master-agent/watch.py --astras     why each Astra's window stays open, and which are closed; changes nothing

It only reads the orchestrator's feed and Agent Manager's live.json, and takes no lane. MEMORY is what it has already
reported, so nothing that happens between two runs is lost or said twice. It closes an Astra whose job looks done and reopens it
when work arrives (look_after_astras).
"""
import contextlib
import ctypes
import glob
import io
import json
import os
import re
import sqlite3
import sys
import time
from datetime import datetime

import astra

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..'))
FEED = os.path.join(ROOT, 'Saved', 'Orchestration', 'orch-status.json')
MEMORY = os.path.join(ROOT, 'Saved', 'Orchestration', 'master-watch.json')
WATCHER_PID = os.path.join(ROOT, 'Saved', 'Orchestration', 'master-watch.pid')


def editor_log_path():
	"""Saved/Logs/<Name>.log of the project: ORCH_UPROJECT, else the single *.uproject in the project root, as the orchestrator finds it."""
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


EDITOR_LOG = editor_log_path()
LIVE = os.path.expandvars(os.path.join('%LOCALAPPDATA%', 'AgentManager', 'live.json'))
MANAGER_STATE = os.path.expandvars(os.path.join('%LOCALAPPDATA%', 'AgentManager', 'state.json'))

POLL_SECONDS = 5
REPEAT_SECONDS = 300
SILENT_HOLD_SECONDS = 90
UNCLAIMED_LANE_SECONDS = 120
FEED_STALE_SECONDS = 120
LONG_HOLD_FACTOR = 1.5
ROUTINE_LINES = ('GRANTED ', 'OPENED ', 'HANDED ', 'QUEUED ')
EDITOR_DIALOGS = ('Restore Packages',)
LONG_HOLD_BLOCKING_SECONDS = 600
LONG_HOLD_BLOCKING_WAITERS = 2
HANDOVER_AFTER_SECONDS = 60
IMPROVE_EVERY_SECONDS = 14400
REORDER_MINUTES = 15
LIVE_STALE_SECONDS = 30
MASTER_AGENT_NAME = 'MASTER AGENT'
MASTER_INBOX = 'master-agent'
ASTRA_IDLE_CLOSE_SECONDS = 600
ASTRA_CLOSABLE_STATE = 'Finished'
ASTRA_QUIT = '/quit'
ASTRA_CLOSE_RETRY_SECONDS = 60
ASTRA_CLOSE_TRIES = 2
ASTRA_LIVE_FRESH_SECONDS = 5
ASTRA_REOPEN_RETRY_SECONDS = 180
ASTRA_REOPEN_PERIOD_SECONDS = 3600
ASTRA_REOPENS_PER_CHANNEL = 4
ASTRA_REOPENS_ALL_CHANNELS = 12
ASTRA_WAKE_RETRY_SECONDS = 300
BRIDGE_STALL_SECONDS = 600
FROZEN_EDITOR_SECONDS = 300
USER_AT_KEYBOARD_SECONDS = 300
CRASHES = os.path.join(ROOT, 'Saved', 'Crashes')
CRASH_CONTEXT = 'CrashContext.runtime-xml'
EDITOR_EXE = 'unrealeditor.exe'
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


editor_log = {'size': None, 'grew_at': 0.0}
hung_since = {}


def hung_editor_pids():
	"""Editors whose window Windows reports as not answering. Quiet logs prove nothing: a frozen game thread's background threads keep logging."""
	user32, kernel32 = ctypes.windll.user32, ctypes.windll.kernel32
	found = set()

	def look_at(window, _):
		if user32.IsWindowVisible(window) and user32.IsHungAppWindow(window):
			pid = ctypes.c_ulong()
			user32.GetWindowThreadProcessId(window, ctypes.byref(pid))
			process = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
			if process:
				path = ctypes.create_unicode_buffer(1024)
				size = ctypes.c_ulong(len(path))
				if kernel32.QueryFullProcessImageNameW(process, 0, path, ctypes.byref(size)) and os.path.basename(path.value).lower() == EDITOR_EXE:
					found.add(pid.value)
				kernel32.CloseHandle(process)
		return True

	user32.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)(look_at), None)
	return found


def editor_dialogs():
	"""A dialog at startup waits for a click nobody is there to give, and the window still answers, so it never reads as frozen."""
	user32, kernel32 = ctypes.windll.user32, ctypes.windll.kernel32
	found = []

	def look_at(window, _):
		if user32.IsWindowVisible(window):
			title = ctypes.create_unicode_buffer(200)
			user32.GetWindowTextW(window, title, len(title))
			if title.value in EDITOR_DIALOGS:
				pid = ctypes.c_ulong()
				user32.GetWindowThreadProcessId(window, ctypes.byref(pid))
				process = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
				if process:
					path = ctypes.create_unicode_buffer(1024)
					size = ctypes.c_ulong(len(path))
					if kernel32.QueryFullProcessImageNameW(process, 0, path, ctypes.byref(size)) and os.path.basename(path.value).lower() == EDITOR_EXE:
						found.append((pid.value, title.value))
					kernel32.CloseHandle(process)
		return True

	user32.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)(look_at), None)
	return found


def seconds_since_user_input():
	"""A PIE with no grant while someone is at the keyboard is the user's: nothing to decide, so it wakes nobody."""
	class LastInput(ctypes.Structure):
		_fields_ = [('size', ctypes.c_uint), ('tick', ctypes.c_uint)]
	last = LastInput()
	last.size = ctypes.sizeof(LastInput)
	ctypes.windll.user32.GetLastInputInfo(ctypes.byref(last))
	return (ctypes.windll.kernel32.GetTickCount() - last.tick) / 1000.0


def new_crash_reports(memory):
	"""An editor that crashes while closing logs nothing the lane can see, and agents just relaunch it, so a report nobody reads is the only sign. Ensures carry on running and stay quiet."""
	seen = memory.get('crashesSeen')
	if seen is None:
		memory['crashesSeen'] = time.time()
		return []
	lines = []
	newest = seen
	for path in glob.glob(os.path.join(CRASHES, '*', CRASH_CONTEXT)):
		stamp = os.path.getmtime(path)
		if stamp <= seen:
			continue
		newest = max(newest, stamp)
		try:
			with open(path, encoding='utf-8', errors='replace') as report:
				text = report.read()
		except OSError:
			continue
		crash_type = re.search(r'<CrashType>(.*?)</CrashType>', text)
		if crash_type and crash_type.group(1) == 'Ensure':
			continue
		message = re.search(r'<ErrorMessage>(.*?)</ErrorMessage>', text, re.S)
		stack = re.search(r'<CallStack>(.*?)</CallStack>', text, re.S)
		named = [row for row in (stack.group(1).split('\n') if stack else []) if '!' in row]
		lines.append('EDITOR CRASH %s: %s | %s' % (time.strftime('%H:%M:%S', time.gmtime(stamp)), (message.group(1).strip()[:160] if message else 'no message'), (named[0].strip()[:120] if named else 'no named frame')))
	memory['crashesSeen'] = newest
	return lines


def frozen_editors():
	"""A long load also stops answering for a while, so only a window silent for FROZEN_EDITOR_SECONDS counts."""
	hung = hung_editor_pids()
	for pid in [known for known in hung_since if known not in hung]:
		del hung_since[pid]
	for pid in hung:
		hung_since.setdefault(pid, time.time())
	return [(pid, time.time() - since) for pid, since in hung_since.items() if time.time() - since >= FROZEN_EDITOR_SECONDS]


def editor_log_is_moving():
	"""A run in the background shows no tool on its holder's card, but it makes the editor log grow. Opened rather than statted: Windows reports a stale size for a file another process holds open."""
	if not EDITOR_LOG:
		return False
	try:
		with open(EDITOR_LOG, 'rb') as log:
			size = log.seek(0, os.SEEK_END)
	except OSError:
		return False
	if size != editor_log['size']:
		editor_log.update(size=size, grew_at=time.time())
	return time.time() - editor_log['grew_at'] < SILENT_HOLD_SECONDS


def read_json(path):
	try:
		with open(path, encoding='utf-8') as f:
			return json.load(f)
	except (OSError, ValueError):
		return None


def save_memory(memory):
	"""A locked file (an antivirus scan) is no reason to stop watching: the next poll saves it. astra.py --keep writes the keep list here while this runs, so the file's own list wins."""
	on_disk = read_json(MEMORY) or {}
	if astra.KEEP_KEY in on_disk:
		memory[astra.KEEP_KEY] = on_disk[astra.KEEP_KEY]
	temp = '%s.%d.tmp' % (MEMORY, os.getpid())
	try:
		with open(temp, 'w', encoding='utf-8') as f:
			json.dump(memory, f)
		os.replace(temp, MEMORY)
	except OSError:
		pass


def read_cards():
	"""With the manager closed live.json stops changing, and every card would read as silent."""
	try:
		if time.time() - os.path.getmtime(LIVE) > LIVE_STALE_SECONDS:
			return []
	except OSError:
		return []
	return (read_json(LIVE) or {}).get('agents') or []


def is_newest_watcher():
	"""An orphaned watcher would use up an event and print it to nobody, so the newest one alone keeps running."""
	try:
		with open(WATCHER_PID, encoding='ascii') as f:
			return f.read().strip() == str(os.getpid())
	except OSError:
		return True


def card_for(owner, cards):
	"""An owner id is kind:pid:ticks, so the pid finds the agent's card."""
	parts = owner.split(':')
	pid = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else None
	return next((c for c in cards if c.get('pid') == pid), None) if pid else None


def label(owner, cards):
	card = card_for(owner, cards)
	return ('#%s %s' % (card.get('number'), card.get('name'))) if card else owner


def seconds_since(iso):
	try:
		return time.time() - datetime.fromisoformat(iso).timestamp()
	except (TypeError, ValueError):
		return None


def hand_over_bridges(cards, memory):
	"""A brief nobody has joined gets an Astra of its own, once, so the user never pastes the line. Only for a poster still running."""
	handed = memory.setdefault('handed', [])
	lines = []
	try:
		db = astra.connect()
		for channel, poster in astra.waiting(db):
			last = db.execute('SELECT MAX(created) FROM inbox WHERE owner=?', (astra.BACKLOG_PREFIX + channel,)).fetchone()[0] or 0
			is_settled = time.time() - last > HANDOVER_AFTER_SECONDS
			is_my_own_post = (card_for(poster, cards) or {}).get('name') == MASTER_AGENT_NAME
			if channel not in handed and is_settled and card_for(poster, cards) and not is_my_own_post:
				handed.append(channel)
				astra.hand_over(channel, wait=False)
				lines.append('HANDED %s to a new Astra, for %s' % (channel, label(poster, cards)))
	except (OSError, ValueError, sqlite3.Error) as error:
		lines.append('HANDOVER FAILED: %s' % error)
	return lines


def live_age():
	try:
		return time.time() - os.path.getmtime(LIVE)
	except OSError:
		return None


def is_pinned(card):
	"""None when the manager's state cannot be read."""
	marks = (read_json(MANAGER_STATE) or {}).get('Marks')
	return None if marks is None else bool((marks.get(card.get('key')) or {}).get('Pinned'))


def partner_on_the_job(db, channel, cards, feed, tasks):
	"""Why another agent on the channel is still on this work, or None. Members from before a restart are dead owners and do not count."""
	for card in cards:
		if card.get('listeningOn') == channel and card.get('isAlive') and astra.channel_of(card) != channel:
			return '%s is waiting on the channel' % card.get('name')
	astra_owner = astra.OWNER_PREFIX + channel
	alive = {agent.get('owner') for agent in feed.get('agents') or [] if agent.get('pid')}
	partners = [member for member in astra.members(db).get(channel, []) if member != astra_owner and member in alive]
	entries = (feed.get('queue') or []) + (feed.get('grants') or [])
	for partner in partners:
		for task in tasks:
			if task['owner'] == partner and astra.mentions_work(astra.task_text(task), channel):
				return 'partner %s has task %s open' % (partner, task['id'][:8])
		for entry in entries:
			if entry.get('owner') == partner and astra.mentions_work(entry.get('purpose'), channel):
				return 'partner %s holds or queues a lane for it' % partner
		if astra.unread_by(db, channel, partner):
			return 'partner %s has not read the channel' % partner
	return None


def astra_facts(db, channel, card, cards, feed, memory, processes):
	thread = astra.thread_of(card)
	tasks = astra.open_tasks(db)
	own = astra.own_task(tasks, channel)
	return {
		'kept': channel in astra.kept(),
		'pinned': is_pinned(card),
		'undelivered': len(astra.undelivered(db, channel)),
		'lastPostAt': astra.last_post_at(db, channel),
		'ownTask': own['id'][:8] if own else None,
		'partner': partner_on_the_job(db, channel, cards, feed, tasks),
		'others': sum(1 for other in cards if other is not card and astra.channel_of(other) == channel and other.get('isAlive')),
		'refused': memory.get('astra', {}).get('refused', {}).get(channel) == card.get('lastEventAt'),
		'thread': thread,
		'windows': astra.windows_of(channel, thread, processes),
		'liveAge': live_age(),
	}


def why_turn_stays(card):
	"""Only a turn that has ended counts: an Astra in its turn may be authoring for hours."""
	if not card.get('isAlive'):
		return 'closed'
	if card.get('state') != ASTRA_CLOSABLE_STATE:
		return 'state is %s' % card.get('state')
	if card.get('tool') or card.get('subagentsRunning'):
		return 'a tool or subagent is running'
	if card.get('question'):
		return 'it asked something'
	quiet = seconds_since(card.get('lastEventAt'))
	if quiet is None or quiet < ASTRA_IDLE_CLOSE_SECONDS:
		return 'its last event was %s s ago' % (None if quiet is None else int(quiet))
	return None


def why_job_stays(facts):
	"""Why an Astra's window stays, or None when its job looks done. A spare window is cheap and closing one mid-review is not, so anything unknown keeps it."""
	if facts['kept']:
		return 'kept with astra.py --keep'
	if facts['pinned'] is not False:
		return 'its card is pinned, or the manager state cannot be read'
	if facts['undelivered']:
		return '%d undelivered messages' % facts['undelivered']
	if facts['lastPostAt'] and time.time() - facts['lastPostAt'] < ASTRA_IDLE_CLOSE_SECONDS:
		return 'a post on its channel %d s ago' % (time.time() - facts['lastPostAt'])
	if facts['ownTask']:
		return 'its task %s is open' % facts['ownTask']
	if facts['partner']:
		return facts['partner']
	if facts['others']:
		return 'another live card has its channel'
	if facts['refused']:
		return 'it did not close last time and has done nothing since'
	if not facts['thread']:
		return 'no thread id to resume it by'
	if len(facts['windows']) != 1:
		return '%d windows run it' % len(facts['windows'])
	if facts['liveAge'] is None or facts['liveAge'] > ASTRA_LIVE_FRESH_SECONDS:
		return 'live.json is not fresh'
	return None


def why_astra_stays(db, channel, card, cards, feed, memory, processes):
	return why_turn_stays(card) or why_job_stays(astra_facts(db, channel, card, cards, feed, memory, processes))


def quietly(action, *arguments):
	"""astra.py prints for a person at a prompt. Here that would sit in the watcher's output, so it goes to the close log."""
	said = io.StringIO()
	with contextlib.redirect_stdout(said):
		result = action(*arguments)
	if said.getvalue().strip():
		astra.note(' '.join(said.getvalue().split()))
	return result


def close_finished_astras(db, feed, cards, memory, processes):
	"""Writes down the thread before typing: a watcher that dies here must still be able to reopen it."""
	book = memory['astra']
	for card in cards:
		channel = astra.channel_of(card)
		if channel is None or channel in book['closing'] or why_turn_stays(card):
			continue
		facts = astra_facts(db, channel, card, cards, feed, memory, processes)
		if why_job_stays(facts):
			continue
		pid = facts['windows'][0]
		book['closing'][channel] = {'thread': facts['thread'], 'pid': pid, 'at': time.time(), 'tries': 1}
		book['closed'].pop(channel, None)
		save_memory(memory)
		astra.note('QUIT %s: pid %d, thread %s, idle since %s' % (channel, pid, facts['thread'], card.get('lastEventAt')))
		quietly(astra.type_into, pid, ASTRA_QUIT)


def settle_closing(db, feed, cards, memory, processes):
	book = memory['astra']
	lines = []
	for channel, record in list(book['closing'].items()):
		if record['pid'] not in astra.windows_of(channel, record['thread'], processes):
			del book['closing'][channel]
			book['closed'][channel] = {'thread': record['thread'], 'closedAt': time.time()}
			astra.note('CLOSED %s: thread %s' % (channel, record['thread']))
			continue
		if time.time() - record['at'] < ASTRA_CLOSE_RETRY_SECONDS:
			continue
		card = next((c for c in cards if astra.channel_of(c) == channel and astra.thread_of(c) == record['thread']), None)
		reason = why_astra_stays(db, channel, card, cards, feed, memory, processes) if card else 'its card has gone'
		if reason:
			del book['closing'][channel]
			astra.note('NOT CLOSED %s: %s, so it stays' % (channel, reason))
		elif record['tries'] >= ASTRA_CLOSE_TRIES:
			del book['closing'][channel]
			book.setdefault('refused', {})[channel] = card.get('lastEventAt')
			astra.note('NOT CLOSED %s: pid %d is still there after %d tries' % (channel, record['pid'], record['tries']))
			lines.append('ASTRA NOT CLOSED %s: pid %d is still there after %d %s, left open' % (channel, record['pid'], record['tries'], ASTRA_QUIT))
		else:
			record['tries'] += 1
			record['at'] = time.time()
			save_memory(memory)
			astra.note('QUIT %s again: pid %d' % (channel, record['pid']))
			quietly(astra.type_into, record['pid'], ASTRA_QUIT)
	return lines


def astra_is_running(channel, record, cards, processes):
	"""A second window for one channel is what this must never open, so a process it cannot read counts as this Astra.
	A card outlives its window by up to a minute (the daemon keeps the thread), so the closed thread's own card only counts if it has acted since."""
	if any(command is None for _, command in processes) or astra.windows_of(channel, record['thread'], processes):
		return True
	closed_for = time.time() - record['closedAt']
	for card in cards:
		if astra.channel_of(card) == channel and card.get('isAlive'):
			quiet = seconds_since(card.get('lastEventAt'))
			if astra.thread_of(card) != record['thread'] or (quiet is not None and quiet < closed_for):
				return True
	return False


def reopen_closed_astras(db, cards, memory, processes):
	"""Memory is saved before the window opens: a watcher that dies in between must not open it again."""
	book = memory['astra']
	lines = []
	now = time.time()
	opens = book['opens'] = [opened for opened in book.get('opens', []) if now - opened[1] < ASTRA_REOPEN_PERIOD_SECONDS]
	for channel, record in list(book['closed'].items()):
		waiting = astra.undelivered(db, channel)
		if astra_is_running(channel, record, cards, processes):
			is_stuck = 'reopenedAt' in record and waiting
			if not is_stuck:
				del book['closed'][channel]
				astra.note('RUNNING %s: its window is up and its inbox read' % channel)
			elif now - record['reopenedAt'] > ASTRA_IDLE_CLOSE_SECONDS and not record.get('stuckSaid'):
				record['stuckSaid'] = True
				astra.note('NOT READING %s: reopened, but %d messages still unread' % (channel, len(waiting)))
				lines.append('ASTRA NOT READING %s: its window is up but %d messages are unread %d s after it reopened' % (channel, len(waiting), now - record['reopenedAt']))
			continue
		if not waiting or now - record.get('reopenedAt', 0) < ASTRA_REOPEN_RETRY_SECONDS:
			continue
		if sum(1 for opened in opens if opened[0] == channel) >= ASTRA_REOPENS_PER_CHANNEL or len(opens) >= ASTRA_REOPENS_ALL_CHANNELS:
			said = book.setdefault('capSaid', {})
			if now - said.get(channel, 0) >= ASTRA_REOPEN_PERIOD_SECONDS:
				said[channel] = now
				astra.note('CAPPED %s: %d windows opened this hour' % (channel, len(opens)))
				lines.append('ASTRA REOPEN CAP %s: a message is waiting, but too many windows opened this hour (%d); not opening another yet' % (channel, len(opens)))
			continue
		is_first_try = 'reopenedAt' not in record
		record['reopenedAt'] = now
		opens.append([channel, now])
		save_memory(memory)
		if is_first_try and astra.can_resume(record['thread']):
			astra.note('REOPEN %s: resuming thread %s' % (channel, record['thread']))
			astra.resume_window(channel, record['thread'])
		else:
			astra.note('REOPEN %s: thread %s cannot be resumed, opening a new one' % (channel, record['thread']))
			quietly(astra.hand_over, channel, False, True)
	return lines


def wake_idle_astras(db, cards, memory, processes):
	"""A Codex reads its inbox only when told to, so a partner's post to an Astra whose turn has ended sits unread until something types the join line into its window."""
	woken = memory['astra'].setdefault('woken', {})
	for card in cards:
		channel = astra.channel_of(card)
		is_idle = card.get('isAlive') and card.get('state') == ASTRA_CLOSABLE_STATE and not card.get('tool') and not card.get('subagentsRunning') and not card.get('question')
		if not channel or not is_idle or not astra.undelivered(db, channel) or time.time() - woken.get(channel, 0) < ASTRA_WAKE_RETRY_SECONDS:
			continue
		windows = astra.windows_of(channel, astra.thread_of(card), processes)
		if len(windows) == 1:
			woken[channel] = time.time()
			astra.note('WOKE %s: pid %d has unread mail' % (channel, windows[0]))
			quietly(astra.type_into, windows[0], astra.join_prompt(channel))


def look_after_astras(feed, cards, memory):
	"""A finished Astra closes and a message brings it back. A close wakes nobody; what it did is in Saved/Orchestration/astra/closed.log."""
	book = memory.setdefault('astra', {})
	book.setdefault('closing', {})
	book.setdefault('closed', {})
	is_candidate = any(astra.channel_of(card) and card.get('isAlive') and card.get('state') == ASTRA_CLOSABLE_STATE for card in cards)
	if not (is_candidate or book['closing'] or book['closed']):
		return []
	with contextlib.closing(astra.connect()) as db:
		processes = astra.codex_processes()
		wake_idle_astras(db, cards, memory, processes)
		lines = settle_closing(db, feed, cards, memory, processes)
		close_finished_astras(db, feed, cards, memory, processes)
		return lines + reopen_closed_astras(db, cards, memory, processes)


def report_astras():
	"""What the rule says about each Astra just now, for a person asking why one is still open."""
	memory = read_json(MEMORY) or {}
	feed = read_json(FEED) or {}
	cards = read_cards()
	book = memory.get('astra', {})
	with contextlib.closing(astra.connect()) as db:
		processes = astra.codex_processes()
		for card in cards:
			channel = astra.channel_of(card)
			if channel:
				windows = astra.windows_of(channel, astra.thread_of(card), processes)
				reason = why_astra_stays(db, channel, card, cards, feed, memory, processes) or 'WOULD CLOSE'
				print('%-28s %-12s windows %-16s %s' % (channel, card.get('state'), windows, reason))
		for channel, record in book.get('closed', {}).items():
			print('%-28s CLOSED       thread %s, %d undelivered' % (channel, record['thread'], len(astra.undelivered(db, channel))))
	print('kept: %s' % (', '.join(astra.kept()) or 'none'))
	return 0


def stalled_bridges(cards):
	"""An Astra whose turn ended and a partner only listening on its channel are each waiting for the other, and no mail moves to show it."""
	for card in cards:
		channel = astra.channel_of(card)
		quiet = seconds_since(card.get('lastEventAt'))
		if not channel or not card.get('isAlive') or card.get('state') != ASTRA_CLOSABLE_STATE or quiet is None or quiet < BRIDGE_STALL_SECONDS:
			continue
		for partner in cards:
			is_listening = any(bridge.get('channel') == channel and bridge.get('isListening') for bridge in partner.get('bridges') or [])
			partner_quiet = seconds_since(partner.get('lastEventAt'))
			if partner is not card and partner.get('isAlive') and is_listening and not partner.get('tool') and partner_quiet is not None and partner_quiet >= BRIDGE_STALL_SECONDS:
				yield channel, card, partner


def master_mail(memory):
	"""Mail for `agent:master-agent`, the address that reaches him whichever agent he is: a Codex has no hooks to show it, and a
	`msg --to master-agent` sent before that owner existed waits in the channel backlog of the same name."""
	try:
		db = astra.connect()
		try:
			rows = db.execute('SELECT id, text FROM inbox WHERE owner IN (?, ?) AND delivered!=1 ORDER BY id',
				('agent:' + MASTER_INBOX, astra.BACKLOG_PREFIX + MASTER_INBOX)).fetchall()
		finally:
			db.close()
	except sqlite3.Error:
		return []
	if not rows or rows[-1][0] <= memory.get('mailSeen', 0):
		return []
	memory['mailSeen'] = rows[-1][0]
	first = rows[-1][1].split('\n', 1)[0][:120]
	return ['MAIL %d for %s, newest: %s | read it: .claude/scripts/orch.cmd --as %s inbox %s' % (len(rows), MASTER_INBOX, first, MASTER_INBOX, MASTER_INBOX)]


def look(feed, cards, memory):
	"""The lines worth waking for, updating what has been seen."""
	lines = []
	said = memory.setdefault('said', {})

	def say(key, line):
		if time.time() - said.get(key, 0) >= REPEAT_SECONDS:
			said[key] = time.time()
			lines.append(line)

	at = feed.get('generatedAt') or 0
	if isinstance(at, (int, float)) and time.time() - at > FEED_STALE_SECONDS:
		say('stale', 'FEED STALE: orch-status.json is %d s old; is the daemon up?' % (time.time() - at))
	lane = feed.get('lane') if (feed.get('lane') or {}).get('id') else None
	queue = [q for q in feed.get('queue') or [] if q.get('id') and q.get('owner')]
	lane_queue = [q for q in queue if q.get('lane') == 'X']
	seen = set(memory.get('queue', []))
	for position, q in enumerate(queue):
		is_shorter_than_one_ahead = any((ahead.get('minutes') or 0) - (q.get('minutes') or 0) >= REORDER_MINUTES for ahead in queue[:position])
		if q['id'] not in seen and (q.get('kind') == 'Build' or is_shorter_than_one_ahead):
			lines.append('QUEUED %s %s ~%s min at #%s: %s | %s' % (q['id'][:8], q['kind'], q.get('minutes'), q.get('position'), label(q['owner'], cards), q.get('purpose', '')[:90]))
	memory['queue'] = [q['id'] for q in queue]
	current = lane['id'] if lane else None
	if current != memory.get('holder'):
		memory['holder'] = current
		if lane and queue:
			lines.append('GRANTED %s %s ~%s min, %d waiting: %s' % (lane['id'][:8], lane['kind'], lane.get('minutes'), len(queue), label(lane['owner'], cards)))
	is_master_holding = lane is not None and (card_for(lane['owner'], cards) or {}).get('name') == MASTER_AGENT_NAME
	if lane and queue and not is_master_holding and not (feed.get('hold') or {}).get('on'):
		held = time.time() - (lane.get('acquiredAt') or time.time())
		card = card_for(lane['owner'], cards)
		quiet = seconds_since(card.get('lastEventAt')) if card else None
		is_pie_running = (feed.get('pie') or {}).get('running')
		is_log_moving = editor_log_is_moving()
		is_asleep = card is not None and not card.get('tool') and not card.get('subagentsRunning') and not is_pie_running and not is_log_moving and quiet is not None and quiet > SILENT_HOLD_SECONDS and held > SILENT_HOLD_SECONDS
		if is_asleep:
			say('silent:' + lane['id'], 'SILENT HOLD %s %s: no tool running and nothing from it for %d s, held %d s, %d waiting: %s' % (lane['id'][:8], lane['kind'], quiet, held, len(queue), label(lane['owner'], cards)))
		is_past_declared = held > (lane.get('minutes') or 15) * 60 * LONG_HOLD_FACTOR
		is_blocking_others = held > LONG_HOLD_BLOCKING_SECONDS and len(queue) >= LONG_HOLD_BLOCKING_WAITERS
		if (is_past_declared or is_blocking_others) and 'long:' + lane['id'] not in said:
			say('long:' + lane['id'], 'LONG HOLD %s %s: %d min against %s declared, %d waiting: %s' % (lane['id'][:8], lane['kind'], held // 60, lane.get('minutes'), len(queue), label(lane['owner'], cards)))
	for line in new_crash_reports(memory):
		lines.append(line)
	lines += master_mail(memory)
	for pid, title in editor_dialogs():
		say('dialog:%d' % pid, 'EDITOR DIALOG pid %d: "%s" is waiting for a click, and everything on the lane waits on it' % (pid, title))
	for pid, frozen_for in frozen_editors():
		say('frozen:%d' % pid, 'FROZEN EDITOR pid %d: its window has not answered for %d s; everything on the lane waits on it' % (pid, frozen_for))
	lines += hand_over_bridges(cards, memory)
	for channel, card, partner in stalled_bridges(cards):
		key = 'stall:%s:%s' % (channel, card.get('lastEventAt'))
		if key not in said:
			say(key, 'BRIDGE STALL %s: Astra pid %s finished at %s and #%s only waits on the channel' % (channel, card.get('pid'), (card.get('lastEventAt') or '')[11:19], partner.get('number')))
	try:
		lines += look_after_astras(feed, cards, memory)
	except Exception as error:
		say('astra', 'ASTRA WATCH FAILED: %r' % error)
	if (lane or queue) and time.time() - memory.get('improvedAt', 0) >= IMPROVE_EVERY_SECONDS:
		memory['improvedAt'] = time.time()
		lines.append('IMPROVE: %d h since the last look at where the time goes' % (IMPROVE_EVERY_SECONDS // 3600))
	is_user_holding = (feed.get('hold') or {}).get('on')
	if queue and not lane and feed.get('occupants') and not is_user_holding and seconds_since_user_input() > USER_AT_KEYBOARD_SECONDS:
		say('occupant', 'OCCUPANT, %d queued behind it: %s' % (len(queue), '; '.join(str(o) for o in feed['occupants'])[:200]))
	is_truly_free = not lane and not feed.get('occupants') and not feed.get('grants') and not is_user_holding
	if lane_queue and is_truly_free:
		memory['freeSince'] = memory.get('freeSince') or time.time()
		if time.time() - memory['freeSince'] > UNCLAIMED_LANE_SECONDS:
			say('unclaimed', 'UNCLAIMED LANE: free for %d s with %d queued, head %s: %s' % (time.time() - memory['freeSince'], len(lane_queue), lane_queue[0]['id'][:8], label(lane_queue[0]['owner'], cards)))
	else:
		memory['freeSince'] = None
	return lines


def main():
	sys.stdout.reconfigure(encoding='utf-8', errors='replace')
	if '--astras' in sys.argv:
		return report_astras()
	memory = read_json(MEMORY) or {}
	with open(WATCHER_PID, 'w', encoding='ascii') as f:
		f.write(str(os.getpid()))
	feed_seen_at = time.time()
	routine = []
	while is_newest_watcher():
		feed = read_json(FEED)
		if feed:
			feed_seen_at = time.time()
			before = json.dumps(memory, sort_keys=True)
			lines = look(feed, read_cards(), memory)
			routine += [line for line in lines if line.startswith(ROUTINE_LINES)]
			needs_a_look = [line for line in lines if not line.startswith(ROUTINE_LINES)]
			if needs_a_look:
				print('\n'.join(routine + needs_a_look), flush=True)
			if json.dumps(memory, sort_keys=True) != before:
				save_memory(memory)
			if needs_a_look:
				return 0
		elif time.time() - feed_seen_at > FEED_STALE_SECONDS:
			print('FEED STALE: orch-status.json is missing or unreadable; is the daemon up?', flush=True)
			return 0
		time.sleep(POLL_SECONDS)
	return 0


if __name__ == '__main__':
	sys.exit(main())
