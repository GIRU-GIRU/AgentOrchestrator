"""Where the shared editor lane's time goes.

Reads the orchestrator journal (Saved/Orchestration/orch-events.log) for every lane grant, queue wait and lone lane
command, and the holder's Claude transcript (with its subagents, from every project folder under ~/.claude/projects) for
what it did while it held the lane. Read-only; touches no lane.

	python .claude/skills/orchestration-benchmark/bench.py [--days 7 | --since YYYY-MM-DD] [--until YYYY-MM-DD]
		[--owner <owner id prefix>] [--top 15] [--detail <grant id prefix>] [--json <path>]

--since and --until also take a time, YYYY-MM-DDTHH:MM:SSZ. Nothing the journal wrote after --until counts, so a past
window gives the same answer on any later day.

A hold is matched to the transcript whose tool call ran its `acquire` (one agent process can run several sessions); a
hold with no tool call of its holder inside it (Codex, a deleted transcript) shows in the lane totals only.
"""
import argparse
import datetime as dt
import glob
import io
import json
import os
import re
import sqlite3
import sys
import tokenize
from collections import Counter, defaultdict

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..'))
JOURNAL = os.path.join(ROOT, 'Saved', 'Orchestration', 'orch-events.log')
DB = os.path.join(ROOT, 'Saved', 'Orchestration', 'orch.db')
PROJECTS = os.path.expanduser(os.path.join('~', '.claude', 'projects'))
SCRATCH_ROOT = os.path.expanduser(os.path.join('~', 'AppData', 'Local', 'Temp', 'claude'))
SCRIPT_ROOTS = (os.path.join(ROOT, '.claude', 'scripts'), os.path.join(ROOT, 'Tools'), os.path.join(ROOT, 'Saved', 'Orchestration'))
SCRIPT_ROOT_SKIPS = {'frames', 'review', 'Logs', 'Crashes', '__pycache__', 'clips', 'video', 'videos', 'captures'}
LANE_KINDS = ('PIE', 'Build', 'Editor', 'Sync', 'Test')
LINE = re.compile(r'^(\S+Z) (\S+)\s+(\S+)\s+(.*)$')
TOOL_USE_ID = re.compile(r'<tool-use-id>(\w+)</tool-use-id>')

# What a holder's wall time is spent on, highest priority first: an instant covered by two calls counts once, as the
# first category in this list.
CATEGORIES = ('full build', 'editor restart', 'headless test', 'harness', 'compile check', 'waiting / polling',
	'orchestrator', 'p4', 'shell', 'subagent', 'read / edit', 'agent thinking', 'waiting on the user')
CATEGORY_RANK = {name: i for i, name in enumerate(CATEGORIES)}
QUICK_TOOLS = {'Read', 'Edit', 'Write', 'Grep', 'Glob', 'NotebookEdit', 'Skill', 'ToolSearch', 'TaskStop', 'Artifact',
	'SendMessage', 'ListAgents', 'AskUserQuestion', 'ScheduleWakeup', 'SendFeedback', 'SubagentHandback'}
COMMAND_TOOLS = ('Bash', 'PowerShell', 'Monitor')
LANE_CATEGORIES = ('full build', 'editor restart', 'headless test', 'harness')
PATCH_TOOLS = ('Edit', 'Write', 'MultiEdit', 'NotebookEdit')
PATCH_SUFFIXES = ('.cpp', '.h', '.py', '.ps1', '.sh', '.js')
SHELL_PATCH = re.compile(r"sed -i|\.replace\(|write_text|open\([^)]*['\"]w|cat > [^\n]*\.(?:py|sh|ps1|js)|patch\w*\.py", re.I)
USER_WAIT_GAP_SECONDS = 60.0
ACQUIRE_MATCH_SECONDS = 1800.0
PURPOSE_SNIPPET_CHARS = 48
BUILD_RESULT_LOOKAHEAD_SECONDS = 900.0
BUILD_REFUSED_SECONDS = 10.0
BACKGROUND_OUTPUT_TAIL_BYTES = 256 * 1024
SCRIPT_MAX_BYTES = 1024 * 1024
SCRIPT_MAX_DEPTH = 3
READ_ONLY_MARKER = 'ORCH: read-only'
READ_ONLY_HEADER_CHARS = 2000

SCRIPT_SUFFIXES = ('.py', '.sh', '.ps1', '.bat', '.cmd')
PYTHON_PROGRAMS = {'python', 'python3', 'python.exe', 'python3.exe', 'py', 'py.exe'}
SHELL_PROGRAMS = {'bash', 'bash.exe', 'sh', 'sh.exe', 'powershell', 'powershell.exe', 'pwsh', 'pwsh.exe', 'cmd', 'cmd.exe'}
ORCH_SCRIPTS = {'orch.cmd', 'orch.py'}
BUILD_PROGRAMS = {'build.bat', 'unrealbuildtool', 'unrealbuildtool.exe', 'unrealbuildtool.dll'}
SKIP_WORDS = {'if', 'then', 'else', 'elif', 'fi', 'do', 'done', 'while', 'until', '!', 'time', 'nohup', 'exec', 'env', 'command',
	'call', 'export', 'try', 'catch', 'finally', '&', '[void]', 'source', '.'}

