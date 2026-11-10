using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Core;

/// <summary>One thing Ctrl+Z can put back.</summary>
public abstract record UndoStep;

public sealed record RemovedCard(string Key, string Name, int Number, bool Pinned, string? PreviousKey, int OrderIndex, bool IsMasterAgent) : UndoStep;

/// <summary>A category box as it was when deleted, and where it stood in the list, so it comes back with its name, colour, size and token stretches.</summary>
public sealed record DeletedCategory(CanvasCategory Category, int Index) : UndoStep;

/// <summary>
/// What Ctrl+Z puts back, newest first: the cards the user took off the canvas and the category boxes they deleted in this run of the
/// manager. It only changes what the canvas shows: it never starts or resumes anything, so an agent whose process has gone comes back as a
/// closed card for the user to resume.
/// </summary>
public sealed class GridUndo
{
    private readonly Lock gate = new();

    private readonly List<UndoStep> steps = [];

    /// <summary>The previous card in its own section is what keeps its place: a drag moves every hidden card to the end of the saved order, so an index alone would not.</summary>
    public static RemovedCard Describe(AgentSnapshot agent, IEnumerable<AgentSnapshot> grid, int orderIndex)
    {
        var previous = grid.TakeWhile(a => a.Key != agent.Key).LastOrDefault(a => a.Kind == agent.Kind && a.Pinned == agent.Pinned);
        return new RemovedCard(agent.Key, agent.Name, agent.Number, agent.Pinned, previous?.Key, Math.Max(0, orderIndex), agent.IsMasterAgent);
    }

    public void Remember(RemovedCard card)
    {
        lock (gate)
        {
            steps.RemoveAll(step => step is RemovedCard removed && removed.Key == card.Key);
            steps.Add(card);
        }
    }

    public void Remember(DeletedCategory category)
    {
        lock (gate)
        {
            steps.Add(category);
        }
    }

    /// <summary>The newest step that may be undone; a card that may not come back (a Master Agent who has gone) is passed over and kept.</summary>
    public UndoStep? TakeLast(Func<RemovedCard, bool> mayComeBack)
    {
        lock (gate)
        {
            var index = steps.FindLastIndex(step => step is not RemovedCard card || mayComeBack(card));
            if (index < 0)
            {
                return null;
            }

            var taken = steps[index];
            steps.RemoveAt(index);
            return taken;
        }
    }

    /// <summary>A card that came back by itself (it acted, or its window refused to close) has nothing left to undo.</summary>
    public void Forget(IEnumerable<string> keys)
    {
        var gone = keys.ToHashSet();
        lock (gate)
        {
            steps.RemoveAll(step => step is RemovedCard card && gone.Contains(card.Key));
        }
    }

    /// <summary>A new agent takes the lowest free number, so a removed card's would be gone by the time it comes back.</summary>
    public IReadOnlyList<int> ReservedNumbers()
    {
        lock (gate)
        {
            return steps.OfType<RemovedCard>().Select(card => card.Number).Where(n => n > 0).ToList();
        }
    }

    public static void BringBack(RemovedCard card, AgentMark mark, List<string> order)
    {
        mark.DismissedAt = null;
        mark.ClosingAt = null;
        mark.Number = card.Number;
        mark.Pinned = card.Pinned;
        order.Remove(card.Key);
        var previous = card.PreviousKey is null ? -1 : order.IndexOf(card.PreviousKey);
        var isAnchorGone = card.PreviousKey is not null && previous < 0;
        order.Insert(isAnchorGone ? Math.Min(card.OrderIndex, order.Count) : previous + 1, card.Key);
    }

    /// <summary>Back at its old place in the list, so it draws behind the same boxes as before.</summary>
    public static void BringBack(DeletedCategory deleted, List<CanvasCategory> categories)
    {
        categories.RemoveAll(category => category.Id == deleted.Category.Id);
        categories.Insert(Math.Clamp(deleted.Index, 0, categories.Count), deleted.Category);
    }
}
