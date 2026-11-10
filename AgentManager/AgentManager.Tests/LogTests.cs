using AgentManager.Core.Diagnostics;

namespace AgentManager.Tests;

public sealed class LogTests
{
    /// <summary>A screenshot run beside the user's copy shares the day's log, so each appends: writing from its own offset would erase the other's lines.</summary>
    [Fact]
    public void LinesFromAnotherWriterSurvive()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentmanager-logtest-" + Guid.NewGuid().ToString("N"));
        AppLog.Configure(directory);
        AppLog.Info("test", "first");
        using (var other = new StreamWriter(new FileStream(AppLog.CurrentFile!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)))
        {
            other.WriteLine("other instance line");
        }

        AppLog.Info("test", "second");

        using var reader = new StreamReader(new FileStream(AppLog.CurrentFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var text = reader.ReadToEnd();
        Assert.Contains("first", text);
        Assert.Contains("other instance line", text);
        Assert.Contains("second", text);
    }
}