HEREDOC = re.compile(r"<<-?\s*(['\"]?)(\w+)\1[^\n]*\n(.*?)(?:^[ \t]*\2[ \t]*$|\Z)", re.S | re.M)
PS_HERE_STRING = re.compile(r"@(['\"])\s*\n.*?\n\1@", re.S)
COMMENT_LINE = re.compile(r'(?m)^\s*#.*$')
SUBSTITUTION = re.compile(r'\$\(([^()]*)\)|`([^`\n]*)`')
WORD = re.compile(r"""(?:"(?:[^"\\]|\\.)*"|'[^']*'|[^\s"'])+""")
VARIABLE = re.compile(r'\$\{?(\w+)\}?')
PS_ASSIGN = re.compile(r'^\s*\$(\w+)\s*=\s*(.*)$', re.S)
BASH_ASSIGN = re.compile(r'^(\w+)=(.*)$', re.S)
HEADLESS_ARGS = re.compile(r'(?<![\w-])-(?:game|server)\b|RunTests|-ExecCmds=Automation', re.I)
COMPILE_ONLY_ARGS = re.compile(r'-singlefile|UnrealHeaderTool|-Mode=', re.I)
LOOP = re.compile(r'\bfor \S+ in\b|\buntil\b|\bwhile\b')
QUIT_CALL = re.compile(r'CloseMainWindow\(|\.WaitForExit\(')
PROCESS_CHECK = re.compile(r'\btasklist\b|\bGet-Process\b|\bGet-CimInstance\b', re.I)
EDITOR_PROCESS = re.compile(r'UnrealEditor|Get-Process -Id', re.I)
WAITS_ON_RESULT = re.compile(r'\[ -[fse] |\btest -[fse]\b|Test-Path|grep -q|Select-String|\.json|\.log\b|\.output|\.done\b|result|HOLD DONE', re.I)
PY_BUILD_TOOL = re.compile(r'Build\.bat|UnrealBuildTool')
PY_HARNESS = re.compile(r'ue_python|remote_execution|\bue\(|\beditor_up\b|\bharness\(')
PY_LAUNCH = re.compile(r'UnrealEditor(?:\.exe)?\b')
PY_SPAWNS = re.compile(r'Popen|Start-Process|startfile|subprocess|os\.system')
PY_WAITS = re.compile(r'time\.sleep')
PY_WAITS_ON = re.compile(r'\.exists\(|isfile\(|getsize\(|\.json|\.log\b')
SCRIPT_REFERENCE = re.compile(r'[\w./\\-]+\.(?:py|sh|ps1)\b')
BUILD_MARKER = re.compile(r'Result: (Succeeded|Failed)|(Target is up to date)|(Unable to build while Live Coding is active)|Total execution time: ([\d.]+)')
BACKGROUND_OUTPUT = re.compile(r'Output is being written to:?\s*(\S+\.output)')
LAUNCH_SAID = re.compile(r'launch', re.I)
RESTART_SCRIPT = re.compile(r'launch|restart|quit|editor_up', re.I)
PROBE_NAME = re.compile(r'^(?:\w+_)?(?:ping|alive|editor_up)\.py$|^(?:probe|status|ready|dirty|probe_editor|pie_check)\.py$', re.I)


def parse_time(text):
	return dt.datetime.fromisoformat(text.replace('Z', '+00:00')).timestamp()


def parse_bound(text, day_part):
	return parse_time(text if 'T' in text else text + day_part)


def fmt_time(t):
	return dt.datetime.fromtimestamp(t, dt.timezone.utc).strftime('%m-%d %H:%M')


def minutes(seconds):
	return seconds / 60.0


def read_journal(since, until):
	grants = {}
	implicit = []
	if not os.path.exists(JOURNAL):
		sys.exit('no journal at %s' % JOURNAL)
	with open(JOURNAL, encoding='utf-8', errors='replace') as f:
		for raw in f:
			m = LINE.match(raw.rstrip('\n'))
			if not m:
				continue
			stamp, event, owner, rest = m.groups()
			t = parse_time(stamp)
			if t >= until:
				continue
			parts = rest.split(' ', 2)
			gid = parts[0]
			if event == 'queue' and len(parts) >= 2:
				grants.setdefault(gid, {'id': gid})
				grants[gid].update(owner=owner, kind=parts[1], purpose=parts[2] if len(parts) > 2 else '', queued=t)
			elif event == 'grant' and len(parts) >= 2:
				g = grants.setdefault(gid, {'id': gid, 'queued': t})
				g.update(owner=owner, kind=parts[1], granted=t)
				g.setdefault('purpose', re.sub(r' \([a-z -]+\)$', '', parts[2] if len(parts) > 2 else ''))
			elif event == 'release' and gid in grants:
				grants[gid]['released'] = t
				grants[gid]['reason'] = rest.split(': ', 1)[1] if ': ' in rest else ''
			elif event in ('cancel', 'abandon') and gid in grants and 'granted' not in grants[gid]:
				grants[gid]['dropped'] = t
			elif event == 'implicit' and len(parts) >= 2:
				kind = parts[1].rstrip(':')
				if kind in LANE_KINDS:
					implicit.append({'time': t, 'owner': owner, 'kind': kind, 'command': parts[2] if len(parts) > 2 else ''})
	lane = [g for g in grants.values() if g.get('kind') in LANE_KINDS and since <= g.get('queued', 0) < until]
	implicit = [c for c in implicit if since <= c['time'] < until]
	return lane, implicit


def read_owners():
	owners = {}
	if not os.path.exists(DB):
		return owners
	db = sqlite3.connect('file:%s?mode=ro' % DB.replace('\\', '/'), uri=True)
	for owner, transcript, title in db.execute('SELECT owner, transcript, title FROM owners'):
		owners[owner] = {'transcript': transcript or '', 'title': title or ''}
	return owners


def base_name(path):
	return re.split(r'[\\/]', path.rstrip('\\/'))[-1]


def native_path(path):
	m = re.match(r'^/([a-zA-Z])/(.*)$', path)
	path = '%s:/%s' % (m.group(1), m.group(2)) if m else path
	return path.replace('\\\\', '/').replace('\\', '/')


class ScriptIndex:
	"""A script a command names, found by path, then in its session's scratchpad, then by file name under .claude/scripts, Tools and Saved/Orchestration."""

	def __init__(self):
		self.by_name = None
		self.scratch = {}

	def _walk(self, root, skips):
		found = defaultdict(list)
		for folder, subfolders, files in os.walk(root):
			subfolders[:] = [s for s in subfolders if s not in skips]
			for name in files:
				if name.lower().endswith(SCRIPT_SUFFIXES):
					found[name.lower()].append(os.path.join(folder, name))
		return found

	def _scratch_of(self, session):
		if not session:
			return {}
		if session not in self.scratch:
			folders = glob.glob(os.path.join(SCRATCH_ROOT, '*', glob.escape(session), 'scratchpad'))
			merged = defaultdict(list)
			for folder in folders:
				for name, paths in self._walk(folder, SCRIPT_ROOT_SKIPS).items():
					merged[name] += paths
			self.scratch[session] = merged
		return self.scratch[session]

	def _named(self):
		if self.by_name is None:
			self.by_name = defaultdict(list)
			for root in SCRIPT_ROOTS:
				for name, paths in self._walk(root, SCRIPT_ROOT_SKIPS).items():
					self.by_name[name] += paths
		return self.by_name

	def find(self, word, cwd, session):
		path = native_path(word)
		candidates = [path] if os.path.isabs(path) else [os.path.join(cwd or ROOT, path), os.path.join(ROOT, path)]
		for candidate in candidates:
			if os.path.isfile(candidate):
				return candidate
		name = base_name(path).lower()
		hits = self._scratch_of(session).get(name) or self._named().get(name)
		return hits[0] if hits else None


