using System.Reflection;
using System.Windows;
using AgentManager.Core;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentManager.App;

public partial class App : Application
{
    private const string BACKGROUND_ARGB = "FF0B0F17";

    /// <summary>Capped, Chromium waits on vsync and queues frames: a dragged card reached the screen 23 ms after the move, 5 ms uncapped. It still draws only when something changes, so a still canvas costs the same.</summary>
    private const string BROWSER_ARGUMENTS = "--disable-frame-rate-limit";

    private ServiceProvider? services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetEnvironmentVariable("AGENTMANAGER_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentManager");
        var claudeDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        var codexDir = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");

        AppLog.Configure(Path.Combine(appData, "logs"));
        AppLog.Tag = e.Args.Contains("--screenshot") ? $"(screenshot pid {Environment.ProcessId}) " : "";
        AppLog.Info("app", $"start {Assembly.GetExecutingAssembly().GetName().Version} pid {Environment.ProcessId} · {Environment.ProcessPath} {string.Join(' ', e.Args)}");
        var forgotten = AgentManager.Core.Windows.Launcher.ForgetAgentSession();
        if (forgotten.Count > 0)
        {
            AppLog.Warn("app", "started from inside an agent session; dropped its variables so they don't reach resumed agents: " + string.Join(", ", forgotten));
        }
        DispatcherUnhandledException += (_, args) => AppLog.Error("app", "unhandled exception on the UI thread", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("app", "unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Error("app", "unobserved task exception", args.Exception);
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", BACKGROUND_ARGB);
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", $"{Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS")} {BROWSER_ARGUMENTS}".Trim());

        var collection = new ServiceCollection();
        collection.AddWpfBlazorWebView();
#if DEBUG
        collection.AddBlazorWebViewDeveloperTools();
#endif
        collection.AddLogging(logging => logging.AddProvider(new AppLogProvider()));
        collection.AddSingleton(ManagerStore.Load(Path.Combine(appData, "state.json")));
        var hasScratchData = Environment.GetEnvironmentVariable("AGENTMANAGER_DATA") is not null;
        var isTestAskingForMasterAgent = Environment.GetEnvironmentVariable("AGENTMANAGER_MASTER_AGENT") == "1";
        var masterAgent = new MasterAgentSetup(Path.Combine(AppContext.BaseDirectory, MasterAgent.PROMPT_FOLDER, MasterAgent.PROMPT_FILE), MasterAgent.MayStartItself(e.Args.Contains("--screenshot"), hasScratchData, isTestAskingForMasterAgent), Path.Combine(appData, MasterAgent.PROMPT_FOLDER));
        collection.AddSingleton(provider => new AgentHub(provider.GetRequiredService<ManagerStore>(), claudeDir, codexDir, Path.Combine(appData, "live.json"), AgentManager.Core.Windows.LogonSession.LogonAt(), masterAgent));
        collection.AddSingleton<Desktop>();
        collection.AddSingleton(new AgentManager.Core.Notes.NotesStore(Path.Combine(appData, "notes")));
        services = collection.BuildServiceProvider();
        services.GetRequiredService<AgentHub>().Start();
        SessionEnding += (_, args) => services?.GetRequiredService<AgentHub>().FreezeOpenWindows($"Windows is ending the session ({args.ReasonSessionEnding})");

        var window = new MainWindow(services, ScreenshotRequest.From(e.Args));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("app", $"exit code {e.ApplicationExitCode}");
        services?.GetService<AgentManager.Core.Notes.NotesStore>()?.Dispose();
        services?.GetService<AgentHub>()?.Dispose();
        services?.Dispose();
        base.OnExit(e);
    }
}
