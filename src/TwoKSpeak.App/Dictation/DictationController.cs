using System.Text;
using System.Threading.Channels;
using TwoKSpeak.App.Audio;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Inference;
using TwoKSpeak.App.Input;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Dictation;
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
/// Runs dictation sessions: one per Ctrl+Win hold. Audio is segmented into phrases as it arrives; phrases are
/// transcribed and typed strictly in order by a single processor, so a new hold can start recording while the
/// previous one is still finishing without their text interleaving.
/// </summary>
public sealed class DictationController
{
    private readonly Func<AppSettings> _settings;
    private readonly WorkerClient _worker;
    private readonly SileroVad _vad;
    private readonly IDictationView _view;
    private readonly Func<AppSettings, IAudioSource> _audioSource;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private Session? _current;
    private int _nextSessionId;

    public DictationController(Func<AppSettings> settings, WorkerClient worker, SileroVad vad, IDictationView view,
        Func<AppSettings, IAudioSource> audioSource)
    {
        _audioSource = audioSource;
        _settings = settings;
        _worker = worker;
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
        _vad.Reset();
        var session = new Session(++_nextSessionId, microphone, new PhraseSegmenter(new SegmenterSettings { PauseMs = settings.PauseMs }));
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
        session.Microphone.Stop();
        _work.Writer.TryWrite(new SessionCancelled(session.Id));
        _view.Hide();
    }

    private async Task PumpAsync(Session session)
    {
        var windows = 0;
        var peak = 0f;
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
            _work.Writer.TryWrite(new SessionEnded(session.Id));
            session.Microphone.Dispose();
            Log.Write($"session {session.Id} audio: {windows * 0.032:F1} s, peak speech probability {peak:F2}, {session.Phrases} phrase(s)");
        }
    }

    private void Dispatch(Session session, SegmenterEvent e)
    {
        switch (e)
        {
            case PhraseCompleted phrase:
                session.Phrases++;
                Interlocked.Increment(ref session.PendingPhrases);
                _work.Writer.TryWrite(new PhraseWork(session.Id, phrase.Context, phrase.Samples));
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
                _view.ShowPreview(Clean(result.Text));
            }
        }
        catch (WorkerException)
        {
            // Previews are best-effort; the committed phrase reports real failures.
        }
    }

    /// <summary>Transcribes and types phrases in arrival order, one at a time.</summary>
    private async Task ProcessAsync(CancellationToken ct)
    {
        var assemblers = new Dictionary<int, (PhraseAssembler Assembler, StringBuilder Text)>();
        var cancelled = new HashSet<int>();

        await foreach (var work in _work.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessOneAsync(work, assemblers, cancelled, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad phrase must not stop every later dictation.
                Log.Write($"dictation work failed: {ex}");
                _view.ShowError("Dictation failed");
            }
        }
    }

    private async Task ProcessOneAsync(Work work, Dictionary<int, (PhraseAssembler Assembler, StringBuilder Text)> assemblers,
        HashSet<int> cancelled, CancellationToken ct)
    {
        if (work is SessionCancelled c)
        {
            cancelled.Add(c.SessionId);
            assemblers.Remove(c.SessionId);
            return;
        }
        if (cancelled.Contains(work.SessionId))
        {
            if (work is SessionEnded)
            {
                cancelled.Remove(work.SessionId);
            }
            return;
        }

        if (!assemblers.TryGetValue(work.SessionId, out var state))
        {
            state = (new PhraseAssembler(), new StringBuilder());
            assemblers[work.SessionId] = state;
        }

        switch (work)
        {
            case PhraseWork phrase:
                await TranscribeAndTypeAsync(phrase, state.Assembler, state.Text, ct);
                if (_current is { } session && session.Id == phrase.SessionId)
                {
                    Interlocked.Decrement(ref session.PendingPhrases);
                }
                break;
            case SessionEnded:
                Type(state.Assembler.Finish(), state.Text);
                assemblers.Remove(work.SessionId);
                if (state.Text.Length > 0)
                {
                    TranscriptCompleted?.Invoke(state.Text.ToString());
                }
                if (_current is { Ended: true } current && current.Id == work.SessionId)
                {
                    _view.Hide();
                }
                break;
        }
    }

    private async Task TranscribeAndTypeAsync(PhraseWork phrase, PhraseAssembler assembler, StringBuilder text, CancellationToken ct)
    {
        try
        {
            var result = await _worker.TranscribeAsync(phrase.Context, phrase.Samples, ct);
            Log.Write($"phrase {phrase.Samples.Length / 16000.0:F1} s transcribed in {result.ElapsedMs:F0} ms");
            Type(assembler.Add(Clean(result.Text), result.SeamPunctuation), text);
        }
        catch (WorkerException ex)
        {
            Log.Write($"transcription failed: {ex.Message}");
            _view.ShowError(ex.Message);
        }
    }

    private string Clean(string text) => _settings().RemoveFillers ? FillerFilter.Apply(text) : text;

    private void Type(string typed, StringBuilder text)
    {
        if (typed.Length == 0)
        {
            return;
        }
        if (!TextTyper.Type(typed))
        {
            Log.Write("typing was rejected by Windows (elevated window?)");
            _view.ShowError("Can't type into this window");
        }
        text.Append(typed);
    }

    private sealed class Session(int id, IAudioSource microphone, PhraseSegmenter segmenter)
    {
        public int Id { get; } = id;
        public IAudioSource Microphone { get; } = microphone;
        public PhraseSegmenter Segmenter { get; } = segmenter;
        public Task Pump { get; set; } = Task.CompletedTask;
        public volatile bool Ended;
        public volatile bool Cancelled;
        public int PendingPhrases;
        public int Phrases;
    }

    private abstract record Work(int SessionId);
    private sealed record PhraseWork(int SessionId, float[] Context, float[] Samples) : Work(SessionId);
    private sealed record SessionEnded(int SessionId) : Work(SessionId);
    private sealed record SessionCancelled(int SessionId) : Work(SessionId);
}