SCRIPTS = ScriptIndex()
SCRIPT_FACTS = {}
WRITTEN_SCRIPTS = {}


def strip_python_noise(text):
	"""Python source with its comments and docstrings out, so a usage note naming a tool does not read as running it."""
	kept = []
	last_significant = None
	try:
		for token in tokenize.generate_tokens(io.StringIO(text).readline):
			if token.type in (tokenize.COMMENT, tokenize.NL):
				continue
			is_bare_string = token.type == tokenize.STRING and last_significant in (None, tokenize.NEWLINE, tokenize.INDENT, tokenize.DEDENT)
			if not is_bare_string:
				if token.type == tokenize.NAME and last_significant == tokenize.NAME:
					kept.append(' ')
				kept.append(token.string)
			last_significant = token.type
	except (tokenize.TokenError, SyntaxError, IndentationError):
		return COMMENT_LINE.sub('', text)
	return ''.join(kept)


def python_facts(text, cwd, session, depth):
	code = strip_python_noise(text)
	facts = Counter()
	if PY_BUILD_TOOL.search(code):
		facts['compile' if COMPILE_ONLY_ARGS.search(code) else 'build'] += 1
	if PY_HARNESS.search(code):
		facts['harness'] += 1
	if PY_LAUNCH.search(code) and PY_SPAWNS.search(code):
		facts['launch'] += 1
	if facts['harness']:
		facts['harness_run'] += 1
	if PY_WAITS.search(code):
		facts['sleep'] += 1
		if PY_WAITS_ON.search(code):
			facts['result_wait'] += 1
	if depth < SCRIPT_MAX_DEPTH:
		for ref in set(SCRIPT_REFERENCE.findall(code)):
			if base_name(ref).lower() == 'ue_python.py':
				continue
			inner = script_facts(ref, cwd, session, depth + 1)
			for key in ('build', 'compile', 'harness', 'harness_run', 'launch', 'quit', 'headless'):
				facts[key] += inner[key]
	return facts


def script_facts(word, cwd, session, depth):
	path = SCRIPTS.find(word, cwd, session)
	if not path:
		body = WRITTEN_SCRIPTS.get((session, base_name(word).lower()))
		if body is None:
			return Counter()
		is_python = word.lower().endswith('.py')
		return python_facts(body, cwd, session, depth) if is_python else command_facts(body, cwd, session, depth)
	key = os.path.normcase(path)
	if key not in SCRIPT_FACTS:
		SCRIPT_FACTS[key] = Counter()
		try:
			if os.path.getsize(path) <= SCRIPT_MAX_BYTES:
				with open(path, encoding='utf-8', errors='replace') as f:
					text = f.read()
				folder = os.path.dirname(path)
				if path.lower().endswith('.py'):
					SCRIPT_FACTS[key] = python_facts(text, folder, session, depth)
				else:
					SCRIPT_FACTS[key] = command_facts(text, folder, session, depth)
				if RESTART_SCRIPT.search(base_name(path)):
					SCRIPT_FACTS[key]['harness_run'] = 0
		except OSError:
			pass
	return SCRIPT_FACTS[key]


def split_statements(text):
	"""The simple commands of a Bash or PowerShell line. Quoted text stays whole."""
	statements = []
	current = []
	quote = None
	i = 0
	while i < len(text):
		ch = text[i]
		if quote:
			current.append(ch)
			if ch == '\\' and quote == '"' and i + 1 < len(text):
				current.append(text[i + 1])
				i += 1
			elif ch == quote:
				quote = None
		elif ch in '\'"':
			quote = ch
			current.append(ch)
		elif ch in ';\n|(){}' or (ch == '&' and text[i - 1:i] != '>' and text[i + 1:i + 2] != '>'):
			statements.append(''.join(current))
			current = []
		else:
			current.append(ch)
		i += 1
	statements.append(''.join(current))
	return [s.strip() for s in statements if s.strip()]


def split_words(statement):
	words = []
	for word in WORD.findall(statement):
		is_quoted = len(word) >= 2 and word[0] == word[-1] and word[0] in '\'"'
		words.append(word[1:-1] if is_quoted else word.replace('"', '').replace("'", ''))
	return words


def expand(text, variables):
	return VARIABLE.sub(lambda m: variables.get(m.group(1).lower(), m.group(0)), text)


def command_facts(text, cwd, session, depth):
	"""What a shell command or shell script really runs: its statements' programs, never text that merely names a tool."""
	facts = Counter()
	written = {}

	def take_heredoc(m):
		line_start = text.rfind('\n', 0, m.start()) + 1
		prefix = text[line_start:m.start()]
		body = m.group(3)
		redirect = re.search(r'(?:>|tee\s)\s*("[^"]*"|\'[^\']*\'|\S+)\s*$', prefix)
		if redirect:
			written[base_name(redirect.group(1).strip('"\'')).lower()] = body
		elif re.search(r'python[\w.]*["\']?(?:\s+-\S*)*\s*$', prefix) and depth < SCRIPT_MAX_DEPTH:
			facts.update(python_facts(body, cwd, session, depth + 1))
		elif re.search(r'\b(?:bash|sh)["\']?(?:\s+-\S*)*\s*$', prefix) and depth < SCRIPT_MAX_DEPTH:
			facts.update(command_facts(body, cwd, session, depth + 1))
		return ' '

	stripped = HEREDOC.sub(take_heredoc, text)
	stripped = PS_HERE_STRING.sub(' ', stripped)
	stripped = COMMENT_LINE.sub('', stripped)
	inner = [a or b for a, b in SUBSTITUTION.findall(stripped)]
	variables = {}
	here = cwd
	for statement in split_statements(stripped) + inner:
		here = note_statement(statement, variables, here, session, written, facts, depth)
	if QUIT_CALL.search(stripped):
		facts['quit'] += 1
	if facts['launch'] and HEADLESS_ARGS.search(stripped):
		facts['headless'] += facts['launch']
		facts['launch'] = 0
	if facts['sleep'] and PROCESS_CHECK.search(stripped) and EDITOR_PROCESS.search(stripped):
		facts['process_wait'] += 1
	if facts['sleep'] and WAITS_ON_RESULT.search(stripped):
		facts['result_wait'] += 1
	if facts['harness'] and LOOP.search(stripped):
		facts['poll'] += 1
	return facts


