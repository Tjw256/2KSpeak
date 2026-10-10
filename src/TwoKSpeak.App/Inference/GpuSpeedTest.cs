using System.Diagnostics;
using System.Globalization;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.App.Inference;

/// <summary>
/// Times speech recognition on an integrated GPU and on the CPU, each in its own worker, on the same phrase. A strong
/// integrated GPU (Radeon 780M class) should beat the CPU; a weak one (the 2-core graphics of desktop Ryzens) is about
/// 4x slower, and nothing Windows reports tells the two apart.
/// </summary>
public static class GpuSpeedTest
{
    /// <summary>The GPU must be clearly faster: on a laptop it shares the processor's power budget.</summary>
    public const double Margin = 0.8;

    private const int Runs = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <returns>Median milliseconds per device; null where the worker failed (a GPU that can't run the model).</returns>
    public static async Task<(double? GpuMs, double? CpuMs)> RunAsync(GpuChoice gpu)
    {
        var gpuMs = await TimeAsync(gpu, RecognitionDevice.Gpu);
        var cpuMs = await TimeAsync(gpu, RecognitionDevice.Cpu);
        return (gpuMs, cpuMs);
    }

    private static async Task<double?> TimeAsync(GpuChoice gpu, RecognitionDevice device)
    {
        var start = new ProcessStartInfo(WorkerClient.ExePath(gpu, device))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "--benchmark", Runs.ToString(CultureInfo.InvariantCulture),
                "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--device", WorkerClient.DeviceArgument(gpu, device),
            },
        };
        using var process = Process.Start(start);
        if (process is null)
        {
            Log.Write($"speed test on {device}: worker did not start");
            return null;
        }
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0 && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            {
                return ms;
            }
            Log.Write($"speed test on {device}: worker exited with {process.ExitCode} (details in worker.log)");
            return null;
        }
        catch (OperationCanceledException)
        {
            Log.Write($"speed test on {device}: no result within {Timeout.TotalMinutes:0} minutes");
            process.Kill();
            return null;
        }
    }
}
