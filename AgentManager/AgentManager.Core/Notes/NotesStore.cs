using System.Globalization;
using System.Text;
using AgentManager.Core.Diagnostics;

namespace AgentManager.Core.Notes;

public sealed record NoteInfo(string Id, string Title, DateTimeOffset Modified, int Length);

/// <summary>
/// The user's notes, one Markdown file each in <c>notes/</c>. Text is staged on every keystroke and written a moment
/// later (and on <see cref="Flush"/>), always through a temp file, so a crash never leaves half a note. Before a note is
/// overwritten its previous text is kept in <c>.history/&lt;id&gt;/</c> every few minutes, and a deleted note goes to
/// <c>.trash/</c>, so nothing the user typed is ever lost to a save, a delete or a bug.
/// </summary>
public sealed class NotesStore : IDisposable
{
    private const int SNAPSHOTS_KEPT = 60;

    private static readonly TimeSpan WRITE_DELAY = TimeSpan.FromMilliseconds(350);

    private static readonly TimeSpan SNAPSHOT_EVERY = TimeSpan.FromMinutes(3);

    private readonly object gate = new();

    private readonly Dictionary<string, string> pending = [];

    private readonly Dictionary<string, DateTimeOffset> lastSnapshot = [];

    private readonly Timer timer;

    public NotesStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Directory { get; }

    public string TrashDirectory => Path.Combine(Directory, ".trash");

    public event Action? Saved;

    public IReadOnlyList<NoteInfo> List()
    {
        lock (gate)
        {
            return Infos(Directory);
        }
    }

    public IReadOnlyList<NoteInfo> Trash()
    {
        lock (gate)
        {
            return System.IO.Directory.Exists(TrashDirectory) ? Infos(TrashDirectory) : [];
        }
    }

    public string Load(string id)
    {
        lock (gate)
        {
            if (pending.TryGetValue(id, out var staged))
            {
                return staged;
            }

            var path = PathOf(id);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
    }

    public string Create()
    {
        var id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];
        lock (gate)
        {
            WriteAtomic(PathOf(id), "");
        }

        AppLog.Info("notes", "new note " + id);
        Saved?.Invoke();
        return id;
    }

    /// <summary>Takes the note's latest text; it reaches the disk within <see cref="WRITE_DELAY"/>.</summary>
    public void Stage(string id, string text)
    {
        lock (gate)
        {
            pending[id] = text;
            timer.Change(WRITE_DELAY, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        var wrote = false;
        lock (gate)
        {
            foreach (var (id, text) in pending.ToList())
            {
                try
                {
                    Snapshot(id);
                    WriteAtomic(PathOf(id), text);
                    pending.Remove(id);
                    wrote = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    AppLog.Error("notes", $"could not save {id}; it stays staged and is retried", exception);
                    timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (wrote)
        {
            Saved?.Invoke();
        }
    }

    public void Delete(string id)
    {
        Flush();
        lock (gate)
        {
            var path = PathOf(id);
            if (!File.Exists(path))
            {
                return;
            }

            System.IO.Directory.CreateDirectory(TrashDirectory);
            File.Move(path, Path.Combine(TrashDirectory, id + ".md"), true);
        }

        AppLog.Info("notes", $"note {id} moved to the trash");
        Saved?.Invoke();
    }

    public void Restore(string id)
    {
        lock (gate)
        {
            var trashed = Path.Combine(TrashDirectory, id + ".md");
            if (!File.Exists(trashed))
            {
                return;
            }

            var target = PathOf(id);
            if (File.Exists(target))
            {
                target = PathOf(id + "-restored");
            }

            File.Move(trashed, target);
        }

        AppLog.Info("notes", $"note {id} restored from the trash");
        Saved?.Invoke();
    }

    public static string TitleOf(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var title = line.Trim().TrimStart('#', '-', '*', '>', ' ', '[', ']', 'x').Replace("**", "").Replace("`", "").Trim();
            if (title.Length > 0)
            {
                return title.Length > 60 ? title[..59] + "…" : title;
            }
        }

        return "untitled";
    }

    public void Dispose()
    {
        Flush();
        timer.Dispose();
    }

    private string PathOf(string id)
    {
        return Path.Combine(Directory, id + ".md");
    }

    private void Snapshot(string id)
    {
        var path = PathOf(id);
        var now = DateTimeOffset.Now;
        var isDue = !lastSnapshot.TryGetValue(id, out var last) || now - last >= SNAPSHOT_EVERY;
        if (!isDue || !File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return;
        }

        var history = Path.Combine(Directory, ".history", id);
        System.IO.Directory.CreateDirectory(history);
        File.Copy(path, Path.Combine(history, now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md"), true);
        lastSnapshot[id] = now;
        foreach (var old in System.IO.Directory.GetFiles(history, "*.md").OrderByDescending(f => f, StringComparer.Ordinal).Skip(SNAPSHOTS_KEPT))
        {
            File.Delete(old);
        }
    }

    private List<NoteInfo> Infos(string directory)
    {
        var notes = new List<NoteInfo>();
        foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*.md"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            var text = pending.TryGetValue(id, out var staged) ? staged : File.ReadAllText(path);
            var modified = pending.ContainsKey(id) ? DateTimeOffset.Now : new DateTimeOffset(File.GetLastWriteTime(path));
            notes.Add(new NoteInfo(id, TitleOf(text), modified, text.Length));
        }

        return notes.OrderByDescending(n => n.Modified).ToList();
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
