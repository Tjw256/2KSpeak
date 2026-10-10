using TwoKSpeak.Engine.Onnx;

namespace TwoKSpeak.Engine.Setup;

/// <param name="Url">Pinned to an immutable revision, tag or versioned archive.</param>
/// <param name="Sha256">Lower-case hex of the downloaded file, checked against the publisher's own checksum.</param>
/// <param name="Extract">For zip archives: file names to copy out of it (from any folder inside); null keeps the file as is.</param>
public sealed record Download(string Url, string Sha256, long Size, string FileName, IReadOnlyList<string>? Extract = null);

public enum ComponentId
{
    /// <summary>CPU speech model and the voice-activity model: enough to dictate.</summary>
    Speech,
    /// <summary>llama.cpp and Qwen3.5-2B.</summary>
    Cleanup,
    /// <summary>Qwen3.5-0.8B, only when chosen.</summary>
    CleanupSmall,
    /// <summary>The fp16 speech model, plus CUDA and cuDNN on NVIDIA.</summary>
    GpuMode,
}

public sealed record Component(ComponentId Id, string Title, IReadOnlyList<(string Directory, Download File)> Files)
{
    public long Size => Files.Sum(f => f.File.Size);
}

/// <summary>
/// Everything 2KSpeak downloads on first run, from the original publishers. Hashes were checked against Hugging Face
/// LFS metadata, NVIDIA's redistrib manifests and GitHub's release digests on 2026-10-10.
/// </summary>
public static class Components
{
    private const string Parakeet = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/8f23f0c03c8761650bdb5b40aaf3e40d2c15f1ce/";
    private const string ParakeetFp16 = "https://huggingface.co/jimmy927/parakeet-tdt-0.6b-v3-onnx-fp16/resolve/6477bf2008b729808e4f502f1b283a2a3d5ca1bb/";
    private const string Nvidia = "https://developer.download.nvidia.com/compute/";
    private const string Llama = "https://github.com/ggml-org/llama.cpp/releases/download/b11534/";

    private static Download ParakeetShared(string root, string name, string sha256, long size) => new(root + name, sha256, size, name);

