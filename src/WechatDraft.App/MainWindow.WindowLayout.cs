using Microsoft.UI.Windowing;
using Windows.Graphics;
using WechatDraft.Core;
using WechatDraft.Windows;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private DrawingRectangle _normalWindowBounds;
    private uint _normalWindowDpi = 96;
    private bool _windowClosing;

    private void ConfigureWindowLayout()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var saved = _settings.WindowLayout;
        var screen = saved is null ? System.Windows.Forms.Screen.FromHandle(hwnd)
            : System.Windows.Forms.Screen.FromPoint(new DrawingPoint(saved.Left, saved.Top));
        var workArea = screen.WorkingArea;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        // Move onto the chosen monitor first so its current DPI determines the restored size.
        AppWindow.Move(new PointInt32(workArea.Left + Math.Min(32, workArea.Width / 4), workArea.Top + Math.Min(32, workArea.Height / 4)));
        _normalWindowDpi = WindowLayoutRules.WindowDpi(hwnd);
        _normalWindowBounds = WindowLayoutRules.Restore(saved, workArea, _normalWindowDpi);
        AppWindow.MoveAndResize(new RectInt32(_normalWindowBounds.Left, _normalWindowBounds.Top, _normalWindowBounds.Width, _normalWindowBounds.Height));
        AppWindow.Changed += (_, _) => TrackWindowLayout();
        AppWindow.Closing += (_, _) => { _windowClosing = true; SaveWindowLayout(); };
    }

    private void TrackWindowLayout()
    {
        if (_windowClosing || AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Restored ||
            AppWindow.Size.Width <= 0 || AppWindow.Size.Height <= 0) return;
        _normalWindowBounds = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _normalWindowDpi = WindowLayoutRules.WindowDpi(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private void SaveWindowLayout()
    {
        try
        {
            // Persist only ordinary window bounds, even when the user maximized/minimized during this run.
            _settings.WindowLayout = WindowLayoutRules.Capture(_normalWindowBounds, _normalWindowDpi);
            _repository.SaveSettings(_settings);
        }
        catch (Exception e)
        {
            // Window preference failures must not prevent closing the app or change input decisions.
            DebugSession.Current?.Event("window_layout_save_failed", new { exceptionType = e.GetType().Name });
        }
    }
}
