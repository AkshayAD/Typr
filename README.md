# Typr

Local-first hold-to-talk dictation for Windows. Early engineering preview, built with C#/.NET 8, WPF, NAudio and sherpa-onnx.

**Not yet validated for everyday use.** The source cross-builds for Windows and has automated logic tests; native microphone, shortcuts, clipboard, focus and multi-monitor behavior still require Windows testing. No public installer or binary release is offered yet.

## Current preview
- Ctrl+Alt hold-to-talk, Ctrl+Alt+Space hands-free, Esc to cancel
- Local streaming English speech recognition using Nemotron EN 0.6B int8
- Live caption strips and microphone meter
- Conservative cleanup that preserves wording
- Verified paste into known writable, non-password fields
- Explicit Copy result fallback; no automatic duplicate paste after uncertain verification
- Resumable, hash-verified speech model download

The current executable is named `AvaVoice.exe`; namespaces and data directories still use `Ava`. Naming cleanup is pending.

## Build

Install a supported .NET 8 SDK from Microsoft, then:

```sh
dotnet test tests/Ava.Core.Tests -c Release
dotnet test tests/Ava.Infrastructure.Tests -c Release
dotnet build src/Ava.Windows -c Release -m:1 -p:BuildInParallel=false
dotnet publish src/Ava.Windows -c Release -r win-x64 --self-contained true -m:1 -p:BuildInParallel=false -o artifacts/windows
```

The published application is self-contained and does not require a runtime installation. The speech model is downloaded separately (approximately 632 MiB); it is not in this repository. No administrator elevation should be required.

The preview uses .NET 8. The final preview build used SDK 8.0.426/runtime 8.0.32. .NET 8 support ends November 10, 2026; migrate to a supported runtime before a longer-term release.

## First test

1. Use a disposable local text field and an idle testing session.
2. Launch the application, choose Download speech model, then Load speech.
3. Test in the built-in practice box first. Hold Ctrl+Alt, speak, then release.
4. Verify cancellation and clipboard preservation before testing other apps.
5. Close mic to release capture and global shortcuts.

Do not bypass Windows security warnings. See [Windows validation](docs/WINDOWS_VALIDATION.md).

## Privacy and safety

Speech runs locally after the model download. No cloud AI, subscription login, API key, telemetry or paid service is enabled by this code. Downloads contact Hugging Face.

Settings are stored under `%APPDATA%\Ava`; recordings and diagnostic logs under `%LOCALAPPDATA%\Ava`. The last 200 WAV recordings are retained locally. Delivery logs contain diagnostic state, not dictated text. Do not publish recordings, settings or logs.

Automatic paste is restricted to explicitly writable/non-password controls and empty/plain-text clipboard contents. Rich clipboard formats remain untouched. Native focus checks and SendInput cannot be atomic; validate behavior in the apps you use. Ctrl+Shift+Z is reserved while global shortcuts are active, including before the first dictation.

## Roadmap

Windows runtime validation; simpler setup; supported-runtime migration; local LLM polishing; review-before-apply voice editing; history, vocabulary and snippets; polished tray/onboarding; signed installer and updater. None of these future features is implied to be complete.

## Licensing

No open-source license has been granted for original project code yet. Public visibility alone does not grant general reuse or redistribution rights. Dependency and model licenses are separate; see [third-party information](THIRD_PARTY_NOTICES.md). Binary redistribution remains gated on a complete dependency-notice review.