def note_statement(statement, variables, cwd, session, written, facts, depth):
	"""Adds one statement's program to `facts` and returns the working directory after it."""
	ps = PS_ASSIGN.match(statement)
	if ps:
		value = ps.group(2).strip()
		if re.match(r'^("[^"]*"|\'[^\']*\')$', value):
			variables[ps.group(1).lower()] = value[1:-1]
			return cwd
		statement = value
	words = split_words(statement)
	while words:
		bash = BASH_ASSIGN.match(words[0])
		if bash and not words[0].startswith('-'):
			variables[bash.group(1).lower()] = expand(bash.group(2), variables)
			words = words[1:]
		elif words[0].lower() in SKIP_WORDS:
			words = words[1:]
		elif words[0].lower() == 'timeout' and len(words) > 1:
			words = words[1:]
			while words and re.match(r'^-|^\d', words[0]):
				words = words[1:]
		else:
			break
	if not words:
		return cwd
	words = [expand(w, variables) for w in words]
	if ' ' in words[0] and not os.path.exists(native_path(words[0])):
		words = split_words(words[0]) + words[1:]
	program = base_name(words[0]).lower()
	args = words[1:]
	if program in ('cd', 'set-location', 'pushd', 'sl') and args:
		target = native_path(args[0])
		return target if os.path.isabs(target) else os.path.join(cwd or ROOT, target)
	if program in ('start', 'start-process'):
		targets = [w for w in args if w and not w.startswith('-')]
		if targets:
			words = targets
			program = base_name(words[0]).lower()
			args = words[1:]
	joined = ' '.join(args)
	if program in BUILD_PROGRAMS or (program in ('dotnet', 'dotnet.exe') and 'unrealbuildtool' in joined.lower()):
		facts['compile' if COMPILE_ONLY_ARGS.search(joined) else 'build'] += 1
	elif program.startswith('unrealeditor'):
		facts['headless' if HEADLESS_ARGS.search(joined) else 'launch'] += 1
	elif program in PYTHON_PROGRAMS:
		note_python(args, cwd, session, written, facts, depth)
	elif program in SHELL_PROGRAMS:
		note_shell(args, cwd, session, written, facts, depth)
	elif program in ORCH_SCRIPTS:
		facts['orch'] += 1
	elif program.endswith(SCRIPT_SUFFIXES):
		note_script(words[0], cwd, session, written, facts, depth)
	elif program in ('sleep', 'start-sleep'):
		facts['sleep'] += 1
	elif program in ('p4', 'p4.exe'):
		facts['p4'] += 1
	elif program in ('curl', 'curl.exe') and '127.0.0.1:8000' in joined:
		facts['harness'] += 1
		facts['scripts'] += 1
	return cwd


def is_editor_probe(word, cwd, session):
	"""A script that only asks the editor whether it is up: waiting for it belongs to the restart, not to a test."""
	if PROBE_NAME.search(base_name(word)):
		return True
	path = SCRIPTS.find(word, cwd, session) if word else None
	if not path:
		return False
	try:
		with open(path, encoding='utf-8', errors='replace') as f:
			text = f.read(READ_ONLY_HEADER_CHARS + 1)
	except OSError:
		return False
	return READ_ONLY_MARKER in text and len(text) <= READ_ONLY_HEADER_CHARS


def note_python(args, cwd, session, written, facts, depth):
	for i, word in enumerate(args):
		if word == '-c' and i + 1 < len(args):
			if depth < SCRIPT_MAX_DEPTH:
				facts.update(python_facts(args[i + 1], cwd, session, depth + 1))
			return
		if word.startswith('-'):
			continue
		if not word.lower().endswith(SCRIPT_SUFFIXES):
			return
		name = base_name(word).lower()
		if name == 'ue_python.py':
			facts['harness'] += 1
			facts['scripts'] += 1
			target = next((w for w in args[i + 1:] if not w.startswith('-')), '')
			if 'quit' in base_name(target).lower():
				facts['quit'] += 1
			elif not is_editor_probe(target, cwd, session):
				facts['harness_run'] += 1
		elif name == 'wait_result.py':
			facts['harness'] += 1
			facts['harness_run'] += 1
		elif name in ORCH_SCRIPTS:
			facts['orch'] += 1
		else:
			note_script(word, cwd, session, written, facts, depth)
		return


def note_shell(args, cwd, session, written, facts, depth):
	for i, word in enumerate(args):
		lowered = word.lower()
		if lowered in ('-c', '-command', '//c', '/c') and i + 1 < len(args):
			if depth < SCRIPT_MAX_DEPTH:
				facts.update(command_facts(' '.join(args[i + 1:]), cwd, session, depth + 1))
			return
		if lowered.endswith(SCRIPT_SUFFIXES):
			note_script(word, cwd, session, written, facts, depth)
			return


def note_script(word, cwd, session, written, facts, depth):
	if depth >= SCRIPT_MAX_DEPTH:
		return
	body = written.get(base_name(word).lower())
	if body is not None:
		is_python = word.lower().endswith('.py')
		facts.update(python_facts(body, cwd, session, depth + 1) if is_python else command_facts(body, cwd, session, depth + 1))
	else:
		facts.update(script_facts(word, cwd, session, depth + 1))


def category_of(facts, with_build=True):
	if with_build and facts['build']:
		return 'full build'
	if facts['harness_run']:
		return 'harness'
	if facts['quit'] or facts['launch'] or facts['process_wait']:
		return 'editor restart'
	if facts['headless']:
		return 'headless test'
	if facts['harness'] or (facts['sleep'] and facts['result_wait']):
		return 'harness'
	if facts['compile']:
		return 'compile check'
	if facts['sleep']:
		return 'waiting / polling'
	if facts['orch']:
		return 'orchestrator'
	if facts['p4']:
		return 'p4'
	return 'shell'


def classify(name, tool_input, cwd=None, session=None):
	"""Category and facts of one tool call."""
	if name in QUICK_TOOLS:
		return 'read / edit', Counter()
	if name == 'Agent':
		return 'subagent', Counter()
	if name.startswith('mcp__unreal'):
		return 'harness', Counter(harness=1, scripts=1)
	if name not in COMMAND_TOOLS:
		return 'shell', Counter()
	facts = command_facts(str(tool_input.get('command', '')), cwd or ROOT, session, 0)
	return category_of(facts), facts


def result_text(block):
	content = block.get('content')
	if isinstance(content, list):
		return ' '.join(str(c.get('text', '')) for c in content if isinstance(c, dict))
	return str(content or '')


