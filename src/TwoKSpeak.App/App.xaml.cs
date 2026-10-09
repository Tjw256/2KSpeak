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
    private Mutex? _singleInstance;
    private TaskbarIcon? _tray;
    private KeyboardHook? _hook;
    private WorkerClient? _worker;
    private SileroVad? _vad;
    private ModelRamCache? _ramCache;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _dictation;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(true, @"Local\2KSpeak.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        Log.Write("starting");
        DispatcherUnhandledException += (_, args) => Log.Write($"unhandled UI exception: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"unhandled exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Write($"unobserved task exception: {args.Exception}");
        _settings = AppSettings.Load();
        var missing = MissingModels(_settings);
        if (missing.Count > 0)
        {
            Log.Write($"missing models: {string.Join(", ", missing)}");
            MessageBox.Show($"2KSpeak can't find its model files:\n\n{string.Join("\n", missing)}\n\nRun tools/fetch-dev-models.sh first.",
                "2KSpeak", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _ramCache = new ModelRamCache();
        if (_settings.KeepModelInRam)
        {
            _ramCache.Hold(ModelFiles(_settings));
        }

        var overlay = new OverlayWindow();
        _vad = new SileroVad(AppPaths.SileroVad);
        _worker = new WorkerClient(() => _settings);
        var testAudio = Environment.GetEnvironmentVariable("TWOKSPEAK_TEST_AUDIO");
        var controller = new DictationController(() => _settings, _worker, _vad, overlay,
            settings => testAudio is null ? new Microphone(settings.MicrophoneDevice) : new WavAudioSource(testAudio));
        controller.TranscriptCompleted += text => Log.Write($"dictation finished ({text.Length} chars)");
        _hook = new KeyboardHook();
        _dictation = Task.Run(() => controller.RunAsync(_hook.Signals, _shutdown.Token));
        CreateTray();
    }

    private void CreateTray()
    {
        var quit = new MenuItem { Header = "Quit 2KSpeak" };
        quit.Click += (_, _) => Shutdown();
        _tray = new TaskbarIcon
        {
            ToolTipText = "2KSpeak — hold Ctrl+Win to dictate",
            Icon = IconFactory.FromDrawing((ImageSource)Resources["TrayIdleIcon"], 16, 20, 24, 32, 48),
            ContextMenu = new ContextMenu { Items = { quit } },
        };
        _tray.ForceCreate();
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
        _singleInstance?.Dispose();
        Log.Write("exited");
        base.OnExit(e);
    }
}
