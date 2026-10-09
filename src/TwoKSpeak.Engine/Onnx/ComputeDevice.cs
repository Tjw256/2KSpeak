using Microsoft.ML.OnnxRuntime;

namespace TwoKSpeak.Engine.Onnx;

public enum ComputeDevice
{
    Cpu,
    Cuda,
    DirectML,
}

public static class OnnxSessionFactory
{
    /// <summary>
    /// Builds session options for the requested device. The native ONNX Runtime package
    /// shipped next to the executable decides which providers actually exist; asking for
    /// one that is missing throws here rather than silently falling back to CPU.
    /// </summary>
    public static SessionOptions CreateOptions(ComputeDevice device)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        switch (device)
        {
            case ComputeDevice.Cpu:
                options.IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2);
                break;
            case ComputeDevice.Cuda:
                using (var cuda = new OrtCUDAProviderOptions())
                {
                    cuda.UpdateOptions(new Dictionary<string, string>
                    {
                        ["device_id"] = "0",
                        // Grow the arena only by what is requested so VRAM stays close to the model size.
                        ["arena_extend_strategy"] = "kSameAsRequested",
                        // Exhaustive search costs seconds on every cold start; heuristic is near-identical here.
                        ["cudnn_conv_algo_search"] = "HEURISTIC",
                    });
                    options.AppendExecutionProvider_CUDA(cuda);
                }
                break;
            case ComputeDevice.DirectML:
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(device), device, null);
        }

        return options;
    }
}
