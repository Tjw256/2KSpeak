using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TwoKSpeak.App.Audio;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine;

namespace TwoKSpeak.App.Ui;

/// <summary>Every control writes straight to the settings store; there is no Save button.</summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] PauseChoicesMs = [300, 500, 750, 1000, 1500];
    private static readonly int[] IdleChoicesMinutes = [1, 5, 10, 30, 60, 0];

    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly KeyboardHook _hook;
    private bool _loading;
    private bool _capturing;

    public SettingsWindow(SettingsStore settings, HistoryStore history, KeyboardHook hook)
    {
        _settings = settings;
        _history = history;
        _hook = hook;
        InitializeComponent();
        SourceInitialized += (_, _) => Dwm.Apply(this, roundCorners: false);
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        VersionText.Text = $"2KSpeak {version?.ToString(3)}";

        HotkeyButton.Click += (_, _) => BeginHotkeyCapture();
        _hook.CaptureCompleted += outcome => Dispatcher.BeginInvoke(() => EndHotkeyCapture(outcome));
        PauseBox.SelectionChanged += (_, _) => Apply(PauseBox, (s, ms) => s with { PauseMs = (int)ms! });
        FillersToggle.Click += (_, _) => Apply(s => s with { RemoveFillers = FillersToggle.IsChecked == true });
        MicrophoneBox.SelectionChanged += (_, _) => Apply(MicrophoneBox, (s, name) => s with { Microphone = (string?)name });
        GpuOption.Checked += (_, _) => Apply(s => s with { Device = RecognitionDevice.Gpu });
        CpuOption.Checked += (_, _) => Apply(s => s with { Device = RecognitionDevice.Cpu });
        IdleBox.SelectionChanged += (_, _) => Apply(IdleBox, (s, minutes) => s with { IdleUnloadMinutes = (int)minutes! });
        RamToggle.Click += (_, _) => Apply(s => s with { KeepModelInRam = RamToggle.IsChecked == true });
        SaveHistoryToggle.Click += (_, _) => Apply(s => s with { SaveHistory = SaveHistoryToggle.IsChecked == true });
        ClearHistoryButton.Click += (_, _) => _history.Clear();
        AutostartToggle.Click += (_, _) => SetAutostart(AutostartToggle.IsChecked == true);
        LogsButton.Click += (_, _) => Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true });

        _settings.Changed += (_, _) => Dispatcher.BeginInvoke(Load);
        _history.Changed += () => Dispatcher.BeginInvoke(() => ClearHistoryButton.IsEnabled = _history.Entries.Count > 0);
        Load();
    }

    /// <summary>Brings the window up; it is hidden rather than closed so it keeps its state.</summary>
    public void ShowAndActivate()
    {
        Load();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Load()
    {
        _loading = true;
        var s = _settings.Current;
        if (!_capturing)
        {
            HotkeyButton.Content = Hotkey.Format(s.Hotkey);
        }
        Fill(PauseBox, PauseChoicesMs.Append(s.PauseMs).Distinct().Order().Select(ms => ((object?)ms, $"{ms} ms")), s.PauseMs);
        FillersToggle.IsChecked = s.RemoveFillers;
        var microphones = Microphone.DeviceNames();
        var choices = new List<(object?, string)> { (null, "Default") };
        choices.AddRange(microphones.Select(name => ((object?)name, name)));
        if (s.Microphone is { } saved && !microphones.Contains(saved))
        {
            choices.Add((saved, $"{saved} (not connected)"));
        }
        Fill(MicrophoneBox, choices, s.Microphone);
        GpuOption.IsChecked = s.Device == RecognitionDevice.Gpu;
        CpuOption.IsChecked = s.Device == RecognitionDevice.Cpu;
        Fill(IdleBox, IdleChoicesMinutes.Append(s.IdleUnloadMinutes).Distinct()
            .Select(m => ((object?)m, m == 0 ? "Never" : $"{m} min")), s.IdleUnloadMinutes);
        IdleBox.IsEnabled = s.Device == RecognitionDevice.Gpu;
        RamToggle.IsChecked = s.KeepModelInRam;
        SaveHistoryToggle.IsChecked = s.SaveHistory;
        ClearHistoryButton.IsEnabled = _history.Entries.Count > 0;
        // The Run key is the truth: a dev build or another tool may have changed it behind the setting's back.
        AutostartToggle.IsChecked = Autostart.IsRegistered();
        _loading = false;
    }

    private static void Fill(ComboBox box, IEnumerable<(object? Value, string Label)> choices, object? selected)
    {
        box.Items.Clear();
        foreach (var (value, label) in choices)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (Equals(value, selected))
            {
                box.SelectedItem = item;
            }
        }
    }

    private void Apply(Func<AppSettings, AppSettings> change)
    {
        if (!_loading)
        {
            _settings.Update(change);
        }
    }

    private void Apply(ComboBox box, Func<AppSettings, object?, AppSettings> change)
    {
        if (box.SelectedItem is ComboBoxItem item)
        {
            Apply(s => change(s, item.Tag));
        }
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            Autostart.Apply(enabled, Environment.ProcessPath!);
            _settings.Update(s => s with { StartWithWindows = enabled });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write($"autostart not changed: {ex.Message}");
            AutostartToggle.IsChecked = Autostart.IsRegistered();
        }
    }

    /// <summary>
    /// Hotkey recording happens in the keyboard hook, which hides the keys from Windows meanwhile, so trying a
    /// combination like Win+Alt doesn't also trigger whatever Windows maps to it.
    /// </summary>
    private void BeginHotkeyCapture()
    {
        if (_capturing)
        {
            return;
        }
        _capturing = true;
        HotkeyButton.Content = "Press keys";
        ShowHotkeyHint("Hold 2 or 3 of Ctrl, Win, Alt, Shift. Esc cancels.");
        _hook.BeginCapture();
    }

    private void EndHotkeyCapture(CaptureOutcome outcome)
    {
        if (!_capturing)
        {
            return;
        }
        _capturing = false;
        ShowHotkeyHint(null);
        if (outcome.Chord is { } chord)
        {
            if (Hotkey.Problem(chord) is { } problem)
            {
                ShowHotkeyHint(problem);
            }
            else
            {
                _settings.Update(s => s with { Hotkey = chord });
            }
        }
        HotkeyButton.Content = Hotkey.Format(_settings.Current.Hotkey);
    }

    private void ShowHotkeyHint(string? text)
    {
        HotkeyHint.Text = text ?? "";
        HotkeyHint.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
