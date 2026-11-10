using System.Text;

namespace AgentManager.Core.Diagnostics;

/// <summary>
/// The manager's log: one line per event in <c>logs/agentmanager-YYYYMMDD.log</c>, flushed as it is written and opened
/// shared, so an agent can tail it while the app runs. Nothing is written until <see cref="Configure"/> is called, which
/// keeps tests and tools that use the core library silent.
/// </summary>
public static class AppLog
{
    private const int KEEP_DAYS = 7;

    private static readonly object GATE = new();

    private static StreamWriter? writer;

    private static DateTime writerDay;

    public static string? Directory { get; private set; }

    public static string? CurrentFile { get; private set; }

    /// <summary>Put in front of every message from this process, so a second instance (a screenshot run) reads apart.</summary>
    public static string Tag { get; set; } = "";

    public static void Configure(string directory)
    {
        lock (GATE)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            Prune(directory);
        }
    }

    public static void Info(string area, string message)
    {
        Write("INFO ", area, message);
    }

    public static void Warn(string area, string message)
    {
        Write("WARN ", area, message);
    }

    public static void Error(string area, string message, Exception? exception = null)
    {
        Write("ERROR", area, exception is null ? message : message + Environment.NewLine + exception);
    }

    private static void Write(string level, string area, string message)
    {
        var now = DateTimeOffset.Now;
        var text = new StringBuilder();
        text.Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fff")).Append(' ').Append(level).Append(" [").Append(area).Append("] ").Append(Tag);
        var lines = message.Replace("\r", "").Split('\n');
        text.Append(lines[0]);
        for (var i = 1; i < lines.Length; i++)
        {
            text.Append(Environment.NewLine).Append("    ").Append(lines[i]);
        }

        lock (GATE)
        {
            if (Directory is null)
            {
                return;
            }

            try
            {
                Open(now.Date);
                writer?.BaseStream.Seek(0, SeekOrigin.End);
                writer?.WriteLine(text.ToString());
            }
            catch (IOException)
            {
                writer = null;
            }
        }
    }

    private static void Open(DateTime day)
    {
        if (writer is not null && writerDay == day)
        {
            return;
        }

        writer?.Dispose();
        CurrentFile = Path.Combine(Directory!, $"agentmanager-{day:yyyyMMdd}.log");
        var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        writerDay = day;
    }

    private static void Prune(string directory)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, "agentmanager-*.log"))
        {
            if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KEEP_DAYS))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
