using System.Threading.Channels;
using TwoKSpeak.Engine.Audio;
using TwoKSpeak.Engine.Vad;

namespace TwoKSpeak.App.Audio;

/// <summary>
/// Development stand-in for the microphone (set TWOKSPEAK_TEST_AUDIO to a 16 kHz mono WAV): plays the file at
/// real-time pace, then silence until stopped, so the whole hotkey-to-typing path can be exercised by a script.
/// </summary>
public sealed class WavAudioSource(string path) : IAudioSource
{
    private readonly Channel<float[]> _windows = Channel.CreateUnbounded<float[]>();
    private readonly CancellationTokenSource _stop = new();

    public ChannelReader<float[]> Windows => _windows.Reader;

    public void Start()
    {
        var samples = WavFile.ReadMono16(path, out _);
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(32));
            var offset = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    var window = new float[SileroVad.WindowSamples];
                    if (offset < samples.Length)
                    {
                        samples.AsSpan(offset, Math.Min(window.Length, samples.Length - offset)).CopyTo(window);
                        offset += window.Length;
                    }
                    _windows.Writer.TryWrite(window);
                }
            }
            catch (OperationCanceledException)
            {
            }
            _windows.Writer.TryComplete();
        });
    }

    public void Stop() => _stop.Cancel();

    public void Dispose() => _stop.Cancel();
}
