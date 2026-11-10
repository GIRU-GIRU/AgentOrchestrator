# CLAUDE.md

Guidance for agents working on this repository. Codex reads it too, through `AGENTS.md`.

## What this is

**Agent Orchestrator** lets many Claude Code and Codex agents work on one Unreal Engine project on one Windows machine without breaking each other's work. The editor, the build output, PIE and the version-control workspace are one shared **lane**; the orchestrator hands it out, queues for it and takes it back from agents that stopped.

| Part | Path | Owning doc |
|---|---|---|
| The orchestrator: CLI, Claude Code hooks and daemon in one Python file over one SQLite db | `.claude/scripts/orch.cmd` → `orch/orch.py` | `.claude/scripts/orchestration.md` |
| Editor helpers it and the skills call (`ue_python.py`, `editor_rebuild.py`, `wait_result.py`, `pie_ready_probe.py`) | `.claude/scripts/tools/` | their docstrings |
| Agent Manager: a WPF + Blazor app showing every agent as a card on a canvas | `AgentManager/` | `agent-manager` skill; `AgentManager/README.md` for users |
| The Unreal Editor plugin: toolbar chip, *Agent Orchestrator* tab, *Take editor* | `AgentOrchestratorPlugin/` | `orchestration.md`, *The editor panel* |
| The MASTER AGENT: the one supervising agent Agent Manager keeps | `AgentManager.App/MasterAgent/`, `.claude/skills/master-agent/` | `master-agent` skill |

The tools run from inside a host project (`.claude/` at its root, the plugin under `Plugins/AgentOrchestrator`). **The project is found, never named:** the single `*.uproject` in the folder that holds `.claude/`, or the one `ORCH_UPROJECT` names; its stem gives `<Name>Editor`, `<Name>Client` / `<Name>Server` and `Saved/Logs/<Name>.log`. Environment switches are `ORCH_*`.

## Build and test

| Part | Build | Test |
|---|---|---|
| Agent Manager (.NET 10, Windows) | `dotnet build AgentManager/AgentManager.sln` (warnings are errors) | `dotnet test AgentManager/AgentManager.Tests`; a UI change is also looked at with `--screenshot` on scratch data (`agent-manager` skill, *Verifying a change*) |
| Orchestrator (Python 3, standard library) | nothing | `python .claude/scripts/tests/orch_test.py` (sandboxed: temp state, fake processes and editor, no daemon) |
| Editor plugin (UE 5 C++) | the host project's `<Name>Editor` target, once its `.uproject` enables the plugin | `Orch.SelfTest` in a running editor (`master-agent` skill) |
| Skill scripts | nothing | `python -m py_compile <file>` |

## Rules

- **Generic, never one project's.** No project, studio, person, machine or absolute path in code, tests, fixtures or docs: read the project at run time and write `<Name>` or `<YourProject>` in examples.
- **Never test against live agents.** `watch.py`, `astra.py`, `panel_selftest.py` and `linger_sample.py` close, open and type into real windows or attach to the real editor; `orch.cmd` commands change the real lane; Agent Manager started without `AGENTMANAGER_DATA` uses the user's state and may start a real MASTER AGENT. Run the test suites above, or each tool on scratch data as its doc describes.
- **The orchestrator decides; the plugin and the app display.** Every lane rule lives in `orch.py`. The plugin holds only what the orchestrator cannot know (which editor window it is), the app only how to show it.
- **Contracts between the parts change on both sides together.** The feed `orch-status.json` (`write_status`; read by `StatusFeed.Parse` and `FOrchestratorStatus::Parse`): add fields, never rename one. Likewise the `orch.db` tables the app reads, the inbox message text it parses, the owner id `kind:pid:ticks`, the CLI's output lines and exit codes, and the hook JSON (`orchestration.md`, *The feed contract*, *Changing orch.py*).
- **`orch.py` is shared infrastructure.** Every agent in a host project calls it on every tool call, so a broken file stops all of them: change a copy, pass `orch_test.py`, then the live file. A new rule that holds agents back goes to the user first, with its measured cost.
- **Style.** C#: 4 spaces, Allman braces, file-scoped namespaces. C++: Unreal style, tabs. Python: tabs. Comments say why, never what. A change comes with tests where the part has them.
- **Docs stay true in the same change.** A behaviour you change, or had to work out by reading code, goes into the owning skill or `orchestration.md` in present tense: the rule, why, and the file or constant that holds it. Name a constant instead of copying its value; correct a wrong line in place.

## Skills

Load the skill a task touches before opening its files. A Codex reads `.claude/skills/<name>/SKILL.md`.

| Skill | Load when |
|---|---|
| [agent-manager](.claude/skills/agent-manager/SKILL.md) | anything under `AgentManager/`: sources (Claude registry and transcripts, Codex rollouts and windows, the feed), `StateClassifier`, the canvas, chat, send and rename, close and Ctrl+Z, the MASTER AGENT's start; "why is this agent red", "card on the wrong window" |
| [master-agent](.claude/skills/master-agent/SKILL.md) | first, in the MASTER AGENT's window; `watch.py` and its lines, reordering, reclaiming a grant, a frozen editor, Astra windows (`astra.py`), proposing orchestrator changes |
| [orchestration-benchmark](.claude/skills/orchestration-benchmark/SKILL.md) | measuring the lane with `bench.py`: hold and queue time, builds and tool calls per hold, proving a saving; "why did I wait" |
| [ai-bridge](.claude/skills/ai-bridge/SKILL.md) | agents messaging each other: `msg --to <work>`, `inbox <work> --wait`, `--as <name>`, handing a job to an Astra |
| [all](.claude/skills/all/SKILL.md) | `/all <message>` to every other live agent |
| [orchestration.md](.claude/scripts/orchestration.md) (read it whole) | the orchestrator itself: commands, grants, the queue, the turn clock, reclaiming, PIE ownership, the user's hold, tasks, the inbox, the editor panel, Codex without hooks, installation |
