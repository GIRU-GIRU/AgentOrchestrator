using AgentManager.Core.Chat;
using AgentManager.Core.Claude;
using AgentManager.Core.Codex;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.Model;
using AgentManager.Core.Orchestration;
using AgentManager.Core.State;
using AgentManager.Core.Windows;
using System.Diagnostics;

namespace AgentManager.Core;

/// <summary>
/// Polls every agent source once a second on its own thread and publishes an immutable <see cref="HubSnapshot"/>. The UI
/// only reads snapshots and calls the commands below; it never touches a source.
/// </summary>
public sealed class AgentHub : IDisposable
{
    private static readonly TimeSpan TICK = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan SAVE_PERIOD = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan SEEN_RESOLUTION = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan CLOSE_WAIT = TimeSpan.FromSeconds(20);

    private const string LOG_AREA = "master-agent";

    private readonly ManagerStore store;

    private static readonly TimeSpan STATS_SAVE_EVERY = TimeSpan.FromMinutes(1);

    private readonly ChatLog chat = new();

    private DateTimeOffset statsSavedAt;

    private DateTimeOffset categoriesSavedAt;

    private readonly ClaudeSource claude;

    private readonly CodexSource codex;

    private readonly Dictionary<string, OrchestratorReader> orchestrators = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> probedCwds = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> ownerAgents = [];

    private readonly Dictionary<string, (OrchestratorReader Reader, string Owner)> agentOwners = [];

    /// <summary>A Claude reads its inbox only from its hooks, so these never see an inbox message and <see cref="inboxOwners"/> leaves them out.</summary>
    private readonly HashSet<string> hooklessClaudes = [];

    private readonly SemaphoreSlim wake = new(0);

    private readonly CancellationTokenSource stop = new();

    private readonly HubJournal journal;

    private readonly TokenRates rates = new();

    private readonly GridUndo removals = new();

    /// <summary>A window takes seconds to open and has no key until it does, so the spot clicked waits for the next new card of that kind.</summary>
    private readonly List<(AgentKind Kind, CanvasCardSpot Spot, DateTimeOffset Until)> spawnSpots = [];

    private static readonly TimeSpan SPAWN_SPOT_WAIT = TimeSpan.FromMinutes(2);

    private Dictionary<string, string> names = [];

    private Dictionary<string, Dictionary<string, string>> channelNames = [];

    private IReadOnlyList<ChatMessage> resolvedChat = [];

    private (int Version, int Names) resolvedFor = (-1, -1);

    private static readonly TimeSpan RESTORE_STAGGER = TimeSpan.FromMilliseconds(700);

    private static readonly TimeSpan ORCH_TIMEOUT = TimeSpan.FromSeconds(15);

    private readonly DateTimeOffset? logonAt;

    private readonly MasterAgentSetup? masterAgentSetup;

    private readonly CodexMasterWatch codexMasterWatch;

    /// <summary>Read from the raw sources, so a Master Agent the user has hidden still counts as running.</summary>
    private volatile string? masterAgentRunningKey;

    /// <summary>Card keys of the Claude sessions the orchestrator is relaunching headless, read each tick while a restore is pending.</summary>
    private IReadOnlySet<string> relaunchingSessions = new HashSet<string>();

    /// <summary>A copy of the agent-to-owner links for the UI thread; the hub thread rebuilds the live map every tick.</summary>
    private Dictionary<string, (OrchestratorReader Reader, string Owner)> inboxOwners = [];

    private bool isOpenWindowsFrozen;

    private DateTimeOffset nextSave;

    private DateTimeOffset nextPrune;

    public AgentHub(ManagerStore store, string claudeDir, string codexDir, string? livePath = null, DateTimeOffset? logonAt = null, MasterAgentSetup? masterAgentSetup = null)
    {
        this.store = store;
        this.logonAt = logonAt;
        this.masterAgentSetup = masterAgentSetup;
        codexMasterWatch = new CodexMasterWatch(masterAgentSetup?.ScriptFolder ?? Path.Combine(Path.GetTempPath(), "AgentManager", MasterAgent.PROMPT_FOLDER));
        claude = new ClaudeSource(claudeDir);
        codex = new CodexSource(codexDir);
        journal = new HubJournal(livePath);
        AppLog.Info("hub", $"sources: claude {claudeDir} · codex {codexDir} · live snapshot {livePath ?? "off"}");
        TakeOverOpenWindows();
    }

    public HubSnapshot Current { get; private set; } = HubSnapshot.Loading;

    public ManagerSettings Settings => store.Settings;

    public event Action? Changed;

    public void Start()
    {
        _ = Task.Run(() => RunAsync(stop.Token));
    }

    public void Refresh()
    {
        wake.Release();
    }

    public AgentSnapshot? Find(string key)
    {
        return Current.Agents.FirstOrDefault(a => a.Key == key);
    }

    public void MarkRead(string key)
    {
        Act(key, "marked read");
        ChangeMark(key, mark => mark.ReadAt = DateTimeOffset.Now);
    }

    public void MarkUnread(string key)
    {
        Act(key, "marked unread");
        ChangeMark(key, mark => mark.ReadAt = null);
    }

    public void Dismiss(string key)
    {
        RememberRemoval(key);
        Act(key, "closed or hidden by the user");
        CloseLeftoverShell(key);
        ChangeMark(key, mark => mark.DismissedAt = DateTimeOffset.Now);
    }

    private void RememberRemoval(string key)
    {
        if (Find(key) is { } agent)
        {
            removals.Remember(GridUndo.Describe(agent, Current.Agents, store.Read(data => data.Settings.ManualOrder.IndexOf(key))));
        }
    }

    /// <summary>
    /// Ctrl+Z: the newest card taken off the canvas or category box deleted, back where it was. It changes only what the canvas shows, so
    /// an agent that has exited comes back closed and nothing is started. A Master Agent who has gone stays gone until New MASTER AGENT.
    /// </summary>
    public UndoStep? BringBackLastRemoved(DateTimeOffset now)
    {
        var running = masterAgentRunningKey;
        var step = removals.TakeLast(r => !r.IsMasterAgent || r.Key == running);
        if (step is DeletedCategory deleted)
        {
            store.Change(data => GridUndo.BringBack(deleted, data.Canvas.Categories));
            AppLog.Info("ui", $"category {deleted.Category.Name}: brought back with ctrl+z");
            Refresh();
            return deleted;
        }

        if (step is not RemovedCard card)
        {
            AppLog.Info("ui", "ctrl+z: nothing to bring back");
            return null;
        }

        store.Change(data =>
        {
            if (!data.Marks.TryGetValue(card.Key, out var mark))
            {
                mark = new AgentMark { FirstSeen = now, LastSeen = now };
                data.Marks[card.Key] = mark;
            }

            GridUndo.BringBack(card, mark, data.Settings.ManualOrder);
        });
        AppLog.Info("ui", $"#{card.Number:00} {card.Name}: brought back with ctrl+z");
        Refresh();
        return card;
    }

    /// <summary>An agent that has exited can leave the shell it ran in open; closing its card closes that window too.</summary>
    private void CloseLeftoverShell(string key)
    {
        var agent = Find(key);
        if (agent is not { IsAlive: false, Shell: { } shell })
        {
            return;
        }

        var processes = ProcessTable.Capture();
        if (!processes.IsAlive(shell.Pid, shell.Start))
        {
            return;
        }

        var result = WindowRaiser.Close(shell.Pid, processes);
        Act(key, (result.IsRaised ? "closed its leftover shell: " : "could not close its shell: ") + result.How);
    }

    public void TogglePin(string key)
    {
        Act(key, "pin toggled");
        ChangeMark(key, mark => mark.Pinned = !mark.Pinned);
    }

