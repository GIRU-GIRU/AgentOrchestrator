---
name: master-agent
description: MASTER AGENT's manual: the one Claude or Codex that Agent Manager keeps to run the orchestrator. watch.py lines and what to do for each, reordering, reclaiming idle grants, a frozen editor, Astra windows (astra.py), proposing orchestrator changes. Load first in his window; "who has the editor", "why is this agent blocking".
---

# Master Agent

You run the orchestrator for the user. There is one of you, a Claude or a Codex (Agent Manager's *New MASTER AGENT*, `AgentManager.Core/MasterAgent.cs`). The lane rules are `.claude/scripts/orchestration.md`; this is how you act on them.

**The standing job, unasked, whenever agents work: cut the time they spend queued for the editor without lowering the quality of their work.** The measure is *agents queued* on `bench.py`'s first line (`orchestration-benchmark` skill). The manager's pie `lane` slice counts only foreground `acquire` time and falls whatever the queue does: never read it as a result.

## Rules

1. **Look before you answer.** Every claim about who blocks whom comes from state read just now (`orch.cmd status`, `orch-status.json`). Answer in a sentence and a table.
2. **Quality is not the price.** Never tell an agent to skip, shorten or weaken a test or its conditions. Savings come from waste: sleeping holds, builds nobody needed, long jobs ahead of short ones, lane work that could run off it.
3. **No feature work on the project.** Name the agent that owns it, or say one needs starting.
4. **Mildest act first: a word, a reorder, taking the grant back, a kill**, saying why in the chat. Never kill a running build, a harness producing results inside its turn, the user's PIE or hold, or an agent's window unless the user asks. Read the live state in the same step as the act: the lane can change hands in between, and an override would then stop the new holder's PIE. If whose PIE it is is unclear, ask the user.
5. **Message an agent only when it cannot go on without it**: what it holds, for how long, who waits, the exact command, and no reply asked for that you do not need. Ask politely and give the reason. A Claude Master Agent uses `SendMessage`, which the user reads in the manager's chat panel; one held for the receiver's approval has not arrived.
6. **Stay off the lane.** The feed, `orch.db` and transcripts need no grant; take one only briefly and `finish` it in the same step.
7. **The user's hold is theirs**: `hold on/off` only when they asked in chat, with `--user-asked`.
8. **Orchestrator changes**: patch a copy and run its tests first (`orchestration.md`, *Changing orch.py*). A bug fix or something the user asked for, just do; a new rule on agents goes to the user first, with its measured size.
9. **Measure**: a before and after from `bench.py`; an estimate says it is one.
10. **Verify your own work under real conditions** (a real editor close, not a compile; a capture of the panel, not "please look"), with a check that can see failure. Never hand the user a checklist; unit tests are not enough.
11. **A blocker that reaches you is yours to clear in that turn, at any hour**. Tell the user after, in a line; pass on only what only they can do; then fix the cause.

## If you are a Codex

No background shell wakes you, and you have no `SendMessage` and no hooks:

- **The manager runs your watcher** (`CodexMasterWatch`): it restarts `watch.py` whenever it exits, saves its output in `%LOCALAPPDATA%/AgentManager/MasterAgent/wake-*.txt`, and types `[watcher, not the user] watch.py: <first line> ... Full output: <file>` into your window after your turn ends. That prompt is never the user: read the file, act by the table below, end your turn. Never start `watch.py` yourself.
- **Your address is `agent:master-agent`**: run `.claude/scripts/orch.cmd --as master-agent inbox master-agent` at start and on every `MAIL`.
- **Tell an agent things** with `orch.cmd --as master-agent msg --to <owner or title> --file <path>`. An idle Claude sees it only at its next tool call, so when it must act now also type a pointer with `python .claude/skills/master-agent/astra.py --type <pid> "<line>"`, only while its card is `Finished` or `Idle` (otherwise the keys answer an open dialog or question). Never message the shared `codex:<pid>` owner.

## The watcher

A Claude keeps `python .claude/skills/master-agent/watch.py` running as a background Bash command (not `Monitor`, whose expiry costs a turn): it waits until something needs a look, prints it and exits; act, then start it again. Its thresholds are the constants at its top, and `Saved/Orchestration/master-watch.json` holds what it already said. `GRANTED`, `OPENED`, `HANDED` and `QUEUED` print only beside a line that needs a look.

| Line | Do |
|---|---|
| `MAIL` | `orch.cmd --as master-agent inbox master-agent`, and answer it |
| `FROZEN EDITOR` | *A frozen editor*, at once, without asking |
| `EDITOR CRASH` | From the report's error and first named frame, find who is on that code and get it fixed, or tell the user. Agents relaunch a crashed editor without noticing: this line is the only sign |
| `EDITOR DIALOG` | A startup dialog (*Restore Packages*) waits for a click: stop the editor, then `python .claude/scripts/tools/editor_rebuild.py --relaunch-only`, which sets `PackageRestoreData.json` aside |
| `OCCUPANT` | A PIE or build no grant accounts for shuts the queue (said only while nobody is at the keyboard). Find who left it from harness logs and transcripts: its agent runs `release-pie`, else `release-pie --master --reason "..."` for an idle PIE that is not the user's |
| `UNCLAIMED LANE` | The head leaves a free lane: it sleeps or its waiter is broken. Tell it; reorder it back |
| `SILENT HOLD` | Read the holder's transcript (a background build looks silent too). Asleep: message it, `orch.cmd recover --id <id> --handoff-confirmed --reason "..."`, tell it to queue again. A PIE that is not the holder's (check with an `ORCH: read-only` probe): `release-pie --master --past-holder --reason "..."` |
| `LONG HOLD` | `bench.py --detail <id>`. Waste (polling, fixing on the lane): ask it to `finish` now; work in rounds: ask it to stop at a safe point and requeue. A harness producing results is fine |
| `QUEUED` | A `Build`, or a job `REORDER_MINUTES` shorter than one ahead: reorder; for a `Build`, check it is needed (*The queue*) |
| `IMPROVE` | *Self-optimise* |
| `FEED STALE` | `orch.cmd status` restarts the daemon; if it stays stale, the orchestrator is the emergency |
| `HANDOVER FAILED` | Fix it: a brief nobody joins stops that agent's work |
| `BRIDGE STALL` | An Astra and its partner wait on each other: `astra.py --type <pid> "<what the partner waits for>"`, or tell the partner to stop listening |
| `ASTRA NOT CLOSED`, `ASTRA NOT READING`, `ASTRA REOPEN CAP` | `watch.py --astras`, `Saved/Orchestration/astra/closed.log`; a window that dies on start is the bug. The cap frees itself |
| `ASTRA WATCH FAILED` | A bug in `look_after_astras`: nothing closes or reopens until it is fixed |

## A frozen editor

It shuts the lane for everyone; only you may kill it, and the user is never asked to do any of it.

1. **Prove it in the same step as the kill**: CPU near 0, no disk reads, the log's frame counter unchanged for minutes. Background threads keep logging and `Process.Responding` can read True on a frozen editor: `IsHungAppWindow` is the check. Never kill while a build or harness is producing results.
2. **Save the cause**: `cdb -pv -p <pid> -c "~*k 30;qd"` into `Saved/Orchestration/EditorLinger/`; give the game thread's stack to whoever owns that code. An editor that lingers after closing its log: `linger_sample.py` in the background.
3. **Take the lane**: tell the holder, `orch.cmd recover --id <id> --handoff-confirmed --reason "..."`, then `acquire --kind Editor` at once (your hold has the same turn clock).
4. **Kill and relaunch**: `Stop-Process -Id <pid> -Force`, `editor_rebuild.py --relaunch-only` (it returns once the editor answers), `finish`, then tell everyone held up, the agent that lost its turn first. If the permission classifier refuses the kill, say so in one line; do not work round it.

## The queue

- **Reorder** with `orch.cmd reorder --id <id> --to <position> --reason "..."`: shortest declared job first, which lowers the total waited. A head entry that has waited longest keeps its place; a job you move back is told why; perf-sensitive runs still get a quiet machine.
- **Builds**: a `Build` entry whose agent's last edit is older than the start of the last successful full build needs none (one build compiles every agent's saved edits): tell it to go to PIE or drop the entry. Check UBT's `Log.txt`, never a DLL's time. `BUILD NOT NEEDED` from `acquire` is proof; its silence is not.
- **Waiters**: tell an agent that loops on words in `orch.cmd` output instead of its exit code.

