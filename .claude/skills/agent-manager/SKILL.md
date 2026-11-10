---
name: agent-manager
description: Agent Manager (`AgentManager/`), the WPF + Blazor canvas of every Claude Code and Codex agent: card states, chat monitor, bridge traces, lanes on cards, the MASTER AGENT's start and dock. Load before changing anything under `AgentManager/`; "why is this agent red / finished / paused", "card on the wrong window", "the canvas lags".
---

# Agent Manager

What the app shows, building, running, keys, the log and `live.json`: `AgentManager/README.md`. This skill is for changing the code. Paths are under `AgentManager/`; `Core/` and `App/` are `AgentManager.Core/` and `AgentManager.App/`.

## Map

| Concern | Where |
|---|---|
| Poll loop and every action | `Core/AgentHub.cs` (`Tick`, `Decorate`, `Number`, `PlaceNewCards`) |
| Claude registry, transcripts, subagents | `Core/Claude/` (`ClaudeRegistry`, `ClaudeSource`, `ClaudeLines`) |
| Codex rollouts, the daemon, windows | `Core/Codex/` (`CodexSource`, `CodexLines`, `CodexWindows`) |
| Turns and tools, both kinds | `Core/Transcripts/` (`SessionTracker`, `ActivityRules`, `Questions`, `BridgeRules`) |
| Card state | `Core/StateClassifier.cs` |
| Orchestrator feed and `orch.db` | `Core/Orchestration/` (`OrchestratorReader`; `StatusFeed.Parse`, `InboxParser` in `OrchestratorData.cs`; `LaneMetrics`; `OrchestratorBriefs`) |
| Chat monitor, name popup | `Core/Chat/` (`ChatLog`, `ChatView`, `SessionOrigins`), `ClaudeOrigins`, `CodexOrigins`, `ChatPanel.razor` |
| Canvas | rules `Core/CanvasLayout.cs`, state `Core/State/CanvasState.cs`, view and drags `App/wwwroot/js/canvas.js` |
| Bridge traces | pairs `BridgeRules.Links`, drawing `App/wwwroot/js/bridges.js` |
| Master Agent | `Core/MasterAgent.cs`, `Core/CodexMasterWatch.cs`, role `App/MasterAgent/master-agent-prompt.md` |
| Close, X, Ctrl+Z | `CardClose`, `GridUndo`, `AgentHub.CloseAgent` / `SettleClose` / `IsHidden` / `DeleteCategory` / `BringBackLastRemoved` |
| Send, rename | `Delivery.Plan`, `AgentRename` + `Composer`, `Core/Windows/ConsoleInput.cs`, `PromptTyping` |
| Restore, stats, notes | `SessionRestore` + `RestorePanel.razor`; `Stats.cs` + `StatsPanel.razor`; `Core/Notes/NotesStore.cs` |
| Saved state | `Core/State/ManagerStore.cs` (`StoreData`, `AgentMark`, `ManagerSettings`) |
| Log, `live.json`; Windows APIs | `Core/Diagnostics/` (`AppLog`, `HubJournal`); `Core/Windows/` |
| Page | `App/Components/Main.razor` (keys `HandleKey`, card menu `MenuFor`), `AgentCard.razor`, `Ascii.cs`, `wwwroot/css/app.css`, `wwwroot/js/fx.js` |

Tests: `AgentManager.Tests`, a file per area; `StateTests.cs` also holds the chat log and orchestrator cases, `TranscriptTests.cs` the line parsers and `BridgeRules`.

## Read-only contract

