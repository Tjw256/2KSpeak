using System.Text;
using System.Threading.Channels;
using TwoKSpeak.App.Audio;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Inference;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Dictation;
using TwoKSpeak.Engine.Ipc;
using TwoKSpeak.Engine.Vad;

namespace TwoKSpeak.App.Dictation;

/// <summary>What the dictation flow shows the user. Implementations marshal to their own thread.</summary>
public interface IDictationView
{
    void ShowListening();
    void ShowFinishing();
    void ShowPreview(string text);
    void ShowError(string message);
    void Hide();
}

/// <summary>
/// Runs dictation sessions: one per hotkey hold. Audio is segmented into phrases as it arrives; phrases are
/// transcribed, cleaned and typed strictly in order by a single processor, so a new hold can start recording while
/// the previous one is still finishing without their text interleaving. Text is typed phrase by phrase, or all at
/// once on release (the session's <see cref="TypeWhen"/>).
/// </summary>
public sealed class DictationController
{
    private readonly Func<AppSettings> _settings;
    private readonly WorkerClient _worker;
    private readonly CuratorClient _curator;
    private readonly SileroVad _vad;
    private readonly IDictationView _view;
    private readonly Func<AppSettings, IAudioSource> _audioSource;
    private readonly Func<string, bool> _type;
    /// <summary>Peak level below which the microphone is treated as muted (normal room noise is well above).</summary>
    private const double SilentMicrophoneDb = -60;
    /// <summary>Cleaning one phrase may delay it at most this long; after that the phrase is typed as recognised.</summary>
    private static readonly TimeSpan PhraseCleanupTimeout = TimeSpan.FromSeconds(2.5);
    /// <summary>Budget for cleaning a whole dictation on release (covers a cold model load plus ~6 ms/char on CPU).</summary>
    private static readonly TimeSpan ReleaseCleanupBase = TimeSpan.FromSeconds(8);
    private const double ReleaseCleanupMsPerChar = 15;

    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private Session? _current;
    private int _nextSessionId;

    public DictationController(Func<AppSettings> settings, WorkerClient worker, CuratorClient curator, SileroVad vad,
        IDictationView view, Func<AppSettings, IAudioSource> audioSource, Func<string, bool>? typer = null)
    {
        _audioSource = audioSource;
        _type = typer ?? TextTyper.Type; // replaceable so the whole pipeline can be exercised without a focused window
        _settings = settings;
        _worker = worker;
        _curator = curator;
        _vad = vad;
        _view = view;
    }

    /// <summary>Raised with the full text of each finished dictation.</summary>
    public event Action<string>? TranscriptCompleted;

    public async Task RunAsync(ChannelReader<ChordSignal> signals, CancellationToken ct)
    {
        var processor = ProcessAsync(ct);
        await foreach (var signal in signals.ReadAllAsync(ct))
        {
            switch (signal)
            {
                case ChordSignal.Start:
                    await BeginAsync();
                    break;
                case ChordSignal.Stop:
                    End();
                    break;
                case ChordSignal.Cancel:
                    Cancel();
                    break;
            }
        }
        _work.Writer.TryComplete();
        await processor;
    }

    private async Task BeginAsync()
    {
        if (_current is { } previous)
        {
            // The VAD is stateful and shared; let the previous session's audio drain first (milliseconds).
            await previous.Pump;
        }

        var settings = _settings();
        var microphone = _audioSource(settings);
        try
        {
            microphone.Start();
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            microphone.Dispose();
            Log.Write($"microphone failed: {ex.Message}");
            _view.ShowError("Microphone unavailable");
            return;
        }

        _worker.Prewarm();
        _curator.Prewarm();
        _vad.Reset();
        var session = new Session(++_nextSessionId, settings.TypeWhen, microphone, new PhraseSegmenter(new SegmenterSettings { PauseMs = settings.PauseMs }));
        session.Pump = PumpAsync(session);
        _current = session;
        _view.ShowListening();
        Log.Write($"session {session.Id} started");
    }

    private void End()
    {
        if (_current is not { } session || session.Ended)
        {
            return;
        }
        session.Ended = true;
        session.Microphone.Stop(); // the pump flushes the last phrase once the microphone drains
        _view.ShowFinishing();
        Log.Write($"session {session.Id} released");
    }