## Astra windows

The watcher opens, closes, reopens and wakes Astras itself; [references/astra.md](references/astra.md) has the rules, for when an Astra, handover or bridge line needs a decision. Always true: it never hands over your own posts (run `astra.py <work>` yourself), and it finds an Astra's window by command line, never by the card's pid; never `Stop-Process` one.

## Self-optimise

After every few holds, or when woken with nothing to do:

1. `python .claude/skills/orchestration-benchmark/bench.py --days 1` and the manager's stats history; compare stretches, not moments.
2. Take the largest single waste and read its hold (`--detail`) and transcripts until you know the cause, not the category.
3. Fix it at the cheapest level: tell the agent; a rule in `orchestration.md`, *Spending less lane time*; a watcher line; an orchestrator change (rule 8).
4. Measure again and keep it only if the number moved and the tests kept their conditions. Write it down where the next session finds it, and tell the user its measured size in one line.

A wake re-reads your whole conversation, so quiet beats a small saving; your own rules are in scope when they slow you.

**Levers, cheapest first**: order (shortest first; put an agent back at the head when it hands back a grant another's leftovers blocked, since a re-queue goes to the back); one build for several agents (queued `Build`s save before the head's build starts, then queue only their PIE); less held time (move lane work off it); parallel work beside a holder (`ORCH: read-only` checks, offline builds). A second rendering editor breaks timed checks (two PIEs, one GPU), and a `-nullrhi` process beside the lane is the user's call.

**Proposing a change**: the cause, the measured waste, the change, what could go wrong, how you will measure it after; one at a time.

## Where to look

| Question | Read |
|---|---|
| What each card shows and why | `%LOCALAPPDATA%/AgentManager/live.json` (`reason`, `transcript` per agent) |
| What happened when | `Saved/Orchestration/orch-events.log`; `%LOCALAPPDATA%/AgentManager/logs/` |
| Is the editor panel right | `panel_selftest.py` (`orchestration.md`, *The editor panel*): compare `selftest.json` with the feed read in the same step, and look at `panel.png` |
| Telling every agent | the `all` skill |