def is_prompt(d, content):
	if d.get('type') != 'user' or d.get('isMeta'):
		return False
	if isinstance(content, str):
		text = content
	elif isinstance(content, list) and any(isinstance(b, dict) and b.get('type') == 'text' for b in content):
		text = ' '.join(b.get('text', '') for b in content if isinstance(b, dict))
	else:
		return False
	return not text.lstrip().startswith(('<task-notification', '<system-reminder', '<command-', '<local-command'))


def build_markers(text):
	"""What a build tool printed, in order: ('result', word), ('uptodate',), ('refused',), ('seconds', value)."""
	found = []
	for m in BUILD_MARKER.finditer(text):
		if m.group(1):
			found.append(('result', m.group(1)))
		elif m.group(2):
			found.append(('uptodate', ''))
		elif m.group(3):
			found.append(('refused', ''))
		else:
			found.append(('seconds', float(m.group(4))))
	return found


def read_transcript(path):
	"""Tool calls (start, end, command, build markers, ...) sorted by start, and the times of the human's prompts."""
	calls = {}
	prompts = []
	done_at = {}
	session = os.path.splitext(os.path.basename(path))[0]
	files = [path] + sorted(glob.glob(os.path.join(os.path.splitext(path)[0], 'subagents', '*.jsonl')))
	for file in files:
		with open(file, encoding='utf-8', errors='replace') as f:
			for raw in f:
				try:
					d = json.loads(raw)
				except ValueError:
					continue
				stamp = d.get('timestamp')
				if not stamp:
					continue
				t = parse_time(stamp)
				if d.get('type') == 'queue-operation':
					m = TOOL_USE_ID.search(str(d.get('content', '')))
					if m and d.get('operation') == 'enqueue':
						done_at[m.group(1)] = t
					continue
				content = (d.get('message') or {}).get('content')
				if isinstance(content, str) and '<task-notification' in content:
					m = TOOL_USE_ID.search(content)
					if m:
						done_at.setdefault(m.group(1), t)
				if file == path and is_prompt(d, content):
					prompts.append(t)
				if not isinstance(content, list):
					continue
				for b in content:
					if not isinstance(b, dict):
						continue
					if b.get('type') == 'tool_use' and d.get('type') == 'assistant':
						tool_input = b.get('input') or {}
						written_to = str(tool_input.get('file_path', ''))
						if b.get('name') == 'Write' and written_to.lower().endswith(SCRIPT_SUFFIXES):
							WRITTEN_SCRIPTS[(session, base_name(written_to).lower())] = str(tool_input.get('content', ''))
						calls[b['id']] = {'id': b['id'], 'start': t, 'end': t, 'tool': b.get('name', ''), 'input': tool_input,
							'cwd': d.get('cwd'), 'session': session, 'batch': (d.get('message') or {}).get('id') or d.get('requestId') or b['id'],
							'background': bool(tool_input.get('run_in_background')), 'markers': [], 'output_file': '', 'said_launch': False,
							'failed': False, 'what': (str(tool_input.get('command', '')) or b.get('name', ''))[:110].replace('\n', ' ').replace('|', '/')}
					elif b.get('type') == 'tool_result' and b.get('tool_use_id') in calls:
						c = calls[b['tool_use_id']]
						c['end'] = max(c['end'], t)
						text = result_text(b)
						c['markers'] = build_markers(text)
						c['said_launch'] = bool(LAUNCH_SAID.search(text))
						m = BACKGROUND_OUTPUT.search(text)
						c['output_file'] = m.group(1) if m else ''
	for tid, t in done_at.items():
		if tid in calls and calls[tid]['background']:
			calls[tid]['end'] = max(calls[tid]['end'], t)
	ordered = sorted(calls.values(), key=lambda c: c['start'])
	batches = defaultdict(list)
	for c in ordered:
		batches[c['batch']].append(c)
	for siblings in batches.values():
		last_start = max(c['start'] for c in siblings)
		running = sum(1 for c in siblings if c['tool'] not in QUICK_TOOLS)
		for c in siblings:
			c['batch_size'] = len(siblings)
			c['batch_last_start'] = last_start
			c['batch_running'] = running
	return ordered, sorted(prompts)


def read_output_tail(path):
	try:
		with open(path, 'rb') as f:
			f.seek(0, os.SEEK_END)
			f.seek(max(0, f.tell() - BACKGROUND_OUTPUT_TAIL_BYTES))
			return f.read().decode('utf-8', 'replace')
	except OSError:
		return ''


def build_runs(markers):
	"""One entry per build tool run. A run ends at its execution time; a Result echoed after that is not a second run."""
	runs = []
	current = {}
	for kind, value in markers:
		if kind == 'result':
			current['result'] = value
		elif kind == 'uptodate':
			current['up_to_date'] = True
		elif kind == 'refused':
			current['refused'] = True
		else:
			current['seconds'] = value
			runs.append(current)
			current = {}
	if current.get('result') and not runs:
		runs.append(current)
	return runs


def settle_build(c, calls, seen_seconds):
	"""A build counts only when a real build command is matched to a result the build tool printed."""
	markers = c['markers']
	if c['background'] and c['output_file']:
		markers = build_markers(read_output_tail(c['output_file'])) or markers
	runs = build_runs(markers)[c['facts']['compile']:]
	runs = [r for r in runs if 'seconds' not in r or (c['session'], r['seconds']) not in seen_seconds]
	if not runs:
		for later in calls:
			if later['start'] <= c['start'] or later['start'] > c['end'] + BUILD_RESULT_LOOKAHEAD_SECONDS:
				continue
			if later is not c and later['tool'] in COMMAND_TOOLS and command_facts(str(later['input'].get('command', '')), later['cwd'] or ROOT, later['session'], 0)['build']:
				break
			fresh = [r for r in build_runs(later['markers']) if 'seconds' not in r or (c['session'], r['seconds']) not in seen_seconds]
			if fresh:
				runs = fresh
				break
	runs = runs[-max(1, c['facts']['build']):]
	for r in runs:
		if 'seconds' in r:
			seen_seconds.add((c['session'], r['seconds']))
	c['runs'] = runs
	c['failed'] = any(r.get('result') == 'Failed' or r.get('refused') for r in runs)


