using System.Windows;

namespace AgentManager.App;

/// <summary>Things the page asks of Windows itself.</summary>
public sealed class Desktop
{
    public void Copy(string text)
    {
        Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
    }
}
