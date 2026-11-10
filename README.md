# AgentOrchestrator

## Agent Manager

A manager for running many agentic AIs on many subscriptions at once: for example 10 Claude agents on 2 Anthropic subscriptions and 4 Codex agents on 3 OpenAI subscriptions.

It is designed for Unreal Engine but its fine to use it on anything. A separate orchestration layer lets the agents work together on the editor, on multiple editors and on shared assets without tripping over each other.

## Master Agent

You can spawn a MASTER AGENT (right click → *New MASTER AGENT*). It manages all the other agents and sorts them out when they get stuck or deadlocked. It is also useful for queueing things like deployments or pushes to run once all the work is completed and tested.

It is also the agent to use for developing Agent Manager itself, if you want to change it or add features.

## Architecture

.NET Blazor, fully vibe coded. I've not bothered to read the code: it works on my machine and I use it all day 🥸

## Bindings

| Input | Does |
|---|---|
| Right click → *New Claude* / *New Codex* | New agent |
| Right click → *New category* | Group agents in a box |
| `B` | Toggle usage stats |
| `C` | Toggle agent chat |
| `A` | `/all`: message every agent |
| `T` | Notes |
| `Space` | Recentre |
| `Ctrl` + left click | Close an agent showing the big ✕ (its process is no longer detected) |

There are other bindings; the app lists them.

## Requirements

- Windows 10 or 11 (the tools use Win32 APIs, Windows Terminal and the WebView2 runtime that comes with Windows 11)
- Python 3, on `PATH` as `python`
- Unreal Engine 5, with the *Python Editor Script Plugin* enabled in your project
- Claude Code and/or the Codex CLI
- The .NET 10 SDK, to build Agent Manager
- Perforce is optional: the orchestrator also gates `p4 sync`, `revert` and friends when you use it

## Install the orchestrator into a UE project

Your project root is the folder that holds `<YourProject>.uproject`. The orchestrator finds the project as the single `*.uproject` there (set `ORCH_UPROJECT` to choose one), and names everything after it: the editor target `<YourProject>Editor`, the editor log `Saved/Logs/<YourProject>.log`. It finds the engine from the project's `EngineAssociation`; set `UE_ROOT` to the engine folder to override that.

1. **Copy the scripts and skills.** Copy this repository's `.claude/scripts/` and `.claude/skills/` into `<YourProject>/.claude/`.
2. **Copy the plugin.** Copy `AgentOrchestratorPlugin/` to `<YourProject>/Plugins/AgentOrchestrator/` and enable it in `<YourProject>.uproject` (it is off by default):
   ```json
   "Plugins": [
       { "Name": "AgentOrchestrator", "Enabled": true }
   ]
   ```
   Build your editor target, or let the editor offer to build the module on its next launch.
3. **Allow Python remote execution**, which `.claude/scripts/tools/ue_python.py` uses to run scripts in the open editor: in `Config/DefaultEngine.ini`,
   ```ini
   [/Script/PythonScriptPlugin.PythonScriptPluginSettings]
   bRemoteExecution=True
   ```
   The editor reads it at startup, so restart it once.
4. **Add the hooks.** Create `<YourProject>/.claude/settings.local.json` (it is per machine; every machine creates its own):
   ```json
   {
     "hooks": {
       "PreToolUse": [
         { "matcher": "*", "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook PreToolUse", "timeout": 180 } ] }
       ],
       "PostToolUse": [
         { "matcher": "*", "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook PostToolUse" } ] }
       ],
       "PostToolUseFailure": [
         { "matcher": "*", "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook PostToolUseFailure" } ] }
       ],
       "Stop": [
         { "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook Stop" } ] }
       ],
       "SessionStart": [
         { "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook SessionStart" } ] }
       ],
       "SessionEnd": [
         { "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook SessionEnd" } ] }
       ],
       "UserPromptSubmit": [
         { "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook UserPromptSubmit" } ] }
       ],
       "Notification": [
         { "hooks": [ { "type": "command", "command": "python .claude/scripts/orch/orch.py hook Notification" } ] }
       ]
     }
   }
   ```
   The paths are relative to the project root, where your sessions start; if they start elsewhere, use `"$CLAUDE_PROJECT_DIR"/.claude/scripts/orch/orch.py`. Keep the PreToolUse `timeout` above the orchestrator's `P4_WAIT` (180 s works): that hook can wait out a short holder, and Claude Code's default would kill it mid-wait. Running sessions pick the file up without a restart. Until it exists only explicit `acquire` / `finish` works, and nothing gates a lone editor command or reclaims a stopped agent's grant.
