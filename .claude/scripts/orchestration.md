# Agent orchestration

Every Claude and Codex session in a project shares one editor, one build output and one Perforce workspace. `orch/orch.py` coordinates them; call it as `.claude/scripts/orch.cmd <command>` from Bash, PowerShell or cmd. Measuring lane time is the `orchestration-benchmark` skill; running the queue is the `master-agent` skill.

State is `Saved/Orchestration/orch.db`; change it only through `orch.cmd`. Beside it: the feed `orch-status.json` (*The feed contract*), the mirror `editor-session.json` (`status --json`), and the journal `orch-events.log` (every grant, release, reclaim, message and relaunch; read it first when something surprises you).

## Commands

| Command | Does |
|---|---|
| `acquire --kind <kind> --purpose "<agent>: <task>: <why>" --minutes N [--wait S]` | Queue and wait up to `S` s (default 540). Exit 0 `GRANTED`, 3 `WAITING` (prints `id=`), 2 refused or dropped |
| `acquire --id <id> --until-granted` | Run in the background: exits 0 granted or 2 dropped |
| `finish --id <id> --evidence "..." [--keep-pie] [--keep-processes]` | Release a grant |
| `cancel --id <id>` | Drop a queue entry |
| `release-pie` | Stop a PIE you left behind (*PIE ownership*) |
| `transfer --id <id> --to <owner>` | Hand a grant to a live agent; its turn starts afresh |
| `begin --id <id> [--editor-pid N]` | Record that you are starting PIE, and in which editor |
| `status [--probe] [--json]` | Grants, queue, occupants, processes, agents, tasks; `--probe` asks a free editor about PIE |
| `task`, `hold`, `msg`, `inbox`, `name` | *Tasks*, *The user's hold*, the `ai-bridge` skill |
| `reorder`, `recover`, `release-pie --master` | Master Agent only |
| `heartbeat`, `reap`, `config resurrect\|lane-turn on\|off` | Mark yourself active; reconcile now; switches |

Global flags go before the command word: `--as <name>`, `--owner <id> --owner-pid <pid>`.

