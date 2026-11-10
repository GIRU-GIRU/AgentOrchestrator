"""Close the shared editor, run the full build and bring the editor back, under the agent's lane grant.

    python .claude/scripts/tools/editor_rebuild.py [--discard /Game/Path/Asset ...] [--check] [--no-relaunch]

--check     says what a close would do and changes nothing; needs no grant.
--discard   packages to throw away instead of refusing the close: reloaded from disk, or saved when
            they were never saved, because deleting an unsaved new asset leaves its package dirty.
            Only ever name work you own, or that its owner told you to discard.
--no-relaunch   leaves the editor closed after the build.
--relaunch-only brings a closed editor back, with no build: the Master Agent's recovery after a crash.
--restart-only closes and reopens the editor without compiling, with the same PIE/unsaved-work checks.

Exit codes: 0 done, 3 no lane grant, 4 build failed (the editor is back), 5 close refused (unsaved work,
named on the way out), 6 the editor did not close or did not come back, 7 a PIE is up, 8 no project or engine found.
A refused close returns at once: nothing waits on an editor that was told no.

The project is the single *.uproject in the project root (the folder that holds .claude/), or the file
ORCH_UPROJECT names; the editor target is <Name>Editor. The engine is UE_ROOT, else the one the project's
EngineAssociation names in the registry, else the launcher's default folder for that version.
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
UE_PYTHON = os.path.join(HERE, 'ue_python.py')
ORCH_CMD = os.path.abspath(os.path.join(HERE, '..', 'orch.cmd'))
WORK_DIR = os.path.join(ROOT, 'Saved', 'Orchestration', 'editor_rebuild')
RESTORE_DATA = os.path.join(ROOT, 'Saved', 'Autosaves', 'PackageRestoreData.json')
NO_WINDOW = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
INSTALLS_KEY = 'SOFTWARE\\EpicGames\\Unreal Engine\\'
BUILDS_KEY = 'Software\\Epic Games\\Unreal Engine\\Builds'

CLOSE_TIMEOUT_SECONDS = 90
CLOSE_POLL_SECONDS = 1
LAUNCH_TIMEOUT_SECONDS = 450
LAUNCH_POLL_SECONDS = 5
EDITOR_CALL_TIMEOUT_SECONDS = 40
MAX_ERROR_LINES = 14

EXIT_DONE = 0
EXIT_NO_LANE = 3
EXIT_BUILD_FAILED = 4
EXIT_REFUSED = 5
EXIT_STUCK = 6
EXIT_PIE = 7
EXIT_NO_PROJECT = 8


def find_uproject():
	"""ORCH_UPROJECT (absolute, or relative to the project root), else the single *.uproject in the root; '' otherwise."""
	override = os.environ.get('ORCH_UPROJECT')
	if override:
		return os.path.abspath(os.path.join(ROOT, override))
	found = glob.glob(os.path.join(glob.escape(ROOT), '*.uproject'))
	return os.path.abspath(found[0]) if len(found) == 1 else ''


def registry_engine_root(association):
	"""A registered source build is a value of HKCU Builds; a launcher install is HKLM <version>\\InstalledDirectory."""
	try:
		import winreg
	except ImportError:
		return ''
	for hive, key, value in ((winreg.HKEY_CURRENT_USER, BUILDS_KEY, association), (winreg.HKEY_LOCAL_MACHINE, INSTALLS_KEY + association, 'InstalledDirectory')):
		try:
			with winreg.OpenKey(hive, key) as handle:
				return winreg.QueryValueEx(handle, value)[0]
		except OSError:
			continue
	return ''


def find_engine(uproject):
	"""The Engine folder holding an editor binary: UE_ROOT's, else the engine's the project's EngineAssociation names, else the launcher's default folder for it."""
	try:
		with open(uproject, encoding='utf-8-sig') as f:
			association = json.load(f).get('EngineAssociation') or ''
	except (OSError, ValueError, AttributeError):
		association = ''
	roots = [os.environ.get('UE_ROOT') or '']
	if association and isinstance(association, str):
		roots.append(registry_engine_root(association))
		if os.environ.get('ProgramFiles'):
			roots.append(os.path.join(os.environ['ProgramFiles'], 'Epic Games', 'UE_' + association))
	for root in roots:
		engine = os.path.join(root, 'Engine')
		if root and os.path.isfile(os.path.join(engine, 'Binaries', 'Win64', 'UnrealEditor.exe')):
			return engine
	return ''


PROJECT = find_uproject()
PROJECT_NAME = os.path.splitext(os.path.basename(PROJECT))[0] if PROJECT else ''
EDITOR_TARGET = PROJECT_NAME + 'Editor'
ENGINE = find_engine(PROJECT) if PROJECT else ''
EDITOR = os.path.join(ENGINE, 'Binaries', 'Win64', 'UnrealEditor.exe')
BUILD = os.path.join(ENGINE, 'Build', 'BatchFiles', 'Build.bat')
UHT_TIMESTAMPS = os.path.join(ROOT, 'Intermediate', 'Build', 'Win64', EDITOR_TARGET, EDITOR_TARGET + '.uhttimestamps')

CLOSE_SCRIPT = '''{marker}
import os

import unreal

DISCARD = {discard!r}
SHOULD_CLOSE = {should_close!r}


def on_disk(name):
	relative = name[len('/Game/'):] + '.uasset'
	return os.path.exists(os.path.join(unreal.SystemLibrary.convert_to_absolute_path(unreal.Paths.project_content_dir()), relative))


def dirty_packages():
	names = [p.get_name() for p in unreal.EditorLoadingAndSavingUtils.get_dirty_content_packages()]
	names += [p.get_name() for p in unreal.EditorLoadingAndSavingUtils.get_dirty_map_packages() if not p.get_name().startswith('/Temp')]
	return names


if unreal.EditorLevelLibrary.get_pie_worlds(False):
	print('REBUILD_PIE_UP')
else:
	if SHOULD_CLOSE:
		for package in list(unreal.EditorLoadingAndSavingUtils.get_dirty_content_packages()):
			name = package.get_name()
			if name not in DISCARD:
				continue
			if on_disk(name):
				# The default mode is interactive, and in it a dirty package is left as it was.
				unreal.EditorLoadingAndSavingUtils.reload_packages([package], unreal.ReloadPackagesInteractionMode.ASSUME_POSITIVE)
			else:
				unreal.EditorLoadingAndSavingUtils.save_packages([package], True)
	dirty = dirty_packages()
	print('REBUILD_DIRTY', dirty)
	if SHOULD_CLOSE and not dirty:
		print('REBUILD_CLOSING')
		unreal.SystemLibrary.quit_editor()
'''

PROBE_SCRIPT = '''# ORCH: read-only
print('REBUILD_EDITOR_UP')
'''


def say(text):
	print(text, flush=True)


def has_lane():
	done = subprocess.run([ORCH_CMD, 'guard', '--operation', 'Work'], stdin=subprocess.DEVNULL, capture_output=True, text=True, errors='replace', creationflags=NO_WINDOW)
	if done.returncode != 0:
		say((done.stdout + done.stderr).strip()[:300])
	return done.returncode == 0


def powershell_quote(text):
	return "'%s'" % text.replace("'", "''")


def editor_running():
	"""An editor of this project in this folder: another copy of the project elsewhere on the machine is not ours to close."""
	pattern = '*%s*%s.uproject*' % (os.path.basename(os.path.dirname(PROJECT)), PROJECT_NAME)
	query = "@(Get-CimInstance Win32_Process -Filter \"name='UnrealEditor.exe'\" | Where-Object { $_.CommandLine -like %s }).Count" % powershell_quote(pattern)
	done = subprocess.run(['powershell.exe', '-NoProfile', '-Command', query], capture_output=True, text=True, errors='replace', creationflags=NO_WINDOW)
	return done.stdout.strip() not in ('', '0')


def run_in_editor(source, name):
	os.makedirs(WORK_DIR, exist_ok=True)
	path = os.path.join(WORK_DIR, '%s_%d.py' % (name, os.getpid()))
	with open(path, 'w', encoding='utf-8') as script:
		script.write(source)
	try:
		done = subprocess.run([sys.executable, UE_PYTHON, path, '--timeout', str(EDITOR_CALL_TIMEOUT_SECONDS)], capture_output=True, text=True, errors='replace', creationflags=NO_WINDOW)
		return done.stdout + done.stderr
	finally:
		os.remove(path)


def newest_crash():
	folders = glob.glob(os.path.join(ROOT, 'Saved', 'Crashes', '*', ''))
	return max(folders, key=os.path.getmtime) if folders else None


def close_editor(discard, check):
	marker = '# ORCH: read-only' if check else '# Closes the shared editor for a build.'
	output = run_in_editor(CLOSE_SCRIPT.format(marker=marker, discard=discard, should_close=not check), 'close')
	dirty = re.search(r'REBUILD_DIRTY (\[.*?\])', output)
	if 'REBUILD_PIE_UP' in output:
		say('REFUSED: a PIE is up')
		return EXIT_PIE
	if check:
		say('editor state: ' + (dirty.group(1) if dirty else output.strip()[-300:]))
		return EXIT_DONE
	if 'REBUILD_CLOSING' not in output:
		say('REFUSED: unsaved work ' + (dirty.group(1) if dirty else output.strip()[-300:]))
		say('Do not save or discard another agent\'s work: ask its owner, then pass --discard for what they said to throw away.')
		return EXIT_REFUSED

	before = newest_crash()
	start = time.time()
	while editor_running():
		if time.time() - start > CLOSE_TIMEOUT_SECONDS:
			say('STUCK: the editor is still running after %d s. It belongs to the Master Agent now: message it with this line.' % CLOSE_TIMEOUT_SECONDS)
			return EXIT_STUCK
		time.sleep(CLOSE_POLL_SECONDS)
	after = newest_crash()
	say('closed in %.0f s%s' % (time.time() - start, '; it crashed on the way out: ' + after if after != before else ''))
	return EXIT_DONE


def build():
	os.makedirs(WORK_DIR, exist_ok=True)
	log_path = os.path.join(WORK_DIR, 'build_%d.log' % os.getpid())
	if os.path.exists(UHT_TIMESTAMPS):
		os.remove(UHT_TIMESTAMPS)
	with open(log_path, 'w', encoding='utf-8') as log:
		subprocess.run([BUILD, EDITOR_TARGET, 'Win64', 'Development', '-Project=' + PROJECT, '-waitmutex'], stdout=log, stderr=subprocess.STDOUT, creationflags=NO_WINDOW)
	text = open(log_path, encoding='utf-8', errors='replace').read()
	result = re.search(r'^Result: (\w+)', text, re.M)
	took = re.search(r'Total execution time: ([\d.]+)', text)
	compiled = len(re.findall(r'\] Compile \[x64\]', text))
	say('build: %s, %s s, %d compiled; log %s' % (result.group(1) if result else 'no result', took.group(1) if took else '?', compiled, log_path))
	if result and result.group(1) == 'Succeeded':
		return True
	for line in [l for l in text.splitlines() if ' error ' in l or 'Error:' in l][:MAX_ERROR_LINES]:
		say(line[:260])
	return False


def set_aside_restore_data():
	"""After a crash the editor stops at a Restore Packages dialog until someone clicks, and nobody is there to click. Only the list that raises the dialog moves; the autosaves stay in Saved/Autosaves."""
	if not os.path.exists(RESTORE_DATA):
		return
	# The editor writes this file as UTF-16, and not always with a byte order mark.
	text = open(RESTORE_DATA, 'rb').read().replace(b'\x00', b'').decode('utf-8', errors='replace')
	names = re.findall(r'"PackagePathName"\s*:\s*"([^"]+)"', text)
	if not names or not re.search(r'"RestoreEnabled"\s*:\s*true', text):
		return
	aside = '%s.%s-set-aside.json' % (RESTORE_DATA[:-len('.json')], time.strftime('%Y%m%d-%H%M%S'))
	os.replace(RESTORE_DATA, aside)
	say('the last session crashed; not restoring %s. Autosaves are still in Saved/Autosaves, listed in %s' % (names, aside))


def relaunch():
	set_aside_restore_data()
	command = "Start-Process -FilePath %s -ArgumentList %s,'-NoLiveCoding'" % (powershell_quote(EDITOR), powershell_quote('"%s"' % PROJECT))
	subprocess.run(['powershell.exe', '-NoProfile', '-Command', command], creationflags=NO_WINDOW)
	start = time.time()
	while time.time() - start < LAUNCH_TIMEOUT_SECONDS:
		time.sleep(LAUNCH_POLL_SECONDS)
		if 'REBUILD_EDITOR_UP' in run_in_editor(PROBE_SCRIPT, 'probe'):
			say('editor up in %.0f s' % (time.time() - start))
			return True
	say('STUCK: the editor did not come back in %d s. Message the Master Agent.' % LAUNCH_TIMEOUT_SECONDS)
	return False


def main():
	parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
	parser.add_argument('--discard', nargs='*', default=[])
	parser.add_argument('--check', action='store_true')
	parser.add_argument('--no-relaunch', action='store_true')
	parser.add_argument('--relaunch-only', action='store_true')
	parser.add_argument('--restart-only', action='store_true')
	options = parser.parse_args()

	if not PROJECT:
		say('NO PROJECT: put one *.uproject in %s, or set ORCH_UPROJECT' % ROOT)
		return EXIT_NO_PROJECT
	if not ENGINE and not options.check:
		say('NO ENGINE: no editor found for %s; set UE_ROOT to the engine folder' % PROJECT)
		return EXIT_NO_PROJECT
	if options.relaunch_only:
		if editor_running():
			say('the editor is already running')
			return EXIT_STUCK
		return EXIT_DONE if relaunch() else EXIT_STUCK
	if options.check:
		return close_editor(options.discard, True) if editor_running() else EXIT_DONE
	if not has_lane():
		return EXIT_NO_LANE
	if editor_running():
		closed = close_editor(options.discard, False)
		if closed != EXIT_DONE:
			return closed

	is_built = True if options.restart_only else build()
	if not options.no_relaunch and not relaunch():
		return EXIT_STUCK
	return EXIT_DONE if is_built else EXIT_BUILD_FAILED


if __name__ == '__main__':
	sys.exit(main())