def settle_calls(calls, inside, seen_seconds):
	"""Category and time spans for the calls a hold covers. A chained call shares its time: the build tool's own stated
	seconds count as the build, and the rest of the call goes to whatever else it ran."""
	for c in inside:
		if 'category' in c:
			continue
		c['category'], c['facts'] = classify(c['tool'], c['input'], c['cwd'], c['session'])
		c['scripts'] = c['facts']['scripts']
		c['poll'] = bool(c['facts']['poll'])
		f = c['facts']
		only_watches_a_process = f['process_wait'] and not (f['quit'] or f['launch'] or f['harness_run'] or f['build'])
		is_bystander = c['background'] and (c['category'] == 'shell' or only_watches_a_process)
		c['spans'] = [] if is_bystander else [(c['start'], c['end'], c['category'])]
		direct = command_facts(HEREDOC.sub(' ', str(c['input'].get('command', ''))), c['cwd'] or ROOT, c['session'], SCRIPT_MAX_DEPTH) if c['tool'] in COMMAND_TOOLS else Counter()
		c['launch'] = bool(direct['launch'] or (c['facts']['launch'] and c['said_launch']))
		if not c['facts']['build']:
			continue
		settle_build(c, calls, seen_seconds)
		rest = category_of(c['facts'], with_build=False)
		if not c['runs'] and not direct['build']:
			c['category'] = rest
			c['spans'] = [(c['start'], c['end'], rest)]
			continue
		stated = sum(r.get('seconds', 0.0) for r in c['runs'])
		if stated > 0:
			cut = min(c['end'], c['start'] + stated)
			c['spans'] = [(c['start'], cut, 'full build'), (cut, c['end'], rest)]
		elif rest == 'harness':
			c['category'] = 'harness'
			c['spans'] = [(c['start'], c['end'], 'harness')]


def acquire_snippet(purpose):
	text = re.split(r'["\\]', purpose or '')[0]
	return text[:PURPOSE_SNIPPET_CHARS] if len(text) >= 16 else ''


def project_folders():
	return [p for p in glob.glob(os.path.join(glob.escape(PROJECTS), '*')) if os.path.isdir(p)]


def session_of(path):
	"""A fork's transcript belongs to its session, whose reader takes in the session's subagents folder."""
	parent = os.path.dirname(path)
	return os.path.dirname(parent) + '.jsonl' if os.path.basename(parent) == 'subagents' else path


def find_acquirers(lane, folders, since):
	"""Grant id -> the transcript whose tool call ran `acquire` with that grant's purpose, closest before its queueing."""
	wanted = {g['id']: acquire_snippet(g.get('purpose', '')) for g in lane}
	wanted = {k: v for k, v in wanted.items() if v}
	found = {}
	for folder in folders:
		sessions = glob.glob(os.path.join(glob.escape(folder), '*.jsonl'))
		forks = glob.glob(os.path.join(glob.escape(folder), '*', 'subagents', '*.jsonl'))
		for path in sessions + forks:
			if os.path.getmtime(path) < since:
				continue
			with open(path, 'rb') as f:
				data = f.read()
			hits = [gid for gid, snip in wanted.items() if snip.encode('utf-8') in data]
			if not hits:
				continue
			for raw in data.split(b'\n'):
				if b'acquire' not in raw or b'tool_use' not in raw:
					continue
				try:
					d = json.loads(raw)
				except ValueError:
					continue
				if d.get('type') != 'assistant' or not d.get('timestamp'):
					continue
				t = parse_time(d['timestamp'])
				for b in (d.get('message') or {}).get('content') or []:
					if not isinstance(b, dict) or b.get('type') != 'tool_use':
						continue
					cmd = str((b.get('input') or {}).get('command', ''))
					if 'acquire' not in cmd:
						continue
					for gid in hits:
						g = next(x for x in lane if x['id'] == gid)
						delta = g.get('queued', g.get('granted', 0)) - t
						if wanted[gid] in cmd and -60 <= delta <= ACQUIRE_MATCH_SECONDS and (gid not in found or delta < found[gid][1]):
							found[gid] = (session_of(path), delta)
	return {gid: path for gid, (path, _) in found.items()}


def queued_others(queue, t, owner):
	return sum(1 for begin, end, who in queue if begin <= t < end and who != owner)


def stamp_is_ambiguous(call, at, name):
	"""True while a batch's stamp cannot say what the time was: the model was still writing the batch, or two of its calls
	could have run. The build tool's own stated seconds are not in doubt."""
	if call.get('batch_size', 1) < 2 or (name == 'full build' and call.get('runs')):
		return False
	return at < call['batch_last_start'] or call['batch_running'] > 1


def split_window(start, end, calls, prompts, queue=(), owner=None):
	"""For [start, end]: seconds, agent-seconds queued behind and batch-stamped seconds per category. Uncovered time is
	thinking, or waiting on the user when a prompt ends it."""
	inside = [c for c in calls if c['end'] > start and c['start'] < end]
	spans = [(a, b, name, c) for c in inside for a, b, name in c.get('spans', [(c['start'], c['end'], c.get('category', 'shell'))]) if b > a]
	edges = {start, end}
	edges |= {max(start, a) for a, _, _, _ in spans if a < end} | {min(end, b) for _, b, _, _ in spans if b > start}
	edges |= {x for begin, stop, _ in queue for x in (begin, stop) if start < x < end}
	spent = defaultdict(float)
	behind = defaultdict(float)
	batched = defaultdict(float)
	ordered = sorted(edges)
	for a, b in zip(ordered, ordered[1:]):
		active = [(name, c) for x, y, name, c in spans if x <= a and y >= b]
		if active:
			name, winner = min(active, key=lambda item: CATEGORY_RANK[item[0]])
			if stamp_is_ambiguous(winner, a, name):
				batched[name] += b - a
		else:
			waited = b - a >= USER_WAIT_GAP_SECONDS and any(a <= p <= b + 1 for p in prompts)
			name = 'waiting on the user' if waited else 'agent thinking'
		spent[name] += b - a
		behind[name] += (b - a) * queued_others(queue, (a + b) / 2.0, owner)
	return spent, inside, behind, batched


def patch_after_lane(inside):
	"""How a hold changed a script or C++ after its first lane call: 'tool' through Edit or Write, 'text' only by a command's text, which can overcount."""
	starts = [c['start'] for c in inside if c.get('category') in LANE_CATEGORIES]
	if not starts:
		return ''
	later = [c for c in inside if c['start'] > min(starts)]
	if any(c['tool'] in PATCH_TOOLS and str(c['input'].get('file_path', '')).lower().endswith(PATCH_SUFFIXES) for c in later):
		return 'tool'
	if any(c['tool'] in COMMAND_TOOLS and SHELL_PATCH.search(str(c['input'].get('command', ''))) for c in later):
		return 'text'
	return ''


