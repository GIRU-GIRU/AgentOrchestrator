using AgentManager.Core.Notes;

namespace AgentManager.Tests;

public sealed class NotesTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "am-notes-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StagedTextIsReadBackAndWrittenOnFlush()
    {
        using var store = new NotesStore(directory);
        var id = store.Create();
        store.Stage(id, "# Shopping\n- milk");

        Assert.Equal("# Shopping\n- milk", store.Load(id));
        store.Flush();

        Assert.Equal("# Shopping\n- milk", File.ReadAllText(Path.Combine(directory, id + ".md")));
        Assert.Equal("Shopping", Assert.Single(store.List()).Title);
    }

    [Fact]
    public void DeletedNotesGoToTheTrashAndComeBack()
    {
        using var store = new NotesStore(directory);
        var id = store.Create();
        store.Stage(id, "keep me");
        store.Delete(id);

        Assert.Empty(store.List());
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(store.TrashDirectory, id + ".md")));

        store.Restore(id);

        Assert.Equal("keep me", store.Load(id));
        Assert.Empty(store.Trash());
    }

    [Fact]
    public void OverwritingKeepsThePreviousTextAsASnapshot()
    {
        using var store = new NotesStore(directory);
        var id = store.Create();
        store.Stage(id, "first");
        store.Flush();
        store.Stage(id, "second");
        store.Flush();

        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(directory, ".history", id)));
        Assert.Equal("first", File.ReadAllText(snapshot));
    }

    [Theory]
    [InlineData("## **Plan** for `today`\nmore", "Plan for today")]
    [InlineData("- [ ] ship it", "ship it")]
    [InlineData("\n\n", "untitled")]
    public void TitleIsTheFirstLineWithoutMarks(string text, string title)
    {
        Assert.Equal(title, NotesStore.TitleOf(text));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
