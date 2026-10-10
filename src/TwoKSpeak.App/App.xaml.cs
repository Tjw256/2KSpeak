using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using TwoKSpeak.App.Audio;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Dictation;
using TwoKSpeak.App.Inference;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using TwoKSpeak.App.Ui;
using TwoKSpeak.Engine;
using TwoKSpeak.Engine.Vad;

namespace TwoKSpeak.App;

public partial class App : Application
{
    private const string InstanceName = @"Local\2KSpeak.SingleInstance";
    /// <summary>Signalled by a second launch (e.g. from the Start menu) to bring up the settings window.</summary>
    private const string ShowSettingsName = @"Local\2KSpeak.ShowSettings";
    private static readonly int[] TrayIconSizes = [16, 20, 24, 32, 48];

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSettingsSignal;
    private TaskbarIcon? _tray;
    private System.Drawing.Icon? _idleIcon;
    private System.Drawing.Icon? _listeningIcon;
    private bool _isListening;
    private KeyboardHook? _hook;
    private WorkerClient? _worker;
    private SileroVad? _vad;
    private ModelRamCache? _ramCache;
    private SettingsStore? _settings;
    private HistoryStore? _history;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _dictation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowSettingsName, out var running))
            {
                // This launch came from the user, so it may hand the foreground to the running instance.
                NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                running.Set();
                running.Dispose();
            }
            Shutdown();
            return;
        }

        Log.Write("starting");
        DispatcherUnhandledException += (_, args) =>
        {
            // A tray app that dies takes the hotkey with it; log and keep running instead.
            Log.Write($"unhandled UI exception: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"unhandled exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Write($"unobserved task exception: {args.Exception}");
        _settings = new SettingsStore(AppPaths.Settings);
        var settings = _settings.Current;
        var missing = MissingModels(settings);
        if (missing.Count > 0)
        {
            Log.Write($"missing models: {string.Join(", ", missing)}");
            MessageBox.Show($"2KSpeak can't find its model files:\n\n{string.Join("\n", missing)}\n\nRun tools/fetch-dev-models.sh first.",
                "2KSpeak", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _history = new HistoryStore(AppPaths.History, settings.SaveHistory);
        _ramCache = new ModelRamCache();
        HoldModelsInRam(settings);

        _vad = new SileroVad(AppPaths.SileroVad);
        _worker = new WorkerClient(() => _settings.Current);
        _hook = new KeyboardHook(settings.Hotkey);
        _flyout = new FlyoutWindow(_settings, _history, _worker, ShowSettings);
        var view = new ListeningView(new OverlayWindow(), listening => Dispatcher.BeginInvoke(() => SetListening(listening)));
        var testAudio = Environment.GetEnvironmentVariable("TWOKSPEAK_TEST_AUDIO");
        var controller = new DictationController(() => _settings.Current, _worker, _vad, view,
            s => testAudio is null ? new Microphone(Microphone.ResolveDevice(s.Microphone)) : new WavAudioSource(testAudio));
        controller.TranscriptCompleted += text =>
        {
            Log.Write($"dictation finished ({text.Length} chars)");
            _history.Add(text, DateTimeOffset.Now);
        };
        _settings.Changed += OnSettingsChanged;
        _dictation = Task.Run(() => controller.RunAsync(_hook.Signals, _shutdown.Token));
        CreateTray(settings);

        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsName);
        ThreadPool.RegisterWaitForSingleObject(_showSettingsSignal, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private void CreateTray(AppSettings settings)
    {
        _idleIcon = IconFactory.FromDrawings((ImageSource)Resources["IconSmall"], (ImageSource)Resources["IconLarge"], TrayIconSizes);
        _listeningIcon = IconFactory.FromDrawings((ImageSource)Resources["IconSmallListening"], (ImageSource)Resources["IconLargeListening"], TrayIconSizes);

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => ShowSettings();
        var quit = new MenuItem { Header = "Quit 2KSpeak" };
        quit.Click += (_, _) => Shutdown();
        _tray = new TaskbarIcon
        {
            ToolTipText = TrayToolTip(settings),
            Icon = TrayIcon(listening: false),
            NoLeftClickDelay = true,
            ContextMenu = new ContextMenu { Items = { settingsItem, quit } },
        };
        _tray.TrayLeftMouseUp += (_, _) => _flyout?.Toggle();
        _tray.ForceCreate();
    }

    private static string TrayToolTip(AppSettings settings) => $"2KSpeak · hold {Hotkey.Format(settings.Hotkey)} to dictate";

    /// <summary>A fresh copy each time: TaskbarIcon disposes the icon it is replacing.</summary>
    private System.Drawing.Icon TrayIcon(bool listening) => (System.Drawing.Icon)(listening ? _listeningIcon! : _idleIcon!).Clone();

    private void SetListening(bool listening)
    {
        if (listening == _isListening)
        {
            return;
        }
        _isListening = listening;
        if (_tray is not null)
        {
            _tray.Icon = TrayIcon(listening);
        }
        _flyout?.SetListening(listening);
    }

    private void ShowSettings()
    {
        _settingsWindow ??= new SettingsWindow(_settings!, _history!, _hook!);
        _settingsWindow.ShowAndActivate();
    }

    private void OnSettingsChanged(AppSettings previous, AppSettings current)
    {
        if (previous.Hotkey != current.Hotkey)
        {
            _hook!.SetChord(current.Hotkey);
            _tray!.ToolTipText = TrayToolTip(current);
        }
        if (previous.Device != current.Device || previous.IdleUnloadMinutes != current.IdleUnloadMinutes)
        {
            _ = ApplyWorkerSettingsAsync();
        }
        if (previous.Device != current.Device || previous.KeepModelInRam != current.KeepModelInRam)
        {
            HoldModelsInRam(current);
        }
        if (previous.SaveHistory != current.SaveHistory)
        {
            _history!.SetSaving(current.SaveHistory);
        }
    }

    private async Task ApplyWorkerSettingsAsync()
    {
        try
        {
            await _worker!.ApplySettingsAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"applying worker settings failed: {ex}");
        }
    }

    private void HoldModelsInRam(AppSettings settings)
    {
        if (settings.KeepModelInRam)
        {
            _ramCache!.Hold(ModelFiles(settings));
        }
        else
        {
            _ramCache!.Release();
        }
    }

    private static IEnumerable<string> ModelFiles(AppSettings settings) => settings.Device == RecognitionDevice.Gpu
        ? [Path.Combine(AppPaths.ParakeetFp16, "encoder-model.fp16.onnx"), Path.Combine(AppPaths.ParakeetFp16, "decoder_joint-model.fp16.onnx")]
        : [Path.Combine(AppPaths.ParakeetInt8, "encoder-model.int8.onnx"), Path.Combine(AppPaths.ParakeetInt8, "decoder_joint-model.int8.onnx")];

    private static List<string> MissingModels(AppSettings settings) =>
        ModelFiles(settings).Append(AppPaths.SileroVad).Where(path => !File.Exists(path)).ToList();

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _hook?.Dispose();
        try
        {
            _dictation?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException)
        {
        }
        _worker?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _vad?.Dispose();
        _ramCache?.Dispose();
        _tray?.Dispose();
        _idleIcon?.Dispose();
        _listeningIcon?.Dispose();
        _showSettingsSignal?.Dispose();
        _singleInstance?.Dispose();
        Log.Write("exited");
        base.OnExit(e);
    }
}
