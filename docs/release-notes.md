First release with an installer. Hold **Ctrl + Win**, speak, let go: the text is typed into whatever field has the cursor. Everything runs on your PC.

## Install

1. Download **2KSpeak-Setup.exe** below and run it.
2. Windows may show **"Windows protected your PC"**, because the installer isn't code-signed yet. Click **More info**, then **Run anyway**.
3. If Windows asks to install the Microsoft Visual C++ runtime, allow it.
4. 2KSpeak opens a setup window and downloads what it needs (about 0.7 GB to start dictating; with an NVIDIA graphics card, about 5 GB in total for the faster GPU mode). You can close the window: the download continues in the background, and the tray icon by the clock shows progress.

No CUDA or other installs are needed; 2KSpeak downloads its own copy. Updates download by themselves and apply the next time 2KSpeak starts.

## Graphics cards

Dictation works on every PC. A supported graphics card makes it faster: text appears almost as soon as you let go, instead of after about 1–2 seconds on the CPU.

- **NVIDIA with 6 GB or more:** supported. GPU mode downloads automatically on first run.
- **AMD Radeon RX 9070 XT:** works in a separate DirectML build that isn't part of this installer yet (see `docs/amd-support.md` in the repository). This installer runs on the CPU on AMD cards.
- **AMD Radeon RX 7000, RX 6000 series and older:** not supported yet; support still has to be done. They run on the CPU.
- **Intel Arc:** untested; runs on the CPU.
- **NVIDIA with less than 6 GB, or no graphics card:** runs on the CPU by default.

## Languages

Speech recognition understands 25 European languages and detects the language by itself: English, Bulgarian, Croatian, Czech, Danish, Dutch, Estonian, Finnish, French, German, Greek, Hungarian, Italian, Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian, Russian, Slovak, Slovenian, Spanish, Swedish and Ukrainian. Removing "um"s and self-corrections is tuned for English and Czech.

## What's in it

- Push-to-talk dictation with a rebindable shortcut and no length limit
- Filler and self-correction cleanup with a small local language model (Qwen3.5 2B or 0.8B, GPU or CPU)
- GPU memory is used only while you dictate and released after a few idle minutes
- Last 5 transcripts in the tray panel, click to copy
- Starts with Windows

Uninstall from **Settings → Apps**; this also removes the downloaded models.
