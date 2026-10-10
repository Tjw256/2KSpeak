# AMD and Intel GPUs

Since 0.6.0 the installer supports AMD (and, untested, Intel) graphics next to NVIDIA. AMD support was started by
[@PepiBikerBTW](https://github.com/PepiBikerBTW) in #8.

## How a GPU is chosen

`Gpu.Choose` (`src/TwoKSpeak.Engine/Onnx/Gpu.cs`) reads the DirectX (DXGI) adapters and picks, in order:

1. An NVIDIA card with 6 GB or more of its own memory: CUDA, as before.
2. An AMD or Intel card with 6 GB or more: speech on DirectML, cleanup on llama.cpp's Vulkan build.
3. An AMD integrated GPU with at least 6 GB of dedicated plus shared memory (16 GB of RAM in practice): DirectML and
   Vulkan, but only if a speed test shows it beats the processor.
4. Otherwise the CPU.

The app and the worker run the same rule, so DirectML's device id (the DXGI index) matches the app's choice.
llama.cpp numbers Vulkan devices its own way, so the app runs `llama-server --list-devices` and passes the device
whose name matches the DXGI adapter. Without that, llama.cpp would split the model across every GPU, an integrated
one included.

`TWOKSPEAK_GPU=<DXGI index>` limits the choice to one adapter (multi-GPU PCs, or testing another adapter's path);
`TWOKSPEAK_VULKAN_DEVICE=VulkanN` overrides the cleanup device.

## Integrated GPUs: the speed test

Integrated graphics range from fast (Radeon 780M, 12 compute units) to far slower than the processor (the 2-unit
Radeon in desktop Ryzen 7000/9000 CPUs), and nothing Windows reports tells them apart. So on first start such a PC
runs on the CPU, downloads GPU mode (the 1.3 GB fp16 model), then runs `2KSpeak.Worker --benchmark 3` on DirectML and
on the CPU with the same 9 s phrase (`tests/fixtures/en-short.wav`, embedded in the worker). The GPU is used for
speech and cleanup only if its median is under 80% of the CPU's. The verdict is stored in the settings
(`GpuSpeedTestPending`) and never repeated; a device the user chose in the meantime is kept.

On integrated GPUs the decoder runs on the CPU: its many tiny steps cost more on the GPU than they gain (29 ms vs
144 ms per phrase on the Ryzen 7 7700's Radeon).

## The second worker

ONNX Runtime's CUDA and DirectML builds have the same file names, and the DirectML package stops at 1.24.4 while
CUDA uses 1.31, so they can't share a folder. The installer ships the DirectML worker in `dml\2KSpeak.Worker.exe`
(self-contained, about 110 MB on disk); the app starts it for GPU work on AMD and Intel. CPU work always uses the main
worker, which includes ONNX Runtime's CPU provider.

`-p:OrtFlavor=DirectML` builds into `bin\dml` and `obj\dml`, so it doesn't disturb the default build. To try it from
a development build:

```powershell
dotnet build 2KSpeak.slnx -c Release
dotnet publish src/TwoKSpeak.Worker -c Release -r win-x64 --self-contained -p:OrtFlavor=DirectML -o src/TwoKSpeak.App/bin/Release/net10.0-windows/dml
```

Without the `dml` folder, the app asks the main worker for DirectML, which fails and falls back to the CPU.

## Measurements

AMD Radeon RX 9070 XT (by @PepiBikerBTW), fp16, 200-frame buckets:

- `cs-short.wav` (9.1 s): correct transcript, about 67–71 ms warm. `en-short.wav` (9.3 s): about 63 ms.
- Varying 2–12 s inputs: about 167 ms mean on the second pass (DirectML recompiles for new shapes).
- About 1.3–1.4 GiB of GPU memory for speech. Vulkan cleanup handled "Přijdu v pondělí, vlastně ne, v úterý ráno."

Ryzen 7 7700 (8 cores) and its integrated Radeon (2 compute units, 485 MB dedicated, 31 GB shared), 2026-10-10:

| | Speech, 2–12 s phrases | Speech, 9.3 s phrase (speed test) | Cleanup prompt / generation |
| --- | --- | --- | --- |
| Integrated Radeon, DirectML / Vulkan | 706 ms (decoder on CPU) | 923–961 ms | 158 / 10 tokens/s |
| CPU | 187 ms | 299–348 ms | 434 / 32 tokens/s |
| RTX 5090 via DirectML, for comparison | 148 ms | | |

The speed test chose the CPU there, as it should. The fast laptop chips (680M, 780M, 880M, 890M) have 6–8 times the
compute units and are expected to beat their processors, but none has been tested yet.

Re-run the bench for another card (`TWOKSPEAK_GPU` picks the adapter):

```powershell
dotnet run --project src/TwoKSpeak.Bench -c Release -p:OrtFlavor=DirectML -- --device dml --model <fp16-model-directory> --precision fp16 --bucket 200 --runs 5 --vary tests/fixtures/en-short.wav
```

CI builds and tests the CPU and DirectML flavors without a GPU; hardware inference is only checked by hand.
