using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentManager.Core.State;

public static class PageZoom
{
    public const double DEFAULT = 0.8;

    /// <summary>WebView2's own limits; a stored value outside them is a damaged state file, not a choice.</summary>
    private const double MIN = 0.25;

    private const double MAX = 5;

    public static double Usable(double zoom)
    {
        return zoom is >= MIN and <= MAX ? zoom : DEFAULT;
    }
}

public sealed class ManagerSettings
{
    public int HistoryHours { get; set; } = 12;

    /// <summary>The window's zoom, as last set with ctrl+scroll: WebView2 forgets it between runs.</summary>
    public double Zoom { get; set; } = PageZoom.DEFAULT;

    public int ChatDays { get; set; } = 3;

    public List<string> OrchestratorDirs { get; set; } = [];

    public bool ShowClosed { get; set; } = true;

    public bool ShowSystemMessages { get; set; }

    public bool ChatOpen { get; set; } = true;

    public bool SoundOn { get; set; } = true;

    /// <summary>The order agents arrived in, which numbers them and places a card brought back with ctrl+z. Where a card sits is <see cref="CanvasState"/>.</summary>
    public List<string> ManualOrder { get; set; } = [];
}

public sealed class AgentMark
{
    public int Number { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset? DismissedAt { get; set; }

    /// <summary>When the user chose Close agent, until the process exits or refuses (<see cref="AgentHub.SettleClose"/>).</summary>
    public DateTimeOffset? ClosingAt { get; set; }

    /// <summary>Closed by the user while no window was known for it: a thread only Codex's daemon holds stays off the grid until a window shows it.</summary>
    public bool ClosedWithNoWindow { get; set; }

    public bool Pinned { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset? LastSeenAlive { get; set; }

    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>What the user decided about each agent (read, dismissed, pinned, its grid number), kept between runs.</summary>
public sealed class ManagerStore
{
    private static readonly TimeSpan FORGET_AFTER = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions OPTIONS = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly object gate = new();

    private readonly string? path;

    private readonly StoreData data;

    private bool isDirty;

    private ManagerStore(string? path, StoreData data)
    {
        this.path = path;
        this.data = data;
    }

    public ManagerSettings Settings => data.Settings;

    public static ManagerStore InMemory()
    {
        return new ManagerStore(null, new StoreData());
    }

    public static ManagerStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = Parse(File.ReadAllText(path));
                if (loaded is not null)
                {
                    return new ManagerStore(path, loaded);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new ManagerStore(path, new StoreData());
    }

    private static StoreData? Parse(string json)
    {
        return JsonSerializer.Deserialize<StoreData>(json, OPTIONS);
    }

    public bool IsKnown(string key)
    {
        lock (gate)
        {
            return data.Marks.TryGetValue(key, out var mark) && mark.LastSeenAlive is not null;
        }
    }

    public bool IsDismissed(string key, DateTimeOffset lastActivity)
    {
        lock (gate)
        {
            return data.Marks.TryGetValue(key, out var mark) && mark.DismissedAt is { } dismissed && dismissed >= lastActivity;
        }
    }

    public AgentMark? Find(string key)
    {
        lock (gate)
        {
            return data.Marks.GetValueOrDefault(key);
        }
    }

    public T Read<T>(Func<StoreData, T> read)
    {
        lock (gate)
        {
            return read(data);
        }
    }

    public void Change(Action<StoreData> change)
    {
        lock (gate)
        {
            change(data);
            isDirty = true;
        }
    }

    public void ChangeIf(Func<StoreData, bool> change)
    {
        lock (gate)
        {
            isDirty |= change(data);
        }
    }

    public void Prune(DateTimeOffset now)
    {
        lock (gate)
        {
            foreach (var key in data.Marks.Where(m => now - m.Value.LastSeen > FORGET_AFTER).Select(m => m.Key).ToList())
            {
                data.Marks.Remove(key);
                isDirty = true;
            }

            Stats.Prune(data.Stats, data.Marks.ContainsKey);
            isDirty |= PruneCanvas(data.Canvas, data.Marks.ContainsKey) > 0;
        }
    }

    /// <summary>A position outlives its card only until the card's mark is forgotten, or the file would grow for ever.</summary>
    public static int PruneCanvas(CanvasState canvas, Func<string, bool> isKnown)
    {
        var forgotten = canvas.Cards.Keys.Where(key => !isKnown(key)).ToList();
        forgotten.ForEach(key => canvas.Cards.Remove(key));
        return forgotten.Count;
    }

    public void SaveIfDirty()
    {
        string json;
        lock (gate)
        {
            if (!isDirty || path is null)
            {
                return;
            }

            json = JsonSerializer.Serialize(data, OPTIONS);
            isDirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (gate)
            {
                isDirty = true;
            }
        }
    }
}

public sealed class StoreData
{
    public ManagerSettings Settings { get; set; } = new();

    public Dictionary<string, AgentMark> Marks { get; set; } = [];

    /// <summary>The agent windows open in this Windows session, kept up to date while the manager runs.</summary>
    public OpenWindowsSnapshot OpenWindows { get; set; } = new();

    /// <summary>The windows open in an earlier Windows session, offered once to restore until the user restores or dismisses them.</summary>
    public List<OpenWindowRecord> PendingRestore { get; set; } = [];

    public MasterAgentState MasterAgent { get; set; } = new();

    public StatsBook Stats { get; set; } = new();

    public CanvasState Canvas { get; set; } = new();
}

/// <summary>The one agent the manager keeps for the user (<see cref="Core.MasterAgent"/>).</summary>
public sealed class MasterAgentState
{
    /// <summary>False once the user has closed him, across restarts of the manager, until they ask for a new one.</summary>
    public bool IsWanted { get; set; } = true;

    /// <summary>Which agent he is. A state file from before the choice holds none, and he was a Claude then.</summary>
    public Model.AgentKind Kind { get; set; } = Model.AgentKind.Claude;

    /// <summary>A Codex's thread id is known only once his thread appears, so this is null while one starts.</summary>
    public string? SessionId { get; set; }

    public string? Cwd { get; set; }

    /// <summary>The Windows logon he last ran in: gone in the same logon means the user closed him, an earlier one means the PC restarted.</summary>
    public DateTimeOffset? LogonAt { get; set; }

    /// <summary>Set while a terminal is on its way, so a second click cannot start a second Master Agent.</summary>
    public DateTimeOffset? StartingAt { get; set; }

    public bool IsResuming { get; set; }

    /// <summary>The session <see cref="TypedStartupCommands"/> is for, so each command is typed once per session.</summary>
    public string? StartupSessionId { get; set; }

    public List<string> TypedStartupCommands { get; set; } = [];

    [JsonIgnore]
    public DateTimeOffset? NextStartupTryAt { get; set; }

    [JsonIgnore]
    public string? LastStartupFailure { get; set; }

    [JsonIgnore]
    public DateTimeOffset? MissingSince { get; set; }
}

public sealed class OpenWindowsSnapshot
{
    public DateTimeOffset? LogonAt { get; set; }

    public DateTimeOffset SavedAt { get; set; }

    public List<OpenWindowRecord> Windows { get; set; } = [];
}

public sealed class OpenWindowRecord
{
    public string Key { get; set; } = "";

    public Model.AgentKind Kind { get; set; }

    public string Name { get; set; } = "";

    public string? Cwd { get; set; }

    public string ResumeCommand { get; set; } = "";

    public string? LastPrompt { get; set; }

    public DateTimeOffset LastActiveAt { get; set; }

    public string Surface { get; set; } = "";
}