**Go by exit codes, never by words in the output.** A purpose can contain any word, and a loop matching `WAITING` (PowerShell's `-match` ignores case) reads its own grant as queued and sleeps on the lane.

## Resources

| Kind | Holds | For |
|---|---|---|
| `PIE`, `Build`, `Editor`, `Sync` | the lane, exclusive | PIE; full builds and Live Coding; editor launch, close or kill, commandlets, `ue_python.py`, remote execution, Unreal MCP `call_tool`; `p4 sync/revert/unshelve/resolve` and the other `P4_CONTENT` verbs |
| `Test` | the lane, shared | `UnrealEditor -game/-server` and the `<Name>`, `<Name>Client`, `<Name>Server` executables (they lock the DLLs a build needs) |
| `Deploy` | deploy, exclusive | commands matching `DEFAULT_DEPLOY_PATTERN`, or `ORCH_DEPLOY_PATTERN` (case-insensitive; one that does not compile is ignored). `RunUAT` packaging is a build |
| `P4` | a short mutex, implicit only | `p4 edit/add/reconcile/submit` and the other `P4_META` verbs |
| none | | reading, searching, editing, `-singlefile` compiles, `ORCH: read-only` scripts, Unreal MCP calls other than `call_tool` |

Any lane grant covers every lane command; a `Test` grant covers only test processes.

## What the orchestrator enforces

- **A grant is yours while you work**: your process is alive with a tool call open, its turn running, or a background tool shell alive (up to `BACKGROUND_MAX`, so a background waiter, inbox loop or harness keeps your grants). `STOP_GRACE` after your turn ends with nothing in the background, or when your process dies, your queue entries drop and your grants are reclaimed.
- **Waiting on the user is stopping.** `AskUserQuestion`, `ExitPlanMode` or a permission prompt open for `USER_WAIT` makes you `waiting-user`: grants and queue places go. `finish` before you ask.
- **Reclaiming cleans up**: it stops a PIE that grant started, kills its test processes and waits out builds, commandlets and `p4 sync` (`WAITED_OUT_KINDS`). The grant stays `releasing`, lane shut, until the PIE stop settles, so the stop never hits the next holder's PIE (`stop_reclaimed_pie`).
- **A hold is a turn once anyone waits.** With someone queued it ends `LANE_TURN` after the grant, never sooner than `LANE_TURN_NOTICE` after the first waiter queued (`lane_turn_due`). Then your next lane command is refused and you can only `finish`; the lane comes back `LANE_TURN_GRACE` later if nothing runs, at `LANE_TURN_HARD_STOP` regardless (builds still waited out). The user's hold stops the clock.
- **No waiting while holding**: an owner holding a grant cannot queue for another, busy resource (deadlock); `acquire` exits 2. A free one is merged into the grant held.
- **FIFO with a head grace**: conflicting requests are served in order, and a head that leaves a free lane unclaimed for `HEAD_GRACE` is passed over. The hook refuses a lane command to anyone queued behind another agent. Only the Master Agent reorders.
- **No grant beside a PIE.** A `PIE`, `Build`, `Editor` or `Sync` entry waits behind any PIE or full build no grant accounts for (`occupants`); PIE is read from the editor's report, else the probe. A stale report of a running PIE is believed for `FROZEN_EDITOR_SECONDS`, since a PIE map load blocks its writer; after that the editor counts as hung, and a hung editor never shuts the lane. An unregistered PIE is the user's while anyone is at the keyboard, and is stopped after `HUMAN_IDLE_FOR_ORPHAN_PIE` without input.
- **No griefing**: `p4 revert` of files another live session edited recently, or a wildcard revert while anyone else edits, is refused (`revert_clash`). A build failing in another session's files tells the builder they are not its to fix and notifies their owner.
- **The P4 mutex follows p4, not the tool call**: it is released once no `p4.exe` has run under its owner for `P4_IDLE`, so a later p4 in a chained command runs unprotected. Give `p4 submit` its own call.
- **A frozen editor is the Master Agent's.** Message it (`msg --to master-agent`) with the pid and evidence; never kill the editor or ask the user to.

## Using the lane

**One command** runs at once when the lane is free, under an implicit grant that ends with it, and is refused, naming the holder, when it is busy. The hook waits only behind a holder about to let go (another agent's single command, a `releasing` grant) for up to `IMPLICIT_WAIT`, and behind the `P4` mutex for up to `P4_WAIT`.

**Multi-step work:**

1. Prepare everything first (*Spending less lane time*).
2. `orch.cmd acquire --kind PIE --purpose "Claude: menu fix: verify the pause menu" --minutes 10 --wait 540`.
3. On exit 3, run `orch.cmd acquire --id <id> --until-granted` in the background and do other work; it wakes you once. A foreground `acquire --id <id> --wait 540` also keeps your place and returns early (exit 3) when a message arrives: run it again.
4. Do the whole job yourself, then `orch.cmd finish --id <id> --evidence "PIE stopped, nothing in flight"`.

**Done means nothing held.** In the same turn, `finish` every grant and `cancel` every queue entry. A PIE with no grant is an occupant and shuts the lane to everyone.

### PIE ownership

`pie_verdict`: a PIE is `ours` when it came up after the grant began and either no input reached that editor in the `PIE_START_INPUT_WINDOW_SECONDS` before it (`pieByInput`) or the holder recorded its start first (a hooked command matching `RX_PIE_START`, or `begin`); `not ours` when it was up before the grant or someone at the keyboard started it; else `unknown`. Only `ours` is ever stopped (by `finish`, a reclaim, `release-pie`, `hold on --stop-pie`, the panel's take); anything else stays up, journalled `pie-left`. If a person may be in the editor when your harness starts PIE, run `begin --id <id>` just before, or their mouse makes the PIE theirs.

- `finish` of a `PIE` or `Editor` grant stops its own PIE; a `Build` or `Sync` grant's stops nothing, since the user may be playing during your build.
- `finish --keep-pie` stages a PIE (`ours` or `unknown`) for the user. It holds the lane like any PIE and stops at the user's next prompt to the agent that staged it.
- `release-pie` stops a PIE you left behind while nobody holds the lane: for the agent that staged it or held one of the last `RELEASE_PIE_LAST_GRANTS` lane grants, if it came up in one of them. Queued behind your own leftover PIE, use it, never a lane command.

### Scripts and test processes

- **A background chain cannot outlive the grant.** No hook sees a `ue_python.py` run inside a script, so `ue_python.py` asks `guard --operation Work` itself and exits 1 on `DENIED`. A wrapper that runs it needs an explicit `acquire`: the hook grants implicitly only a command naming `ue_python.py`. Once a `( ... ) &` chain's tool shell exits, `guard` finds the agent through `CLAUDE_PID`, without counting it as working. Anything short of a clear `DENIED` runs the script (no `orch.cmd`, `ORCH_GUARD_TIMEOUT_SECONDS`, a human's terminal).
- **Read-only scripts**: a `ue_python.py` script with `ORCH: read-only` in its first `READ_ONLY_HEADER_BYTES` runs beside any PIE, the user's included, with no grant. Name it by a literal path with no option before it, and keep the promise: no spawns, property writes, saves, or PIE start or stop.
- **Test processes** die with the grant unless `acquire` or `finish` has `--keep-processes`. Yours are those under your process tree, plus an `UnrealEditor -game/-server` started while you held the lane. A packaged game with no agent ancestor is the user's and never claimed.
- **Parallel subagents of one session share its owner** (`claude:<pid>:<ticks>`), so `acquire` hands each fork the grant another holds. They still acquire, then serialise with `mkdir Saved/Orchestration/ForkLane/lock` (atomic) holding an `owner.txt` (task, step, time), removed when the lane step ends. Waiters append to `ForkLane/queue.txt`; only its first line takes a free lock, and a fork that just released queues again. An `owner.txt` unchanged for 40 minutes is stale.

## Tasks

`task start "<title>" [--next ...] [--files ...]`, `update <id> --next ...`, `block <id> --on user --why ...`, `done <id> --evidence ...`, `handoff <id> --note ... [--to <owner>]`, `cancel`, `claim`, `list [--all]`.

- The Stop hook refuses to end a turn, once, while you hold a grant, are queued with no background waiter, or have a task `in_progress`.
- A task whose process died without `SessionEnd`, or ended for a reason outside `DELIBERATE_END_REASONS`, becomes `orphaned` (unless blocked on the user), and `resurrect` relaunches it with `claude -p --resume <session>`: one at a time, at most `RESUME_MAX_ATTEMPTS`, never while a live process runs that session (`running_sessions`). Logs: `Saved/Orchestration/resume/`; `config resurrect off` disables it.
- A resumed session takes back its tasks, channels and undelivered mail on its next hook, or a hookless Claude on its next `orch.cmd` call (`adopt_resumed_session`).
- A task with no live owner and no update for `TASK_AGE_OUT_DAYS` expires; `task claim` revives it.

## Talk only when needed

Every message makes both sides re-read their whole conversation, and wakes an idle receiver. Send one only when the other agent cannot go on without it (a job, a result it waits for, a blocker only it can clear), with everything in it and no reply asked for that you do not need. No thanks, acknowledgements or progress reports; what an agent may want later goes in a file, the task ledger or a changelist description.

Messaging is the `ai-bridge` skill. The Master Agent, Claude or Codex, is `orch.cmd msg --to master-agent "..."` (or `--file <path>`).

## Spending less lane time

Every minute you hold the lane, everyone queued waits that minute. These rules come from `bench.py` measurements (`orchestration-benchmark` skill); add one only once it is measured.

- **Prepare before you acquire**: the code, a `-singlefile` compile of every changed `.cpp` (UHT first for a header with reflection markup), checkouts, the harness, what you will measure. The lane is for the full build, the editor restart, the run and reading its numbers.
- **One prepared run and one full build per hold, then finish**, before you write up. Write fixes after you release, then queue again; only with nobody queued may a quick fix and its rebuild stay in the hold. A hooked holder that edits a `.cpp`, `.h`, `.py`, `.ps1`, `.sh` or `.js` while someone waits gets one note per grant (`lane_patch_note`).
- **A build nobody needs says so.** All agents share one source tree, so one build compiles everyone's saved edits. A `Build` `acquire` prints `BUILD NOT NEEDED` when UBT's logs prove that the last full `<Name>Editor Win64 Development` build succeeded and no source, `.uproject` or UHT timestamp changed since (`build_not_needed_line`). Silence proves nothing: a `-singlefile` run with the editor open usually causes it.
- **Declare the minutes you expect**: the Master Agent orders the queue shortest declared job first.
- **Few tool calls while holding**: each costs a model turn. Chain back-to-back steps into one call; run each harness stage as one script that checks itself and stops at its first failure.
- **Use the tools** (flags and exit codes in their docstrings). `python .claude/scripts/tools/editor_rebuild.py` closes, builds and relaunches, stopping at once when a close is refused; `--check` (no grant) lists what would block the next agent's close, so run it before you `finish`. `wait_result.py` returns on a result file, an editor-log traceback, a `--driver-pid` exiting, or PIE up or down, instead of a sleep loop or repeated probe calls.
- **Turn the editor's background throttle off from your script, for the run**, never with a launch flag: a launch with `bThrottleCPUWhenNotForeground=False` renders at full rate while nobody looks.
- **Headless `Test` runs**: short, processes killed when the script ends, even on failure, and never up while the editor saves (they lock the assets they loaded).

## The user's hold

The user takes the editor with **Take editor** (the level editor toolbar, or the **Agent Orchestrator** tab under Window > Tools) or `orch.cmd hold on [--reason "..."]` in their own terminal, and gives it back with **Resume agents** or `hold off`. State: meta key `human_hold`.

- While it is on, lane commands are refused with `PAUSED` (after the hook waits `IMPLICIT_WAIT`), no lane grant is given, `guard` allows only `Inspect`, and the orchestrator runs nothing in the editor. Reading, editing, `-singlefile`, `p4 edit/add`, `Deploy` and read-only scripts still run.
- Nobody is preempted: running commands finish, grants and queue places are kept. Wait with `orch.cmd hold wait` (exit 0 `RELEASED`, 3 `STILL HELD`; repeat) and work meanwhile; ending your turn lets `STOP_GRACE` reclaim the grant.
- Only the user flips it: from an agent's process tree `hold on/off` is refused unless the user asked in chat (`--user-asked`). The editor's button counts as the user, even in an editor an agent launched (`caller_is_agent`).
- It never lapses on idle. A hold taken in the editor ends `ORPHANED_HOLD_SECONDS` after no editor runs; a terminal hold only with `hold off`.
- The panel's take stops the holder's PIE only when *Stop the agent's PIE when I take the editor* is ticked (per user, `GEditorPerProjectIni`) and the holder's grant is on this editor and `ownsPie` (`--stopped-pie`). From a terminal: `hold on --stop-pie`.

## The editor panel

`AgentOrchestratorPlugin/` is an editor-only plugin: a toolbar chip with the take/resume toggle, and the **Agent Orchestrator** tab (hold, lane holder, the project's editors, queue, agents).

- **The orchestrator decides, the plugin displays.** Every rule is in `orch.py`. The plugin acts only by running `orch.cmd` off the game thread with no console window (`FOrchestratorStatus::RunOrch`): `hold on|off --source editor`, and `status` when the feed is older than `STALE_AFTER_SECONDS`, since with no agent active nothing else restarts the daemon.
- **Its agent rows are Agent Manager's** (`%LOCALAPPDATA%/AgentManager/live.json`, `ReadManagerFile`), or the feed's when that file is older than `MANAGER_STALE_SECONDS`.
- **Install**: copy `AgentOrchestratorPlugin/` into the project as `Plugins/AgentOrchestrator` and enable it in the `.uproject` (`"Plugins": [{"Name": "AgentOrchestrator", "Enabled": true}]`; `EnabledByDefault` is false). Any full `<Name>Editor` build compiles it; it has no reflection markup.
- **Trap**: an editor that finds the plugin enabled but its code missing or broken asks every user to rebuild it, and declining closes the editor. Never enable it before its code compiles; to compile it with the editor open, build a host project that lists its folder in `AdditionalPluginDirectories`.
- **Verify**: `.claude/skills/master-agent/panel_selftest.py` (commands in its header; it takes the lane) runs `Orch.SelfTest`, which reads the strings back from the real widgets, drives `DecideTakeClick` through its cases, and writes `Saved/Orchestration/editors/selftest.json` last (`result`, `failures`) beside `panel.png` and `chip.png`. It injects no input, so a button wired to the wrong function passes.

### The feed contract

`write_status` writes `Saved/Orchestration/orch-status.json` on every hook, CLI call and daemon tick (`DAEMON_PERIOD`). The plugin (`FOrchestratorStatus::Parse`), Agent Manager (`StatusFeed.Parse`) and `watch.py` parse it. **Add fields; never rename or retype one**, or the readers silently show defaults. Times are Unix seconds.

- `summary.dot` is orange only while an agent holds or queues for the lane, blue under the hold, red while an editor is `frozen`, else green: an occupant with nobody queued stays green. The plugin greys it when the feed is stale.
- Grant rows carry `activity` (plain words, `grant_activity`), shown instead of `phase`, which only records the PIE protocol; also `ownsPie`, `editorPid`, `turnDueAt` and `turnBackAt` (0 with no clock).
- `editors` lists the project's editors, main first (`editor_rows`); their `pie` is true, false, or null when nothing can tell, never a guess.
- `displayName` and the other `*Name` fields are what to show (`display_name`); `title` is the raw session title (`refresh_titles`, the same rule as Agent Manager's).

The editor's own report, `Saved/Orchestration/editors/<pid>.json` (`pid`, `pie`, `at`, `pieSince`, `pieByInput`), is written by `FOrchestratorStatus::ReportPieState` every `PIE_REPORT_INTERVAL_SECONDS` and at once on PIE start, and deleted on shutdown. One older than `EDITOR_REPORT_TTL` is no answer, so a frozen editor reads null.

## Codex and other clients without Claude hooks

The orchestrator finds a Codex process among the command's ancestors. If it cannot, pass `--owner codex:<stable-session-id> --owner-pid <codex pid>` before the command word on every call, or set `ORCH_OWNER`; any other client uses `--as <name>` (owner `agent:<name>`). Without hooks nothing gates or reminds you:

- Run `acquire` before any lane operation and `finish` after it.
- While holding, make an `orch.cmd` call at least every `CLI_SILENCE` (`heartbeat` is cheapest), or you count as stopped. A turn overrun is not refused, only reclaimed, PIE stopped.
- `ue_python.py` still refuses a non-read-only script until your owner holds the lane. Every Codex window runs under one app-server owner, so one window's grant passes them all, and `inbox`, which drains by owner, gives the mail to whichever window reads first: give each window its own `--as <name>`.

## Installation and configuration

The project root holds `.claude/`. The project is the single `*.uproject` there, or the file `ORCH_UPROJECT` names (absolute or relative to the root); with none, or several, no editor or game counts as the project's. Its stem `<Name>` gives `<Name>Editor`, the executables `<Name>`, `<Name>Client`, `<Name>Server` (optionally `-Win64-<Config>`) and the log `Saved/Logs/<Name>.log`. The engine is `UE_ROOT`, else the one `EngineAssociation` names in the registry, else the launcher's default folder (`ue_python.py`'s docstring has its own lookup and the `bRemoteExecution` setting it needs). `ORCH_STATE` moves the state folder (the plugin honours it), `ORCH_NO_DAEMON` never starts the daemon, `ORCH_SANDBOX` (the tests') kills nothing, asks no editor and starts no daemon or session.

**Hooks.** Each machine creates `.claude/settings.local.json` itself (the repository `README.md` has it): `python .claude/scripts/orch/orch.py hook <Event>` for `PreToolUse`, `PostToolUse` and `PostToolUseFailure` (matcher `*`), `Stop`, `SessionStart`, `SessionEnd`, `UserPromptSubmit` and `Notification`. Give `PreToolUse` a `timeout` above `P4_WAIT`, or Claude Code kills it mid-wait. Running sessions pick the file up. Without it nothing gates a lone lane command or reclaims a stopped agent.

**Check it.** Hook events add lines to `Saved/Orchestration/orch-hook-fields.log` (until its size cap, `log_hook_fields`). Probe the gate with `python -c "print('Build.bat')"`: it classifies as a build and, if wrongly allowed, only prints. `echo Build.bat` is no probe: `echo` is a reader.

**Trap: classification ignores quoting.** `classify_command` splits on newlines, `;`, `&&`, `||`, `|`, `{` and `}` before reading each segment's program, so `grep "a\|ue_python" x.py` has a segment naming `ue_python.py` and is refused while the lane is held. A heredoc body is read as commands too, unless it is fed to `cat` or `tee` (`strip_data_heredocs`): a `python - <<EOF` whose script mentions `ue_python.py` is an editor call. Use the Grep and Edit tools, or write the script to a file first.

## Changing orch.py

- Every agent runs `orch.py` on every tool call, so a broken one stops them all. Patch a copy of `.claude/scripts/{orch,tests}`, run its tests, then change the live file.
- Tests: `python .claude/scripts/tests/orch_test.py`, against a temporary state folder, a fake process table and clock, with every editor call, kill and sleep faked; the `orch.py` and `ue_python.py` copies they start run under `ORCH_SANDBOX` and `ORCH_NO_DAEMON`.
- The daemon exits when `orch.py`'s modification time changes; the next call starts the new code.
- A live two-agent test needs no second login: be agent B with `orch.cmd --owner orchtest:B --owner-pid <pid of a long-lived process you started> <command>`. B lives while that process does (kill it to simulate a crash) and has no hooks, so let B hold the lane while you, the hooked session, are refused.