    public void ChangeSettings(Action<ManagerSettings> change)
    {
        store.Change(data => change(data.Settings));
        AppLog.Info("ui", "settings: " + System.Text.Json.JsonSerializer.Serialize(store.Settings));
        Refresh();
    }

    public CanvasBoard Board()
    {
        return store.Read(data => new CanvasBoard(
            data.Canvas.Cards.ToDictionary(card => card.Key, card => new CanvasPoint { X = card.Value.X, Y = card.Value.Y, Z = card.Value.Z }),
            data.Canvas.Categories.Select(c => new CanvasCategory { Id = c.Id, Name = c.Name, Colour = c.Colour, X = c.X, Y = c.Y, Width = c.Width, Height = c.Height, Tokens = c.Tokens, TokensSince = c.TokensSince, TokenHistory = c.TokenHistory.ToList() }).ToList(),
            data.Canvas.HasView,
            data.Canvas.PanX,
            data.Canvas.PanY,
            data.Canvas.Zoom));
    }

    public void SetCanvasView(double panX, double panY, double zoom, double width, double height)
    {
        store.Change(data =>
        {
            var canvas = data.Canvas;
            canvas.HasView = true;
            (canvas.PanX, canvas.PanY, canvas.Zoom, canvas.ViewWidth, canvas.ViewHeight) = (panX, panY, zoom, width, height);
        });
    }

    /// <summary>The cards a drag moved land together, and on top of everything else.</summary>
    public void MoveCards(IReadOnlyList<CanvasCardMove> moves)
    {
        store.Change(data =>
        {
            var top = data.Canvas.Cards.Values.Select(card => card.Z).DefaultIfEmpty(0).Max();
            foreach (var move in moves)
            {
                data.Canvas.Cards[move.Key] = new CanvasPoint { X = move.X, Y = move.Y, Z = ++top };
            }
        });
    }

    public CanvasCategory AddCategory(double x, double y)
    {
        CanvasCategory? added = null;
        store.Change(data =>
        {
            added = CanvasLayout.NewCategory(data.Canvas.Categories, x, y);
            added.TokensSince = DateTimeOffset.Now;
            data.Canvas.Categories.Add(added);
        });
        AppLog.Info("ui", $"new category at {x:0},{y:0}");
        return added!;
    }

    public void MoveCategory(string id, double x, double y, IReadOnlyList<CanvasCardMove> carried)
    {
        ChangeCategory(id, category => (category.X, category.Y) = (x, y));
        if (carried.Count > 0)
        {
            MoveCards(carried);
        }
    }

    public void ResizeCategory(string id, double width, double height)
    {
        ChangeCategory(id, category => (category.Width, category.Height) = (width, height));
    }

    public void RenameCategory(string id, string? typed)
    {
        ChangeCategory(id, category => category.Name = CanvasLayout.CleanCategoryName(typed, category.Name));
    }

    public void RecolourCategory(string id, string colour)
    {
        ChangeCategory(id, category => category.Colour = colour);
    }

    public void ResetCategoryTokens(string id)
    {
        ChangeCategory(id, category => CanvasLayout.ResetTokens(category, DateTimeOffset.Now));
        AppLog.Info("ui", "category tokens reset");
    }

    /// <summary>The box goes and everything on it stays where it is; Ctrl+Z brings the box back whole.</summary>
    public void DeleteCategory(string id)
    {
        DeletedCategory? deleted = null;
        store.Change(data =>
        {
            var index = data.Canvas.Categories.FindIndex(category => category.Id == id);
            if (index >= 0)
            {
                deleted = new DeletedCategory(data.Canvas.Categories[index], index);
                data.Canvas.Categories.RemoveAt(index);
            }
        });
        if (deleted is not null)
        {
            removals.Remember(deleted);
            AppLog.Info("ui", $"category {deleted.Category.Name} deleted");
        }
    }

    private void ChangeCategory(string id, Action<CanvasCategory> change)
    {
        store.Change(data =>
        {
            if (data.Canvas.Categories.FirstOrDefault(category => category.Id == id) is { } category)
            {
                change(category);
            }
        });
    }

    /// <summary>Starts a new Claude or Codex in a new terminal window in <paramref name="cwd"/>; its card appears at <paramref name="at"/> when one is given.</summary>
    public void NewAgent(AgentKind kind, string cwd, CanvasCardSpot? at = null)
    {
        var command = Launcher.AgentCommand(kind);
        AppLog.Info("ui", $"new {kind}: {command} in {cwd}");
        if (at is not null)
        {
            lock (spawnSpots)
            {
                spawnSpots.Add((kind, at, DateTimeOffset.Now + SPAWN_SPOT_WAIT));
            }
        }

        Launcher.RunInTerminal(command, cwd, kind.ToString());
        Refresh();
    }

    public string DefaultFolder()
    {
        return ChooseFolder(ProjectFolder(), Current.Agents);
    }

