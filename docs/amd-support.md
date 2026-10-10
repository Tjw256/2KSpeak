# AMD GPU builds on Windows

The default build and release workflow continue to use CUDA for NVIDIA. Build a separate DirectML variant for AMD GPUs:

```powershell
dotnet build 2KSpeak.slnx -c Release -p:OrtFlavor=DirectML
dotnet test --project tests/TwoKSpeak.Engine.Tests -c Release -p:OrtFlavor=DirectML --no-build
dotnet test --project tests/TwoKSpeak.App.Tests -c Release -p:OrtFlavor=DirectML --no-build
dotnet publish src/TwoKSpeak.App -c Release -r win-x64 --self-contained -p:OrtFlavor=DirectML -o publish-dml
dotnet publish src/TwoKSpeak.Worker -c Release -r win-x64 --self-contained -p:OrtFlavor=DirectML -o publish-dml
```

Use the same `OrtFlavor` for the app and worker. ONNX Runtime's CUDA and DirectML native packages cannot simply be combined in one output directory. This change does not publish an AMD installer or change the existing auto-update channel; use a development/published build until a separate release channel is available.

In the DirectML build:

- Speech recognition uses the existing fp16 Parakeet models and DirectML. The hardware DXGI adapter with the largest dedicated memory is selected, avoiding an integrated GPU when a discrete card is available.
- The worker accepts `--device dml` and applies the same 200-frame buckets used for CUDA.
- Cleanup downloads the SHA-256-pinned llama.cpp b11534 Vulkan runtime into a separate `runtimes/llama-vulkan` directory, so existing CUDA DLLs cannot mix with it. CUDA and cuDNN are excluded from GPU-mode downloads.
- AMD, Intel and NVIDIA adapters with at least 6 GiB of dedicated memory can enable GPU mode on first run. Hardware inference has only been verified on AMD RX 9070 XT; Intel and other cards remain unverified.
- The existing CPU fallback and idle unloading remain in use.

llama.cpp chooses Vulkan devices independently of DXGI. To select a specific cleanup GPU, run its `llama-server.exe --list-devices`, then set `TWOKSPEAK_VULKAN_DEVICE` to the reported name (for example `Vulkan0`) before starting the app. The override is applied only to GPU cleanup in a DirectML build.

## Local hardware validation

AMD Radeon RX 9070 XT, Windows, fp16 speech model, 200-frame buckets:

- Czech fixture `cs-short.wav`: 9.1 s audio, correct reference transcript, about 67–71 ms for warm inference in the initial local build.
- English fixture `en-short.wav`: 9.3 s audio, about 63 ms warm inference; reference differences include number formatting.
- Varying 2–12 s inputs: about 167 ms mean on the second pass. DirectML still recompiles for changing shapes; these results do not replace the original CUDA benchmark.
- Approximately 1.3–1.4 GiB of process GPU memory for speech.
- Vulkan cleanup correctly handled the Czech correction “Přijdu v pondělí, vlastně ne, v úterý ráno.”

Re-run the bench for another card:

```powershell
dotnet run --project src/TwoKSpeak.Bench -c Release -p:OrtFlavor=DirectML -- --device dml --model <fp16-model-directory> --precision fp16 --bucket 200 --runs 5 tests/fixtures/cs-short.wav
```

CI checks both CPU and DirectML builds without downloaded models or a hardware GPU. Real microphone capture, hotkey insertion, NVIDIA hardware inference and Intel hardware inference require additional hardware testing.
