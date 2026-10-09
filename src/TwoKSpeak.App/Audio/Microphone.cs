using System.Threading.Channels;
using NAudio.Wave;
using TwoKSpeak.Engine.Vad;

namespace TwoKSpeak.App.Audio;

/// <summary>
/// Captures 16 kHz mono from a microphone (Windows resamples from the device format) and delivers fixed
/// 512-sample windows for the VAD. Opened only while recording, so the privacy indicator is honest.
/// </summary>
public sealed class Microphone : IAudioSource
{
    private readonly WaveIn _waveIn;
    private readonly Channel<float[]> _windows = Channel.CreateUnbounded<float[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly float[] _pending = new float[SileroVad.WindowSamples];
    private int _pendingCount;

    /// <param name="deviceNumber">WinMM device index; -1 is the system default microphone.</param>
    public Microphone(int deviceNumber)
    {
        _waveIn = new WaveIn
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 32,
            NumberOfBuffers = 4,
        };
        _waveIn.DataAvailable += OnData;
        _waveIn.RecordingStopped += (_, e) => _windows.Writer.TryComplete(e.Exception);
    }

    public ChannelReader<float[]> Windows => _windows.Reader;

    public static IReadOnlyList<string> DeviceNames() =>
        Enumerable.Range(0, WaveIn.DeviceCount).Select(i => WaveIn.GetCapabilities(i).ProductName).ToList();

    public void Start() => _waveIn.StartRecording();

    public void Stop() => _waveIn.StopRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            _pending[_pendingCount++] = BitConverter.ToInt16(e.Buffer, i) / 32768f;
            if (_pendingCount == _pending.Length)
            {
                _windows.Writer.TryWrite((float[])_pending.Clone());
                _pendingCount = 0;
            }
        }
    }

    public void Dispose()
    {
        _waveIn.Dispose();
        _windows.Writer.TryComplete();
    }
}
