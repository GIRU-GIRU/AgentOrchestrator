using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core;

public sealed record StateVerdict(AgentState State, bool IsRead, string Reason);

public static class StateClassifier
{
    /// <summary>
    /// A task counts as this agent's unfinished work only if it was touched in its last turns. The orchestrator never closes a
    /// task by itself, and without this any card that once forgot <c>task done</c> would read paused for the rest of its life.
    /// </summary>
    private const int OPEN_TASK_TURNS = 2;

    /// <summary>
    /// Red when the agent waits on the user (a prompt or question it blocks on, a turn that ended on a question, a task it
    /// filed as blocked on the user, a message that leaves the next step to the user, or a local server it left up for the user
    /// to look at), even with something running in the background; paused, with no colour, when its turn ended with its
    /// recent orchestrator task still open, since the work is not done; finished, with no colour, when its turn ended with nothing
    /// left for the user; purple when it holds the editor lane or is calling the editor, since holding the lane is not finished
    /// even after its turn ends; orange when it is stuck on an
    /// orchestrator lane (queued, or paused by the user's hold); yellow when it asked the user something but carries
    /// on working; green while it works. Marking an agent read clears red and yellow until the agent next asks.
    /// </summary>
    public static StateVerdict Classify(AgentSnapshot agent, DateTimeOffset? readAt)
    {
        if (!agent.IsAlive)
        {
            return new StateVerdict(AgentState.Closed, false, "process gone");
        }

        var hasBackgroundWork = agent.RunningSubagents > 0 || agent.RunningCommands > 0;
        var isBlockedOnUser = agent.PendingQuestion || agent.WaitingFor is not null;
        var hasTurnEnded = !agent.IsWorking && agent.HasPrompt;
        var isTurnOver = hasTurnEnded && !hasBackgroundWork;
        var endsOnAQuestion = hasTurnEnded && EndsOnAQuestion(agent);
        var request = hasTurnEnded ? RequestInLastTurn(agent) : null;
        var hasTaskOnUser = hasTurnEnded && agent.Lane is { TaskBlockedOnUser: true, QueuedKind: null };
        var hasLeftAServer = isTurnOver && agent.RunningServers > 0;
        var waitsOnUser = isBlockedOnUser || endsOnAQuestion || request is not null || hasTaskOnUser || hasLeftAServer;
        var editorHold = EditorHold(agent);
        if (isTurnOver && !waitsOnUser)
        {
            return editorHold ?? OpenTask(agent) ?? new StateVerdict(AgentState.Finished, false, "turn ended, nothing asked");
        }

        if (waitsOnUser)
        {
            var since = agent.NeedsInputSince ?? agent.LastEventAt;
            var isRead = readAt is { } read && read >= since;
            var why = isBlockedOnUser ? "waiting for " + (agent.WaitingFor ?? "an answer: " + agent.QuestionText)
                : endsOnAQuestion ? "turn ended on a question: " + agent.QuestionText
                : hasTaskOnUser ? "its task is blocked on you: " + agent.Lane!.TaskTitle
                : request is not null ? "waiting on you: " + request
                : "it left a local server up for you to look at";
            if (!isRead)
            {
                return new StateVerdict(AgentState.NeedsInput, false, why);
            }

            if (isTurnOver || isBlockedOnUser)
            {
                return editorHold ?? new StateVerdict(AgentState.Idle, true, why + ", marked read");
            }
        }

        if (editorHold is not null)
        {
            return editorHold;
        }

        if (agent.Lane is { IsStuck: true } lane)
        {
            var why = lane.Paused ? "paused by the editor hold" : $"queued #{lane.QueuePosition} for {lane.QueuedKind}";
            return new StateVerdict(AgentState.LaneBlocked, false, why);
        }

        if (!agent.IsWorking && !hasBackgroundWork)
        {
            return new StateVerdict(AgentState.Idle, false, "never prompted");
        }

        if (agent.CurrentTool is { UsesEditor: true } editorCall)
        {
            return new StateVerdict(AgentState.UsingEditor, false, "calling the editor: " + editorCall.Detail);
        }

        var hasUnreadQuestion = agent.AsksQuestion && !(readAt is { } seen && agent.QuestionAt is { } asked && seen >= asked);
        if (hasUnreadQuestion)
        {
            return new StateVerdict(AgentState.Asked, false, "asked: " + agent.QuestionText);
        }

        return new StateVerdict(AgentState.Working, false, WorkingReason(agent));
    }

    private static StateVerdict? EditorHold(AgentSnapshot agent)
    {
        if (agent.Lane is not { HoldsLane: true, Paused: false } lane)
        {
            return null;
        }

        var purpose = string.IsNullOrEmpty(lane.HeldPurpose) ? "" : ": " + lane.HeldPurpose;
        return new StateVerdict(AgentState.UsingEditor, false, $"holds the editor lane ({lane.HeldKind}){purpose}");
    }

    private static StateVerdict? OpenTask(AgentSnapshot agent)
    {
        if (agent.Lane is not { TaskTitle: { } title, TaskBlockedOnUser: false } lane)
        {
            return null;
        }

        var prompts = agent.Prompts;
        var isStale = lane.TaskUpdatedAt is { } updated && prompts.Count >= OPEN_TASK_TURNS && updated < prompts[^OPEN_TASK_TURNS].At;
        if (isStale)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(lane.TaskBlockedOn))
        {
            var about = string.IsNullOrEmpty(lane.TaskWhy) ? title : lane.TaskWhy;
            return new StateVerdict(AgentState.Paused, false, $"waiting on {lane.TaskBlockedOn}: {about}");
        }

        var next = string.IsNullOrEmpty(lane.TaskNext) ? title : lane.TaskNext;
        return new StateVerdict(AgentState.Paused, false, $"task still open, next: {next}");
    }

    private static string WorkingReason(AgentSnapshot agent)
    {
        if (agent.IsWorking)
        {
            return "turn running";
        }

        if (agent.RunningSubagents > 0)
        {
            return $"{agent.RunningSubagents} subagents running";
        }

        var listening = agent.Bridges.FirstOrDefault(b => b.IsListening);
        return listening is not null ? "waiting on " + listening.Channel : $"{agent.RunningCommands} background commands running";
    }

    /// <summary>Text from before the user's last prompt has been answered, whatever it asked.</summary>
    private static string? RequestInLastTurn(AgentSnapshot agent)
    {
        var isFromThisTurn = agent.LastTextAt is { } said && (agent.LastPromptAt is not { } prompted || said >= prompted);
        return isFromThisTurn ? Questions.FindRequest(agent.LastText) : null;
    }

    /// <summary>
    /// A question earlier in the turn that the agent then worked past does not count; only one in (or after) its last
    /// message does, as that is what the user reads when the turn ends.
    /// </summary>
    private static bool EndsOnAQuestion(AgentSnapshot agent)
    {
        return agent.AsksQuestion && agent.QuestionAt is { } asked && (agent.LastTextAt is not { } said || asked >= said);
    }
}
