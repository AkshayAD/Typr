# Typr

A local-first voice-to-text workspace for Windows. Built with C#/.NET 8, WPF, NAudio and sherpa-onnx. No account, API key or cloud transcription required.

This is a development preview. The redesigned interface and synthetic speech path have automated Windows coverage; real microphones, global shortcuts, external-app insertion, long sessions and mixed-monitor DPI still need the [Windows validation checklist](docs/WINDOWS_VALIDATION.md). There is no signed installer or public binary release yet.

## The desktop experience

- **Workspace:** visible Start / Finish / Cancel practice dictation, listening timer, microphone meter and a recoverable latest transcript.
- **History:** opt-in local transcript retention, newest 200 entries, search, selection, copy, individual removal and confirmed clear-all.
- **Vocabulary:** explicit spoken-phrase → preferred-spelling rules, applied to completed Clean output.
- **Snippets:** spoken phrases expand into literal reusable text, including multiline content.
- **Appearance:** Light, Dark and System themes, Iris/Forest/Ocean accents, live captions, floating indicator, line count and readable caption backgrounds.
- **Settings:** resumable/hash-verified model setup, microphone lifecycle, Clean/Raw output and opt-in bounded audio retention.

English recognition uses the local Nemotron EN 0.6B int8 streaming model. Clean output uses conservative deterministic cleanup and your explicit rules. Raw bypasses both. AI rewriting, adaptive per-app styles, multilingual recognition, configurable shortcuts, tray behavior and an updater remain future work.

The [detailed product and design plan](docs/PRODUCT_DESIGN_PLAN.md) covers the complete experience and the evidence needed to assess Wispr Flow parity. [Implementation status](docs/IMPLEMENTATION_STATUS.md) separates delivered features, actual test evidence and remaining work. Visual improvements do not establish speech-quality or reliability parity.

## Run and build

Use a supported .NET SDK with Windows desktop targeting support:

```powershell
dotnet test tests/Ava.Core.Tests -c Release
dotnet test tests/Ava.Infrastructure.Tests -c Release
dotnet test tests/Ava.Windows.Tests -c Release
dotnet build src/Ava.Windows -c Release -m:1 -p:BuildInParallel=false
dotnet run --project src/Ava.Windows -c Release
```

WPF integration tests run on Windows and use isolated temporary profiles. The real-model replay test is skipped unless explicitly configured; see [Development](docs/DEVELOPMENT.md).

For a self-contained local build:

```powershell
dotnet publish src/Ava.Windows -c Release -r win-x64 --self-contained true -m:1 -p:BuildInParallel=false -o artifacts/windows
```

The executable remains `AvaVoice.exe`, with existing `Ava` project names and data locations retained for compatibility. The visible application is Typr. The speech model is separate (about 632 MiB) and is never committed.

.NET 8 support ends November 10, 2026. Migration to a supported LTS runtime is a release prerequisite; this change retains the existing runtime to keep the UI work bounded.

## First dictation

1. Open **Settings** and choose **Download speech model**, or expand advanced settings to select an existing model folder.
2. Choose **Enable microphone**. Typr verifies the model and opens the communications-default microphone.
3. In **Workspace**, choose **Start dictation**, speak, then choose **Finish dictation**. Practice text is inserted directly into the built-in field without changing the clipboard.
4. In another writable text field, hold **Ctrl + Alt**, speak, then release. Use a disposable field for initial testing.
5. Choose **Close microphone** in Settings when finished. Closing the app also releases capture and shortcuts.

Shortcuts are fixed for now: Ctrl+Alt hold-to-talk; add Space for hands-free, then Ctrl+Alt again finishes; add Shift for Raw; Esc cancels recording/processing before insertion; Ctrl+Shift+Z explicitly repeats the latest result. That repeat shortcut is reserved while the microphone is enabled.

## Privacy and recovery

After model download, speech runs on your PC. Downloads contact Hugging Face. There is no telemetry, model-training upload, paid service or mandatory login in this code.

- The microphone stays open for pre-roll after you enable it, until you close it or quit. It never starts automatically on launch.
- New profiles save neither audio nor history by default. Existing explicit audio-retention preferences are preserved.
- Optional completed audio is bounded to the configured limit (up to 200 WAVs). Cancelled/faulted captures are discarded. Switching retention off affects future recordings; existing files remain.
- History is separately opt-in, stored locally and bounded to 200 completed transcripts. Switching it off stops new saves without deleting existing entries.
- Settings/vocabulary/snippets: `%APPDATA%\Ava\settings.json`. History: `%LOCALAPPDATA%\Ava\history.json`. Audio/diagnostics: `%LOCALAPPDATA%\Ava\audio` and `logs`.
- Do not publish personal settings, recordings, transcripts or logs. Corrupt/future-version settings and history files are preserved instead of overwritten.

Automatic insertion retains the original safeguards for known writable, non-password fields, focus checks and compatible clipboard contents. Rich clipboard formats are left untouched. Native focus and input injection cannot be atomic. Ambiguous delivery offers Copy rather than automatically retrying; cancelling cannot undo text already inserted.

## Licensing

No open-source license has been granted for original project code yet. Public visibility alone does not grant reuse or redistribution rights. Dependencies/models have separate licenses; see [third-party information](THIRD_PARTY_NOTICES.md). Binary redistribution remains gated on dependency-notice review.
