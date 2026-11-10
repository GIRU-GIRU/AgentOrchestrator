using AgentManager.Core;
using AgentManager.Core.Model;

namespace AgentManager.Tests;

/// <summary>A Codex Master Agent is woken by the manager typing his watcher's output into his window.</summary>
public sealed class CodexMasterWatchTests
{
    [Fact]
    public void TheWakeIsOneLineThatSaysItIsNotTheUser()
    {
        var line = CodexMasterWatch.WakeLine([("C:/w/wake-1.txt", "QUEUED Build by Docs Agent, 4 min\nIMPROVE lane 31%\n")]);

        Assert.StartsWith(CodexMasterWatch.WAKE_PREFIX, line);
        Assert.Contains("QUEUED Build by Docs Agent, 4 min", line);
        Assert.Contains("(+1 more lines)", line);
        Assert.Contains("C:/w/wake-1.txt", line);
        Assert.DoesNotContain("\n", line);
    }

    [Fact]
    public void SeveralWakesNameTheNewestAndEveryFile()
    {
        var line = CodexMasterWatch.WakeLine([("a.txt", "OCCUPANT old"), ("b.txt", "SILENT HOLD new")]);

        Assert.Contains("SILENT HOLD new", line);
        Assert.DoesNotContain("OCCUPANT old", line);
        Assert.Contains("a.txt, b.txt", line);
    }

    [Fact]
    public void PickerCharactersNeverReachHisBox()
    {
        var line = CodexMasterWatch.WakeLine([("x.txt", "mail from agent:astra-docs cost $3 @once")]);

        Assert.DoesNotContain("@", line);
        Assert.DoesNotContain("$", line);
    }

    [Fact]
    public void ALongFirstLineIsCut()
    {
        var line = CodexMasterWatch.WakeLine([("x.txt", new string('a', 900))]);

        Assert.Contains("…", line);
        Assert.True(line.Length < 500);
    }

    [Fact]
    public void HeIsOnlyWokenAtHisPrompt()
    {
        var at = new AgentSnapshot { Key = "codex:t", Kind = AgentKind.Codex, SessionId = "t", Name = "MASTER AGENT", Surface = "CLI", IsAlive = true, Pid = 5, TranscriptPath = "", ResumeCommand = "" };

        Assert.True(CodexMasterWatch.IsAtPrompt(at));
        Assert.False(CodexMasterWatch.IsAtPrompt(at with { IsWorking = true }));
        Assert.False(CodexMasterWatch.IsAtPrompt(at with { PendingQuestion = true }));
        Assert.False(CodexMasterWatch.IsAtPrompt(at with { Pid = null }));
    }

    [Fact]
    public void TheManagerRunsHisWatcherAndKeepsWhatItSaid()
    {
        var root = Path.Combine(Path.GetTempPath(), "am-codex-watch-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var wakes = Path.Combine(root, "wakes");
        var script = Path.Combine(project, CodexMasterWatch.WATCHER);
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "open('runs.txt', 'a').write('x')\nprint('SILENT HOLD 1234abcd PIE: test line')\n");
        var master = new AgentSnapshot { Key = "codex:t", Kind = AgentKind.Codex, SessionId = "t", Name = "MASTER AGENT", Surface = "CLI", IsAlive = true, IsWorking = true, Pid = 5, TranscriptPath = "", ResumeCommand = "" };
        using var watch = new CodexMasterWatch(wakes);
        try
        {
            var deadline = DateTimeOffset.Now.AddSeconds(30);
            while (DateTimeOffset.Now < deadline && (!Directory.Exists(wakes) || Directory.GetFiles(wakes).Length == 0 || RunsOf(project) < 2))
            {
                watch.Tick(master, project, DateTimeOffset.Now);
                Thread.Sleep(200);
            }

            var kept = Directory.GetFiles(wakes, "wake-*.txt");
            Assert.NotEmpty(kept);
            Assert.Contains("SILENT HOLD 1234abcd", File.ReadAllText(kept[0]));
            Assert.True(RunsOf(project) >= 2, "it starts the watcher again once it exits");

            watch.Tick(null, project, DateTimeOffset.Now);
            var runs = RunsOf(project);
            Thread.Sleep(1500);
            watch.Tick(null, project, DateTimeOffset.Now);
            Assert.Equal(runs, RunsOf(project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int RunsOf(string project)
    {
        var runs = Path.Combine(project, "runs.txt");
        return File.Exists(runs) ? File.ReadAllText(runs).Length : 0;
    }
}
