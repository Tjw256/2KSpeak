using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TwoKSpeak.App.Dictation;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Ui;

/// <summary>
/// Small click-through pill near the text caret: red dot while listening, the phrase in progress as grey
/// text, and short error messages. It never takes focus, so typing continues into the user's window.
/// </summary>
public partial class OverlayWindow : Window, IDictationView
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int PreviewChars = 90;

    private static readonly Brush Listening = Frozen("#FF5C62");
    private static readonly Brush Idle = Frozen("#8D8D8D");
    private readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, GWL_EXSTYLE);
            SetWindowLongPtr(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        _errorTimer.Tick += (_, _) =>
        {
            _errorTimer.Stop();
            Hide();
        };
    }

    private static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    public void ShowListening() => Ui(() =>
    {
        _errorTimer.Stop();
        Dot.Fill = Listening;
        SetMessage(null);
        ShowNearCaret();
    });

    public void ShowFinishing() => Ui(() => Dot.Fill = Idle);

    public void ShowPreview(string text) => Ui(() =>
    {
        if (IsVisible && !_errorTimer.IsEnabled)
        {
            SetMessage(text.Length > PreviewChars ? "…" + text[^PreviewChars..] : text);
        }
    });

    public void ShowError(string message) => Ui(() =>
    {
        Dot.Fill = Idle;
        SetMessage(message);
        if (!IsVisible)
        {
            ShowNearCaret();
        }
        _errorTimer.Stop();
        _errorTimer.Start();
    });

    void IDictationView.Hide() => Ui(() =>
    {
        if (!_errorTimer.IsEnabled)
        {
            Hide();
        }
    });

    private void SetMessage(string? text)
    {
        Message.Text = text ?? "";
        Message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);

    /// <summary>Below the caret when the focused app reports one; otherwise bottom-centre of the active monitor.</summary>
    private void ShowNearCaret()
    {
        Show();
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)(ActualWidth * dpi.DpiScaleX);

        int x, y;
        if (TryGetCaret(out var caret))
        {
            x = caret.X;
            y = caret.Y + (int)(8 * dpi.DpiScaleY);
        }
        else
        {
            GetCursorPos(out var cursor);
            var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info);
            x = (info.rcWork.Left + info.rcWork.Right - width) / 2;
            y = info.rcWork.Bottom - (int)(96 * dpi.DpiScaleY);
        }
        SetWindowPos(handle, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static bool TryGetCaret(out POINT bottomLeft)
    {
        bottomLeft = default;
        var foreground = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.hwndCaret == 0)
        {
            return false;
        }
        bottomLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
        return ClientToScreen(info.hwndCaret, ref bottomLeft);
    }
}
