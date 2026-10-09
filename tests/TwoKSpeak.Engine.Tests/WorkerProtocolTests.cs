using TwoKSpeak.Engine.Ipc;

namespace TwoKSpeak.Engine.Tests;

public class WorkerProtocolTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task TranscribeRequestRoundTrips()
    {
        using var stream = new MemoryStream();
        await WorkerProtocol.WriteRequestAsync(stream, new TranscribeRequest([0.5f, -1f], [0.25f, 0f, 1f]), Ct);
        stream.Position = 0;

        var request = Assert.IsType<TranscribeRequest>(await WorkerProtocol.ReadRequestAsync(stream, Ct));

        Assert.Equal([0.5f, -1f], request.Context);
        Assert.Equal([0.25f, 0f, 1f], request.Samples);
        Assert.Null(await WorkerProtocol.ReadRequestAsync(stream, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(",")]
    public async Task TranscribeResponseKeepsSeamDistinctions(string? seam)
    {
        using var stream = new MemoryStream();
        await WorkerProtocol.WriteResponseAsync(stream, new TranscribeResponse("Žluťoučký kůň", seam, 12.5), Ct);
        stream.Position = 0;

        var response = Assert.IsType<TranscribeResponse>(
            await WorkerProtocol.ReadResponseAsync(stream, WorkerRequestKind.Transcribe, Ct));

        Assert.Equal(new TranscribeResponse("Žluťoučký kůň", seam, 12.5), response);
    }

    [Fact]
    public async Task ErrorsAreReadForAnyExpectedKind()
    {
        using var stream = new MemoryStream();
        await WorkerProtocol.WriteResponseAsync(stream, new ErrorResponse("CUDA failed"), Ct);
        stream.Position = 0;

        var response = await WorkerProtocol.ReadResponseAsync(stream, WorkerRequestKind.Ready, Ct);

        Assert.Equal(new ErrorResponse("CUDA failed"), response);
    }

    [Fact]
    public async Task RejectsOversizedFrames()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);

        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerProtocol.ReadRequestAsync(stream, Ct));
    }
}
