# Development

- `src/Ava.Core`: pure hotkey, cleanup/guard, caption, delivery-plan and settings logic.
- `src/Ava.Infrastructure`: model download/integrity, audio conversion and streaming recognition.
- `src/Ava.Windows`: WPF host, hook, overlays and conservative clipboard delivery.
- `tools/Ava.Benchmark`: local 16kHz mono PCM16 WAV recognition benchmark, comparing 2/4 threads.
- `tests`: core and infrastructure tests.

Run the tests and cross-build before proposing changes. A Windows tester must separately validate native behavior. Do not treat a Linux pass as a microphone or paste pass.

No GitHub Actions workflow is configured. Build/test commands run locally; no cloud CI spending is requested or implied.

## Current evidence

The original baseline passed 98 core tests and 9 infrastructure tests. The redesign adds settings/personalization, history/audio-retention, and native WPF interaction coverage. Current counts and native observations are in [Implementation status](IMPLEMENTATION_STATUS.md). The WPF suite runs on Windows with isolated temporary profiles; it does not access a real microphone or clipboard. Synthetic replay exercises the actual recognizer separately. Real-device/external-app validation is still required.

## Native interface tests

```powershell
dotnet test tests/Ava.Windows.Tests -c Release
```

The suite owns one STA dispatcher, loads the actual application resources, and invokes real controls/events in isolated windows. It verifies navigation, persistence/reopen, vocabulary/snippets, history search/selection/removal, actual overlay preferences, corrupt-file preservation, failed-save session behavior and idle window closure. It does not invoke application startup or install global hooks.

## Opt-in real-model replay

Use an explicitly synthetic or consented PCM16, mono, 16 kHz WAV. Neither model files nor WAV fixtures are committed.

```powershell
$env:TYPR_TEST_MODEL = (Resolve-Path .models/nemotron).Path
$env:TYPR_TEST_WAV = (Resolve-Path artifacts/synthetic-test.wav).Path
dotnet test tests/Ava.Infrastructure.Tests -c Release
```

The replay test runs paced audio through `SpeechSession`, finalizes a transcript, cancels during finalization, then verifies a fresh recording returns the same transcript under a different generation. It uses no microphone. Without both environment variables the replay test is explicitly skipped.

For interactive native replay, set `AVA_TEST_WAV` only in the process launching Typr, then use the normal setup/practice buttons. Clear it before ordinary use. Do not treat injected keyboard input as a physical-global-shortcut test: the native hook intentionally ignores injected input.

## Contribution privacy

Use synthetic fixtures. Never commit raw recordings, dictated text, clipboard contents, account identifiers, credentials, settings, local file paths, private document links or personal machine inventories. Public issues must be sanitized before posting.
