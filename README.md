# Typr

A personal voice-to-text workspace for Windows. Speak into a writable field, finish from a shortcut or movable companion bar, and keep your completed text within reach.

This is a development preview built with C#/.NET 8, WPF, NAudio and sherpa-onnx. There is no signed installer or public binary release yet. Read the [design plan](docs/PRODUCT_DESIGN_PLAN.md), [companion and writing-quality specification](docs/COMPANION_AND_POLISH_PLAN.md), [model research](docs/ENGLISH_MODEL_RESEARCH.md), and [implementation evidence](docs/IMPLEMENTATION_STATUS.md).

## System requirements

| Requirement | What you need |
| --- | --- |
| Operating system | Windows 10 or 11, **64-bit x64**, receiving security updates. Tested on Windows 11. No ARM, macOS or Linux package. |
| Hardware baseline | **8 GB RAM, four CPU cores, 2 GB free storage** is a practical starting configuration for live English recognition. These hardware figures are provisional recommendations, not a certified minimum. No discrete GPU required. |
| Re-transcription | Small adds **375 MB** of model files. Large v3 adds **1.78 GB**; **16 GB RAM and 4 GB additional free space** are recommended for staging and decoding. Retained recordings use additional space. |
| Audio | Microphone and Windows microphone access. Select a Typr input, or use the system default input. Shared capture opens only during an explicit recording. |
| Insertion | Writable, non-password field at the same privilege level as Typr. Unsupported/protected/elevated fields may require Copy. |
| Internet | Required for downloads, provider login, CLI updates and AI polish. Local speech works offline after download with AI polish off. |
| AI polish | Eligible ChatGPT or Claude subscription and official CLI sign-in. Typr can install/update its private CLI copy. Provider availability and subscription allowances apply; no API key required. |
| Runtime | Self-contained **Typr.exe includes .NET**. No separate SDK/runtime required to run it. Source builds need a compatible .NET SDK with Windows desktop targeting support. |

