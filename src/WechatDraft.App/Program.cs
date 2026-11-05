using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WechatDraft.Core;

namespace WechatDraft.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstance = new Mutex(true, "Local\\WechatDraft.Desktop", out var created);
        if (!created) return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new DraftApplication();
        });
    }
}

public sealed partial class DraftApplication : Application
{
    private MainWindow? _window;
    public DraftApplication()
    {
        UnhandledException += (_, e) =>
        {
            // Never silently resume a failed input action. Keep diagnostics free of API keys and chat content.
            if (DebugSession.IsSupported) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                DateTimeOffset.UtcNow.ToString("O") + " " + e.Exception.GetType().Name + " " + e.Message + "\n" + e.Exception.StackTrace + "\n");
        };
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { _window = new MainWindow(); _window.Activate(); }
        catch (Exception e)
        {
            if (DebugSession.IsSupported) File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                DateTimeOffset.UtcNow.ToString("O") + " " + e.GetType().Name + " " + e.Message + "\n" + e.StackTrace + "\n");
            Exit();
        }
    }
}
