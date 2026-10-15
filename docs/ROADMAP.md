# Usability roadmap

## Next: prove the Windows loop

Validate microphone, cancellation, safe insertion and clipboard restoration in a disposable text field before adding more features. Keep transcript recovery through Copy result prominent.

## Make the first run simple

One guided sequence: microphone meter, one recommended local speech model with resumable download, shortcut practice, then a disposable external text-field check. Show unmistakable listening/processing/ready feedback without stealing focus, and make Esc cancellation obvious. The current model is English-only; Hindi/Hinglish needs a separate quality and hardware evaluation.

## Install and remove cleanly

After Windows validation and dependency-notice review, add a per-user installer, Start menu entry and clean uninstall. Startup-at-login should be explicit. Signing and SmartScreen reputation are separate work; an unsigned download cannot be promised warning-free.

## Keep local and cloud choices clear

Local recognition is the baseline. Optional local polishing and any future cloud providers need clearly separated setup, privacy, latency and cost explanations. No provider is enabled merely because it appears on this roadmap.

## Design references, not copied implementations

- [Handy](https://github.com/cjpais/Handy): local dictation onboarding and simple model choice; inspect its current license before reusing any code.
- [VoiceInk shortcuts](https://tryvoiceink.com/docs/shortcuts): discoverability and keyboard workflows; UX research only.
- [Superwhisper models](https://ai.superwhisper.com/models): distinguish local and cloud model choices.
- [Epicenter](https://github.com/EpicenterHQ/epicenter): workflow research only; do not assume historic permissive licensing applies to current code.
- [Microsoft SmartScreen reputation guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation): installer/signing expectations.

These references do not imply affiliation, license compatibility, copied code or completed features.
