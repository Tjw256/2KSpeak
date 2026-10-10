using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.Engine.Curator;

public sealed class CuratorException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One llama-server process serving the curator model on loopback. A random port and a per-launch API key keep
/// other local programs from using it; a kill-on-close job ends it with 2KSpeak.
/// </summary>
public sealed class LlamaServer : IAsyncDisposable
{
    private const int ContextTokens = 2048;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(90);

    private readonly Process _process;
    private readonly KillOnCloseJob _job;
    private readonly HttpClient _http;
    private readonly Queue<string> _recentOutput = new();

    private LlamaServer(Process process, KillOnCloseJob job, HttpClient http)
    {
        _process = process;
        _job = job;
        _http = http;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    /// <param name="gpu">Offload all layers to this GPU; null runs on the CPU.</param>
    public static async Task<LlamaServer> StartAsync(string runtimeDirectory, string modelPath, GpuChoice? gpu, CancellationToken ct)
    {
        var exe = Path.Combine(runtimeDirectory, "llama-server.exe");
        if (!File.Exists(exe))
        {
            throw new CuratorException($"llama-server not found in {runtimeDirectory}.");
        }
        if (!File.Exists(modelPath))
        {
            throw new CuratorException($"Curator model not found: {Path.GetFileName(modelPath)}.");
        }

        var port = FreeLoopbackPort();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var start = new ProcessStartInfo(exe)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = runtimeDirectory,
            ArgumentList =
            {
                "-m", modelPath,
                "-ngl", gpu is not null ? "99" : "0",
                "-c", ContextTokens.ToString(),
                "-t", Math.Max(1, Environment.ProcessorCount / 2).ToString(), // physical cores; SMT threads don't help
                "-np", "1",
                // Load the weights fully instead of memory-mapping the file (b11534's replacement for --no-mmap).
                "--load-mode", "none",
                "--host", "127.0.0.1",
                "--port", port.ToString(),
                "--api-key", key,
            },
        };
        if (gpu is null)
        {
            // GPU builds would otherwise still use the GPU for parts of the prompt.
            start.ArgumentList.Add("--device");
            start.ArgumentList.Add("none");
        }
        else if (gpu.Backend == GpuBackend.Cuda)
        {
            // The CUDA build of llama.cpp loads cudart and cuBLAS from the GPU mode download, shared with the worker.
            start.Environment["PATH"] = AppPaths.CudaRuntime + ";" + Environment.GetEnvironmentVariable("PATH");
        }
        else
        {
            // llama.cpp numbers Vulkan devices its own way and would split the model across all of them, an integrated
            // GPU included; pin it to the worker's adapter (matched by name) unless overridden.
            var device = Environment.GetEnvironmentVariable("TWOKSPEAK_VULKAN_DEVICE") is { Length: > 0 } forced
                ? forced
                : await VulkanDeviceAsync(exe, gpu.Adapter.Name, ct);
            if (device is not null)
            {
                start.ArgumentList.Add("--device");
                start.ArgumentList.Add(device);
            }
        }

        var job = new KillOnCloseJob();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CuratorException("Could not start llama-server.");
            job.Add(process);
        }
        catch
        {
            job.Dispose();
            throw;
        }

        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var server = new LlamaServer(process, job, http);
        process.OutputDataReceived += (_, e) => server.Remember(e.Data);
        process.ErrorDataReceived += (_, e) => server.Remember(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await server.WaitUntilHealthyAsync(ct);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>Sends a chat completion body (see <see cref="CuratorPrompt.Build"/>) and returns the answer text.</summary>
    public async Task<string> CompleteAsync(string body, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("v1/chat/completions", content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new CuratorException($"llama-server returned {(int)response.StatusCode}.");
            }
            return CuratorPrompt.ParseResponse(json);
        }
        catch (HttpRequestException ex)
        {
            throw new CuratorException($"llama-server request failed: {ex.Message}", ex);
        }
    }

    private async Task WaitUntilHealthyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoadTimeout);
        while (true)
        {
            if (_process.HasExited)
            {
                throw new CuratorException($"llama-server exited with code {_process.ExitCode}: {RecentOutput()}");
            }
            try
            {
                using var response = await _http.GetAsync("health", timeout.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CuratorException($"llama-server did not become ready within {LoadTimeout.TotalSeconds:F0} s.");
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    /// <summary>The Vulkan device (e.g. "Vulkan1") whose name matches the DXGI adapter, from llama-server --list-devices.</summary>
    private static async Task<string?> VulkanDeviceAsync(string exe, string adapterName, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe, "--list-devices")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var process = Process.Start(start) ?? throw new CuratorException("Could not start llama-server.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await stderr;
        await process.WaitForExitAsync(ct);
        return ParseVulkanDevice(output, adapterName);
    }

    /// <summary>Lines look like "  Vulkan1: AMD Radeon(TM) Graphics (31881 MiB, 30287 MiB free)".</summary>
    internal static string? ParseVulkanDevice(string listing, string adapterName) =>
        listing.Split('\n').Select(line => Regex.Match(line, @"^\s*(Vulkan\d+): (.+) \(\d+ MiB"))
            .Where(m => m.Success && m.Groups[2].Value.Trim() == adapterName.Trim())
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault();

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            if (_recentOutput.Count > 8)
            {
                _recentOutput.Dequeue();
            }
        }
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
        {
            return string.Join(" | ", _recentOutput);
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or stuck: closing the job below terminates it either way.
        }
        _job.Dispose();
        _process.Dispose();
    }
}
