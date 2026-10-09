using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace TwoKSpeak.Engine.Ipc;

public enum WorkerRequestKind : byte
{
    /// <summary>Wait until the model is loaded and warmed up; replies with a description of the device.</summary>
    Ready = 1,
    Transcribe = 2,
}

public abstract record WorkerRequest;
public sealed record ReadyRequest : WorkerRequest;
public sealed record TranscribeRequest(float[] Context, float[] Samples) : WorkerRequest;

public abstract record WorkerResponse;
public sealed record ReadyResponse(string Description) : WorkerResponse;
public sealed record TranscribeResponse(string Text, string? SeamPunctuation, double ElapsedMs) : WorkerResponse;
public sealed record ErrorResponse(string Message) : WorkerResponse;

/// <summary>
/// Framing between the tray app and the inference worker over a named pipe:
/// every message is a 4-byte little-endian length followed by that many bytes of payload.
/// </summary>
public static class WorkerProtocol
{
    private const int MaxFrameBytes = 256 * 1024 * 1024;
    private const byte ResponseOk = 0;
    private const byte ResponseError = 1;

    public static async Task WriteRequestAsync(Stream stream, WorkerRequest request, CancellationToken ct)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            switch (request)
            {
                case ReadyRequest:
                    writer.Write((byte)WorkerRequestKind.Ready);
                    break;
                case TranscribeRequest transcribe:
                    writer.Write((byte)WorkerRequestKind.Transcribe);
                    WriteFloats(writer, transcribe.Context);
                    WriteFloats(writer, transcribe.Samples);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request), request, null);
            }
        }
        await WriteFrameAsync(stream, payload.ToArray(), ct);
    }

    public static async Task<WorkerRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var frame = await ReadFrameAsync(stream, ct);
        if (frame is null)
        {
            return null;
        }
        using var reader = new BinaryReader(new MemoryStream(frame), Encoding.UTF8);
        return (WorkerRequestKind)reader.ReadByte() switch
        {
            WorkerRequestKind.Ready => new ReadyRequest(),
            WorkerRequestKind.Transcribe => new TranscribeRequest(ReadFloats(reader), ReadFloats(reader)),
            var kind => throw new InvalidDataException($"Unknown request kind {kind}."),
        };
    }

    public static async Task WriteResponseAsync(Stream stream, WorkerResponse response, CancellationToken ct)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            switch (response)
            {
                case ErrorResponse error:
                    writer.Write(ResponseError);
                    writer.Write(error.Message);
                    break;
                case ReadyResponse ready:
                    writer.Write(ResponseOk);
                    writer.Write(ready.Description);
                    break;
                case TranscribeResponse transcribe:
                    writer.Write(ResponseOk);
                    writer.Write(transcribe.Text);
                    writer.Write(transcribe.SeamPunctuation is not null);
                    writer.Write(transcribe.SeamPunctuation ?? "");
                    writer.Write(transcribe.ElapsedMs);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(response), response, null);
            }
        }
        await WriteFrameAsync(stream, payload.ToArray(), ct);
    }

    public static async Task<WorkerResponse> ReadResponseAsync(Stream stream, WorkerRequestKind expected, CancellationToken ct)
    {
        var frame = await ReadFrameAsync(stream, ct) ?? throw new EndOfStreamException("Worker closed the connection.");
        using var reader = new BinaryReader(new MemoryStream(frame), Encoding.UTF8);
        if (reader.ReadByte() == ResponseError)
        {
            return new ErrorResponse(reader.ReadString());
        }
        return expected switch
        {
            WorkerRequestKind.Ready => new ReadyResponse(reader.ReadString()),
            WorkerRequestKind.Transcribe => ReadTranscribe(reader),
            _ => throw new ArgumentOutOfRangeException(nameof(expected), expected, null),
        };

        static TranscribeResponse ReadTranscribe(BinaryReader reader)
        {
            var text = reader.ReadString();
            var hasSeam = reader.ReadBoolean();
            var seam = reader.ReadString();
            return new TranscribeResponse(text, hasSeam ? seam : null, reader.ReadDouble());
        }
    }

    private static void WriteFloats(BinaryWriter writer, float[] values)
    {
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static float[] ReadFloats(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var values = new float[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <returns>The payload, or null if the stream ended cleanly before a new frame.</returns>
    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (read == 0)
        {
            return null;
        }
        if (read < header.Length)
        {
            throw new EndOfStreamException("Truncated frame header.");
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException($"Frame length {length} is out of range.");
        }
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }
}
