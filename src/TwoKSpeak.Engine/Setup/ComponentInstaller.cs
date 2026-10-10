using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TwoKSpeak.Engine.Setup;

public enum InstallStage
{
    Downloading,
    /// <summary>No connection or the server is failing; retrying with the partial download kept.</summary>
    WaitingForNetwork,
    Verifying,
    Extracting,
    Done,
}

public sealed record InstallProgress(ComponentId Id, InstallStage Stage, long Done, long Total);

/// <summary>A failure the user has to act on (disk full, damaged or vanished download); network outages are retried instead.</summary>
public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads, verifies and unpacks components. Downloads resume after an interruption, every file is checked against
/// its pinned SHA-256 before use, and files already present with the right hash are kept (a re-install or a manual
/// copy costs nothing). A marker records what was installed, so startup checks are cheap.
/// </summary>
public sealed class ComponentInstaller(HttpClient http, string root, string downloads)
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private const int BufferSize = 1 << 20;

    /// <summary>The default locations, with a client that follows redirects and identifies the app.</summary>
    public static ComponentInstaller CreateDefault(string version)
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("2KSpeak", version));
        return new ComponentInstaller(client, AppPaths.LocalRoot, AppPaths.Downloads);
    }

    /// <summary>Delay before retrying after a network failure; tests shorten it.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    private string MarkerPath(ComponentId id) => Path.Combine(root, "installed", $"{id}.json");

    public bool IsInstalled(Component component)
    {
        try
        {
            var path = MarkerPath(component.Id);
            if (!File.Exists(path))
            {
                return false;
            }
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));
            return marker is not null
                && marker.Hashes.Order().SequenceEqual(component.Files.Select(f => f.File.Sha256).Order())
                && marker.Files.All(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task InstallAsync(Component component, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var total = component.Size;
        long finished = 0;
        var outputs = new List<string>();
        EnsureFreeSpace(component);
        Directory.CreateDirectory(downloads);

        foreach (var (directory, file) in component.Files)
        {
            Directory.CreateDirectory(directory);
            if (file.Extract is null)
            {
                var target = Path.Combine(directory, file.FileName);
                progress.Report(new(component.Id, InstallStage.Verifying, finished, total));
                if (!await HasHashAsync(target, file, ct))
                {
                    var part = Path.Combine(downloads, file.FileName + ".part");
                    await DownloadVerifiedAsync(component.Id, file, part, finished, total, progress, ct);
                    File.Move(part, target, overwrite: true);
                }
                outputs.Add(target);
            }
            else
            {
                var archive = Path.Combine(downloads, file.FileName);
                await DownloadVerifiedAsync(component.Id, file, archive, finished, total, progress, ct);
                progress.Report(new(component.Id, InstallStage.Extracting, finished + file.Size, total));
                outputs.AddRange(Extract(archive, directory, file.Extract));
                File.Delete(archive);
            }
            finished += file.Size;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath(component.Id))!);
        var marker = new Marker(component.Files.Select(f => f.File.Sha256).ToList(), outputs);
        await File.WriteAllTextAsync(MarkerPath(component.Id), JsonSerializer.Serialize(marker), ct);
        progress.Report(new(component.Id, InstallStage.Done, total, total));
    }

    private void EnsureFreeSpace(Component component)
    {
        // Archives need room for themselves and their unpacked contents until they are deleted. Plain files already in
        // place at the right size are most likely kept, so they don't count.
        var needed = component.Files.Sum(f =>
            f.File.Extract is not null ? f.File.Size * 2
            : File.Exists(Path.Combine(f.Directory, f.File.FileName)) && new FileInfo(Path.Combine(f.Directory, f.File.FileName)).Length == f.File.Size ? 0
            : f.File.Size);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        if (drive.IsReady && drive.AvailableFreeSpace < needed)
        {
            throw new InstallException($"Not enough free space on {drive.Name.TrimEnd('\\')}. {component.Title} needs {Gb(needed)}, " +
                $"{Gb(drive.AvailableFreeSpace)} is free.");
        }
    }

    private static string Gb(long bytes) => $"{bytes / 1e9:0.0} GB";

    /// <summary>Downloads to <paramref name="path"/> (resuming a partial file) until it matches the pinned hash.</summary>
    private async Task DownloadVerifiedAsync(ComponentId id, Download file, string path, long before, long total,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await DownloadAsync(id, file, path, before, total, progress, ct);
            progress.Report(new(id, InstallStage.Verifying, before + file.Size, total));
            if (await HasHashAsync(path, file, ct))
            {
                return;
            }
            File.Delete(path);
            if (attempt == 2)
            {
                throw new InstallException($"{file.FileName} arrived damaged twice.");
            }
        }
    }

    private async Task DownloadAsync(ComponentId id, Download file, string path, long before, long total,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var refusals = 0;
        while (true)
        {
            var have = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (have > file.Size)
            {
                File.Delete(path);
                have = 0;
            }
            if (have == file.Size)
            {
                return;
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
                if (have > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(have, null);
                }
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                {
                    if (++refusals >= 3)
                    {
                        throw new InstallException($"{file.FileName} is no longer available from its publisher ({(int)response.StatusCode}).");
                    }
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                }
                response.EnsureSuccessStatusCode();
                // A server that ignores the range sends the whole file again.
                var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                await using var target = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await CopyAsync(source, target, id, before + (append ? have : 0), total, progress, ct);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new InstallException($"The disk filled up while downloading {file.FileName}. Free some space and try again.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException
                || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                progress.Report(new(id, InstallStage.WaitingForNetwork, before + (File.Exists(path) ? new FileInfo(path).Length : 0), total));
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    /// <summary>ERROR_HANDLE_DISK_FULL or ERROR_DISK_FULL: retrying would never succeed, unlike a lost connection.</summary>
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 39 or 112;

    private static async Task CopyAsync(Stream source, Stream target, ComponentId id, long done, long total,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        var lastReport = DateTime.MinValue;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallTimeout); // a connection that stops sending counts as lost
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0)
            {
                return;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (DateTime.UtcNow - lastReport >= ProgressInterval)
            {
                lastReport = DateTime.UtcNow;
                progress.Report(new(id, InstallStage.Downloading, done, total));
            }
        }
    }

    private static async Task<bool> HasHashAsync(string path, Download file, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
        {
            return false;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        return hash == file.Sha256;
    }

    /// <summary>Copies matching entries (by file name, wildcards allowed) flat into <paramref name="directory"/>.</summary>
    private static List<string> Extract(string archive, string directory, IReadOnlyList<string> patterns)
    {
        var matchers = patterns.Select(p => new Regex("^" + Regex.Escape(p).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase)).ToList();
        var written = new List<string>();
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0 || !matchers.Any(m => m.IsMatch(entry.Name)))
            {
                continue;
            }
            var target = Path.Combine(directory, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            written.Add(target);
        }
        if (written.Count == 0)
        {
            throw new InstallException($"{Path.GetFileName(archive)} did not contain the expected files.");
        }
        return written;
    }

    private sealed record Marker(List<string> Hashes, List<string> Files);
}
