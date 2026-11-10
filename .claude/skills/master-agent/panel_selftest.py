"""Runs the editor plugin's self-test in the open editor. It takes the lane (no `ORCH: read-only`), because it opens a tab, which would take focus from a PIE being played.

    python .claude/scripts/tools/ue_python.py .claude/skills/master-agent/panel_selftest.py
    python .claude/scripts/tools/wait_result.py Saved/Orchestration/editors/selftest.json --timeout 60 --since <epoch seconds before the first line>

The command returns at once and works over a few frames. It deletes the old selftest.json first and writes the new one last, so its appearance
means the run is over. Read `result` and `failures`, then look at panel.png and chip.png beside it (orchestration.md, *The editor panel*).
"""
import unreal

unreal.SystemLibrary.execute_console_command(None, 'Orch.SelfTest')
print('Orch.SelfTest started')
