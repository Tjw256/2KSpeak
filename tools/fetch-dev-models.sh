#!/usr/bin/env bash
# Dev-time fetch of models and runtimes into %LOCALAPPDATA%/2KSpeak. The app will download these itself on first run.
set -u
ROOT="$LOCALAPPDATA/2KSpeak"
M="$ROOT/models"; RT="$ROOT/runtimes"
mkdir -p "$M/parakeet-tdt-0.6b-v3" "$M/silero-vad" "$M/curator" "$RT/dl"
get() { [ -s "$2" ] && { echo "SKIP $(basename "$2")"; return; }; curl -sS -L --retry 5 -C - -o "$2.part" "$1" && mv "$2.part" "$2" && echo "OK $(basename "$2") $(stat -c %s "$2")" || echo "FAIL $1"; }
P=https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main
for f in config.json vocab.txt nemo128.onnx decoder_joint-model.onnx decoder_joint-model.int8.onnx encoder-model.int8.onnx encoder-model.onnx encoder-model.onnx.data; do get "$P/$f" "$M/parakeet-tdt-0.6b-v3/$f"; done
get https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx "$M/silero-vad/silero_vad.onnx"
get https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q8_0.gguf "$M/curator/Qwen3.5-0.8B-Q8_0.gguf"
get https://huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-Q4_K_M.gguf "$M/curator/Qwen3.5-2B-Q4_K_M.gguf"
B=https://github.com/ggml-org/llama.cpp/releases/download/b11534
get "$B/llama-b11534-bin-win-cuda-13.4-x64.zip" "$RT/dl/llama-b11534-cuda13.4.zip"
get "$B/cudart-llama-bin-win-cuda-13.4-x64.zip" "$RT/dl/cudart-cuda13.4.zip"
echo DONE1

# --- fp16 Parakeet (GPU) and CUDA 13 / cuDNN 9 runtime for ONNX Runtime 1.31 CUDA EP ---
F=https://huggingface.co/jimmy927/parakeet-tdt-0.6b-v3-onnx-fp16/resolve/6477bf2008b729808e4f502f1b283a2a3d5ca1bb
mkdir -p "$M/parakeet-tdt-0.6b-v3-fp16"
for f in encoder-model.fp16.onnx decoder_joint-model.fp16.onnx nemo128.onnx vocab.txt config.json; do get "$F/$f" "$M/parakeet-tdt-0.6b-v3-fp16/$f"; done
N=https://developer.download.nvidia.com/compute
get "$N/cuda/redist/cuda_cudart/windows-x86_64/cuda_cudart-windows-x86_64-13.4.92-archive.zip" "$RT/dl/cudart.zip"
get "$N/cuda/redist/libcublas/windows-x86_64/libcublas-windows-x86_64-13.8.0.4-archive.zip" "$RT/dl/cublas.zip"
get "$N/cuda/redist/libcufft/windows-x86_64/libcufft-windows-x86_64-12.4.0.43-archive.zip" "$RT/dl/cufft.zip"
get "$N/cudnn/redist/cudnn/windows-x86_64/cudnn-windows-x86_64-9.27.0.42_cuda13-archive.zip" "$RT/dl/cudnn.zip"
echo DONE2
