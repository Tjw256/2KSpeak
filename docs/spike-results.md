# Engine spike — measured results

Measured 2026-10-09 on the development machine: RTX 5090 (32 GB), Ryzen 7 7700, 48 GB DDR5, Windows 11.
VRAM is the dedicated memory charged to the process (WDDM `GPU Process Memory` counter), not the whole GPU.
Reproduce with `src/TwoKSpeak.Bench` and the clips in `tests/fixtures` (Windows TTS, 16 kHz mono).

## Speech recognition — Parakeet TDT 0.6B v3

| Variant | 9 s phrase | 35 s clip | VRAM | Cold start¹ | Notes |
|---|---|---|---|---|---|
| fp16, CUDA (ORT 1.31, CUDA 13.4, cuDNN 9.27) | ~80 ms | ~240 ms | 1.8 GB loaded, 2.0 GB after first run | ~3.6 s (7.5 s on the very first run) | Czech clip word-perfect |
| int8, CPU | ~300 ms | ~1.1 s | 0 | ~3.0 s | Czech numbers come out as digits |
| fp16, DirectML (ORT 1.24.4) | ~62 ms (first shape) / ~250 ms (any other) | ~380 ms | 1.5 GB | ~2.9 s | Rejected — re-compiles for every new input length |
| int8, CUDA | ~430 ms | ~1.5 s | 0.8 GB | ~6 s | Rejected — quantized ops fall back to CPU |

¹ Process start to first transcript, kernel cache warm.

Stage split for the 9 s phrase on CUDA: features 20 ms (CPU), encoder 22 ms, TDT decode loop 36 ms.
Running the decoder on CPU instead was slower (fp16 on CPU), so the decoder stays on the GPU.
cuDNN workspace size and graph optimization level made no measurable VRAM difference.

Word error rate on the English clips (8 % / 16.5 %) is almost entirely number formatting
("5" for "five", "4K" for "four K") plus "coil wine" for "coil whine".

Sources: fp32/int8 export [istupakov/parakeet-tdt-0.6b-v3-onnx](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx);
fp16 conversion [jimmy927/parakeet-tdt-0.6b-v3-onnx-fp16](https://huggingface.co/jimmy927/parakeet-tdt-0.6b-v3-onnx-fp16)
at revision `6477bf2008b729808e4f502f1b283a2a3d5ca1bb` (weights fp16, inputs/outputs fp32).

## Varying input lengths (found in end-to-end testing)

The table above repeats one clip, so every call has the same input shape. Real phrases all differ in length,
and the CUDA provider re-plans kernels whenever the shape changes: the encoder took 130–250 ms per phrase
instead of ~22 ms. Sweeping 28 lengths between 2 and 12 s (encoder mean, GPU otherwise idle unless noted):

| Feature padding | Encoder | Notes |
|---|---|---|
| none | 151 ms | |
| 1 s buckets (100 frames) | 66 ms | |
| 2 s buckets (200 frames) | 41 ms | chosen; under a concurrent LLM load ~50 ms vs ~145 ms unpadded |
| 8 s buckets | 33 ms | more wasted compute per call |

The true length is passed with the padded tensor, so padded frames are masked out; a model test checks the
transcript is identical with and without padding. The arena strategy (`kNextPowerOfTwo` vs `kSameAsRequested`)
made no difference except +0.9 GB VRAM, so `kSameAsRequested` stays.

With 2 s buckets in the app: warm phrases transcribe in ~75–100 ms end to end inside the worker
(3 s of context included); the first phrase landing in a new bucket costs ~180 ms once.

## Curator LLM — deletion-only cleanup

llama.cpp b11534 (CUDA 13.4), context 2048, temperature 0, four few-shot examples, ten test sentences
(English and Czech fillers, self-corrections, and questions/instructions that must not be answered).
"Pass" means the output only deleted words from the input.

| Model | Pass | GPU latency / VRAM | CPU latency (8 threads) |
|---|---|---|---|
| Qwen3.5-2B Q4_K_M | 10/10 | ~80 ms / 1.8 GB | ~460 ms |
| Qwen3.5-0.8B Q8_0 | 8/10 | ~80 ms / 1.3 GB | ~340 ms |

The 0.8B model cleans less and once wrote a poem when the transcript said "write me a poem";
the deletion-only guard rejected it, so the raw text would have been typed.

## Decisions

- GPU mode: fp16 on CUDA. CPU mode: int8.
- Default: GPU recognition, Qwen3.5-2B curator on CPU (keeps dictation at ~2 GB VRAM), 5-minute idle unload.
- The GPU worker is a separate process that exits on idle, which is the only way to return CUDA's
  per-process overhead (~0.6 GB) along with the weights.
