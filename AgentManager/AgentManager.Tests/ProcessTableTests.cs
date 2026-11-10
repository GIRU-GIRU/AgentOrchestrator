using System.Diagnostics;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

public sealed class ProcessTableTests
{
    [Fact]
    public void ThisProcessIsListedWithItsExeParentAndStart()
    {
        using var self = Process.GetCurrentProcess();
        var table = ProcessTable.Capture();
        var entry = table.Get(Environment.ProcessId);

        Assert.NotNull(entry);
        Assert.Equal(self.ProcessName + ".exe", entry.Exe, ignoreCase: true);
        Assert.True(table.Exists(entry.ParentPid));
        Assert.Equal(self.StartTime.ToFileTime(), table.StartTime(Environment.ProcessId), (long)TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void AProcessThisUserCannotOpenIsStillNamed()
    {
        var table = ProcessTable.Capture();

        Assert.NotEmpty(table.Named("services.exe"));
    }
}
