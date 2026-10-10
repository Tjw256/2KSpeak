# 2KSpeak

Local push-to-talk dictation for Windows. Hold **Ctrl+Win**, speak, and the text is typed into whatever field has focus. Runs offline from the system tray.

## Install

Download **2KSpeak-Setup.exe** from the [latest release](https://github.com/Tjw256/2KSpeak/releases/latest) and run
it. The installer isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC": click
**More info**, then **Run anyway**.

On first start 2KSpeak downloads what it needs from the original publishers, each file checked against a pinned
SHA-256: speech recognition first (0.7 GB, enough to dictate), then text cleanup (1.4 GB), then GPU mode on a
supported graphics card (see below; 3.2 GB on NVIDIA with CUDA and cuDNN, 1.3 GB on AMD and Intel). Nothing has to be
installed beforehand, not even CUDA. A setup window explains each part; it can be closed, and downloads resume after a lost connection or a
restart. Until GPU mode arrives, dictation runs on the CPU.

Updates download silently from GitHub releases and apply the next time 2KSpeak starts, or right away from
"Update ready · Restart" in the tray panel. Uninstalling (Settings → Apps) also removes the downloaded models,
settings and history.

## GPU support

Dictation works on any PC; a supported graphics card makes it faster and keeps the CPU free. 2KSpeak picks the card
itself on first start.

| Graphics card | Status |
| --- | --- |
| NVIDIA with 6 GB or more | **Supported.** Speech and cleanup on CUDA. |
| AMD Radeon with 6 GB or more (RX 9070 XT, RX 7000, RX 6000 series) | **Supported.** Speech on DirectML, cleanup on Vulkan. Tested on the RX 9070 XT; other Radeon cards use the same path but haven't been tested. AMD support was started by [@PepiBikerBTW](https://github.com/PepiBikerBTW). |
| AMD Ryzen with Radeon graphics (laptops, handhelds, desktop APUs) | **Supported, decided by a speed test.** After GPU mode downloads, 2KSpeak times the graphics chip against the processor once and uses whichever is faster. Strong chips (Radeon 680M, 780M, 880M, 890M) should win; the small 2-core graphics in desktop Ryzen 7000 and 9000 CPUs lose (tested on a Ryzen 7 7700: 923 ms vs 299 ms on the processor) and stay on the CPU. Needs 16 GB of RAM. |
| Intel Arc with 6 GB or more | Same DirectML path as AMD; untested. |
| Intel integrated graphics, NVIDIA under 6 GB, no graphics card | Runs on the CPU: text appears about 1–2 s after you let go instead of almost at once. |

If a graphics card fails to load or run the models, 2KSpeak falls back to the CPU. On a PC with several graphics
cards, `TWOKSPEAK_GPU=<number>` picks one by its DirectX adapter number (details in
[docs/amd-support.md](docs/amd-support.md)).

## Behaviour

- **Hold to talk.** Recording lasts as long as the keys are held; there is no length limit. The shortcut can be
  changed to any 2–3 of Ctrl, Win, Alt and Shift (Ctrl+Alt is refused because it is AltGr on many layouts).
- **Typed on release, or as you speak.** By default the whole dictation is typed when you let go, while an overlay
  shows the text so far. Alternatively each phrase is typed as soon as you pause.
- **25 languages**, detected automatically, with punctuation and capitalisation, via NVIDIA Parakeet TDT 0.6B v3:
  English, Bulgarian, Croatian, Czech, Danish, Dutch, Estonian, Finnish, French, German, Greek, Hungarian, Italian,
  Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian, Russian, Slovak, Slovenian, Spanish, Swedish and
  Ukrainian. Cleanup (below) is tuned for English and Czech; other languages are transcribed but get
  less filler removal.
- **GPU on demand.** The model is loaded to VRAM when you start talking and released after an idle timeout. CPU mode
  is available; if the GPU can't load or run the model, recognition continues on the CPU.
- **Tray panel.** Left-click the tray icon for status (with VRAM in use), the last five transcripts (click one to
  copy it), the speech GPU/CPU switch and the cleanup model. Right-click for Settings and Quit. Starting 2KSpeak
  again opens Settings.
- **Settings.** Shortcut, typing mode, phrase pause, cleanup model and device, microphone, speech device, VRAM idle
  timeout, RAM caching, saved history and start with Windows. Changes apply immediately.
- **Cleanup.** Hesitation sounds are removed by a word filter; then a small local LLM (Qwen3.5 2B by default, or
  0.8B) deletes fillers and self-corrections ("at five, no, actually at six" → "at six"). It may only delete words:
  any answer that adds, rewords or translates is discarded and the recognised text is typed instead, as it is when
  the model is slow or missing. It runs on the GPU when the card can hold it (see GPU support), otherwise on the CPU
  (about 1 s per dictation).

## Development

Requires the .NET 10 SDK on Windows.

```sh
dotnet build 2KSpeak.slnx -c Release            # CUDA flavor of ONNX Runtime (default)
dotnet test --project tests/TwoKSpeak.Engine.Tests -c Release
dotnet test --project tests/TwoKSpeak.App.Tests -c Release
```

- `src/TwoKSpeak.App` — tray app: hotkey hook, microphone, phrase segmentation, typing, overlay, tray panel, settings.
- `tools/render-app-icon.ps1` — renders `Ui/Icons.xaml` into `Assets/2KSpeak.ico` after artwork changes.
- `src/TwoKSpeak.Worker` — inference process; exits on idle so GPU memory is returned.
- `src/TwoKSpeak.Engine` — Parakeet TDT ONNX pipeline, Silero VAD, phrase segmenter, filler filter, IPC protocol,
  curator (llama-server process, prompt, deletion-only guard), and the first-run downloader (`Setup/Components.cs`
  pins every URL and hash).
- `src/TwoKSpeak.Bench` — latency/VRAM benchmark; results in [docs/spike-results.md](docs/spike-results.md).
- `tools/fetch-dev-models.sh` — downloads models and the CUDA/llama.cpp runtimes into `%LOCALAPPDATA%\2KSpeak`.
  Model tests skip when the models are absent. A development build also downloads them itself on first start.
- `TWOKSPEAK_DATA_DIR` — points models, logs, settings and history at another folder, e.g. to test a first run.

Pass `-p:OrtFlavor=Cpu` to build against the CPU-only runtime (used by CI). `-p:OrtFlavor=DirectML` builds the AMD/Intel
worker into separate `bin\dml` and `obj\dml` folders; to try it from a development build, publish it into the app's
`dml` folder (see [docs/amd-support.md](docs/amd-support.md)).

End-to-end check without a microphone: start `2KSpeak.exe` with `TWOKSPEAK_TEST_AUDIO` set to a 16 kHz mono WAV
(the app then "hears" that file instead of the microphone), then run `tools/e2e-dictation.ps1`. Quit it afterwards:
while it runs it also answers the real shortcut, and types the WAV's text instead of what you say.

Releases: pushing a `v*` tag runs `.github/workflows/release.yml`, which publishes the app and worker self-contained,
packs them with Velopack (`vpk`, with the Visual C++ runtime as a prerequisite) and publishes the GitHub release
that installed copies update from. The DirectML worker goes into `publish\dml`, next to the CUDA one. Release notes
come from `docs/release-notes.md`. The app installs to `%LOCALAPPDATA%\2KSpeak.App`; downloads stay in
`%LOCALAPPDATA%\2KSpeak`, because Setup wipes its own folder on a repair.

Logs: `%LOCALAPPDATA%\2KSpeak\logs` (`app.log`, `worker.log`) — timings and errors only, never transcript text.

### How dictation works
1. A low-level keyboard hook detects the shortcut (Ctrl+Win by default). Its keys are then hidden from Windows, so
   text can be typed while they are held without triggering shortcuts; a third key restores them (Win+Ctrl+→ still works).
2. Audio is split into phrases by Silero VAD at short pauses. Each phrase goes to the worker together with the
   preceding 3 s of audio, so capitalisation and punctuation across the pause come out right.
3. A phrase's trailing punctuation is held back until the next phrase shows whether the pause ended a sentence,
   so nothing typed ever has to be deleted.

## License

MIT — see [LICENSE](LICENSE).