def analyse(args):
	now = dt.datetime.now(dt.timezone.utc).timestamp()
	since = parse_bound(args.since, 'T00:00:00Z') if args.since else now - args.days * 86400
	until = parse_bound(args.until, 'T23:59:59Z') if args.until else now
	lane, implicit = read_journal(since, until)
	owners = read_owners()
	if args.owner:
		lane = [g for g in lane if g.get('owner', '').startswith(args.owner)]
		implicit = [c for c in implicit if c['owner'].startswith(args.owner)]
	folders = sorted(set(project_folders()) | {os.path.dirname(o['transcript']) for o in owners.values() if o['transcript']})
	acquirers = find_acquirers(lane, folders, since)
	parsed = {}
	seen_seconds = set()

	def calls_in(path):
		if path not in parsed:
			parsed[path] = read_transcript(path) if path and os.path.exists(path) else ([], [])
		return parsed[path]

	held = [g for g in lane if 'granted' in g]
	for g in held:
		g['end'] = min(g.get('released', until), now, until)
		g['hold'] = g['end'] - g['granted']
		g['wait'] = g['granted'] - g.get('queued', g['granted'])
	queue = []
	for g in lane:
		begin = g.get('queued')
		end = g.get('granted', g.get('dropped', min(now, until)))
		if begin is not None and end > begin:
			queue.append((begin, end, g.get('owner')))
	# A queued entry's wait is charged to every other agent's grant it overlapped.
	for begin, end, who in queue:
		for h in held:
			if h['owner'] == who:
				continue
			overlap = min(end, h['end']) - max(begin, h['granted'])
			if overlap > 0:
				h['made_wait'] = h.get('made_wait', 0.0) + overlap

	totals = defaultdict(float)
	behind_totals = defaultdict(float)
	batched_totals = defaultdict(float)
	covered = 0.0
	for g in sorted(held, key=lambda g: g['granted']):
		g['transcript'] = acquirers.get(g['id']) or owners.get(g['owner'], {}).get('transcript', '')
		calls, prompts = calls_in(g['transcript'])
		overlapping = [c for c in calls if c['end'] > g['granted'] and c['start'] < g['end']]
		settle_calls(calls, overlapping, seen_seconds)
		split, inside, behind, batched = split_window(g['granted'], g['end'], calls, prompts, queue, g['owner'])
		if not inside:
			g['split'] = None
			continue
		g['split'], g['inside'], g['behind'], g['batched'] = split, inside, behind, batched
		g['patch_after_lane'] = patch_after_lane(inside)
		covered += g['hold']
		for k, v in split.items():
			totals[k] += v
		for k, v in behind.items():
			behind_totals[k] += v
		for k, v in batched.items():
			batched_totals[k] += v
	attribute_builds(held)

	for c in implicit:
		calls, _ = calls_in(owners.get(c['owner'], {}).get('transcript', ''))
		near = [x['end'] - x['start'] for x in calls if abs(x['start'] - c['time']) <= 5.0]
		c['seconds'] = max(near) if near else 0.0
	return dict(since=since, until=until, lane=lane, held=held, totals=totals, behind=behind_totals, batched=batched_totals,
		covered=covered, implicit=implicit, owners=owners)


def attribute_builds(held):
	"""A call two holds overlap (one ended, the next began mid-chain) counts in the hold it spent the most time in."""
	best = {}
	for g in held:
		for c in g.get('inside', []):
			share = min(c['end'], g['end']) - max(c['start'], g['granted'])
			if c['id'] not in best or share > best[c['id']][0]:
				best[c['id']] = (share, g['id'])
	for g in held:
		inside = g.get('inside', [])
		mine = [c for c in inside if best[c['id']][1] == g['id']]
		runs = [r for c in mine for r in c.get('runs', [])]
		g['builds'] = len(runs)
		g['failed_builds'] = sum(1 for r in runs if r.get('result') == 'Failed' or r.get('refused'))
		g['refused_builds'] = sum(1 for r in runs if r.get('refused') or (r.get('result') == 'Failed' and r.get('seconds', BUILD_REFUSED_SECONDS) < BUILD_REFUSED_SECONDS))
		g['up_to_date_builds'] = sum(1 for r in runs if r.get('up_to_date'))
		g['build_seconds'] = sum(r.get('seconds', 0.0) for r in runs)
		g['stamped_builds'] = sum(1 for r in runs if 'seconds' in r)
		g['launches'] = sum(1 for c in mine if c.get('launch'))
		g['script_calls'] = sum(max(1, c.get('scripts', 0)) for c in mine if c.get('category') == 'harness')
		g['poll_calls'] = sum(1 for c in mine if c.get('poll'))
		g['batched_calls'] = sum(1 for c in mine if c.get('batch_size', 1) > 1)


def short_owner(owner, owners):
	title = owners.get(owner, {}).get('title', '')
	tag = ':'.join(owner.split(':')[:2])
	return (title[:24] + ' ' if title else '') + '(' + tag + ')'


def transcript_folder(g):
	return os.path.basename(os.path.dirname(g['transcript'])) if g.get('split') is not None else 'no transcript'


