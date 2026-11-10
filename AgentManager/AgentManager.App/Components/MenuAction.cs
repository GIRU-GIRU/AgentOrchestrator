namespace AgentManager.App.Components;

public enum MenuAction
{
    Details,
    Raise,
    MarkRead,
    MarkUnread,
    Resume,
    Dismiss,
    CloseAgent,
    Rename,
    Pin,
    ShowMessages,
    SendMessage,
    CopyResume,
    CopySessionId,
    CopyLastText,
    OpenFolder,
    RevealTranscript,
}

public sealed record ChatTally(int Sent, int Received, DateTimeOffset? LastAt)
{
    public int Total => Sent + Received;
}

public sealed record MenuItem(MenuAction Action, string Label, string Hint, bool IsDanger = false);

public sealed record CardRename(string Note, bool IsBlocked);
