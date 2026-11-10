# Agent Manager

One window that shows every coding agent on this machine, Claude Code (CLI, desktop app, IDE extensions) and Codex, as cards on a canvas you arrange yourself, with what the agents say to each other, who holds or waits for an Agent Orchestrator lane, and the MASTER AGENT, the one agent you talk to about the others. WPF hosting Blazor, .NET 10, Windows only.

It only reads the agents' files and the orchestrator's, and writes only its own data folder. Changing its code: the `agent-manager` skill (`.claude/skills/agent-manager/SKILL.md`).

## Build, run, test

Needs the .NET 10 SDK and the WebView2 runtime (part of Windows 11).

```
dotnet build AgentManager.sln
dotnet run --project AgentManager.App
dotnet test AgentManager.Tests
```

Warnings are errors. `publish.cmd` publishes a Release build to `Publish/`; close the app first, or the exe cannot be replaced. The tests build their fixtures in scratch folders and never read your agents.

## What a card means

| Colour | State | When |
|---|---|---|
| green | working | its turn, a subagent or a background command runs |
| blue | building | working, and the current tool is a build |
| purple | editor | it holds the orchestrator's editor lane (even after its turn) or is calling the editor |
| red | needs you | a dialog, a question or a request for your next step in its last message, a task blocked on you, or a local server left up for you, whatever runs in the background |
| yellow | asked | it asked you something and keeps working |
| orange | queued | waiting for a lane (the card names who is ahead), or paused by your hold |
| none, ⏸ | paused | turn over, its recent orchestrator task still open |
| none, ✓ | finished | turn over, nothing asked, no recent task open |
| none | idle | never prompted, or marked read |
| red ✕ | closed | its window is gone; right-click to resume it or clear it |

MASTER AGENT's card adds an electric border and a chip to his state. A card is titled with the session's name (your `/rename`, else Claude's generated title, else the first prompt; a Codex thread's name; `Astra: <channel>` for a window `astra.py` opened) and shows the model, folder, last prompts and reply, current tool, tokens, context, a time split, today's lane use, subagents and orchestrator task. The header shows the orchestrator's daemon and your hold.

## Using it

- **Canvas.** Drag a card to move it, drag empty space (or the middle button) to pan, wheel to zoom, Space to fit. Cards stay where you put them. Right-click empty space for *New MASTER AGENT* (Claude or Codex), *New Claude*, *New Codex* (a terminal in the orchestrator's project, so it gets the project's hooks and skills), or a category box, which carries its cards when dragged and counts their agents and tokens.
- **Cards.** Click raises the agent's own window. Right-click for details, mark read, resume, rename, pin, send, its messages, copy commands or ids, its folder and transcript, hide, close.
- **Chat (c).** Every message agents send each other, once: Claude `SendMessage`, subagent hand-backs, Codex agent calls, the orchestrator inbox. Hover a name for the first prompts of that session.
- **Bridges.** An agent on a bridge channel shows one line on its card's bottom border (`⇄ Astra · api-docs · 3 msgs · 2m ago`), and two cards on one channel are joined by a trace, while another agent is on the channel and it is in use.
- **Send (a)** types a message into each chosen agent's own console as your prompt, skipping agents with a dialog open; a Claude with no console gets it through the orchestrator inbox. **Rename (F2)** types `/rename <name>` the same way, setting a Claude draft aside first and refusing over a Codex draft.
- **Close window** (the last, red menu item; a second click confirms on a running agent) closes the agent's window and the card with it; double-click a finished card for an ✕ that does the same; Ctrl+click closes a finished agent or clears a closed card. **Ctrl+Z** undoes the newest removal in this run, further back with each press: a card taken off the canvas returns to its place, a deleted category box returns whole. It never starts anything.
- **Restore.** The first time it opens after a restart or sign-out, it offers the agent windows of the last Windows session, each resumed in its own terminal.
- **MASTER AGENT** is started by the manager in the orchestrator's project. Closing his window removes him, across restarts of the manager, until *New MASTER AGENT*; a PC restart resumes him. There is one at a time. Pinned, he is docked top left, clear of pan and zoom. His job is the `master-agent` skill.
- **Stats** count from the last reset (the `stats` button keeps past stretches). **Notes (t)** are `.md` files with snapshots and a trash. **Sounds** (♪) play when an agent starts waiting on you or finishes.

Keys: arrows select, Enter raises, `z` details, `m` mark read, `F2` rename, `r` resume, `p` pin, Del hide, Ctrl+Z undo a removal, `a` send, `c` chat, `t` notes, `/` filter, Space fit, Esc close panels.

## Where the data comes from

| Source | Read for |
|---|---|
| `~/.claude/sessions/<pid>.json` | running Claude sessions: name, busy / idle / waiting, folder |
| `~/.claude/projects/<project>/<session>.jsonl`, `<session>/subagents/` | what a Claude session did, sent and received |
| `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`, `~/.codex/session_index.jsonl` | Codex threads, timings, questions, agent calls, names |
| Codex processes and consoles | which window shows which thread (inferred: Codex's daemon holds every thread) |
| `<project>/Saved/Orchestration/orch-status.json`, `orch.db` (read-only) | lane, queue, hold, tasks, bridge channels, inbox, grant history |
| `state.json` (written) | read marks, hidden cards, pins, numbers, canvas, settings, the Master Agent, the restore list, stats |

`CLAUDE_CONFIG_DIR` and `CODEX_HOME` move the Claude and Codex folders, as for the CLIs. An orchestrator is found from any agent's folder (`Saved/Orchestration` up its parents), `ORCH_STATE`, or `OrchestratorDirs` in `state.json`. `AGENTMANAGER_DATA` moves the data folder from `%LOCALAPPDATA%/AgentManager`. A `--screenshot` run never starts a Master Agent, nor does a run on another data folder unless `AGENTMANAGER_MASTER_AGENT=1`.

## Logs and live state

- `logs/agentmanager-YYYYMMDD.log` in the data folder: one line per event, flushed and opened shared, so `tail -f` follows it. Areas: `app` (start, exit, WebView2, crashes), `hub` (first scan, slow ticks by phase), `agent` (each agent appearing, leaving and changing state, with the reason), `chat`, `master-agent` and `master` (his start, resume, typed commands, a Codex master's watcher), `orch` (attaching, lane, queue, hold and daemon changes, `orch.db` read errors), `ui` (every click and what it did), `notes`, `blazor` and `js` (page errors). Old logs are pruned.
- `live.json` in the data folder, rewritten every second: each agent's state and reason, process, tool, last message, question, tokens, lane, bridges, transcript and resume command; the bridge links; the orchestrators; the last messages.

## Checking the UI without a person

```
AgentManager.exe --screenshot out.png [--delay ms] [--size WxH] [--eval "js"]
```

Renders, runs the script (its result goes to `out.txt`; a promise is awaited), saves the page as `out.png` and the window as `out.window.png`, and exits. Run it with `AGENTMANAGER_DATA` on a scratch copy of `state.json`, or a script that drops a card or flips a toggle saves over your layout. Driving it and fake agents: the skill's *Verifying a change*.