    public static Component Speech { get; } = new(ComponentId.Speech, "Speech recognition",
    [
        (AppPaths.ParakeetInt8, new(Parakeet + "encoder-model.int8.onnx", "6139d2fa7e1b086097b277c7149725edbab89cc7c7ae64b23c741be4055aff09", 652183999, "encoder-model.int8.onnx")),
        (AppPaths.ParakeetInt8, new(Parakeet + "decoder_joint-model.int8.onnx", "eea7483ee3d1a30375daedc8ed83e3960c91b098812127a0d99d1c8977667a70", 18202004, "decoder_joint-model.int8.onnx")),
        (AppPaths.ParakeetInt8, ParakeetShared(Parakeet, "nemo128.onnx", "a9fde1486ebfcc08f328d75ad4610c67835fea58c73ba57e3209a6f6cf019e9f", 139764)),
        (AppPaths.ParakeetInt8, ParakeetShared(Parakeet, "vocab.txt", "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93939)),
        (AppPaths.ParakeetInt8, ParakeetShared(Parakeet, "config.json", "666903c76b9798caf2c210afd4f6cd60b08a8dbf9800ec8d7a3bc0d2148ac466", 97)),
        (Path.GetDirectoryName(AppPaths.SileroVad)!, new("https://github.com/snakers4/silero-vad/raw/bfdc0193023f121ea5b3cc7b176dbed570a68a59/src/silero_vad/data/silero_vad.onnx",
            "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3", 2327524, "silero_vad.onnx")),
    ]);

    private static readonly Download Qwen2B = new("https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/f6d5376be1edb4d416d56da11e5397a961aca8ae/Qwen3.5-2B-Q4_K_M.gguf",
        "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223", 1280835840, "Qwen3.5-2B-Q4_K_M.gguf");

    // The CUDA build also runs on the CPU when no CUDA runtime is present; it is only worth its size on GPU machines.
    private static readonly Download LlamaCuda = new(Llama + "llama-b11534-bin-win-cuda-13.4-x64.zip",
        "f3afa61156fb333952c61a3ce95e9264d9f1b04746dedb6d0edac162637123f0", 153459229, "llama-b11534-bin-win-cuda-13.4-x64.zip", ["*"]);
    private static readonly Download LlamaCpu = new(Llama + "llama-b11534-bin-win-cpu-x64.zip",
        "ebd5e25f58a6ccccc9c4a44e47bf6690ea68b00f4bcc48b25dc1a07b9cc03f2e", 19513525, "llama-b11534-bin-win-cpu-x64.zip", ["*"]);
    private static readonly Download LlamaVulkan = new(Llama + "llama-b11534-bin-win-vulkan-x64.zip",
        "9e6f267aa98fc17758dacb91454f809b7729aeb88a98a4a05d94393847cd0c8a", 33459008, "llama-b11534-bin-win-vulkan-x64.zip", ["*"]);

    /// <param name="gpu">The llama.cpp build for this PC's GPU (CUDA or Vulkan); the CPU build without one.</param>
    public static Component Cleanup(GpuBackend? gpu) => new(ComponentId.Cleanup, "Text cleanup",
    [
        (AppPaths.LlamaRuntime, gpu switch { GpuBackend.Cuda => LlamaCuda, GpuBackend.DirectML => LlamaVulkan, _ => LlamaCpu }),
        (AppPaths.CuratorModels, Qwen2B),
    ]);

    public static Component CleanupSmall { get; } = new(ComponentId.CleanupSmall, "Small text cleanup",
    [
        (AppPaths.CuratorModels, new("https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/6ab461498e2023f6e3c1baea90a8f0fe38ab64d0/Qwen3.5-0.8B-Q8_0.gguf",
            "0ad885ffd4bb022fc4f0d33a3308fa108ef8613159d3b3a67e23abca056b7a6c", 811843840, "Qwen3.5-0.8B-Q8_0.gguf")),
    ]);

    private static readonly Component CudaGpuMode = new(ComponentId.GpuMode, "GPU mode",
    [
        (AppPaths.CudaRuntime, new(Nvidia + "cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.4.92-archive.zip",
            "621a70c4778287b1abf8c27c61b841d797fda2aeff01033374dd19624bcfa249", 2737397, "cuda_cudart-13.4.92.zip", ["cudart64_13.dll"])),
        (AppPaths.CudaRuntime, new(Nvidia + "cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.8.0.4-archive.zip",
            "0974318e9861a61cb9091cf7de8d4c2880e9787fe311cb4bcbd08865663c5f43", 422415931, "libcublas-13.8.0.4.zip", ["cublas64_13.dll", "cublasLt64_13.dll"])),
        (AppPaths.CudaRuntime, new(Nvidia + "cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-12.4.0.43-archive.zip",
            "69d0ad8dc3a1be66f01a748a8206d0ceaafa56939474663fbe790dc3e91d2009", 159602693, "libcufft-12.4.0.43.zip", ["cufft64_12.dll"])),
        (AppPaths.CudaRuntime, new(Nvidia + "cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.27.0.42_cuda13-archive.zip",
            "15595c7b52896262a0e590f02b3b3851c6a2c9046a72047526deaf59a9cbf7d5", 1330803635, "cudnn-9.27.0.42_cuda13.zip", ["cudnn*.dll"])),
        (AppPaths.ParakeetFp16, new(ParakeetFp16 + "encoder-model.fp16.onnx", "1c5d0a4c16a37504b6fe91c54ab80b3b01a325ec7b1ad13bea2834104e146056", 1238962617, "encoder-model.fp16.onnx")),
        (AppPaths.ParakeetFp16, new(ParakeetFp16 + "decoder_joint-model.fp16.onnx", "b33a73b7c1d71b9d5a0911f5cb478be3dcbf79f53355c531ab1cd1dcd68ad8ef", 36266140, "decoder_joint-model.fp16.onnx")),
        (AppPaths.ParakeetFp16, ParakeetShared(ParakeetFp16, "nemo128.onnx", "a9fde1486ebfcc08f328d75ad4610c67835fea58c73ba57e3209a6f6cf019e9f", 139764)),
        (AppPaths.ParakeetFp16, ParakeetShared(ParakeetFp16, "vocab.txt", "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93939)),
        (AppPaths.ParakeetFp16, ParakeetShared(ParakeetFp16, "config.json", "666903c76b9798caf2c210afd4f6cd60b08a8dbf9800ec8d7a3bc0d2148ac466", 97)),
    ]);

    /// <summary>DirectML comes with the app's second worker, so AMD and Intel GPUs need only the fp16 model.</summary>
    public static Component GpuMode(GpuBackend backend) => backend == GpuBackend.Cuda
        ? CudaGpuMode
        : CudaGpuMode with { Files = CudaGpuMode.Files.Where(f => f.Directory != AppPaths.CudaRuntime).ToArray() };
}
