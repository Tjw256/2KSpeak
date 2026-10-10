2KSpeak now uses AMD graphics too: Radeon graphics cards, and the Radeon graphics built into Ryzen laptops when it is faster than the processor. Hold **Ctrl + Win**, speak, let go: the text is typed into whatever field has the cursor. Everything runs on your PC.

## Install

1. Download **2KSpeak-Setup.exe** below and run it.
2. Windows may show **"Windows protected your PC"**, because the installer isn't code-signed yet. Click **More info**, then **Run anyway**.
3. If Windows asks to install the Microsoft Visual C++ runtime, allow it.
4. 2KSpeak opens a setup window and downloads what it needs: about 0.7 GB to start dictating, then text cleanup and GPU mode (about 5 GB in total on NVIDIA, 3.3 GB on AMD). You can close the window: the download continues in the background, and the tray icon by the clock shows progress.

Already have 0.5.0? It updates itself. On an AMD graphics card it then downloads GPU mode and switches over.

## Graphics cards

Dictation works on every PC. A supported graphics card makes it faster: text appears almost as soon as you let go, instead of after about 1–2 seconds on the CPU.

- **NVIDIA with 6 GB or more:** supported.
- **AMD Radeon with 6 GB or more:** supported. Tested on the RX 9070 XT; RX 7000 and RX 6000 cards use the same path but haven't been tested yet. AMD support was started by [@PepiBikerBTW](https://github.com/PepiBikerBTW).
- **AMD Ryzen with Radeon graphics (laptops, handhelds):** after GPU mode downloads, 2KSpeak tests once whether the graphics chip is faster than the processor and uses the faster one. Needs 16 GB of RAM. The small graphics in desktop Ryzen 7000 and 9000 processors are slower than the processor, so those PCs stay on the CPU.
- **Intel Arc with 6 GB or more:** should work like AMD; untested.
- **Intel integrated graphics, NVIDIA with less than 6 GB, or no graphics card:** runs on the CPU.

If the graphics card can't run the models, 2KSpeak falls back to the CPU by itself.

## Languages

Speech recognition understands 25 European languages and detects the language by itself: English, Bulgarian, Croatian, Czech, Danish, Dutch, Estonian, Finnish, French, German, Greek, Hungarian, Italian, Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian, Russian, Slovak, Slovenian, Spanish, Swedish and Ukrainian. Removing "um"s and self-corrections is tuned for English and Czech.

## What's in it

- Push-to-talk dictation with a rebindable shortcut and no length limit
- Filler and self-correction cleanup with a small local language model (Qwen3.5 2B or 0.8B, GPU or CPU)
- GPU memory is used only while you dictate and released after a few idle minutes
- Last 5 transcripts in the tray panel, click to copy
- Starts with Windows

Uninstall from **Settings → Apps**; this also removes the downloaded models.
