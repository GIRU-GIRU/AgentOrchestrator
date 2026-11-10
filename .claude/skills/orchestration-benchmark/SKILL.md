---
name: orchestration-benchmark
description: Measuring where the shared editor lane's time goes with bench.py, from orch-events.log and the Claude transcripts - hold and queue time, agent-minutes behind each hold, builds, launches and tool calls per hold, one hold call by call; "testing blocks everyone", "why did I wait an hour", "find a time saving", "is this saving real".
---

# Orchestration benchmark

`bench.py` measures how the lane (`PIE`, `Build`, `Editor`, `Sync`, `Test`) is spent. The rules for spending less of it are `orchestration.md`, *Spending less lane time*; a saving found here is written there.

## Rules

- **Measure before claiming a saving**: a before and after on the same measure, never an estimate shown as a result. Tell the person you work for in one line, with its size.
- **It only reads** the journal, `orch.db` (opened read-only) and transcripts. It takes no lane and must never write orchestrator state.
- **Each machine measures its own lane**: the journal and transcripts are local.
- **A saving that slows builds or tests**, adds a process to babysit, or adds a lane rule agents cannot follow is not one. Change how agents work before changing `orch.py`, which is the user's decision.

## Running it

```
python .claude/skills/orchestration-benchmark/bench.py --days 7
python .claude/skills/orchestration-benchmark/bench.py --since YYYY-MM-DD --until YYYY-MM-DD --owner <owner id prefix>
python .claude/skills/orchestration-benchmark/bench.py --days 3 --detail <grant id prefix>
python .claude/skills/orchestration-benchmark/bench.py --days 7 --json <scratchpad>/bench.json
```

`--since` and `--until` also take `YYYY-MM-DDTHH:MM:SSZ`; nothing journalled after `--until` counts, so a past window gives the same answer later. Times are UTC. `--top N` sets the table rows; `--json` writes the analysis without each hold's call list. A week can take a minute: it reads every transcript modified in the window, in every folder under `~/.claude/projects`, because sessions started outside the project carry much of the waiting.

## Reading the report

- **First line**: lane hours per kind, then **agents queued**, the hours agents waited for the lane (the Master Agent's measure), and how many agent-minutes of it were behind another agent's hold.
- **Agent-minutes behind** a hold: every queued entry's wait charged to each other owner's hold it overlapped, so it can exceed the hold. It is what a hold cost everyone else.
- **Inside the holds**: matched hold time split by category (below), with the agent-minutes behind each and *Batched min*.
- **Counters**: full builds (failed, refused, up to date) and the build tool's own seconds; editor launches; harness calls, and those in polling loops; tool calls; holds *patched after the first lane call* (a patch seen only in a command's text, `SHELL_PATCH`, can overcount, so the Edit/Write count is given apart).
- **Tables**: holds per transcript folder, holds by agent-minutes behind them, the longest waits. `--detail` gives each call's start, the gap before it, its seconds, batch size, category and command.

## Categories

Every second of a matched hold goes to one category; an instant two calls cover counts once, as the earlier one in `CATEGORIES`. A category comes from the program each statement runs (`command_facts`: heredoc bodies and comments dropped; a script it runs is found by path, in the session's scratchpad, under `SCRIPT_ROOTS` or in the transcript's own `Write` of it, and read the same way), never from text that merely names a tool.

| Category | Detected by |
|---|---|
| full build | the build tool as a program, without `-singlefile` or `-Mode=`, with a result it printed |
| harness | a `ue_python.py` test run, `wait_result.py`, an Unreal MCP call, a script doing these, a sleep loop testing a file or log |
| editor restart | an editor launch, quit, or wait on the editor process, with no test run in the call (a probe of the editor, `is_editor_probe`, is no test run) |
| headless test | the editor run with `-game`, `-server` or automation arguments |
| compile check | `-singlefile` and UHT runs |
| waiting / polling | a sleep with nothing identifiable to wait for |
| orchestrator | `orch.cmd` or `orch.py` inside the hold |
| p4, shell, subagent, read / edit | `p4`; any other command; the `Agent` tool; file tools |
| agent thinking | gaps between calls: the model's turn and API latency. Falls with the number of calls |
| waiting on the user | a gap of `USER_WAIT_GAP_SECONDS` or more that ends in a human prompt |

## How a hold is matched

- The journal joins each explicit grant's `queue`, `grant` and `release` lines by the first eight characters of its id. A lone lane command logs only `implicit`; its length comes from the owner's latest transcript.
- A hold belongs to the transcript (forks included) whose tool call ran `acquire` with the hold's purpose, the closest one before it was queued within `ACQUIRE_MATCH_SECONDS`, matched on the purpose's opening `PURPOSE_SNIPPET_CHARS` up to any quote or backslash (`acquire_snippet`, which drops one too short to trust). Unmatched, it falls back to `owners.transcript`, the owner's latest session only. So give purposes a few plain words before any quote.
- A call runs from its `tool_use` to its `tool_result`; a background call ends at its `<task-notification>`.

## Traps

- **Batched calls**: every result of one message stamps the slowest call's end, so a quick `Edit` can show the batch's time. *Batched min* bounds that error; `--detail` shows batch sizes.
- **No transcript**: a Codex holder, a deleted transcript, or an unmatched purpose. Such holds count in the totals only.
- **Restart inside a test**: a call that restarts the editor and then tests counts as `harness`; measure restart overhead from the editor log against `procs.gone_at`.
- **Scripts as they are now**: scripts are read in their current state (or as the transcript last wrote them) and an `if` is invisible, so a conditional launch counts only if the output mentions it. A script holding a tool's name in a string, as `bench.py` does, reads as that tool.
- **Builds** count only with a result the build tool printed: in the call's output, its background output file, or a later call within `BUILD_RESULT_LOOKAHEAD_SECONDS`. A call two holds overlap counts in the one it spent most time in.
- **History is short**: the journal rotates to `orch-events.log.1`, which `bench.py` does not read (and `orch.db` keeps only recent finished grants). Measure a stretch before it rotates away.
- **No stopwatch**: categories are inferred from transcripts; check a surprising number with `--detail`.

## Extending it

`bench.py` imports as a module. `analyse(args)` takes an object with `days`, `since`, `until` and `owner`, and returns a dict whose `held` list has one dict per hold: `id`, `owner`, `kind`, `purpose`, `queued`, `granted`, `end`, `hold`, `wait`, `made_wait`, `transcript`, `split` (seconds per category, `None` when unmatched), `behind`, `batched`, `inside` (its calls) and the build counters. Write one-off analyses in your scratchpad against it, and fold a measure into `bench.py` once it has proved useful twice.
