---
name: all
description: The /all command - send one message to every other live agent on this machine (Claude sessions by SendMessage, named Codex agents through the orchestrator inbox) and report who was reached; "/all wrap up", "tell all the agents", "tell everyone".
---

# /all

`/all <message>` sends the same message to every other live agent. Any session can run it; usually the Master Agent does.

1. **Claude sessions**: `ListAgents` lists every peer on this machine but you. `SendMessage` each one by its name exactly as its row prints it, adding ` [ref]` only when two rows share a name.
2. **The first line stands alone**: a receiver's human previews only that line. Send the user's words as written; do not soften or extend them.
3. **Codex** is not in `ListAgents`. For each `agent:<name>` owner in `agents` of `Saved/Orchestration/orch-status.json` (Astras, a Codex Master Agent), run `.claude/scripts/orch.cmd msg --to agent:<name> --file <path>`; it reads the message at its next `orch.cmd` call, and the Master Agent's watcher types the join line into an idle Astra with mail. An unnamed Codex window runs under the shared `codex:<pid>` owner, whose mail goes to whichever window reads first: report it as not reached.
4. **Report in a table** who was reached and who was not. A `[Cross-session delivery notice]` means that session holds the message for its user's approval or refused it: not delivered. A successful send means it reached the session's inbox, read at its next tool round.

`/all wrap up`, before a PC restart, sends Agent Manager's *wrap up* preset word for word: `WRAP_UP` in `AgentManager/AgentManager.App/Components/SendPanel.razor`.
