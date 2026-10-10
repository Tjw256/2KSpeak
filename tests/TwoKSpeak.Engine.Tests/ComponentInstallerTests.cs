using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using TwoKSpeak.Engine.Onnx;
using TwoKSpeak.Engine.Setup;

namespace TwoKSpeak.Engine.Tests;

public sealed class ComponentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "2kspeak-installer-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ComponentInstaller Installer() =>
        new(new HttpClient(_server), _root, Path.Combine(_root, "downloads")) { RetryDelay = TimeSpan.Zero };

    private Download Serve(string name, byte[] content, IReadOnlyList<string>? extract = null)
    {
        _server.Files[name] = content;
        return new Download($"https://example.test/{name}", Convert.ToHexStringLower(SHA256.HashData(content)), content.Length, name, extract);
    }

    private Component Single(Download file) => new(ComponentId.Speech, "Speech model", [(Path.Combine(_root, "models"), file)]);

    private static byte[] Bytes(int count, int seed = 1) => Enumerable.Range(0, count).Select(i => (byte)(i * seed)).ToArray();

    private static async Task<List<InstallProgress>> Run(ComponentInstaller installer, Component component)
    {
        var reports = new List<InstallProgress>();
        await installer.InstallAsync(component, new SyncProgress(reports), CancellationToken.None);
        return reports;
    }

    [Fact]
    public async Task InstallsVerifiesAndMarks()
    {
        var component = Single(Serve("model.onnx", Bytes(300_000)));
        var installer = Installer();
        Assert.False(installer.IsInstalled(component));

        var reports = await Run(installer, component);

        Assert.Equal(Bytes(300_000), File.ReadAllBytes(Path.Combine(_root, "models", "model.onnx")));
        Assert.True(installer.IsInstalled(component));
        Assert.Equal(InstallStage.Done, reports[^1].Stage);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "downloads")));
    }

    [Fact]
    public async Task ResumesAfterTheConnectionDrops()
    {
        var component = Single(Serve("big.bin", Bytes(3_000_000, 7)));
        _server.FailAfterBytes = 1_000_000; // first response breaks a third of the way in

        var reports = await Run(Installer(), component);

        Assert.Equal(Bytes(3_000_000, 7), File.ReadAllBytes(Path.Combine(_root, "models", "big.bin")));
        Assert.Contains(reports, r => r.Stage == InstallStage.WaitingForNetwork);
        Assert.Equal(1_000_000, _server.RangeStarts.Single(s => s > 0)); // the second request continued where the first stopped
    }

    [Fact]
    public async Task RedownloadsADamagedFileOnce()
    {
        var component = Single(Serve("model.onnx", Bytes(50_000)));
        _server.CorruptNextResponse = true;

        await Run(Installer(), component);

        Assert.Equal(Bytes(50_000), File.ReadAllBytes(Path.Combine(_root, "models", "model.onnx")));
        Assert.Equal(2, _server.Requests);
    }

    [Fact]
    public async Task KeepsAFileThatIsAlreadyThereWithTheRightHash()
    {
        var component = Single(Serve("model.onnx", Bytes(50_000)));
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        File.WriteAllBytes(Path.Combine(_root, "models", "model.onnx"), Bytes(50_000));

        await Run(Installer(), component);

        Assert.Equal(0, _server.Requests);
    }

    [Fact]
    public async Task ExtractsOnlyTheRequestedFilesFromAnArchive()
    {
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "pkg/bin/x64/cudnn64_9.dll", "pkg/bin/x64/cudnn_ops64_9.dll", "pkg/include/cudnn.h", "pkg/LICENSE" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        var component = Single(Serve("cudnn.zip", zipped.ToArray(), ["cudnn*.dll"]));

        await Run(Installer(), component);

        Assert.Equal(["cudnn64_9.dll", "cudnn_ops64_9.dll"], Directory.GetFiles(Path.Combine(_root, "models")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(Path.Combine(_root, "downloads", "cudnn.zip")));
    }

    [Fact]
    public async Task AChangedManifestMeansNotInstalled()
    {
        var installer = Installer();
        await Run(installer, Single(Serve("model.onnx", Bytes(1000))));

        Assert.False(installer.IsInstalled(Single(Serve("model.onnx", Bytes(1000, 3)))));
    }

    [Fact]
    public async Task GivesUpWhenTheFileIsGone()
    {
        var component = Single(Serve("model.onnx", Bytes(1000)));
        _server.Files.Clear();

        await Assert.ThrowsAsync<InstallException>(() => Run(Installer(), component));
    }

    [Fact]
    public void ManifestEntriesArePinnedAndConsistent()
    {
        var all = new[]
        {
            Components.Speech, Components.Cleanup(GpuBackend.Cuda), Components.Cleanup(GpuBackend.DirectML), Components.Cleanup(null),
            Components.CleanupSmall, Components.GpuMode(GpuBackend.Cuda), Components.GpuMode(GpuBackend.DirectML),
        };
        foreach (var (_, file) in all.SelectMany(c => c.Files))
        {
            Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
            Assert.StartsWith("https://", file.Url);
            Assert.DoesNotContain("/main/", file.Url); // revisions, tags or versioned archives only
            Assert.True(file.Size > 0);
        }
    }

    private sealed class SyncProgress(List<InstallProgress> reports) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => reports.Add(value);
    }

    /// <summary>In-memory HTTP server with Range support and injectable failures.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public long? FailAfterBytes { get; set; }
        public bool CorruptNextResponse { get; set; }
        public int Requests { get; private set; }
        public List<long> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (!Files.TryGetValue(request.RequestUri!.Segments[^1], out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var start = request.Headers.Range?.Ranges.First().From ?? 0;
            RangeStarts.Add(start);
            var body = content[(int)start..];
            if (CorruptNextResponse)
            {
                CorruptNextResponse = false;
                body = body.ToArray();
                body[0] ^= 0xFF;
            }
            Stream stream = new MemoryStream(body);
            if (FailAfterBytes is { } limit)
            {
                FailAfterBytes = null;
                stream = new BreakingStream(stream, limit);
            }
            var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
            return Task.FromResult(response);
        }
    }

    private sealed class BreakingStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= limit)
            {
                throw new IOException("connection reset");
            }
            var read = inner.Read(buffer, offset, (int)Math.Min(count, limit - _read));
            _read += read;
            return read;
        }
    }
}