    private void Cancel()
    {
        if (_current is not { } session)
        {
            return;
        }
        session.Cancelled = true;
        session.Ended = true;
        session.Microphone.Stop(); // the processor skips this session's remaining work
        _view.Hide();
    }

    private async Task PumpAsync(Session session)
    {
        var windows = 0;
        var peak = 0f;
        var loudest = 0f;
        try
        {
            await foreach (var window in session.Microphone.Windows.ReadAllAsync())
            {
                if (session.Cancelled)
                {
                    continue; // drain without work
                }
                var probability = _vad.Process(window);
                windows++;
                foreach (var sample in window)
                {
                    loudest = Math.Max(loudest, Math.Abs(sample));
                }
                peak = Math.Max(peak, probability);
                foreach (var e in session.Segmenter.Add(window, probability))
                {
                    Dispatch(session, e);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"audio capture failed: {ex}");
            _view.ShowError("Microphone stopped");
        }
        finally
        {
            if (!session.Cancelled)
            {
                foreach (var e in session.Segmenter.Finish())
                {
                    Dispatch(session, e);
                }
            }
            var seconds = windows * 0.032;
            var level = 20 * Math.Log10(Math.Max(loudest, 1e-6f));
            _work.Writer.TryWrite(new SessionEnded(session, NoSpeechMessage(session, seconds, level)));
            session.Microphone.Dispose();
            Log.Write($"session {session.Id} audio: {seconds:F1} s, peak level {level:F0} dBFS, peak speech probability {peak:F2}, {session.Phrases} phrase(s)");
        }
    }

    /// <summary>
    /// A hold of a second or more that produced no phrase gets a hint instead of silently doing nothing, so a
    /// muted or wrong microphone is noticed. A near-zero level means the device delivers silence.
    /// </summary>
    private static string? NoSpeechMessage(Session session, double seconds, double levelDb)
    {
        if (session.Cancelled || session.Phrases > 0 || seconds < 1.0)
        {
            return null;
        }
        return levelDb < SilentMicrophoneDb ? "Microphone is silent" : "No speech heard";
    }

    private void Dispatch(Session session, SegmenterEvent e)
    {
        switch (e)
        {
            case PhraseCompleted phrase:
                session.Phrases++;
                Interlocked.Increment(ref session.PendingPhrases);
                _work.Writer.TryWrite(new PhraseWork(session, phrase.Context, phrase.Samples));
                break;
            case PhrasePreview preview when Volatile.Read(ref session.PendingPhrases) == 0:
                _ = PreviewAsync(session, preview);
                break;
        }
    }

    private async Task PreviewAsync(Session session, PhrasePreview preview)
    {
        try
        {
            var result = await _worker.TryPreviewAsync(preview.Context, preview.Samples, CancellationToken.None);
            if (result is not null && !session.Ended && ReferenceEquals(session, _current))
            {
                ShowProgress(session, Filter(result.Text));
            }
        }
        catch (WorkerException)
        {
            // Previews are best-effort; the committed phrase reports real failures.
        }
    }

    /// <summary>
    /// Overlay text while holding. When typing as you speak the committed phrases are already in the document, so
    /// only the words in flight are shown; when typing on release nothing is typed yet, so the overlay carries it all.
    /// </summary>
    private void ShowProgress(Session session, string inFlight)
    {
        var text = session.Mode == TypeWhen.Released ? (session.Pending + " " + inFlight).Trim() : inFlight;
        if (text.Length > 0)
        {
            _view.ShowPreview(text);
        }
    }

    /// <summary>Transcribes, cleans and types phrases in arrival order, one at a time.</summary>
    private async Task ProcessAsync(CancellationToken ct)
    {
        await foreach (var work in _work.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessOneAsync(work, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad phrase must not stop every later dictation.
                Log.Write($"dictation work failed: {ex}");
                _view.ShowError("Dictation failed");
            }
        }
    }

    private async Task ProcessOneAsync(Work work, CancellationToken ct)
    {
        var session = work.Session;
        switch (work)
        {
            case PhraseWork phrase:
                try
                {
                    if (!session.Cancelled)
                    {
                        await ProcessPhraseAsync(phrase, ct);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref session.PendingPhrases);
                }
                break;
            case SessionEnded ended when !session.Cancelled:
                await FinishSessionAsync(session, ct);
                if (ended.NoSpeech is { } hint)
                {
                    _view.ShowError(hint);
                }
                else if (session.Ended && ReferenceEquals(_current, session))
                {
                    _view.Hide();
                }
                break;
        }
    }

    private async Task ProcessPhraseAsync(PhraseWork phrase, CancellationToken ct)
    {
        var session = phrase.Session;
        TranscribeResponse result;
        try
        {
            result = await _worker.TranscribeAsync(phrase.Context, phrase.Samples, ct);
            Log.Write($"phrase {phrase.Samples.Length / 16000.0:F1} s transcribed in {result.ElapsedMs:F0} ms");
        }
        catch (WorkerException ex)
        {
            Log.Write($"transcription failed: {ex.Message}");
            _view.ShowError(ex.Message);
            return;
        }

        _curator.Prewarm(); // keeps the cleanup model from idling out during a long hold
        var text = Filter(result.Text);
        if (session.Mode == TypeWhen.Speaking)
        {
            text = await _curator.CleanAsync(text, PhraseCleanupTimeout, ct) ?? text;
        }
        if (session.Cancelled)
        {
            return; // Win+Ctrl+→ arrived while this phrase was being recognised
        }

        var piece = session.Assembler.Add(text, result.SeamPunctuation);
        if (session.Mode == TypeWhen.Speaking)
        {
            Type(piece, session);
        }
        else
        {
            session.Pending += piece;
            if (!session.Ended)
            {
                ShowProgress(session, "");
            }
        }
    }

    /// <summary>
    /// Types what is left. On release that is the whole dictation, cleaned in one pass so the model also sees
    /// self-corrections that span phrases; if cleanup fails or is too slow, the uncleaned text is typed.
    /// </summary>
    private async Task FinishSessionAsync(Session session, CancellationToken ct)
    {
        var rest = session.Pending + session.Assembler.Finish();
        if (session.Mode == TypeWhen.Released && rest.Trim().Length > 0)
        {
            var timeout = ReleaseCleanupBase + TimeSpan.FromMilliseconds(ReleaseCleanupMsPerChar * rest.Length);
            rest = await _curator.CleanAsync(rest, timeout, ct) ?? rest;
        }
        if (!session.Cancelled)
        {
            Type(rest, session);
        }
        if (session.Typed.Length > 0)
        {
            TranscriptCompleted?.Invoke(session.Typed.ToString());
        }
    }

    private string Filter(string text) => _settings().Cleanup == Cleanup.Off ? text : FillerFilter.Apply(text);

    private void Type(string typed, Session session)
    {
        if (typed.Length == 0)
        {
            return;
        }
        if (!_type(typed))
        {
            Log.Write("typing was rejected by Windows (elevated window?)");
            _view.ShowError("Can't type into this window");
        }
        session.Typed.Append(typed);
    }

    /// <summary>One hold. Audio fields belong to the pump; the text fields belong to the processor.</summary>
    private sealed class Session(int id, TypeWhen mode, IAudioSource microphone, PhraseSegmenter segmenter)
    {
        public int Id { get; } = id;
        /// <summary>Fixed at the start of the hold, so changing the setting mid-dictation can't split one.</summary>
        public TypeWhen Mode { get; } = mode;
        public IAudioSource Microphone { get; } = microphone;
        public PhraseSegmenter Segmenter { get; } = segmenter;
        public Task Pump { get; set; } = Task.CompletedTask;
        public volatile bool Ended;
        public volatile bool Cancelled;
        public int PendingPhrases;
        public int Phrases;
        public PhraseAssembler Assembler { get; } = new();
        /// <summary>Recognised but not yet typed (type-on-release mode).</summary>
        public volatile string Pending = "";
        public StringBuilder Typed { get; } = new();
    }

    private abstract record Work(Session Session);
    private sealed record PhraseWork(Session Session, float[] Context, float[] Samples) : Work(Session);
    private sealed record SessionEnded(Session Session, string? NoSpeech = null) : Work(Session);
}