5. **Tell your agents.** Add a line to your project's `CLAUDE.md` (and `AGENTS.md` for Codex) saying that the editor, builds, PIE, test processes and workspace-changing p4 go through `.claude/scripts/orch.cmd`, with the rules in `.claude/scripts/orchestration.md`.
6. **Check it.** `.claude/scripts/orch.cmd status` shows the lane, the queue and the agents; new lines in `Saved/Orchestration/orch-hook-fields.log` mean the hooks are live. A harmless probe of the gate is `python -c "print('Build.bat')"`: it is classified as a build, and if wrongly allowed it only prints.

Codex runs no Claude hooks, so a Codex agent calls `acquire` before editor work and `finish` after it; `orchestration.md`, *Codex and other clients without Claude hooks*, has the rest.

## Use it

An agent that needs the editor for several steps takes the lane, does the whole job, and gives it back:

```
.claude/scripts/orch.cmd acquire --kind PIE --purpose "Claude: lighting fix - verify in PIE" --minutes 15 --wait 540
.claude/scripts/orch.cmd finish --id <id> --evidence "PIE stopped, no build in flight"
```

`acquire` exits 0 when granted and 3 when queued (then `acquire --id <id> --until-granted` waits in the background and exits 0 granted or 2 dropped). A single editor command just runs when the lane is free and is refused, naming the holder, when it is not. Agents message each other through the same tool (`orch.cmd msg --to <work> "..."`, `orch.cmd inbox <work> --wait 540`). You take the editor back with *Take editor* in the editor, or `orch.cmd hold on` in your own terminal, and give it back with *Resume agents* / `orch.cmd hold off`. Everything else is in [`.claude/scripts/orchestration.md`](.claude/scripts/orchestration.md).

## Build and run Agent Manager

```
cd AgentManager
dotnet build AgentManager.sln
dotnet test AgentManager.Tests
dotnet run --project AgentManager.App
```

`publish.cmd` publishes a Release build to `AgentManager/Publish/AgentManager.exe`, which is the copy to pin or put a shortcut to (close the app before republishing). It finds an orchestrator from the working folders of the agents it sees, so it needs no setup; `AgentManager/README.md` explains every card, key and panel, and where each fact comes from. Its own state, log and a live JSON snapshot of the grid are in `%LOCALAPPDATA%\AgentManager`.

## Repository layout

| Path | What it is |
|---|---|
| `.claude/scripts/` | The orchestrator (`orch.cmd`, `orch/orch.py`), its rules (`orchestration.md`), its tests (`tests/orch_test.py`, run with `python .claude/scripts/tests/orch_test.py`) and editor helpers (`tools/`) |
| `.claude/skills/` | Claude Code skills: `agent-manager`, `master-agent`, `orchestration-benchmark` (measuring lane time with `bench.py`), `ai-bridge`, `all` |
| `AgentManager/` | The Agent Manager app (.NET, WPF + Blazor) and its tests |
| `AgentOrchestratorPlugin/` | The Unreal Editor plugin |
| `CLAUDE.md`, `AGENTS.md` | Guidance for agents working on this repository |

## License

MIT; see [LICENSE](LICENSE).