Platform details follow [.NET Windows support](https://learn.microsoft.com/en-us/dotnet/core/install/windows). .NET 8 support ends November 10, 2026; a supported LTS migration is required before public release. Hardware floors and multilingual accuracy still need representative-device evaluation.

## The experience

- **Dictate and paste:** keyboard hold or hands-free, with target verification, rich clipboard preservation and Copy recovery. Bar/button recording is an explicit opt-in; shortcuts are the default.
- **Movable companion:** drag anywhere, including controls; independently customize hover/right-click actions; show/hide bar and live text; adjust background opacity, size and hover delay while keeping text legible.
- **Microphone on demand:** preparing the model leaves audio hardware closed. Finish/release closes capture after a 300 ms ending tail; cancel closes it without that delay. No idle pre-roll, loopback capture, system mute/default-device changes or exclusive input.
- **Your AI subscription:** default **GPT-6 Luna, low reasoning**; ChatGPT/Claude priority, connection/model refresh, sign-in, CLI updates and effort comparisons in Settings.
- **Your words:** punctuation, case, digits, bullets and self-corrections while preserving meaning and English/Roman Hindi. Raw bypasses cleanup and AI. AI can still make mistakes; review important text.
- **Recoverable history:** opt-in local history, original/final search, metadata, retained-audio playback, preview re-polish/re-transcription, apply and restore previous versions.
- **Personalization:** System/Light/Dark, three accents, vocabulary, literal spoken snippets, captions and independent audio/history retention.

The **live recognizer is English** (Nemotron EN 0.6B int8). Multilingual **Whisper Small** and **Large v3** re-transcribe explicitly retained audio. AI polish can Romanize recognized Hindi; it cannot recover Hindi speech the live recognizer missed. Small is the CPU recommendation. Large v3 is an optional quality experiment, not a measured accuracy guarantee.

## First dictation

1. In Settings, download the local model (about 632 MiB), or select an existing folder under Advanced.
2. Optionally choose **Prepare speech** to verify/load the model ahead of time. This leaves the microphone off.
3. Click Workspace's practice field, hold **Ctrl+Alt**, wait for **Listening**, speak and release. On first use, model preparation can take a moment; releasing during preparation cancels the recording start.
4. Repeat in a disposable writable external field. Enable **Allow starting dictation from the bar and buttons** only if you want those additional recording controls.
5. Connect your provider for polish. Choose Raw or turn AI polish off to keep output local.
6. Finish/release/cancel to close the input. **Unload speech** also releases the prepared model from memory; staying prepared keeps the microphone off.

Defaults: **Ctrl+Shift+M** prepares/unloads speech; **Ctrl+Shift+Space** starts/finishes recording; **Ctrl+Shift+V** pastes last. Change/disable keyboard shortcuts in Settings; conflicts are reported. **Ctrl+Alt** holds to talk, add **Space** hands-free, add **Shift** Raw; **Esc** cancels pending work including cold preparation. The legacy Ctrl+Shift+Z repeat chord is also disabled by the keyboard-shortcuts switch.

Typr leaves other applications' microphone controls, playback and Windows defaults alone. Bluetooth Classic can still switch the headset to its voice profile whenever any app uses its microphone. For consistent headset playback, choose a built-in/USB input inside Typr. See the [Microsoft-backed hardware notes](docs/ENGLISH_MODEL_RESEARCH.md#microphone-quality-and-hardware-impact). New hardware and on-screen checks are [deferred for an idle session](docs/DEFERRED_DESKTOP_TESTS.md).

## Privacy and recovery

Speech and audio re-transcription run locally. Downloads contact the model host. **AI polish sends recognized text to your selected provider**, through its official local CLI and your own sign-in. Recordings are not sent. A “first” priority permits your other connected provider as fallback; “only” does not. No silent API-credit fallback.

Integrations use the [Codex app server](https://learn.chatgpt.com/docs/app-server) and [Claude CLI print interface](https://code.claude.com/docs/en/headless). The providers own authentication and credential storage; Typr does not extract OAuth tokens or broker other users' sign-ins. Review [Claude integration conditions](https://code.claude.com/docs/en/legal-and-compliance) before changing/distributing authentication flows.

- New profiles retain neither history nor audio by default. Each can retain up to 200 entries/files independently.
- Settings/vocabulary/snippets: `%APPDATA%\Typr\settings.json`. History/audio/models/diagnostics: `%LOCALAPPDATA%\Typr`.
- Old profiles migrate without overwriting an existing Typr profile or deleting original data. Older history entries may lack audio links.
- Disabling retention stops future saves; existing data remains. Removing history does not delete audio. Each entry keeps up to 10 previous text versions.
- Corrupt/future-version files are preserved. Cancellation cannot undo inserted text. Ambiguous insertion is not automatically retried.
- Personal data, credentials, logs, models and test recordings must not be published.

## Build and run

```powershell
dotnet test tests/Typr.Core.Tests -c Release
dotnet test tests/Typr.Infrastructure.Tests -c Release
dotnet test tests/Typr.Windows.Tests -c Release
dotnet build src/Typr.Windows -c Release -m:1 -p:BuildInParallel=false
dotnet run --project src/Typr.Windows -c Release
dotnet publish src/Typr.Windows -c Release -r win-x64 --self-contained true -m:1 -p:BuildInParallel=false -o artifacts/windows
```

Run `artifacts/windows/Typr.exe`; models are separate downloads. Windows tests include separate-process clipboard/input cases: leave the desktop idle while those run. Other UI checks use temporary profiles and no microphone. Model replay is opt-in; see [Development](docs/DEVELOPMENT.md) and [Windows validation](docs/WINDOWS_VALIDATION.md).

## Licensing

No open-source license has been granted for original project code. Public visibility alone does not grant reuse/redistribution rights. Dependencies/models have separate licenses; see [third-party information](THIRD_PARTY_NOTICES.md). Binary redistribution requires notice review.
