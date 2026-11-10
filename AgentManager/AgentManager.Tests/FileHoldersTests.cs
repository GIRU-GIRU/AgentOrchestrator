using System.Diagnostics;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

public sealed class FileHoldersTests
{
    private const int POLLS = 30;

    /// <summary>Long enough that the opening process holds the file about as long as it does not, which is what fails the second call.</summary>
    private const int OPENER_FILE_BYTES = 2 * 1024 * 1024;

    [Fact]
    public void NobodyHoldingTheFileIsAnAnswer()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Empty(FileHolders.Pids(path)!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A process opening the file between the Restart Manager's two list calls fails the second one, and that must not read
    /// as nobody holding it, or a running Codex would flash a closed card whenever a second manager or a <c>tail</c> read
    /// its rollout.
    /// </summary>
    [Fact]
    public void HolderIsStillFoundWhileAnotherProcessKeepsOpeningTheFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[OPENER_FILE_BYTES]);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var opener = new ProcessStartInfo("cmd.exe", $"/c for /L %i in (1,1,1000000) do @type \"{path}\" >nul") { UseShellExecute = false, CreateNoWindow = true };
        using var churn = Process.Start(opener)!;
        try
        {
            for (var poll = 0; poll < POLLS; poll++)
            {
                Assert.Contains(Environment.ProcessId, FileHolders.Pids(path)!);
            }
        }
        finally
        {
            churn.Kill();
            churn.WaitForExit();
            held.Dispose();
            File.Delete(path);
        }
    }
}
