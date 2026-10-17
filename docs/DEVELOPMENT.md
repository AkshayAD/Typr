# Development

- `src/Typr.Core`: pure hotkey, cleanup/guard, caption, delivery-plan and settings logic.
- `src/Typr.Infrastructure`: model download/integrity, audio conversion and streaming recognition.
- `src/Typr.Windows`: WPF host, hook, overlays and conservative clipboard delivery.
- `tools/Typr.Benchmark`: local 16kHz mono PCM16 WAV recognition benchmark, comparing 2/4 threads.
- `tests`: core and infrastructure tests.

Run the tests and cross-build before proposing changes. A Windows tester must separately validate native behavior. Do not treat a Linux pass as a microphone or paste pass.

No GitHub Actions workflow is configured. Build/test commands run locally; no cloud CI spending is requested or implied.

## Current evidence

The original baseline passed 98 core tests and 9 infrastructure tests. The redesign adds settings/personalization, history/audio-retention, and native WPF interaction coverage. Current counts and native observations are in [Implementation status](IMPLEMENTATION_STATUS.md). The WPF presentation suite uses isolated temporary profiles with no microphone. Separate DeliveryIntegrationTests explicitly drive a disposable native target and preserve/restore the clipboard; run them only while the desktop is idle. Synthetic replay exercises the actual recognizer separately. Real-device and broader target-application validation remain required.

## Native interface tests

```powershell
dotnet test tests/Typr.Windows.Tests -c Release
```

The suite owns one STA dispatcher, loads the actual application resources, and invokes real controls/events in isolated windows. It verifies navigation, persistence/reopen, vocabulary/snippets, history search/selection/removal, actual overlay preferences, corrupt-file preservation, failed-save session behavior and idle window closure. It does not invoke application startup or install global hooks.

## Opt-in real-model replay

Use an explicitly synthetic or consented PCM16, mono, 16 kHz WAV. Neither model files nor WAV fixtures are committed.

```powershell
$env:TYPR_TEST_MODEL = (Resolve-Path .models/nemotron).Path
$env:TYPR_TEST_WAV = (Resolve-Path artifacts/synthetic-test.wav).Path
dotnet test tests/Typr.Infrastructure.Tests -c Release
```

The replay test runs paced audio through `SpeechSession`, finalizes a transcript, cancels during finalization, then verifies a fresh recording returns the same transcript under a different generation. It uses no microphone. Without both environment variables the replay test is explicitly skipped.

Screen testing resumed after gaming; current outstanding checks and the computer-use policy stop are [recorded here](DEFERRED_DESKTOP_TESTS.md). For interactive native replay, set `TYPR_REPLAY_WAV` only in the process launching Typr, then prepare speech and use a recording shortcut (or explicitly opt into button recording). Clear it before ordinary use. Do not treat injected keyboard input as a physical-global-shortcut test: the native hook intentionally ignores injected input.

## Contribution privacy

Use synthetic fixtures. Never commit raw recordings, dictated text, clipboard contents, account identifiers, credentials, settings, local file paths, private document links or personal machine inventories. Public issues must be sanitized before posting.

## Headless checks during active desktop use

```powershell
dotnet test tests/Typr.Core.Tests -c Release
dotnet test tests/Typr.Infrastructure.Tests -c Release --filter 'FullyQualifiedName!~SpeechReplayTests'
dotnet test tests/Typr.Windows.Tests -c Release --filter 'FullyQualifiedName~WindowIntegrationTests|FullyQualifiedName~BarPresentationTests|FullyQualifiedName~RecordingPreparationTests'
```

These named Windows fixtures never show windows or create native handles/hooks, inspect external fields, enumerate devices or send input. RecordingPreparationTests use the actual speech worker with fake capture/recognition to check cold-start cancellation and shortcut-only consent. MicrophoneLifecycleTests cover shared input ownership with fake adapters. Keep real replay/model environment variables unset. Broader native suites and inference belong to an idle session.
