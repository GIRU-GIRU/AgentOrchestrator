using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentManager.Core;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;

namespace AgentManager.App;

public partial class MainWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private const int DWMWA_CAPTION_COLOR = 35;

    private const int CAPTION_COLOR_BGR = 0x00170F0B;

    private static readonly TimeSpan SCRIPT_TIMEOUT = TimeSpan.FromSeconds(30);

    private readonly ScreenshotRequest? screenshot;

    public MainWindow(IServiceProvider services, ScreenshotRequest? screenshot)
    {
        this.screenshot = screenshot;
        Resources.Add("services", services);
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar();
        // WebView2 keeps drawing a minimized window; hidden, it stops.
        StateChanged += (_, _) =>
        {
            if (WebView.WebView is { } control)
            {
                control.Visibility = WindowState == WindowState.Minimized ? Visibility.Hidden : Visibility.Visible;
            }
        };
        WebView.BlazorWebViewInitialized += (_, args) =>
        {
            var core = args.WebView.CoreWebView2;
            AppLog.Info("app", $"WebView2 {core.Environment.BrowserVersionString} ready");
            core.ProcessFailed += (_, failure) => AppLog.Error("app", $"WebView2 process failed: {failure.ProcessFailedKind} ({failure.Reason}, exit {failure.ExitCode})");
            ApplyZoom(services.GetRequiredService<AgentHub>());
        };
        if (screenshot is not null)
        {
            Width = screenshot.Width;
            Height = screenshot.Height;
            WebView.BlazorWebViewInitialized += async (_, _) => await CaptureAsync();
        }
    }

    private void ApplyZoom(AgentHub hub)
    {
        var control = WebView.WebView;
        control.ZoomFactor = PageZoom.Usable(hub.Settings.Zoom);
        control.ZoomFactorChanged += (_, _) =>
        {
            var zoom = control.ZoomFactor;
            if (zoom != hub.Settings.Zoom)
            {
                hub.ChangeSettings(settings => settings.Zoom = zoom);
            }
        };
    }

    private void DarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var on = 1;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        var color = CAPTION_COLOR_BGR;
        DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref color, sizeof(int));
    }

    private async Task CaptureAsync()
    {
        AppLog.Info("app", $"screenshot mode: {screenshot!.Path} after {screenshot.DelayMs} ms");
        await Task.Delay(screenshot.DelayMs);
        if (screenshot.Script is not null)
        {
            var result = await EvaluateAsync(screenshot.Script);
            await File.WriteAllTextAsync(Path.ChangeExtension(screenshot.Path, ".txt"), result);
            await Task.Delay(600);
        }

        await using (var file = File.Create(screenshot.Path))
        {
            await WebView.WebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        }

        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var window = File.Create(Path.ChangeExtension(screenshot.Path, ".window.png")))
        {
            encoder.Save(window);
        }

        Close();
    }

    /// <summary>
    /// ExecuteScriptAsync returns "{}" for a promise, so the script's value is awaited in the page and read back once
    /// it settles; a script that drives the page over several frames (a drag, an animation) can return a promise.
    /// </summary>
    private async Task<string> EvaluateAsync(string script)
    {
        var core = WebView.WebView.CoreWebView2;
        await core.ExecuteScriptAsync($$"""
            window.__shot = { done: false };
            (async () => {
                try { window.__shot.value = await ({{script}}); }
                catch (e) { window.__shot.value = 'error: ' + (e?.stack ?? e); }
                window.__shot.done = true;
            })();
            """);
        var deadline = DateTime.UtcNow + SCRIPT_TIMEOUT;
        while (await core.ExecuteScriptAsync("window.__shot.done") != "true" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return await core.ExecuteScriptAsync("window.__shot.value");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>
/// <c>--screenshot &lt;png&gt; [--delay ms] [--size WxH] [--eval js]</c>: render, save the page as a PNG (and the result of
/// <c>js</c> beside it as .txt) and exit.
/// </summary>
public sealed record ScreenshotRequest(string Path, int DelayMs, int Width, int Height, string? Script)
{
    public static ScreenshotRequest? From(string[] args)
    {
        var index = Array.IndexOf(args, "--screenshot");
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        var delay = Value(args, "--delay") is { } d && int.TryParse(d, out var ms) ? ms : 6000;
        var size = Value(args, "--size")?.Split('x');
        var width = size is { Length: 2 } && int.TryParse(size[0], out var w) ? w : 1560;
        var height = size is { Length: 2 } && int.TryParse(size[1], out var h) ? h : 940;
        return new ScreenshotRequest(System.IO.Path.GetFullPath(args[index + 1]), delay, width, height, Value(args, "--eval"));
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
