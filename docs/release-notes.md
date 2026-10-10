First release with an installer. Hold **Ctrl + Win**, speak English or Czech, let go: the text is typed into whatever field has the cursor. Everything runs on your PC.

## Install

1. Download **2KSpeak-Setup.exe** below and run it.
2. Windows may show **"Windows protected your PC"**, because the installer isn't code-signed yet. Click **More info**, then **Run anyway**.
3. If Windows asks to install the Microsoft Visual C++ runtime, allow it.
4. 2KSpeak opens a setup window and downloads what it needs (about 0.7 GB to start dictating; with an NVIDIA graphics card, about 5 GB in total for the faster GPU mode). You can close the window: the download continues in the background, and the tray icon by the clock shows progress.

No CUDA or other installs are needed; 2KSpeak downloads its own copy. Updates download by themselves and apply the next time 2KSpeak starts.

## What's in it

- Push-to-talk dictation with a rebindable shortcut and no length limit
- Filler and self-correction cleanup with a small local language model (Qwen3.5 2B or 0.8B, GPU or CPU)
- GPU memory is used only while you dictate and released after a few idle minutes
- Last 5 transcripts in the tray panel, click to copy
- Starts with Windows

Uninstall from **Settings → Apps**; this also removes the downloaded models.
