using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Inference;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using static TwoKSpeak.App.Input.NativeMethods;

namespace TwoKSpeak.App.Ui;

/// <summary>
/// Left-click tray panel: model status, the last transcripts (click to copy) and the GPU/CPU switch.
/// Opens next to the tray and closes as soon as it loses focus.
/// </summary>
public partial class FlyoutWindow : Window
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const double Gap = 12;
    private const long ReopenGuardMs = 300;
    private const double GiB = 1024.0 * 1024 * 1024;

    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly WorkerClient _worker;
    private readonly DispatcherTimer _vramTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private long _hiddenAt;
    private bool _listening;
    private long? _vramBytes;

    public FlyoutWindow(SettingsStore settings, HistoryStore history, WorkerClient worker, Action openSettings)
    {
        _settings = settings;
        _history = history;
        _worker = worker;
        InitializeComponent();
        SourceInitialized += (_, _) => Dwm.Apply(this, roundCorners: true);

        SettingsButton.Click += (_, _) =>
        {
            HideFlyout();
            openSettings();
        };
        GpuOption.Checked += (_, _) => _settings.Update(s => s with { Device = RecognitionDevice.Gpu });
        CpuOption.Checked += (_, _) => _settings.Update(s => s with { Device = RecognitionDevice.Cpu });
        Deactivated += (_, _) => HideFlyout();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideFlyout();
            }
        };

        _settings.Changed += (_, _) => Ui(Refresh);
        _history.Changed += () => Ui(RefreshHistory);
        _worker.StateChanged += _ => Ui(RefreshStatus);
        _vramTimer.Tick += async (_, _) => await UpdateVramAsync();
    }

    public void SetListening(bool listening) => Ui(() =>
    {
        _listening = listening;
        RefreshStatus();
    });

    /// <summary>Tray left-click. The click that took focus away already hid the panel; it must not reopen it.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }
        if (Environment.TickCount64 - _hiddenAt < ReopenGuardMs)
        {
            return;
        }
        Refresh();
        Opacity = 0; // positioned in device pixels after layout; avoids a flash at the old position
        Show();
        GetCursorPos(out var cursor);
        PlaceNearTray(cursor);
        Opacity = 1;
        Activate();
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        _vramTimer.Start();
        _ = UpdateVramAsync();
    }

    private void HideFlyout()
    {
        if (!IsVisible)
        {
            return;
        }
        _hiddenAt = Environment.TickCount64;
        _vramTimer.Stop();
        Hide();
    }

    private void Refresh()
    {
        var device = _settings.Current.Device;
        GpuOption.IsChecked = device == RecognitionDevice.Gpu;
        CpuOption.IsChecked = device == RecognitionDevice.Cpu;
        EmptyText.Text = $"Hold {Hotkey.Format(_settings.Current.Hotkey)} to dictate";
        RefreshStatus();
        RefreshHistory();
    }

    private void RefreshStatus()
    {
        var state = _worker.State;
        StatusDot.Fill = (Brush)FindResource(_listening ? "Listening" : state == WorkerState.Ready ? "Text1" : "Text3");
        StatusText.Text = _listening ? "Listening" : state switch
        {
            WorkerState.Ready => "Ready",
            WorkerState.Loading => "Loading",
            WorkerState.Failed => "Model failed to load",
            _ => "Idle",
        };

        var requested = _settings.Current.Device;
        var active = _worker.ActiveDevice;
        var detail = active == RecognitionDevice.Cpu && requested == RecognitionDevice.Gpu
            ? "CPU, GPU unavailable"
            : (active ?? requested) == RecognitionDevice.Gpu ? "GPU" : "CPU";
        if (active == RecognitionDevice.Gpu && _vramBytes is > 0 and var bytes)
        {
            detail += $" · {bytes / GiB:0.0} GB";
        }
        StatusDetail.Text = "· " + detail;
    }

    private async Task UpdateVramAsync()
    {
        var pid = _worker.ProcessId;
        _vramBytes = pid is { } id && _worker.ActiveDevice == RecognitionDevice.Gpu
            ? await Task.Run(() => VramMeter.DedicatedBytes(id))
            : null;
        RefreshStatus();
    }

    private void RefreshHistory()
    {
        HistoryList.Children.Clear();
        var entries = _history.Entries;
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var entry in entries)
        {
            HistoryList.Children.Add(CreateRow(entry));
        }
    }

    private Button CreateRow(HistoryEntry entry)
    {
        var text = new TextBlock
        {
            Text = entry.Text,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 36,
        };
        var time = new TextBlock
        {
            Text = RelativeTime(entry.Time),
            Foreground = (Brush)FindResource("Text3"),
            FontSize = 12,
            Margin = new Thickness(12, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(time, 1);
        grid.Children.Add(text);
        grid.Children.Add(time);

        var row = new Button { Style = (Style)FindResource("RowButton"), Content = grid, Margin = new Thickness(0, 0, 0, 2) };
        System.Windows.Automation.AutomationProperties.SetName(row, $"Copy: {entry.Text}");
        var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            time.Text = RelativeTime(entry.Time);
        };
        row.Click += (_, _) =>
        {
            if (CopyToClipboard(entry.Text))
            {
                time.Text = "Copied";
                revert.Stop();
                revert.Start();
            }
        };
        return row;
    }

    /// <summary>The clipboard is briefly locked by whichever app touched it last; retry a few times.</summary>
    private static bool CopyToClipboard(string text)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (ExternalException) when (attempt < 4)
            {
                Thread.Sleep(30);
            }
            catch (ExternalException ex)
            {
                Log.Write($"clipboard unavailable: {ex.Message}");
                return false;
            }
        }
    }

    private static string RelativeTime(DateTimeOffset time)
    {
        var age = DateTimeOffset.Now - time;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "now";
        }
        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min";
        }
        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h";
        }
        return time.LocalDateTime.ToString("d MMM");
    }

    /// <summary>
    /// Bottom-right of the work area by the cursor (the tray click), staying inside the work area for any
    /// taskbar position.
    /// </summary>
    private void PlaceNearTray(POINT cursor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST), ref info);
        var work = info.rcWork;

        // Move onto the tray's monitor first and measure there: with mixed scaling (e.g. 150% and 100%)
        // Windows rescales the window when it changes monitor, so a size measured elsewhere is wrong.
        SetWindowPos(handle, 0, work.Left, work.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
        UpdateLayout();
        GetWindowRect(handle, out var bounds);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var gap = (int)(Gap * VisualTreeHelper.GetDpi(this).DpiScaleX);

        var x = Math.Clamp(cursor.X - width / 2, work.Left + gap, Math.Max(work.Left + gap, work.Right - width - gap));
        int y;
        if (cursor.Y >= work.Bottom)
        {
            y = work.Bottom - height - gap; // taskbar at the bottom
        }
        else if (cursor.Y < work.Top)
        {
            y = work.Top + gap; // taskbar at the top
        }
        else
        {
            // Taskbar on a side: sit beside it, vertically near the cursor.
            y = Math.Clamp(cursor.Y - height / 2, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - height - gap));
            x = cursor.X < work.Left ? work.Left + gap : work.Right - width - gap;
        }
        SetWindowPos(handle, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
    }

    private void Ui(Action action) => Dispatcher.BeginInvoke(action);
}
