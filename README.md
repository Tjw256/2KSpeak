# 2KSpeak

Local push-to-talk dictation for Windows. Hold **Ctrl+Win**, speak, and the text is typed into whatever field has focus. Runs offline from the system tray.

> Work in progress — see open pull requests for the current state.

## Planned behaviour

- **Hold to talk.** Recording lasts as long as the keys are held; there is no length limit.
- **Text as you speak.** Each phrase is typed when you pause briefly; a small overlay previews the words in flight.
- **English and Czech** via NVIDIA Parakeet TDT 0.6B v3, with punctuation and capitalisation.
- **GPU on demand.** The model is loaded to VRAM when you start talking and released after an idle timeout. CPU mode is available.
- **Filler cleanup.** A word filter, optionally followed by a small local LLM that may only delete words (fillers, self-corrections) — never add or change them.
- **Tray history.** The last five transcripts; click one to copy it.

## License

MIT — see [LICENSE](LICENSE).
