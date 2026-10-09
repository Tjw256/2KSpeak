using System.Threading.Channels;

namespace TwoKSpeak.App.Audio;

/// <summary>16 kHz mono audio delivered as 512-sample windows; the channel completes after <see cref="Stop"/>.</summary>
public interface IAudioSource : IDisposable
{
    ChannelReader<float[]> Windows { get; }
    void Start();
    void Stop();
}