def report(r, top):
	held, owners = r['held'], r['owners']
	print('# Lane benchmark %s to %s (UTC)\n' % (fmt_time(r['since']), fmt_time(r['until'])))
	by_kind = defaultdict(float)
	for g in held:
		by_kind[g['kind']] += g['hold']
	total_hold = sum(by_kind.values())
	waited = sum(g['wait'] for g in held) + sum(g['dropped'] - g['queued'] for g in r['lane'] if 'dropped' in g and 'queued' in g)
	agent_behind = sum(g.get('made_wait', 0.0) for g in held)
	print('Lane held %.1f h in %d grants (%s); agents queued %.1f h for it, %.0f agent-min of that behind another agent\'s hold.' % (total_hold / 3600, len(held),
		', '.join('%s %.1f h' % (k, v / 3600) for k, v in sorted(by_kind.items(), key=lambda kv: -kv[1])), waited / 3600, minutes(agent_behind)))
	print('Lone lane commands (implicit grants): %d, %.1f min.\n' % (len(r['implicit']), minutes(sum(c['seconds'] for c in r['implicit']))))
	with_t = [g for g in held if g.get('split') is not None]
	folders = defaultdict(lambda: [0, 0.0, 0.0])
	for g in held:
		row = folders[transcript_folder(g)]
		row[0] += 1
		row[1] += g['hold']
		row[2] += g.get('made_wait', 0.0)
	print('## Where the holders ran\n')
	print('| Transcript folder | Holds | Lane hours | Agent-min behind |')
	print('|---|---|---|---|')
	for name, row in sorted(folders.items(), key=lambda kv: -kv[1][2]):
		print('| %s | %d | %.1f | %.0f |' % (name, row[0], row[1] / 3600, minutes(row[2])))
	print()
	if r['covered']:
		behind_total = sum(r['behind'].values())
		print('## Inside the holds (%.1f h matched to a transcript, %.0f%% of the lane time)\n' % (r['covered'] / 3600, 100 * r['covered'] / max(total_hold, 1)))
		print('| Spent on | Minutes | Share | Agent-min behind | Share | Batched min |')
		print('|---|---|---|---|---|---|')
		for name in CATEGORIES:
			v = r['totals'].get(name, 0.0)
			if v >= 30:
				w = r['behind'].get(name, 0.0)
				print('| %s | %.0f | %.0f%% | %.0f | %.0f%% | %.0f |' % (name, minutes(v), 100 * v / r['covered'], minutes(w), 100 * w / max(behind_total, 1), minutes(r['batched'].get(name, 0.0))))
		builds = sum(g['builds'] for g in with_t)
		refused = sum(g['refused_builds'] for g in with_t)
		print('\nIn %d holds: %d full builds (%d failed, %d of them refused or inside 10 s, usually the editor still closing; %d up to date), the build tool\'s own time %.0f min over %d stamped builds; %d editor launches, %d harness calls (%d inside polling loops), %d tool calls.' % (
			len(with_t), builds, sum(g['failed_builds'] for g in with_t), refused, sum(g['up_to_date_builds'] for g in with_t), minutes(sum(g['build_seconds'] for g in with_t)),
			sum(g['stamped_builds'] for g in with_t), sum(g['launches'] for g in with_t), sum(g['script_calls'] for g in with_t), sum(g['poll_calls'] for g in with_t), sum(len(g['inside']) for g in with_t)))
		patched = [g for g in with_t if g['patch_after_lane']]
		print('Patched a script or C++ after the first lane call: %d of %d holds (%d through Edit or Write, the rest only by a command\'s text, which can overcount), with %.0f of %.0f agent-min behind them.' % (
			len(patched), len(with_t), sum(1 for g in patched if g['patch_after_lane'] == 'tool'), minutes(sum(g.get('made_wait', 0.0) for g in patched)), minutes(sum(g.get('made_wait', 0.0) for g in with_t))))
		print('Batched: %d calls were issued in one message with others, and every result in a batch stamps the slowest call\'s end. *Batched min* is where the category rests on such a stamp, so it may belong to a sibling or to the model writing the batch.\n' % sum(g['batched_calls'] for g in with_t))
	print('## Holds by the agent-minutes queued behind them\n')
	print('| Grant | Granted | Hold min | Agent-min behind | Kind | Holder | Builds (failed) | Harness calls | Top time | Purpose |')
	print('|---|---|---|---|---|---|---|---|---|---|')
	for g in sorted(held, key=lambda g: (-g.get('made_wait', 0.0), -g['hold']))[:top]:
		split = g.get('split')
		if split:
			best = sorted(split.items(), key=lambda kv: -kv[1])[:3]
			top_time = ', '.join('%s %.0f' % (k, minutes(v)) for k, v in best if v >= 30)
			builds, scripts = '%d (%d)' % (g['builds'], g['failed_builds']), str(g['script_calls'])
		else:
			top_time, builds, scripts = 'no transcript', '-', '-'
		print('| %s | %s | %.0f | %.0f | %s | %s | %s | %s | %s | %s |' % (g['id'], fmt_time(g['granted']), minutes(g['hold']),
			minutes(g.get('made_wait', 0.0)), g['kind'], short_owner(g['owner'], owners), builds, scripts, top_time,
			g.get('purpose', '')[:60].replace('|', '/')))
	waits = sorted([g for g in held if g['wait'] >= 60], key=lambda g: -g['wait'])[:top]
	if waits:
		print('\n## Longest waits for the lane\n')
		print('| Queued | Waited min | Kind | Waiter | Purpose |')
		print('|---|---|---|---|---|')
		for g in waits:
			print('| %s | %.0f | %s | %s | %s |' % (fmt_time(g['queued']), minutes(g['wait']), g['kind'],
				short_owner(g['owner'], owners), g.get('purpose', '')[:60].replace('|', '/')))


def detail(r, prefix):
	for g in [g for g in r['held'] if g['id'].startswith(prefix)]:
		print('# %s %s %s, held %.0f min from %s, %.0f agent-min behind it\n%s\n' % (g['id'], g['kind'], short_owner(g['owner'], r['owners']),
			minutes(g['hold']), fmt_time(g['granted']), minutes(g.get('made_wait', 0.0)), g.get('transcript', '')))
		if not g.get('split'):
			print('No tool call of the holder inside this hold.')
			continue
		last = g['granted']
		print('| At | Gap before s | Seconds | Batch | Category | Call |')
		print('|---|---|---|---|---|---|')
		for c in g['inside']:
			print('| %s | %.0f | %.0f | %s | %s | %s |' % (dt.datetime.fromtimestamp(c['start'], dt.timezone.utc).strftime('%H:%M:%S'),
				max(0.0, c['start'] - last), c['end'] - c['start'], c['batch_size'] if c['batch_size'] > 1 else '', c['category'], c['what']))
			last = max(last, c['end'])
		print('\n' + ', '.join('%s %.1f min (%.0f agent-min behind)' % (k, minutes(v), minutes(g['behind'].get(k, 0.0))) for k, v in sorted(g['split'].items(), key=lambda kv: -kv[1])))
		print('Batch: how many calls one message issued. Their results all stamp the slowest call\'s end.')


def main():
	parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
	parser.add_argument('--days', type=float, default=7.0)
	parser.add_argument('--since')
	parser.add_argument('--until')
	parser.add_argument('--owner')
	parser.add_argument('--top', type=int, default=15)
	parser.add_argument('--detail')
	parser.add_argument('--json')
	args = parser.parse_args()
	r = analyse(args)
	if args.detail:
		detail(r, args.detail)
	else:
		report(r, args.top)
	if args.json:
		out = {k: r[k] for k in ('since', 'until', 'covered')}
		out['totals'] = dict(r['totals'])
		out['behind'] = dict(r['behind'])
		out['batched'] = dict(r['batched'])
		out['held'] = [{k: v for k, v in g.items() if k != 'inside'} for g in r['held']]
		out['implicit'] = r['implicit']
		with open(args.json, 'w', encoding='utf-8') as f:
			json.dump(out, f, indent=1, default=str)


if __name__ == '__main__':
	main()
