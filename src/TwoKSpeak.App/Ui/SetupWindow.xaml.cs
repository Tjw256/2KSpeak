using CancelEventArgs = System.ComponentModel.CancelEventArgs;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using TwoKSpeak.App.Setup;
using TwoKSpeak.Engine.Onnx;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.App.Ui;

/// <summary>
/// First-run downloads, written for someone who has never heard of CUDA: what each part is for, how far along it is,
/// and that they can close the window and keep working. Hidden rather than closed; downloads never depend on it.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly SetupCoordinator _setup;
    private readonly SettingsStore _settings;
    private readonly Dictionary<ComponentId, Row> _rows = [];
    private bool _refreshQueued;

    public SetupWindow(SetupCoordinator setup, SettingsStore settings)
    {
        _setup = setup;
        _settings = settings;
        InitializeComponent();
        SourceInitialized += (_, _) => Dwm.Apply(this, roundCorners: false);
        CloseButton.Click += (_, _) => Hide();
        RetryButton.Click += (_, _) => _setup.Retry();
        _setup.Changed += QueueRefresh;
        _settings.Changed += (_, _) => QueueRefresh();
    }

    public void ShowAndActivate()
    {
        Refresh();
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

    /// <summary>Progress arrives several times a second from the download thread; coalesce it into one UI update.</summary>
    private void QueueRefresh()
    {
        lock (_rows)
        {
            if (_refreshQueued)
            {
                return;
            }
            _refreshQueued = true;
        }
        Dispatcher.BeginInvoke(() =>
        {
            lock (_rows)
            {
                _refreshQueued = false;
            }
            if (IsVisible)
            {
                Refresh();
            }
        });
    }

    private void Refresh()
    {
        var status = _setup.Status();
        if (!status.Select(s => s.Component.Id).SequenceEqual(_rows.Keys))
        {
            BuildRows(status);
        }
        foreach (var item in status)
        {
            _rows[item.Component.Id].Update(item);
        }

        var hotkey = Hotkey.Format(_settings.Current.Hotkey);
        var speechReady = _setup.IsReady(ComponentId.Speech);
        var failure = status.FirstOrDefault(s => s.State == ComponentState.Failed);
        var waiting = status.Any(s => s is { State: ComponentState.Working, Stage: InstallStage.WaitingForNetwork });
        var complete = status.All(s => s.State == ComponentState.Ready);

        Heading.Text = complete ? "2KSpeak is ready" : "Setting up 2KSpeak";
        RetryButton.Visibility = failure is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = failure?.Error
            ?? (waiting ? "Waiting for an internet connection. The download continues by itself when you're back online."
            : complete ? "Everything is downloaded."
            : _setup.Remaining() is { } left ? $"About {Duration(left)} left." : "Starting the download…");

        NextStepText.Text = complete || speechReady
            ? $"To dictate, click into any text field, hold {hotkey}, speak, and let go. Your words are typed where the cursor is."
                + (complete ? "" : " The rest finishes in the background; you can close this window.")
            : "You can close this window: the download continues in the background. 2KSpeak lives in the tray, next to the clock.";
        CloseButton.Content = complete ? "Done" : "Close";
    }

    private void BuildRows(IReadOnlyList<ComponentStatus> status)
    {
        Rows.Children.Clear();
        _rows.Clear();
        foreach (var item in status)
        {
            if (Rows.Children.Count > 0)
            {
                Rows.Children.Add(new Rectangle { Style = (Style)FindResource("Divider") });
            }
            var row = new Row(this, item.Component);
            _rows[item.Component.Id] = row;
            Rows.Children.Add(row.Root);
        }
    }

    private static string Duration(TimeSpan left) => left.TotalMinutes switch
    {
        < 1 => "a minute",
        < 60 => $"{Math.Ceiling(left.TotalMinutes):0} minutes",
        _ => $"{left.TotalHours:0.0} hours",
    };

    private static string Gb(long bytes) => bytes < 1_000_000_000 ? $"{bytes / 1e6:0} MB" : $"{bytes / 1e9:0.0} GB";

    /// <summary>What each part does, in words that don't assume any technical background.</summary>
    private static string Purpose(ComponentId id) => id switch
    {
        ComponentId.Speech => "Turns your voice into text. Needed to dictate.",
        ComponentId.Cleanup => "Removes “um”s and words you corrected yourself.",
        ComponentId.CleanupSmall => "A lighter cleanup model for slower computers.",
        _ when Gpu.Detected?.MeasureFirst == true => "Tests whether your graphics chip is faster than the processor, and uses it if so.",
        _ => "Makes dictation faster on your graphics card.",
    };

    private sealed class Row
    {
        private readonly SetupWindow _owner;
        private readonly Component _component;
        private readonly TextBlock _state = new() { VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _detail;
        private readonly ProgressBar _meter;

        public Row(SetupWindow owner, Component component)
        {
            _owner = owner;
            _component = component;
            var title = new TextBlock { Text = component.Title };
            _detail = new TextBlock { Style = (Style)owner.FindResource("Hint"), Margin = new Thickness(0, 2, 12, 0) };
            _meter = new ProgressBar { Style = (Style)owner.FindResource("Meter"), Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(_state, Dock.Right);
            var text = new StackPanel { Children = { title, _detail } };
            var header = new DockPanel { Children = { _state, text } };
            Root = new StackPanel { Margin = new Thickness(0, 14, 0, 14), Children = { header, _meter } };
            System.Windows.Automation.AutomationProperties.SetName(Root, component.Title);
        }

        public StackPanel Root { get; }

        public void Update(ComponentStatus status)
        {
            var size = Gb(_component.Size);
            _detail.Text = $"{Purpose(_component.Id)} {size}";
            _meter.Visibility = status.State == ComponentState.Working ? Visibility.Visible : Visibility.Collapsed;
            _meter.Value = _component.Size == 0 ? 0 : (double)status.Done / _component.Size;
            _state.Foreground = (Brush)_owner.FindResource(status.State is ComponentState.Ready or ComponentState.Working ? "Text1" : "Text3");
            _state.Text = status.State switch
            {
                ComponentState.Ready => "Ready",
                ComponentState.Failed => "Stopped",
                ComponentState.Waiting => "Waiting",
                _ => status.Stage switch
                {
                    InstallStage.Verifying when status.Done == 0 => "Checking",
                    InstallStage.Verifying => "Verifying",
                    InstallStage.Extracting => "Unpacking",
                    InstallStage.WaitingForNetwork => "Offline",
                    _ => $"{100.0 * status.Done / Math.Max(1, _component.Size):0}%",
                },
            };
            System.Windows.Automation.AutomationProperties.SetHelpText(Root, _state.Text);
        }
    }
}
