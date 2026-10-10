using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using TwoKSpeak.App.Audio;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Dictation;
using TwoKSpeak.App.Inference;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using TwoKSpeak.App.Setup;
using TwoKSpeak.App.Ui;
using TwoKSpeak.Engine;
using TwoKSpeak.Engine.Setup;
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
    private CuratorClient? _curator;
    private SileroVad? _vad;
    private ModelRamCache? _ramCache;
    private SettingsStore? _settings;
    private HistoryStore? _history;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private SetupCoordinator? _setup;
    private SetupWindow? _setupWindow;
    private Updater? _updater;
    /// <summary>Completes once speech recognition is downloaded; dictation starts then.</summary>
    private readonly TaskCompletionSource _speechReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        var firstRun = !File.Exists(AppPaths.Settings);
        _settings = new SettingsStore(AppPaths.Settings);
        // A card supported by this build with room for both models gets GPU mode; otherwise use CPU.
        var gpuCapable = GpuCapability.CanRunCurator();
        if (firstRun)
        {
            var device = gpuCapable ? RecognitionDevice.Gpu : RecognitionDevice.Cpu;
            Log.Write($"first run: speech and cleanup on {device}");
            _settings.Update(s => s with { Device = device, CleanupDevice = device });
        }
        else if (_settings.Current.CleanupDevice is null)
        {
            Log.Write($"cleanup device chosen: {(gpuCapable ? "GPU" : "CPU")}");
            _settings.Update(s => s with { CleanupDevice = gpuCapable ? RecognitionDevice.Gpu : RecognitionDevice.Cpu });
        }
        var settings = _settings.Current;
        _setup = new SetupCoordinator(ComponentInstaller.CreateDefault(Version), gpuCapable, () => _settings.Current);
        if (_setup.IsReady(ComponentId.Speech))
        {
            _speechReady.SetResult();
        }

        _history = new HistoryStore(AppPaths.History, settings.SaveHistory);
        _ramCache = new ModelRamCache();
        HoldModelsInRam(settings);

        _worker = new WorkerClient(() => _settings.Current, _setup.IsReady);
        _curator = new CuratorClient(() => _settings.Current, _setup.IsReady);
        _hook = new KeyboardHook(settings.Hotkey);
        _updater = new Updater();
        _flyout = new FlyoutWindow(_settings, _history, _worker, _curator, _setup, _updater, ShowSettings, ShowSetup, RestartToUpdate);
        var view = new ListeningView(new OverlayWindow(), listening => Dispatcher.BeginInvoke(() => SetListening(listening)));
        _settings.Changed += OnSettingsChanged;
        _dictation = Task.Run(() => RunDictationAsync(view, _shutdown.Token));
        CreateTray(settings);

        _setup.Installed += id => Dispatcher.BeginInvoke(() => OnInstalled(id));
        _setup.Failed += message => Dispatcher.BeginInvoke(() =>
            Notify("Download stopped", $"{message} Open 2KSpeak from the tray to try again."));
        _setup.Start();
        if (!_setup.IsReady(ComponentId.Speech))
        {
            ShowSetup();
        }
        _updater.Ready += () => Dispatcher.BeginInvoke(() => Notify("Update ready", "2KSpeak updates the next time it starts."));
        _updater.Start();

        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsName);
        ThreadPool.RegisterWaitForSingleObject(_showSettingsSignal, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Until speech recognition is downloaded a hold only says so; then the VAD loads and the dictation controller
    /// takes over the hotkey signals.
    /// </summary>
    private async Task RunDictationAsync(IDictationView view, CancellationToken ct)
    {
        var signals = _hook!.Signals;
        while (!_speechReady.Task.IsCompleted)
        {
            var readable = signals.WaitToReadAsync(ct).AsTask();
            if (await Task.WhenAny(readable, _speechReady.Task) != readable)
            {
                break;
            }
            if (!await readable)
            {
                return;
            }
            while (!_speechReady.Task.IsCompleted && signals.TryRead(out var signal))
            {
                if (signal == ChordSignal.Start)
                {
                    view.ShowError(SpeechPendingMessage());
                }
            }
        }

        _vad = new SileroVad(AppPaths.SileroVad);
        var testAudio = Environment.GetEnvironmentVariable("TWOKSPEAK_TEST_AUDIO");
        var controller = new DictationController(() => _settings!.Current, _worker!, _curator!, _vad, view,
            s => testAudio is null ? new Microphone(Microphone.ResolveDevice(s.Microphone)) : new WavAudioSource(testAudio));
        controller.TranscriptCompleted += text =>
        {
            Log.Write($"dictation finished ({text.Length} chars)");
            _history!.Add(text, DateTimeOffset.Now);
        };
        await controller.RunAsync(signals, ct);
    }

    private string SpeechPendingMessage()
    {
        var speech = _setup!.Status().First(s => s.Component.Id == ComponentId.Speech);
        return speech.State == ComponentState.Failed
            ? "Speech model download stopped"
            : $"Speech model still downloading · {100.0 * speech.Done / speech.Component.Size:0}%";
    }

    private void OnInstalled(ComponentId id)
    {
        var hotkey = Hotkey.Format(_settings!.Current.Hotkey);
        switch (id)
        {
            case ComponentId.Speech:
                _speechReady.TrySetResult();
                Notify("2KSpeak is ready", $"Hold {hotkey} and speak to type. The rest keeps downloading in the background.");
                break;
            case ComponentId.Cleanup:
            case ComponentId.CleanupSmall:
                Notify("Text cleanup is on", "2KSpeak now removes “um”s and words you corrected yourself.");
                break;
            case ComponentId.GpuMode:
                Notify("GPU mode is on", "Dictation now runs on your graphics card, which is much faster.");
                break;
        }
        if (id is ComponentId.Speech or ComponentId.GpuMode)
        {
            HoldModelsInRam(_settings.Current);
        }
    }

    private void Notify(string title, string message)
    {
        try
        {
            _tray?.ShowNotification(title, message, NotificationIcon.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Write($"notification not shown: {ex.Message}");
        }
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
        // Clicking a download notification opens the setup window while there is something to show.
        _tray.TrayBalloonTipClicked += (_, _) =>
        {
            if (!_setup!.IsComplete)
            {
                ShowSetup();
            }
        };
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
        _settingsWindow ??= new SettingsWindow(_settings!, _history!, _hook!, _setup!);
        _settingsWindow.ShowAndActivate();
    }

    private void ShowSetup()
    {
        _setupWindow ??= new SetupWindow(_setup!, _settings!);
        _setupWindow.ShowAndActivate();
    }

    private void RestartToUpdate()
    {
        // The updater exits this process; take the tray icon down first so no ghost icon is left behind.
        _tray?.Dispose();
        _tray = null;
        _updater!.ApplyAndRestart();
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
        if (previous.Cleanup != current.Cleanup || previous.CleanupDevice != current.CleanupDevice
            || previous.IdleUnloadMinutes != current.IdleUnloadMinutes)
        {
            _ = ApplyCuratorSettingsAsync();
        }
        if (previous.Device != current.Device || previous.KeepModelInRam != current.KeepModelInRam)
        {
            HoldModelsInRam(current);
        }
        if (previous.Device != current.Device || previous.Cleanup != current.Cleanup || previous.CleanupDevice != current.CleanupDevice)
        {
            _setup!.Start(); // queue whatever the new choice needs
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

    private async Task ApplyCuratorSettingsAsync()
    {
        try
        {
            await _curator!.ApplySettingsAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"applying cleanup settings failed: {ex}");
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

    /// <summary>
    /// The files the worker will load. Only installed components: a mapped file can't be replaced, and the installer
    /// may still be writing a component that isn't ready.
    /// </summary>
    private IEnumerable<string> ModelFiles(AppSettings settings)
    {
        if (settings.Device == RecognitionDevice.Gpu && _setup!.IsReady(ComponentId.GpuMode))
        {
            return [Path.Combine(AppPaths.ParakeetFp16, "encoder-model.fp16.onnx"), Path.Combine(AppPaths.ParakeetFp16, "decoder_joint-model.fp16.onnx")];
        }
        return _setup!.IsReady(ComponentId.Speech)
            ? [Path.Combine(AppPaths.ParakeetInt8, "encoder-model.int8.onnx"), Path.Combine(AppPaths.ParakeetInt8, "decoder_joint-model.int8.onnx")]
            : [];
    }

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
        _curator?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _vad?.Dispose();
        _ramCache?.Dispose();
        _updater?.Dispose();
        _tray?.Dispose();
        _idleIcon?.Dispose();
        _listeningIcon?.Dispose();
        _showSettingsSignal?.Dispose();
        _singleInstance?.Dispose();
        Log.Write("exited");
        base.OnExit(e);
    }
}
