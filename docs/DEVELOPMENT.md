# Development

- `src/Ava.Core`: pure hotkey, cleanup/guard, caption, delivery-plan and settings logic.
- `src/Ava.Infrastructure`: model download/integrity, audio conversion and streaming recognition.
- `src/Ava.Windows`: WPF host, hook, overlays and conservative clipboard delivery.
- `tools/Ava.Benchmark`: local 16kHz mono PCM16 WAV recognition benchmark, comparing 2/4 threads.
- `tests`: core and infrastructure tests.

Run the tests and cross-build before proposing changes. A Windows tester must separately validate native behavior. Do not treat a Linux pass as a microphone or paste pass.

No GitHub Actions workflow is configured. Build/test commands run locally; no cloud CI spending is requested or implied.

## Current evidence

The preview source passed 98 pure-core tests and 9 infrastructure tests. Its Windows x64 Release cross-build passed with zero warnings/errors. An independent static review covered focus eligibility, clipboard ownership, cancellation and late asynchronous operations. Native Windows validation is still pending.

## Contribution privacy

Use synthetic fixtures. Never commit raw recordings, dictated text, clipboard contents, account identifiers, credentials, settings, local file paths, private document links or personal machine inventories. Public issues must be sanitized before posting.
