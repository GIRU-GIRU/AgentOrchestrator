using System.Text.RegularExpressions;
using AgentManager.Core.Model;

namespace AgentManager.Core.Transcripts;

public static partial class ActivityRules
{
    [GeneratedRegex(@"orch(\.cmd|\.py)\b.*\b(acquire|hold\s+wait)\b|editor-session\.ps1|uelock", RegexOptions.IgnoreCase)]
    private static partial Regex LaneWait();

    [GeneratedRegex(@"\bdotnet\s+test\b|\bpytest\b|RunTests|ue_python\.py|UnrealEditor(-Cmd)?(\.exe)?""?\s.*\s-(game|server)\b|\bnpm\s+(run\s+)?test\b|\bcargo\s+test\b|\bgo\s+test\b|playwright|_test\.py\b|harness|\bvitest\b|\bjest\b|\b\w+(Client|Server)(-Win64-\w+)?\.exe\b", RegexOptions.IgnoreCase)]
    private static partial Regex Test();

    [GeneratedRegex(@"\bBuild\.(bat|sh)\b|UnrealBuildTool|\bmsbuild\b|\bdotnet\s+(build|publish|run)\b|-singlefile|\bcmake\b|\bnpm\s+run\s+build\b|\bcargo\s+build\b|\btsc\b|Mode=UnrealHeaderTool", RegexOptions.IgnoreCase)]
    private static partial Regex Build();

    [GeneratedRegex(@"ue_python\.py|(localhost|127\.0\.0\.1):8000/mcp|remote_execution", RegexOptions.IgnoreCase)]
    private static partial Regex EditorCall();

    [GeneratedRegex(@"\bserve(r)?\.py\b|\bhttp\.server\b|\brunserver\b|\b(npm|pnpm|yarn)\s+(run\s+)?(dev|start|serve)\b|\bnpx\s+serve\b|\bvite\b|\blive-server\b|\buvicorn\b|\bflask\s+run\b|\bwebpack(-dev-server|\s+serve)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Server();

    /// <summary>A command that serves a page until it is stopped: it never finishes, so nothing is being worked on while it runs.</summary>
    public static bool IsServer(string? command)
    {
        return !string.IsNullOrEmpty(command) && Server().IsMatch(command);
    }

    public static Activity ForCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return Activity.Tools;
        }

        if (LaneWait().IsMatch(command))
        {
            return Activity.Lane;
        }

        if (Test().IsMatch(command))
        {
            return Activity.Test;
        }

        return Build().IsMatch(command) ? Activity.Build : Activity.Tools;
    }

    public static Activity ForTool(string name, string? command)
    {
        return name switch
        {
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "apply_patch" => Activity.Code,
            _ when IsShell(name) => ForCommand(command),
            _ => Activity.Tools,
        };
    }

    /// <summary>The calls the orchestrator classifies as talking to the running editor (<c>RX_EDITOR_RPC</c> and <c>mcp__unreal*</c> in orch.py).</summary>
    public static bool UsesEditor(string name, string? command)
    {
        if (name.StartsWith("mcp__unreal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsShell(name) && command is not null && EditorCall().IsMatch(command);
    }

    private static bool IsShell(string name)
    {
        return name is "Bash" or "PowerShell" or "shell" or "exec_command" or "local_shell" or "exec";
    }
}