    /// <summary>
    /// Where a new agent starts: the orchestrated project, whose hooks, CLAUDE.md and skills only reach a session started
    /// there. Following the folder most agents use kept new sessions in the home folder once most of them were there.
    /// </summary>
    internal static string ChooseFolder(string? project, IReadOnlyList<AgentSnapshot> agents)
    {
        var folder = project ?? agents.Where(a => a.IsAlive && !string.IsNullOrEmpty(a.Cwd)).GroupBy(a => a.Cwd!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? agents.Select(a => a.Cwd).FirstOrDefault(c => !string.IsNullOrEmpty(c));
        return folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal static string ProjectOf(string stateDir)
    {
        return Path.GetFullPath(Path.Combine(stateDir, "..", ".."));
    }

    private string? ProjectFolder()
    {
        return Current.Orchestrators.Select(view => ProjectOf(view.StateDir)).FirstOrDefault(Directory.Exists);
    }

    /// <summary>Before the first scan nobody knows he is not running.</summary>
    private bool MayMasterAgentBeRunning => masterAgentRunningKey is not null || Current.IsLoading;

    public bool CanStartMasterAgent => store.Read(data => MasterAgent.CanStartNew(data.MasterAgent, MayMasterAgentBeRunning));

    /// <summary>
    /// The menu's New item: starts one in a fresh session when there is none. The check and the mark that blocks a second
    /// start are taken together, so a double click cannot start two.
    /// </summary>
    public bool StartNewMasterAgent(AgentKind kind, DateTimeOffset now)
    {
        var mayBeRunning = MayMasterAgentBeRunning;
        var sessionId = NewMasterAgentSession(kind);
        var cwd = ChooseFolder(ProjectFolder(), Current.Agents);
        var canStart = false;
        store.ChangeIf(data =>
        {
            canStart = MasterAgent.CanStartNew(data.MasterAgent, mayBeRunning);
            if (canStart)
            {
                BeginFreshMasterAgent(data, kind, sessionId, cwd, now);
            }

            return canStart;
        });
        if (!canStart)
        {
            AppLog.Info("ui", $"new {MasterAgent.NAME} refused: there is one already");
            return false;
        }

        AppLog.Info("ui", $"new {MasterAgent.NAME} ({kind}): {DescribeStart(sessionId)} in {cwd}");
        LaunchMasterAgent(() => FreshMasterAgentCommand(kind, sessionId), cwd);
        Refresh();
        return true;
    }

    private string MasterAgentPrompt => masterAgentSetup?.PromptPath ?? Path.Combine(AppContext.BaseDirectory, MasterAgent.PROMPT_FOLDER, MasterAgent.PROMPT_FILE);

    /// <summary>A Claude is given its session id; a Codex makes its own thread, found by its first prompt.</summary>
    private static string? NewMasterAgentSession(AgentKind kind)
    {
        return kind == AgentKind.Claude ? Guid.NewGuid().ToString() : null;
    }

    private static string DescribeStart(string? sessionId)
    {
        return sessionId is null ? "his thread opens with his first prompt" : "session " + sessionId;
    }

    private void BeginFreshMasterAgent(StoreData data, AgentKind kind, string? sessionId, string cwd, DateTimeOffset now)
    {
        MasterAgent.BeginFresh(data.MasterAgent, kind, sessionId, cwd, logonAt, now);
        if (sessionId is not null)
        {
            data.Marks[MasterAgent.KeyPrefix(kind) + sessionId] = new AgentMark { Pinned = true, FirstSeen = now, LastSeen = now };
        }
    }

    private string FreshMasterAgentCommand(AgentKind kind, string? sessionId)
    {
        var command = Launcher.AgentCommand(kind);
        return kind == AgentKind.Codex ? WriteCodexMasterScript(MasterAgent.CodexFreshScript(command, MasterAgentPrompt)) : MasterAgent.FreshCommand(command, sessionId!, MasterAgentPrompt);
    }

    private string WriteCodexMasterScript(string content)
    {
        var folder = masterAgentSetup?.ScriptFolder ?? Path.Combine(Path.GetTempPath(), "AgentManager", MasterAgent.PROMPT_FOLDER);
        Directory.CreateDirectory(folder);
        var path = MasterAgent.CodexScriptPath(folder);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>The command is built inside the guard because a Codex's is a script written to disk, which can fail as a terminal can.</summary>
    private void LaunchMasterAgent(Func<string> commandLine, string cwd)
    {
        try
        {
            Launcher.RunInTerminal(commandLine(), cwd, MasterAgent.NAME);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error(LOG_AREA, "could not start a terminal for him", exception);
            store.Change(data =>
            {
                data.MasterAgent.IsWanted = false;
                data.MasterAgent.StartingAt = null;
                data.MasterAgent.IsResuming = false;
            });
            throw new InvalidOperationException(exception.Message, exception);
        }
    }

    /// <summary>Keeps the Master Agent going: starts him when the manager opens, brings him back after a restart, and lets him go when the user closed him.</summary>
    private void SettleMasterAgent(List<AgentSnapshot> raw, DateTimeOffset now)
    {
        var (running, kind) = store.Read(data => (MasterAgent.RunningSession(data.MasterAgent, raw), data.MasterAgent.Kind));
        masterAgentRunningKey = running is null ? null : MasterAgent.KeyPrefix(kind) + running;
        if (masterAgentSetup is not { MayStartItself: true })
        {
            return;
        }

        var step = MasterAgentStep.None;
        store.ChangeIf(data =>
        {
            var state = data.MasterAgent;
            var before = (state.IsWanted, state.SessionId, state.LogonAt, state.StartingAt, state.IsResuming);
            step = MasterAgent.Next(state, running, logonAt, now);
            if (step == MasterAgentStep.Gone && MasterAgent.KeyOf(state) is { } key && data.Marks.TryGetValue(key, out var mark))
            {
                mark.DismissedAt = now;
            }

            var isAdopted = before.SessionId is null && state.SessionId is not null;
            if (isAdopted && MasterAgent.KeyOf(state) is { } adopted)
            {
                DockAdoptedMasterAgent(data, adopted, now);
            }

            return before != (state.IsWanted, state.SessionId, state.LogonAt, state.StartingAt, state.IsResuming);
        });
        try
        {
            RunMasterAgentStep(step, now);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warn(LOG_AREA, "not started: " + exception.Message);
        }

        var agent = raw.FirstOrDefault(a => a.Kind == kind && a.SessionId == running);
        TypeStartupCommand(agent, now);
        codexMasterWatch.Tick(agent, ProjectFolder(), now);
    }

    /// <summary>A Codex's card was already a plain card (his window had one before his thread opened), so docking him is done when his thread is found.</summary>
    private static void DockAdoptedMasterAgent(StoreData data, string key, DateTimeOffset now)
    {
        if (data.Marks.TryGetValue(key, out var mark))
        {
            mark.Pinned = true;
            return;
        }

        data.Marks[key] = new AgentMark { Pinned = true, FirstSeen = now, LastSeen = now };
    }

    private void TypeStartupCommand(AgentSnapshot? agent, DateTimeOffset now)
    {
        if (agent?.Pid is not { } pid || store.Read(data => MasterAgent.NextStartupCommand(data.MasterAgent, agent, now)) is not { } command)
        {
            return;
        }

        var (isSent, how) = ConsoleInput.Type(pid, command);
        if (isSent)
        {
            AppLog.Info(LOG_AREA, $"{command}: {how}");
            store.Change(data => MasterAgent.StartupCommandTyped(data.MasterAgent, agent.SessionId, command));
            return;
        }

        if (store.Read(data => MasterAgent.StartupCommandFailed(data.MasterAgent, how, now)))
        {
            AppLog.Warn(LOG_AREA, $"{command}: {how}");
        }
    }

    private void RunMasterAgentStep(MasterAgentStep step, DateTimeOffset now)
    {
        var (sessionId, cwd, kind) = store.Read(data => (data.MasterAgent.SessionId, data.MasterAgent.Cwd, data.MasterAgent.Kind));
        switch (step)
        {
            case MasterAgentStep.StartFresh:
                StartFreshMasterAgent(now);
                break;
            case MasterAgentStep.Resume when sessionId is not null && (kind == AgentKind.Codex || claude.HasTranscript(sessionId, cwd)):
                AppLog.Info(LOG_AREA, $"the PC restarted, so he comes back: {kind} session {sessionId} in {cwd}");
                store.Change(data => MasterAgent.BeginResume(data.MasterAgent, logonAt, now));
                LaunchMasterAgent(() => ResumeMasterAgentCommand(kind, sessionId), cwd ?? ChooseFolder(ProjectFolder(), Current.Agents));
                break;
            case MasterAgentStep.Resume:
                AppLog.Info(LOG_AREA, "the PC restarted and he had no conversation to resume, so a fresh one starts");
                StartFreshMasterAgent(now);
                break;
            case MasterAgentStep.Gone:
                AppLog.Info(LOG_AREA, $"his window was closed, so he stays gone until New {MasterAgent.NAME}");
                break;
            case MasterAgentStep.NeverStarted:
                AppLog.Warn(LOG_AREA, $"no session appeared within {MasterAgent.STARTING_WINDOW.TotalSeconds:0} s, so the manager will not try again until New {MasterAgent.NAME}");
                break;
        }
    }

    /// <summary>Waits for an orchestrator to attach: started from the home folder he would have none of the project's hooks or skills.</summary>
    private void StartFreshMasterAgent(DateTimeOffset now)
    {
        if (ProjectFolder() is not { } project)
        {
            return;
        }

        var kind = store.Read(data => data.MasterAgent.Kind);
        var sessionId = NewMasterAgentSession(kind);
        var isStartedByTheMenu = false;
        store.ChangeIf(data =>
        {
            isStartedByTheMenu = MasterAgent.IsStarting(data.MasterAgent, now);
            if (!isStartedByTheMenu)
            {
                BeginFreshMasterAgent(data, kind, sessionId, project, now);
            }

            return !isStartedByTheMenu;
        });
        if (isStartedByTheMenu)
        {
            return;
        }

        AppLog.Info(LOG_AREA, $"starting him ({kind}): {DescribeStart(sessionId)} in {project}");
        LaunchMasterAgent(() => FreshMasterAgentCommand(kind, sessionId), project);
    }

    private string ResumeMasterAgentCommand(AgentKind kind, string sessionId)
    {
        var command = Launcher.AgentCommand(kind);
        return kind == AgentKind.Codex ? WriteCodexMasterScript(MasterAgent.CodexResumeScript(command, sessionId, MasterAgentPrompt)) : MasterAgent.ResumeCommand(command, sessionId, MasterAgentPrompt);
    }

    /// <summary>Slow (a process table and a console attach), so call it off the UI thread.</summary>
    public RaiseResult CloseAgent(string key)
    {
        var agent = Find(key);
        if (agent is { Pid: null, IsMasterAgent: false })
        {
            return CloseCardWithNoWindow(key);
        }

        var result = agent?.Pid is { } pid ? WindowRaiser.Close(pid, ProcessTable.Capture()) : new RaiseResult(false, "no process id known");
        Act(key, (result.IsRaised ? "closed: " : "close failed: ") + result.How);
        if (result.IsRaised)
        {
            if (agent is { IsMasterAgent: false })
            {
                RememberRemoval(key);
            }

            ChangeMark(key, mark => mark.ClosingAt = DateTimeOffset.Now);
        }

        Refresh();
        return result;
    }

    /// <summary>
    /// A Codex thread only the daemon holds has no window to ask, and a card that cannot be closed would be stuck on the grid, so the
    /// card goes.
    /// </summary>
    private RaiseResult CloseCardWithNoWindow(string key)
    {
        const string HOW = "no window is known for it, so its card was taken off the grid";
        RememberRemoval(key);
        Act(key, "closed: " + HOW);
        ChangeMark(key, mark =>
        {
            mark.DismissedAt = DateTimeOffset.Now;
            mark.ClosedWithNoWindow = true;
        });
        return new RaiseResult(true, HOW);
    }

    public RaiseResult Raise(string key)
    {
        var agent = Find(key);
        var result = agent?.Pid is { } pid ? WindowRaiser.Raise(pid, ProcessTable.Capture()) : new RaiseResult(false, "no process id known");
        Act(key, (result.IsRaised ? "raised " : "raise failed: ") + result.How);
        return result;
    }

    public void Resume(string key)
    {
        var agent = Find(key);
        if (agent is null)
        {
            return;
        }

        Act(key, $"resume: {agent.ResumeCommand} in {agent.Cwd}");
        try
        {
            Launcher.RunInTerminal(agent.ResumeCommand, agent.Cwd, agent.Name);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("ui", "resume could not start a terminal", exception);
            throw;
        }

        ChangeMark(key, mark => mark.DismissedAt = null);
    }

    public IReadOnlyList<DeliveryPlan> PlanDelivery(IEnumerable<string> keys)
    {
        var owners = inboxOwners;
        return keys.Select(Find).OfType<AgentSnapshot>().Select(a => Delivery.Plan(a, owners.ContainsKey(a.Key))).ToList();
    }

    /// <summary>Sends the user's text to each agent by the route <see cref="Delivery.Plan"/> picks; slow (a console takes a third of a second), so call it off the UI thread.</summary>
    public IReadOnlyList<DeliveryResult> Send(IReadOnlyList<string> keys, string text)
    {
        var results = new List<DeliveryResult>();
        foreach (var plan in PlanDelivery(keys))
        {
            var (isSent, how) = plan.Route switch
            {
                DeliveryRoute.Console when Find(plan.Key)?.Pid is { } pid => ConsoleInput.Type(pid, text),
                DeliveryRoute.Inbox => SendThroughInbox(plan.Key, text),
                _ => (false, plan.Why),
            };
            Act(plan.Key, (isSent ? "sent: " : "not sent: ") + how + " · \"" + Transcripts.Json.OneLine(text, 80) + "\"");
            results.Add(new DeliveryResult(plan.Key, plan.Name, isSent, how));
        }

        return results;
    }

    /// <summary>What is in the agent's input box right now; attaches to its console, so call it off the UI thread.</summary>
    public ComposerReading PeekComposer(string key)
    {
        if (Find(key) is not { Pid: { } pid } agent)
        {
            return new ComposerReading(ComposerState.NotShowing);
        }

        return ConsoleInput.Attached(pid, console => Composer.Read(agent.Kind, console.ReadScreen()), _ => new ComposerReading(ComposerState.NotShowing));
    }

    /// <summary>Types <c>/rename</c> into the agent's console (see <see cref="AgentRename.Run"/>); slow, so call it off the UI thread.</summary>
    public RenameResult Rename(string key, string name)
    {
        var agent = Find(key);
        if (agent is null)
        {
            return new RenameResult(false, "its card has gone");
        }

        var plan = AgentRename.Plan(agent);
        var result = plan.CanType
            ? ConsoleInput.Attached(agent.Pid!.Value, console => AgentRename.Run(agent.Kind, name, console, Thread.Sleep), why => new RenameResult(false, why))
            : new RenameResult(false, plan.Why);
        Act(key, (result.IsSent ? "renamed: " : "not renamed: ") + result.How + " · \"" + name + "\"");
        return result;
    }

    private (bool IsSent, string How) SendThroughInbox(string key, string text)
    {
        if (!inboxOwners.TryGetValue(key, out var link))
        {
            return (false, "no orchestrator owner");
        }

        var orchCmd = Path.GetFullPath(Path.Combine(link.Reader.StateDir, "..", "..", ".claude", "scripts", "orch.cmd"));
        var file = Path.Combine(Path.GetTempPath(), $"agentmanager-msg-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(file, text);
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/d", "/c", orchCmd, "--as", "user", "msg", "--to", link.Owner, "--file", file })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(ORCH_TIMEOUT);
            return process.ExitCode == 0 ? (true, $"orchestrator inbox of {link.Owner}") : (false, "orch msg failed: " + Transcripts.Json.OneLine(output, 160));
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, "orch msg could not run: " + exception.Message);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The windows open in the last Windows session that are not running again yet: not open in a window, and not being
    /// relaunched headless by the orchestrator (orch.py also never relaunches a session already running), so no agent
    /// ever runs twice.
    /// </summary>
    public IReadOnlyList<OpenWindowRecord> PendingRestore()
    {
        var live = Current.Agents.Where(a => a.IsAlive).Select(a => a.Key).ToHashSet();
        var relaunching = relaunchingSessions;
        return store.Read(data => data.PendingRestore.Where(w => !live.Contains(w.Key) && !relaunching.Contains(w.Key)).ToList());
    }

    public async Task<IReadOnlyList<DeliveryResult>> RestoreAsync(IReadOnlyList<string> keys)
    {
        var pending = PendingRestore();
        var chosen = pending.Where(w => keys.Contains(w.Key)).ToList();
        var results = keys.Where(k => pending.All(w => w.Key != k))
            .Select(k => new DeliveryResult(k, NameOf(k), false, "already running again, so it was not restored a second time"))
            .ToList();
        foreach (var window in chosen)
        {
            try
            {
                Launcher.RunInTerminal(window.ResumeCommand, window.Cwd, window.Name);
                ChangeMark(window.Key, mark => mark.DismissedAt = null);
                results.Add(new DeliveryResult(window.Key, window.Name, true, $"{window.ResumeCommand} in {window.Cwd}"));
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                results.Add(new DeliveryResult(window.Key, window.Name, false, exception.Message));
            }

            AppLog.Info("ui", $"restore {window.Name}: {results[^1].How}");
            await Task.Delay(RESTORE_STAGGER);
        }

        DismissRestore($"restored {results.Count(r => r.IsSent)} of {chosen.Count} chosen");
        return results;
    }

    public void DismissRestore(string why)
    {
        var count = store.Read(data => data.PendingRestore.Count);
        store.Change(data => data.PendingRestore.Clear());
        AppLog.Info("ui", $"restore list of {count} cleared: {why}");
    }

    /// <summary>
    /// Windows ending the session closes the agents and the manager in no set order; keeping the list from that moment
    /// stops agents that die first from dropping out of what the next session offers to restore.
    /// </summary>
    public void FreezeOpenWindows(string why)
    {
        isOpenWindowsFrozen = true;
        store.SaveIfDirty();
        AppLog.Info("hub", $"open windows kept as they are: {why}");
    }

    private void TakeOverOpenWindows()
    {
        store.Change(data =>
        {
            var saved = data.OpenWindows;
            var isNewSession = SessionRestore.IsEarlierLogon(saved.LogonAt, logonAt);
            if (isNewSession && saved.Windows.Count > 0)
            {
                data.PendingRestore = saved.Windows.ToList();
                AppLog.Info("hub", $"{saved.Windows.Count} agent windows were open when the manager last ran in an earlier Windows session (logon {saved.LogonAt:yyyy-MM-dd HH:mm}, saved {saved.SavedAt:yyyy-MM-dd HH:mm}): offering to restore them");
            }

            if (isNewSession || saved.LogonAt is null)
            {
                data.OpenWindows = new OpenWindowsSnapshot { LogonAt = logonAt };
            }
        });
    }

    private void RecordOpenWindows(List<AgentSnapshot> agents, DateTimeOffset now)
    {
        if (isOpenWindowsFrozen)
        {
            return;
        }

        var live = agents.Where(SessionRestore.IsRestorable).OrderBy(a => a.Key, StringComparer.Ordinal).Select(SessionRestore.Record).ToList();
        store.ChangeIf(data =>
        {
            var saved = data.OpenWindows;
            var isChanged = !SessionRestore.IsSameSet(saved.Windows, live);
            if (!isChanged && now - saved.SavedAt < SessionRestore.REFRESH)
            {
                return false;
            }

            if (isChanged)
            {
                AppLog.Info("agent", $"open windows to restore after a restart: {live.Count}" + (live.Count > 0 ? " (" + string.Join(", ", live.Select(w => w.Name)) + ")" : ""));
            }

            data.OpenWindows = new OpenWindowsSnapshot { LogonAt = logonAt, SavedAt = now, Windows = live };
            return true;
        });
    }

    private void Act(string key, string what)
    {
        var agent = Find(key);
        var label = agent is null ? key : $"#{agent.Number:00} {agent.Name}";
        AppLog.Info("ui", $"{label}: {what}");
    }

    public void Dispose()
    {
        stop.Cancel();
        codexMasterWatch.Dispose();
        store.SaveIfDirty();
    }

    private void ChangeMark(string key, Action<AgentMark> change)
    {
        store.Change(data =>
        {
            if (!data.Marks.TryGetValue(key, out var mark))
            {
                mark = new AgentMark { FirstSeen = DateTimeOffset.Now, LastSeen = DateTimeOffset.Now };
                data.Marks[key] = mark;
            }

            change(mark);
        });
        Refresh();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Tick(DateTimeOffset.Now);
            }
            catch (Exception exception)
            {
                var error = exception.GetType().Name + ": " + exception.Message;
                if (error != Current.Error)
                {
                    AppLog.Error("hub", "tick failed", exception);
                }

                Current = Current with { IsLoading = false, Error = error };
                Changed?.Invoke();
            }

            try
            {
                await wake.WaitAsync(TICK, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Tick(DateTimeOffset now)
    {
        var clock = Stopwatch.StartNew();
        var phases = new List<(string, TimeSpan)>();
        var lap = clock.Elapsed;
        void Phase(string name)
        {
            phases.Add((name, clock.Elapsed - lap));
            lap = clock.Elapsed;
        }

        var processes = ProcessTable.Capture();
        Phase("processes");
        claude.Poll(processes, chat, store, now);
        Phase("claude");
        codex.Poll(processes, chat, store, NameOf, now);
        Phase("codex");
        foreach (var (newKey, oldKey) in claude.TakeReplacements().Concat(codex.TakeReplacements()))
        {
            Continue(newKey, oldKey, now);
        }

        var raw = claude.Snapshots(now).Concat(codex.Snapshots(now)).ToList();
        SettleCloses(raw, processes, now);
        SettleMasterAgent(raw, now);
        DiscoverOrchestrators(raw);
        foreach (var reader in orchestrators.Values)
        {
            reader.Poll(chat, store.Settings.ChatDays, NameOf, now);
        }

        Phase("orchestrator");

        var nextNames = new Dictionary<string, string>();
        claude.CollectNames(nextNames);
        codex.CollectNames(nextNames);
        MapOwners(raw, nextNames);
        MapBridgeMembers(raw, nextNames);
        inboxOwners = agentOwners.Where(o => !hooklessClaudes.Contains(o.Key)).ToDictionary(o => o.Key, o => o.Value);
        relaunchingSessions = store.Read(data => data.PendingRestore.Count) == 0
            ? new HashSet<string>()
            : orchestrators.Values.SelectMany(o => o.RelaunchingSessions()).Select(id => ClaudeSource.KEY_PREFIX + id).ToHashSet();
        names = nextNames;
        channelNames = ChannelNames(raw);

        var resolved = ResolveChat();
        var agents = Decorate(raw, now, resolved);
        RecordOpenWindows(agents, now);
        ObserveCategoryTokens(agents, now);
        var views = orchestrators.Values.Select(o => o.View(now, NameOf)).Where(v => v is not null).Cast<OrchestratorView>().ToArray();
        var error = orchestrators.Values.Select(o => o.Error).FirstOrDefault(e => e is not null);
        Current = new HubSnapshot(now, false, agents, resolved, views, error, ObserveStats(raw, now));
        Phase("snapshot");
        Changed?.Invoke();
        journal.Observe(Current, clock.Elapsed, phases);

        if (now >= nextSave)
        {
            nextSave = now + SAVE_PERIOD;
            store.SaveIfDirty();
        }

        if (now >= nextPrune)
        {
            nextPrune = now + TimeSpan.FromHours(1);
            store.Prune(now);
        }
    }

    private List<AgentSnapshot> Decorate(List<AgentSnapshot> raw, DateTimeOffset now, IReadOnlyList<ChatMessage> resolved)
    {
        var byChannel = resolved.Where(m => m.Channel == ChatChannel.Orchestrator && m.Topic is not null).ToLookup(m => m.Topic!);
        var rawByKey = raw.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        var settings = store.Settings;
        var historyStart = now - TimeSpan.FromHours(settings.HistoryHours);
        var visible = new List<AgentSnapshot>();
        var closedAstras = new List<string>();
        var marks = store.Read(data => data.Marks.ToDictionary(m => m.Key, m => m.Value));
        var masterAgentKey = store.Read(data => MasterAgent.KeyOf(data.MasterAgent));
        foreach (var agent in raw)
        {
            var isMasterAgent = agent.Key == masterAgentKey;
            if (isMasterAgent && !agent.IsAlive)
            {
                continue;
            }

            if (Transcripts.BridgeRules.LeavesNoClosedCard(agent.Kind, agent.Name, agent.IsAlive))
            {
                closedAstras.Add(agent.Key);
                continue;
            }

            var mark = marks.GetValueOrDefault(agent.Key);
            var isDismissed = IsHidden(mark, agent.IsAlive, agent.LastEventAt, agent.Pid is not null);
            var isRecent = agent.IsAlive || agent.LastEventAt >= historyStart || mark?.LastSeenAlive is not null;
            if (isDismissed || !isRecent || (!agent.IsAlive && !settings.ShowClosed))
            {
                continue;
            }

            var lane = LaneOf(agent, now);
            var (rate, _) = rates.Sample(agent.Key, agent.Tokens.Total + agent.SubagentTokens, now);
            var decorated = agent with { Lane = lane, TokenRate = rate, Bridges = BridgesOf(agent, byChannel, rawByKey, now), IsClosing = mark?.ClosingAt is not null, ClosingSince = mark?.ClosingAt };
            var verdict = StateClassifier.Classify(decorated, mark?.ReadAt);
            visible.Add(decorated with { Name = isMasterAgent ? MasterAgent.NAME : agent.Name, IsMasterAgent = isMasterAgent, State = verdict.State, StateReason = verdict.Reason, IsRead = verdict.IsRead, Pinned = mark?.Pinned == true, Number = mark?.Number ?? 0 });
        }

        rates.Forget(visible.Select(a => a.Key));
        removals.Forget(visible.Where(a => !a.IsClosing).Select(a => a.Key).Concat(closedAstras));
        Number(visible, now);
        var numbered = visible.Select(a => a with { Number = store.Find(a.Key)?.Number ?? a.Number });
        var order = store.Read(data => data.Settings.ManualOrder.Select((key, index) => (key, index)).GroupBy(p => p.key).ToDictionary(g => g.Key, g => g.First().index));
        var ordered = numbered.OrderByDescending(a => a.Pinned).ThenBy(a => order.GetValueOrDefault(a.Key, int.MaxValue)).ThenBy(a => a.Number);
        return ordered.ToList();
    }

    private void Number(List<AgentSnapshot> visible, DateTimeOffset now)
    {
        store.ChangeIf(data =>
        {
            var changed = false;
            var used = new HashSet<int>(removals.ReservedNumbers());
            var needNumbers = new List<string>();
            foreach (var agent in visible.OrderBy(a => a.StartedAt))
            {
                if (!data.Marks.TryGetValue(agent.Key, out var mark))
                {
                    mark = new AgentMark { FirstSeen = now };
                    data.Marks[agent.Key] = mark;
                }

                if (now - mark.LastSeen > SEEN_RESOLUTION)
                {
                    mark.LastSeen = now;
                    changed = true;
                }

                if (agent.IsAlive && (mark.LastSeenAlive is null || now - mark.LastSeenAlive > SEEN_RESOLUTION))
                {
                    mark.LastSeenAlive = now;
                    changed = true;
                }

                if (mark.Number > 0 && used.Add(mark.Number))
                {
                    continue;
                }

                needNumbers.Add(agent.Key);
            }

            foreach (var agent in visible.OrderBy(a => a.StartedAt).Where(a => !data.Settings.ManualOrder.Contains(a.Key)))
            {
                data.Settings.ManualOrder.Add(agent.Key);
                changed = true;
            }

            changed |= PlaceNewCards(data.Canvas, visible, data.Settings.ManualOrder, now);

            var forgotten = data.Settings.ManualOrder.RemoveAll(key => !data.Marks.ContainsKey(key));
            changed |= forgotten > 0;

            var next = 1;
            foreach (var key in needNumbers)
            {
                while (used.Contains(next))
                {
                    next++;
                }

                var mark = data.Marks[key];
                if (mark.DismissedAt is not null)
                {
                    mark.DismissedAt = null;
                    AppLog.Info("agent", $"{NameOf(key)}: its card was handed to another session of its window, but it is still running, so it is back as #{next:00}");
                }

                mark.Number = next;
                used.Add(next);
                changed = true;
            }

            return changed;
        });
    }

    /// <summary>In arrival order, so the first opening of the canvas lays the existing cards out as the grid had them.</summary>
    private bool PlaceNewCards(CanvasState canvas, List<AgentSnapshot> visible, List<string> arrivalOrder, DateTimeOffset now)
    {
        var isLifted = CanvasLayout.LiftDocked(canvas, visible);
        var hasDock = visible.Any(CanvasLayout.IsDocked);
        var unplaced = visible.Where(a => !CanvasLayout.IsDocked(a) && !canvas.Cards.ContainsKey(a.Key)).OrderBy(a => arrivalOrder.IndexOf(a.Key)).ToList();
        foreach (var agent in unplaced)
        {
            var z = canvas.Cards.Values.Select(card => card.Z).DefaultIfEmpty(0).Max() + 1;
            if (agent.IsMasterAgent && CanvasLayout.TakeParked(canvas, agent.Key) is { } parked)
            {
                canvas.Cards[agent.Key] = new CanvasPoint { X = parked.X, Y = parked.Y, Z = z };
                continue;
            }

            var (x, y) = !agent.IsMasterAgent && TakeSpawnSpot(agent.Kind, now) is { } spot ? (spot.X, spot.Y) : CanvasLayout.SpotForNewCard(canvas, hasDock);
            canvas.Cards[agent.Key] = new CanvasPoint { X = x, Y = y, Z = z };
        }

        return isLifted || unplaced.Count > 0;
    }

    private CanvasCardSpot? TakeSpawnSpot(AgentKind kind, DateTimeOffset now)
    {
        lock (spawnSpots)
        {
            spawnSpots.RemoveAll(spot => spot.Until < now);
            var index = spawnSpots.FindIndex(spot => spot.Kind == kind);
            if (index < 0)
            {
                return null;
            }

            var taken = spawnSpots[index].Spot;
            spawnSpots.RemoveAt(index);
            return taken;
        }
    }

    /// <summary>The window's own process is read here, because a Codex thread's <c>IsAlive</c> is only refreshed every few seconds and the card is animating meanwhile.</summary>
    private void SettleCloses(List<AgentSnapshot> raw, ProcessTable processes, DateTimeOffset now)
    {
        store.ChangeIf(data =>
        {
            var changed = false;
            foreach (var agent in raw)
            {
                var isRunning = agent.IsAlive && !(agent.Pid is { } pid && processes.Get(pid) is null);
                if (data.Marks.TryGetValue(agent.Key, out var mark) && SettleClose(mark, isRunning, now))
                {
                    changed = true;
                    AppLog.Info("agent", isRunning
                        ? $"{agent.Name}: still running {CLOSE_WAIT.TotalSeconds:0} s after Close window, so its card is back to normal"
                        : $"{agent.Name}: its window closed after Close window, so its card is closed for good");
                }
            }

            return changed;
        });
    }

    /// <summary>
    /// The card shows it being closed until the process has gone, and then leaves for good, so no closed card is left behind.
    /// A process still running after <see cref="CLOSE_WAIT"/> refused to close (its terminal asked and the user said no), so
    /// its card goes back to normal.
    /// </summary>
    public static bool SettleClose(AgentMark mark, bool isAlive, DateTimeOffset now)
    {
        if (mark.ClosingAt is not { } closing || (isAlive && now - closing < CLOSE_WAIT))
        {
            return false;
        }

        if (!isAlive)
        {
            mark.DismissedAt = now;
        }

        mark.ClosingAt = null;
        return true;
    }

    /// <summary>
    /// Hidden while the user's close, or a card handed on to the window's next session (<see cref="Continue"/>, which leaves
    /// number 0), is newer than the agent's last event. A handed-on session that is still running was handed on by mistake (a
    /// window that moves on leaves its old session dead), so it shows again. A card closed with no window known for it stays
    /// away however much its thread writes, until a window shows that thread.
    /// </summary>
    public static bool IsHidden(AgentMark? mark, bool isAlive, DateTimeOffset lastEventAt, bool hasWindow = true)
    {
        if (mark?.ClosingAt is not null)
        {
            return false;
        }

        if (mark?.ClosedWithNoWindow == true && !hasWindow)
        {
            return true;
        }

        if (mark?.DismissedAt is not { } dismissed || dismissed < lastEventAt)
        {
            return false;
        }

        var wasHandedOn = mark.Number == 0;
        return !(wasHandedOn && isAlive);
    }

    /// <summary>The same window moved on to another session: the new one takes the old card's number, place and pin, and stays the Master Agent if it was him.</summary>
    private void Continue(string newKey, string oldKey, DateTimeOffset now)
    {
        removals.Forget([oldKey]);
        store.Change(data =>
        {
            MasterAgent.HandOn(data.MasterAgent, oldKey, newKey);
            if (data.Marks.TryGetValue(oldKey, out var old))
            {
                data.Marks[newKey] = new AgentMark { Number = old.Number, Pinned = old.Pinned, FirstSeen = now, LastSeen = now, LastSeenAlive = now };
                old.DismissedAt = now;
                old.Number = 0;
            }

            CarryCanvasPosition(data.Canvas, newKey, oldKey);
            var order = data.Settings.ManualOrder;
            order.Remove(newKey);
            var index = order.IndexOf(oldKey);
            if (index >= 0)
            {
                order[index] = newKey;
            }
        });
        AppLog.Info("agent", $"{NameOf(oldKey)} → {newKey}: the same window started another session (/clear or /resume), so its card carries on");
    }

    /// <summary>A card is a window: the session a window moved on to takes the old one's spot on the canvas.</summary>
    public static void CarryCanvasPosition(CanvasState canvas, string newKey, string oldKey)
    {
        if (canvas.Cards.Remove(oldKey, out var position))
        {
            canvas.Cards[newKey] = position;
        }
    }

    private IReadOnlyList<BridgeInfo> BridgesOf(AgentSnapshot agent, ILookup<string, ChatMessage> byChannel, Dictionary<string, AgentSnapshot> agents, DateTimeOffset now)
    {
        var owners = ownerAgents.Where(o => o.Value == agent.Key).Select(o => o.Key[OrchestratorReader.OWNER_PREFIX.Length..]).ToHashSet();
        if (owners.Count == 0 || !agent.IsAlive)
        {
            return [];
        }

        var bridges = new List<BridgeInfo>();
        foreach (var reader in orchestrators.Values)
        {
            foreach (var (channel, members) in reader.Channels)
            {
                if (!members.Any(owners.Contains))
                {
                    continue;
                }

                var messages = byChannel[channel].ToList();
                var last = messages.LastOrDefault();
                var isListening = agent.ListeningOn is { } listening && (listening == channel || listening == Transcripts.BridgeRules.ANY_CHANNEL);
                var peers = members.Where(m => !owners.Contains(m)).Select(owner =>
                {
                    var ownerKey = OrchestratorReader.OWNER_PREFIX + owner;
                    var peerKey = ownerAgents.GetValueOrDefault(ownerKey);
                    var mapped = peerKey is null ? null : agents.GetValueOrDefault(peerKey);
                    var isCodexOwner = Transcripts.BridgeRules.OwnerKind(owner) == AgentKind.Codex;
                    var peer = (isCodexOwner ? CodexThreadOn(channel, agent, agents) : null) ?? mapped;
                    var isLiveOwner = reader.Feed?.Agents.Any(a => a.Owner == owner) == true;
                    var name = Transcripts.BridgeRules.PeerName(owner, peer?.Model, peer?.Name, NameOf(ownerKey));
                    return new BridgePeer(name, peer?.IsAlive ?? isLiveOwner, peer?.Key);
                }).GroupBy(p => p.Name).Select(g => g.OrderByDescending(p => p.IsAlive).First()).ToList();
                bridges.Add(new BridgeInfo(channel, peers, messages.Count, last?.At, last?.From, isListening, last?.FromKey));
            }
        }

        return Transcripts.BridgeRules.OnCard(bridges, agent.ListeningOn, now);
    }

    /// <summary>
    /// Every Codex window shares the daemon's owner and an <c>agent:</c> owner has no process, so neither says whether the Astra on
    /// this channel is still open. The thread whose own commands used the channel does.
    /// </summary>
    private static AgentSnapshot? CodexThreadOn(string channel, AgentSnapshot agent, Dictionary<string, AgentSnapshot> agents)
    {
        return agents.Values.Where(a => a.Kind == AgentKind.Codex && a.Key != agent.Key && a.BridgeChannels.Contains(channel))
            .OrderByDescending(a => a.IsAlive)
            .ThenByDescending(a => a.LastEventAt)
            .FirstOrDefault();
    }

    private LaneInfo? LaneOf(AgentSnapshot agent, DateTimeOffset now)
    {
        if (!agentOwners.TryGetValue(agent.Key, out var link))
        {
            return null;
        }

        var lane = link.Reader.LaneFor(link.Owner, now);
        return lane.QueuedBehindOwner is { } ahead ? lane with { QueuedBehind = NameOf(OrchestratorReader.OWNER_PREFIX + ahead) } : lane;
    }

    private void MapOwners(List<AgentSnapshot> agents, Dictionary<string, string> nextNames)
    {
        ownerAgents.Clear();
        agentOwners.Clear();
        hooklessClaudes.Clear();
        var bySession = agents.Where(a => a.Kind == AgentKind.Claude).GroupBy(a => a.SessionId).ToDictionary(g => g.Key, g => g.First().Key);
        var claudeByPid = agents.Where(a => a.Kind == AgentKind.Claude && a.IsAlive && a.Pid is not null).GroupBy(a => a.Pid!.Value).ToDictionary(g => g.Key, g => g.First().Key);
        var codexByPid = codex.LivePids().GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().Key);
        var liveCodex = agents.Where(a => a.Kind == AgentKind.Codex && a.IsAlive).Select(a => a.Key).ToList();
        foreach (var reader in orchestrators.Values)
        {
            var live = reader.Feed?.Agents.Select(a => (a.Owner, a.Kind, a.Pid, a.SessionId, IsLive: true)) ?? [];
            var known = reader.Owners.Values.Select(o => (o.Owner, o.Kind, o.Pid, o.SessionId, IsLive: false));
            foreach (var (owner, kind, pid, sessionId, isLive) in known.Concat(live))
            {
                var ownerKey = OrchestratorReader.OWNER_PREFIX + owner;
                string? agentKey = null;
                if (kind == "claude")
                {
                    agentKey = ClaudeAgentOf(sessionId, pid, isLive, bySession, claudeByPid);
                }
                else if (kind == "codex")
                {
                    agentKey = codexByPid.GetValueOrDefault(pid);
                    var isOnlyCodex = agentKey is null && liveCodex.Count == 1 && reader.Feed?.Agents.Any(a => a.Owner == owner) == true;
                    agentKey ??= isOnlyCodex ? liveCodex[0] : null;
                }

                if (agentKey is not null)
                {
                    ownerAgents[ownerKey] = agentKey;
                    if (isLive || !agentOwners.ContainsKey(agentKey))
                    {
                        agentOwners[agentKey] = (reader, owner);
                    }

                    if (kind == "claude" && sessionId.Length == 0)
                    {
                        hooklessClaudes.Add(agentKey);
                    }
                }

                var name = agentKey is not null && nextNames.TryGetValue(agentKey, out var agentName) ? agentName : reader.OwnerName(owner);
                nextNames[ownerKey] = name;
                if (agentKey is not null)
                {
                    nextNames.TryAdd(agentKey, name);
                }
            }
        }
    }

    /// <summary>
    /// A session started outside the project runs none of its hooks, so its owner never learns a session id: the orchestrator
    /// found it by walking up to its claude.exe, and that pid is the link while the owner is live. A dead owner's pid may be reused.
    /// </summary>
    internal static string? ClaudeAgentOf(string sessionId, int pid, bool isLive, IReadOnlyDictionary<string, string> bySession, IReadOnlyDictionary<int, string> byPid)
    {
        if (sessionId.Length > 0)
        {
            return bySession.GetValueOrDefault(sessionId) ?? ClaudeSource.KEY_PREFIX + sessionId;
        }

        return isLive ? byPid.GetValueOrDefault(pid) : null;
    }

    /// <summary>
    /// A bridge member the orchestrator cannot tie to a session (a Codex owner records no session id) is the one agent of
    /// its kind whose own commands used that channel.
    /// </summary>
    private void MapBridgeMembers(List<AgentSnapshot> agents, Dictionary<string, string> nextNames)
    {
        foreach (var reader in orchestrators.Values)
        {
            foreach (var (channel, members) in reader.Channels)
            {
                foreach (var owner in members)
                {
                    var ownerKey = OrchestratorReader.OWNER_PREFIX + owner;
                    if (ownerAgents.ContainsKey(ownerKey))
                    {
                        continue;
                    }

                    if (Transcripts.BridgeRules.OwnerKind(owner) is not { } kind)
                    {
                        continue;
                    }

                    var taken = members.Select(m => ownerAgents.GetValueOrDefault(OrchestratorReader.OWNER_PREFIX + m)).Where(k => k is not null).ToHashSet();
                    var candidates = agents.Where(a => a.Kind == kind && a.BridgeChannels.Contains(channel) && !taken.Contains(a.Key)).ToList();
                    var live = candidates.Where(a => a.IsAlive).ToList();
                    var match = live.Count == 1 ? live[0] : candidates.Count == 1 ? candidates[0] : null;
                    if (match is null)
                    {
                        continue;
                    }

                    ownerAgents[ownerKey] = match.Key;
                    nextNames[ownerKey] = match.Name;
                }
            }
        }
    }

    private void DiscoverOrchestrators(IEnumerable<AgentSnapshot> agents)
    {
        var candidates = store.Settings.OrchestratorDirs.ToList();
        var fromEnvironment = Environment.GetEnvironmentVariable("ORCH_STATE");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        foreach (var cwd in agents.Select(a => a.Cwd).Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!probedCwds.Add(cwd!))
            {
                continue;
            }

            for (var dir = new DirectoryInfo(cwd!); dir is not null; dir = dir.Parent)
            {
                var state = Path.Combine(dir.FullName, "Saved", "Orchestration");
                if (File.Exists(Path.Combine(state, "orch-status.json")))
                {
                    candidates.Add(state);
                    break;
                }
            }
        }

        foreach (var dir in candidates)
        {
            var full = Path.GetFullPath(dir);
            if (!orchestrators.ContainsKey(full) && Directory.Exists(full))
            {
                orchestrators[full] = new OrchestratorReader(full);
                AppLog.Info("orch", "attached " + full);
            }
        }
    }

    public IReadOnlyList<StatsPeriod> StatsHistory()
    {
        return store.Read(data => data.Stats.History.ToList());
    }

    public bool ResetStats(DateTimeOffset now)
    {
        var isReset = false;
        store.ChangeIf(data =>
        {
            isReset = Stats.Reset(data.Stats, now);
            return isReset;
        });
        AppLog.Info("ui", isReset ? "stats reset" : "stats reset ignored: one was just made");
        Refresh();
        return isReset;
    }

    /// <summary>Written once a minute like the stats: a crash costs at most a minute of a meter that is only a guide.</summary>
    private void ObserveCategoryTokens(List<AgentSnapshot> agents, DateTimeOffset now)
    {
        store.ChangeIf(data =>
        {
            var isUsed = CanvasLayout.AccrueTokens(data.Canvas, agents);
            var isDue = isUsed && now - categoriesSavedAt >= STATS_SAVE_EVERY;
            categoriesSavedAt = isDue ? now : categoriesSavedAt;
            return isDue;
        });
    }

    /// <summary>The figures change every tick and rebuild from the transcripts after a crash, so the file is only written once a minute.</summary>
    private StatsPeriod ObserveStats(List<AgentSnapshot> raw, DateTimeOffset now)
    {
        StatsPeriod? period = null;
        store.ChangeIf(data =>
        {
            foreach (var agent in raw)
            {
                Stats.Observe(data.Stats, agent);
            }

            period = Stats.Current(data.Stats, now);
            var isDue = now - statsSavedAt >= STATS_SAVE_EVERY;
            statsSavedAt = isDue ? now : statsSavedAt;
            return isDue;
        });
        return period!;
    }

    private string NameOf(string key)
    {
        if (names.TryGetValue(key, out var name))
        {
            return name;
        }

        var colon = key.IndexOf(':');
        var id = colon >= 0 ? key[(colon + 1)..] : key;
        return key.StartsWith(OrchestratorReader.OWNER_PREFIX, StringComparison.Ordinal) ? string.Join(' ', id.Split(':').Take(2)) : (id.Length > 8 ? id[..8] : id);
    }

    private IReadOnlyList<ChatMessage> ResolveChat()
    {
        var namesHash = names.Count ^ names.Values.Aggregate(17, (hash, value) => hash * 31 + value.GetHashCode());
        namesHash = channelNames.Values.SelectMany(c => c).Aggregate(namesHash, (hash, member) => hash * 31 + member.Key.GetHashCode() + member.Value.GetHashCode());
        var stamp = (chat.Version, namesHash);
        if (stamp == resolvedFor)
        {
            return resolvedChat;
        }

        resolvedFor = stamp;
        resolvedChat = chat.Snapshot().Select(Resolve).ToArray();
        return resolvedChat;
    }

    /// <summary>What each channel's members go by in the chat, the same names a card's bridge line uses, so an Astra is "Astra" and not its window's first prompt.</summary>
    private Dictionary<string, Dictionary<string, string>> ChannelNames(List<AgentSnapshot> agents)
    {
        var byKey = agents.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        var channels = new Dictionary<string, Dictionary<string, string>>();
        foreach (var reader in orchestrators.Values)
        {
            foreach (var (channel, members) in reader.Channels)
            {
                channels[channel] = members.Distinct().ToDictionary(member => member, member =>
                {
                    var key = OrchestratorReader.OWNER_PREFIX + member;
                    var mapped = ownerAgents.GetValueOrDefault(key) is { } agentKey ? byKey.GetValueOrDefault(agentKey) : null;
                    var thread = Transcripts.BridgeRules.OwnerKind(member) == AgentKind.Codex
                        ? agents.Where(a => a.Kind == AgentKind.Codex && a.BridgeChannels.Contains(channel)).OrderByDescending(a => a.IsAlive).ThenByDescending(a => a.LastEventAt).FirstOrDefault()
                        : null;
                    var peer = thread ?? mapped;
                    return Transcripts.BridgeRules.PeerName(member, peer?.Model, peer?.Name, NameOf(key));
                });
            }
        }

        return channels;
    }

    private ChatMessage ResolveBridgePost(ChatMessage message, Dictionary<string, string> members)
    {
        var sender = message.FromKey is { } key && key.StartsWith(OrchestratorReader.OWNER_PREFIX, StringComparison.Ordinal) ? key[OrchestratorReader.OWNER_PREFIX.Length..] : null;
        var fromKey = message.FromKey is not null ? ownerAgents.GetValueOrDefault(message.FromKey) ?? message.FromKey : null;
        var from = sender is not null && members.TryGetValue(sender, out var senderName) ? senderName : names.GetValueOrDefault(fromKey ?? "") ?? message.From;
        var others = members.Keys.Where(m => m != sender).ToList();
        var toKey = others.Count == 1 ? ownerAgents.GetValueOrDefault(OrchestratorReader.OWNER_PREFIX + others[0]) ?? OrchestratorReader.OWNER_PREFIX + others[0] : null;
        return message with { From = from, To = Transcripts.BridgeRules.Audience(members, sender), FromKey = fromKey, ToKey = toKey, Recipients = Transcripts.BridgeRules.AudienceNames(members, sender).Count };
    }

    private ChatMessage Resolve(ChatMessage message)
    {
        if (message.Channel == ChatChannel.Orchestrator && message.Topic is { } channel && channelNames.TryGetValue(channel, out var members))
        {
            return ResolveBridgePost(message, members);
        }

        var fromKey = message.FromKey is not null ? ownerAgents.GetValueOrDefault(message.FromKey) ?? message.FromKey : null;
        var toKey = message.ToKey is not null ? ownerAgents.GetValueOrDefault(message.ToKey) ?? message.ToKey : null;
        var from = fromKey is not null && names.TryGetValue(fromKey, out var fromName) ? fromName : message.From;
        var to = toKey is not null && names.TryGetValue(toKey, out var toName) ? toName : message.To;
        return message with { From = from, To = to, FromKey = fromKey, ToKey = toKey };
    }
}