- Never write, lock or hold open a transcript, a registry file, a rollout or `orch.db`: agents append while the manager reads. Open files with `JsonlTail.OpenShared`, SQLite with `Mode=ReadOnly`, and ask who holds a file through `FileHolders` (Restart Manager), never by trying an exclusive open.
- It writes only its data folder (`AGENTMANAGER_DATA`, else `%LOCALAPPDATA%/AgentManager`). It acts on agents only for the user (send, rename, restore, close) and for the Master Agent (start, startup commands, a Codex master's watcher). Its one orchestrator command is Send's inbox fallback; it has no hold button.
- All console attaching goes through `ConsoleInput.Attached` (its `GATE`): a process has one console at a time, so two threads would detach each other.
- `Launcher.ForgetAgentSession` drops the `CLAUDE*` / `CODEX*` variables a parent agent set, or a Claude the manager resumes thinks it is a child and stops saving its transcript.
- **Log events, not ticks:** `AppLog` writes one line per event (a state change, a message, a lane change, a user action and its result, an error once), found by `HubJournal` diffing snapshots. What an agent debugging the app would need belongs in the log or `live.json`. `AppLog` stays silent until `Configure`, so tests never write the real log.

## Formats it does not own

Another program writes each of these: change a parser only against a real sample, and a contract only with the other side.

- **Claude registry** `<claude dir>/sessions/<pid>.json`: alive while that pid runs with that `procStart`. `status` stays `busy` after `end_turn` while a background subagent runs, so `IsWorking` is busy and not `SessionTracker.HasTurnEndedForNow`; `IsProcessBusy` keeps the raw value for deciding when typing is safe.
- **Claude transcripts** `<claude dir>/projects/<folder>/<sessionId>.jsonl`, subagents in `<sessionId>/subagents/agent-<id>.jsonl` + `.meta.json`. Sidechain lines are skipped in the main transcript only.
- **Claude messages:** the sender has the `SendMessage` call; a receiver has a `queue-operation` / `enqueue` line holding `<cross-session-message>` or `<agent-message>` (a hand-back). Read the enqueue: a message that arrives mid-turn never becomes a `user` line.
- **Codex rollouts** `<codex dir>/sessions/YYYY/MM/DD/rollout-*.jsonl`: time comes from `item_completed` timings, not line gaps (`HasItemTimings`); `request_user_input` blocks (red), `request_user_input_async` does not (yellow); `parent_thread_id` marks a subagent; review threads are ignored. Recency is the newest event read (`CodexSource.LastWrite`), never the file time, which the daemon keeps stale.
- **Feed** `Saved/Orchestration/orch-status.json`, written by `write_status` in `orch.py`, read by `StatusFeed.Parse` and the editor plugin: add fields, never rename one.
- **`orch.db`:** `inbox` (text from `cmd_msg` / `notify`, parsed by `InboxParser`), `grants` (`LaneMetrics`), `owners`, `tasks`, `meta` keys `channel_members:<work>`.
- **Mirrors of `orch.py`:** `OrchestratorReader.Conflicts` = `conflicts`; `ActivityRules.UsesEditor` = `RX_EDITOR_RPC` + `mcp__unreal*`; card titles = `refresh_titles`; `BridgeRules.AstraJoinLine` matches `join_prompt` in `astra.py`.
- **Layout:** the project is `StateDir/../..` (`AgentHub.ProjectOf`), where Send's fallback runs `.claude/scripts/orch.cmd`. An orchestrator is found from an agent's folder, `ORCH_STATE` or `OrchestratorDirs`.
- **Codex windows:** the app-server daemon holds every rollout and Codex records no thread-to-window link, so the window is inferred, and a wrong one sends Raise, Send and Close to someone else. Read [references/codex-windows.md](references/codex-windows.md) before changing `CodexSource` or `CodexWindows`.

## State rules

`StateClassifier.Classify` (`StateTests.cs`); the order of `AgentState` in `Model/Enums.cs` is the attention order.

- **A false finished is worse than a false red:** red must mean "waiting on you", and a wrong red clears with `m`. Red beats background work.
- **Red:** a dialog (`PendingQuestion`, `WaitingFor`); a turn ended on a question (`EndsOnAQuestion`); a last message leaving the next step to the user (`Questions.FindRequest`); the newest task blocked on the user (`TaskBlockedOnUser`); a turn over with a local server up (`ActivityRules.IsServer`).
- **The transcript ends the turn.** `TurnResumed` reopens it on a main-thread `api-request`, an assistant line or a `ClaudeLines.WAKE_PREFIXES` line; a system reminder, a local command (`/rename`) or `!` input keeps the agent at its prompt.
- **Paused:** turn over, nothing running, newest open task touched within the last `OPEN_TASK_TURNS` prompts (a forgotten task would pause the card for ever). Tasks come from the feed and `orch.db`, since the feed carries only the newest.
- **Finished:** turn over, no subagents or background commands, nothing asked, no recent open task. `RunningCommands` is Claude-only and ends at a final `<task-notification>` or `TaskStop`; a server is not work.
- **Purple:** `LaneInfo.HoldsLane` or a current tool that `UsesEditor`; beats finished, idle and yellow. **Orange:** queued or paused by the user's hold; the status names `QueuedBehind`.
- **Yellow:** working with an unread question (`Questions.Find`). **Green:** a turn or subagent running. Never prompted is idle.
- A missed red: add the agent's real sentence to `MessagesThatLeftTheNextStepToTheUser`, widen `Questions`, check against real turn endings. A long-lived command `IsServer` misses keeps a card green: add it there.

## Card identity

- **A card is a window.** `/clear` or `/resume` starts a new session in the same process; `TakeReplacements` → `AgentHub.Continue` moves the number, pin, canvas spot and order slot to it, and marks the old one dismissed with number 0. Only while the manager runs.
- `IsHidden`: dismissed only until a newer event; a handed-on session still alive was handed on by mistake and shows again; `ClosedWithNoWindow` holds until a window shows the thread.
- **Titles:** Claude: a registry name with `nameSource` `user`, else `custom-title` / `agent-name`, else `ai-title`, else the first prompt; never the derived registry name. Codex: `thread_name` from `session_index.jsonl`; an Astra is `BridgeRules.AstraTitle`.
- **A closed Astra leaves no card** (`LeavesNoClosedCard`, in `Decorate`): skipped, not dismissed, so the thread running again gets its card back; dropped from `GridUndo` too.

## Close and Ctrl+Z

- `Main.BeginClose` marks the card at once and runs `AgentHub.CloseAgent` off the UI thread. `ClosingAt` animates it until `SettleClose`: gone → dismissed; alive after `CLOSE_WAIT` → refused, back to normal.
- `WindowRaiser.Close` ends the process itself when it finds no window. A card with no pid is only taken off the grid (`CloseCardWithNoWindow`).
- `CardClose.CanOffer` (finished, alive, not closing) gates the double-click X; Ctrl+click never closes the Master Agent. `Main.ClickFinished` holds an offering card's raise for `DOUBLE_CLICK_WAIT`, or the raised window takes the second click.
- `GridUndo` is one history of `UndoStep`s, newest first: a `RemovedCard` (hide, close, clear) or a `DeletedCategory` (`AgentHub.DeleteCategory` records the box and its index). For a card it changes marks only and never starts anything (no agent runs twice); its canvas spot survives because positions live as long as the card's mark. Removed cards keep their numbers reserved (`ReservedNumbers`). Tests: `GridUndoTests`, `CategoryUndoTests`.

## Master Agent

Rules `MasterAgent.cs` (`MasterAgentTests`), state `StoreData.MasterAgent`, run by `AgentHub.SettleMasterAgent`. His job: the `master-agent` skill.

- **Only one.** `CanStartNew` stays false until `Next` says he is gone; `StartNewMasterAgent` checks and sets `StartingAt` under one store lock. `RunningSession` adopts a live session of his kind by id, start mark or name (`NAME`, `SESSION_NAME`) instead of starting another, so rename refuses those names (`IsReservedName`).
- **Closed means gone:** missing for `GONE_CONFIRM` in the same Windows logon sets `IsWanted` false and leaves no ✕. An earlier logon resumes him; `SessionRestore.IsRestorable` leaves him out of the restore panel.
- **Starting:** Claude via `FreshCommand` (`-n SESSION_NAME`: a space does not survive Windows Terminal and cmd). Codex has no session id or system-prompt flag and opens no thread until its first prompt, so `start-codex.cmd` (`CodexFreshScript`) opens with `CODEX_START_MARK` and `IsCodexStart` adopts that thread.
- **Typing into his window:** startup commands (`StartupCommandsFor`) one per tick, once per session, only at his prompt (`NextStartupCommand`: `IsProcessBusy` false, no dialog), since keys typed while a background subagent runs queue as a message. A Codex master's watcher (`CodexMasterWatch`, running `watch.py`) wakes him with a `WAKE_PREFIX` line through `PromptTyping.TypeIfEmpty`.
- **`MayStartItself`** is false for `--screenshot` and for an `AGENTMANAGER_DATA` run (unless `AGENTMANAGER_MASTER_AGENT=1`); it gates the automatic start, the typing and the watcher, not the menu item. A scratch data folder still reads the real `~/.claude` and adopts the real one by name: point `CLAUDE_CONFIG_DIR` and `CODEX_HOME` at empty folders too.
- **Dock:** pinned means docked (`CanvasLayout.IsDocked`), rendered in `#canvas-dock` outside `#canvas-world` so pan and zoom never move it; `LiftDocked` parks his spot in `CanvasState.Parked`, `TakeParked` restores it.
- He and every new agent start in the orchestrator's project (`AgentHub.DefaultFolder`): a session started elsewhere gets none of its hooks or skills.
- `AgentManager.ico` is drawn from `MasterChip.razor`: change both together.

## Canvas and page

- **A card never moves by itself:** only a drag or a spawn writes a position (`PlaceNewCards`, `SpotForNewCard`, spawn spots kept for `SPAWN_SPOT_WAIT`). Render reads positions from `Hub.Board()`, never the snapshot, or a tick computed before a drop paints the card back.
- **Style ownership:** .NET renders `left`, `top` and `z-index`. A drag never touches layout while it lasts: the card (or a box and the cards it carries, `.canvas-carried`) is its own layer (`will-change: translate`) and moves by `style.translate`, written once per frame (`scheduleDrag`), and `left` / `top` are written once, on drop (`commit`; `undo` on Esc). Bridges add that translate to a card's offsets (`bridges.js` `slotOf`). Blazor rewrites a card's whole style attribute, so `fx.flip` (after each render and on style mutations) calls `canvas.reapply` and `bridges.reflow`. Never render a style on `#canvas` or `#canvas-world`: their transform is the view.
- **Measuring a drag:** frame times inside the page are already flat at the display's rate, so measure the rendering work with a DevTools trace on a scratch run (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<n>` with its own `WEBVIEW2_USER_DATA_FOLDER`, `Tracing.start` with the `devtools.timeline` and `cc` categories, the drag driven by `Runtime.evaluate`) and compare `Layout`, `Paint`, `RasterTask`, `Layerize` and `DroppedFrame` before and after.
- `.scr` and `.num` text belongs to `fx.js`; Blazor renders only `data-text`.
- `--z` lives on one stylesheet rule (`zoomRule` in `canvas.js`); a variable on `#canvas-world` restyles every node at each zoom step.
- **At rest it costs nothing:** `fx.stepLoops` steps every infinite CSS animation from a timer, new ones included, reading every animation before writing any (a read after a write recalculates style once per animation), and skips while the page is hidden; a minimized window hides its WebView (`MainWindow` `StateChanged`), or WebView2 keeps drawing it. Never put a SMIL `<animate>` on a filter: it runs every display frame.
- **Frames are uncapped** (`App.xaml.cs` `BROWSER_ARGUMENTS`, `--disable-frame-rate-limit`): capped, Chromium waits on vsync and queues frames, and a dragged card reached the screen about five frames late. Anything on `requestAnimationFrame` runs as fast as the machine can, so a cosmetic loop runs on a timer (`fx.nextTextFrame`).
- **An entry animation fills `backwards`,** never `both` or `forwards`: filled, `card-in` held `opacity`, `transform` and `filter` above every rule, so `.card.dimmed` and `.card:hover` did nothing.
- **The host stays quiet:** `ProcessTable.Capture` reads one process at a time (`EnumProcesses`; `SystemProcessIdInformation` names one it cannot open), never `CreateToolhelp32Snapshot`, which walks every thread on the machine and took a large part of a core every poll on a busy one.
- CSS classes are global: a collision (`detail`, `body`) breaks the grid and looks like a rendering bug. Prefix component classes.
- **One colour, one meaning,** from the cards (the scheme heads `app.css`); page chrome is `--accent`. `--claude` / `--codex` mark the kind only through `KindIcon.razor` and the far-zoom wash, never a border, corner or badge. Ask before adding a colour.
- **Bridges:** a card shows a channel only while `BridgeRules.IsLive`, on an out-of-flow `.bridge` line so its height never changes. `BridgeRules.Links` (`BridgeLinkTests`) picks the traces; `bridges.js` only draws, into two SVGs Blazor renders empty and never fills (`#bridge-under` before the cards, `#bridge-over` after), in canvas units, with a frame loop only during a drag (`bridges.track`).
- Keep `Microsoft.AspNetCore.Components.WebView.Wpf` on 9.x (`Directory.Packages.props` says why). WebView2 browser arguments are set once, in `App.xaml.cs`, the same for every copy: a host whose arguments differ from a browser process already running on its data folder fails to start (`0x8007139F`), so a scratch run gets its own `WEBVIEW2_USER_DATA_FOLDER`.

## Chat and the name popup

- `ChatLog` merges the sender's and receivers' records of a message by text within `MERGE_WINDOW`, same sender only (`IsSameSender`); a broadcast is one row whose recipients are counted at the merge, never by splitting on commas. Encrypted Codex text becomes `CodexEncrypted.NOTE` with the ciphertext as `matchText`, or two would merge.
- `SessionOrigins.Find` runs off the UI thread and never touches the hub's sources. `ChatPanel`'s instance (`ForThisUser`) reads the environment after `ForgetAgentSession`, unlike `App`, so a `CLAUDE_CONFIG_DIR` set only in the launching shell reaches the hub, not the popup.
- `[Cross-session` notices are dropped only in `ClaudeOrigins`; the tracker still counts them as prompts, and changing that moves the state rules. Codex origins never read user-role `message` items (harness context), and only a rollout's first `session_meta` is its own.

## Send, rename, restore, stats, notes

- **Send** types into the agent's own console (`ConsoleInput.Type`: key events into `CONIN$`, Enter separately), never the desktop. `Delivery.Plan` skips an agent with a dialog open (keys would answer it), uses the inbox for a Claude with no console, and skips a Codex with no known window.
- **Rename** never sets a title: it types `/rename`, and the name returns through the session's own record (`Main.WarnIfTitleStays` if not). `Composer.Read` finds the box from the caret; Enter goes only when the box `ReadsAs` the command, else the typing is erased. A Claude draft is stashed with Ctrl+S (`PressStash`), never blind (on an empty box it pops an older stash); a Codex draft refuses; in Codex `@` and `$` open pickers that take the Enter. Proven only on scratch CLIs: never test by typing into a live agent.
- **Restore:** `StoreData.OpenWindows` carries `LogonSession.LogonAt` (logon, not boot: Fast Startup) and `App.SessionEnding` freezes it; an earlier logon moves it to `PendingRestore`. No agent runs twice: the panel leaves out sessions running or in `OrchestratorReader.RelaunchingSessions`, and `resurrect` in `orch.py` skips `running_sessions`.
- **Stats** are per-agent baselines from the last reset (`StatsTests`): `Latest` keeps a closed card's figures, an older session counts from `FirstEventAt` (not the process start), `RESET_GUARD` ignores a double reset (a duplicate `@key` throws). The lane slice counts `acquire` tool calls, so a background `--until-granted` waiter counts as nothing: measure the queue with `bench.py`.
- **Notes:** every keystroke goes through `NotesStore.Stage`, which keeps snapshots (`.history/`) and a trash (`.trash/`); no change may bypass them.

## Verifying a change

1. `dotnet test AgentManager.Tests`; the rules are pure functions, so add the case there.
2. **UI:** `AgentManager.exe --screenshot out.png [--delay ms] [--size WxH] [--eval js]` writes `out.png`, `out.window.png` and the script's result as `out.txt` (a returned promise is awaited, so a script can drive several frames). Look at the image before reporting.
   - Always set `AGENTMANAGER_DATA` to a scratch copy of `state.json`: a drop, toggle or reset saves, and a run on the user's file races their manager's saves.
   - Drag: `PointerEvent` `pointerdown` on the element, `pointermove` / `pointerup` on `document`; a synthetic drag makes no click. Keys: `keydown`. The name popup: a bubbling `mouseover` on a name (`fx.originHover` is one delegated listener).
   - A capture leaves out layers that are animating, and `out.window.png` of the WebView is blank: read `getComputedStyle` at set times from a promise. Cost at rest: `document.getAnimations()`.
   - `bridges.update([{from, to, channel, isActive}])` draws a trace with no real bridge.
3. **Fake agents:** a synthetic click on a real card raises or closes a real window. Point `AGENTMANAGER_DATA`, `CLAUDE_CONFIG_DIR` and `CODEX_HOME` at scratch folders with registry files for windowless dummy processes whose parent has exited (so `WindowRaiser` finds nothing of the user's), a transcript each, and for lanes an `orch-status.json` in a folder listed in `OrchestratorDirs`.
4. **Console typing:** a scratch CLI on its own config folder and a local fake model server, hosted on a pseudo console so `AttachConsole` works on its pid, with agent variables stripped from its environment.
5. **A click that "does nothing"** may have worked: `WindowRaiser.Raise` raises a window where it is, perhaps on another monitor, while the log says `raised`.
6. **A running manager:** README, *Logs and live state*. A screenshot run shares the log, so each write seeks to the end and its lines are tagged. A copy in `Publish/` lacks a change until `publish.cmd` runs again.
