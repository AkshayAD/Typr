# Desktop verification: 18 October 2026

The user explicitly resumed screen testing after gaming. Tests used isolated temporary profiles, synthetic audio and disposable fields. Personal settings/history and provider credentials were not used. No real microphone recording was retained.

## Verified on the current dictation path

- Full Windows Release suite before the minimal-pill follow-up: **45 passed**, including five native delivery tests and the nonactivating window style check. Rich clipboard restoration, busy clipboard recovery without typing, cancelled delivery, changed-focus refusal and editable/read-only/password UIA handling passed.
- Full Infrastructure Release suite with real Nemotron synthetic replay: **101 passed, zero skipped**, no build warnings/errors. Paced recognition, cancelled finalization and a fresh recording generation returned the expected sentence. The real-model case took 24.26 seconds including several recordings/cancellation checks.
- Actual desktop end-to-end test: prepare local speech with **Ctrl+Shift+M**, focus a disposable separate-process WPF text field, start with **Ctrl+Shift+Space**, replay the synthetic 5.92-second sample, finish with the same shortcut. The sentence appeared once in the original field; Typr reported **Pasted and verified. AI polish is off.** The insertion log confirmed its normal four keyboard events, matching foreground destination and released modifiers.
- That end-to-end run exercised the actual recognizer, registered global shortcuts, target capture, formatting bypass/local output and native clipboard delivery together. It was not a direct text-injection substitute for recognition. It used synthetic replay, so it establishes no physical microphone quality or hardware lifetime result.
- On-screen inspection confirmed the former full-width translucent toolbar and shortcut-only disabled starts. The user then requested a substantially smaller resting pill and growth on hover. The later pill change needs its own physical visual/dragging check.

## Bounded local model measurement

The existing benchmark processed the same 5.915-second synthetic file three times per thread setting. All six runs returned the same expected phrase.

| CPU threads | Cold load | Mean file decode including finalization | Mean finalization | Working set |
| --- | --- | --- | --- | --- |
| 2 | 4.25 s | 2.14 s, RTF 0.362 | 348 ms | 764–770 MiB |
| 4 | 3.74 s | 1.74 s, RTF 0.294 | 280 ms | About 772 MiB |

These are file-decode measurements on one machine, not measured live key-release-to-paste latency, sustained game impact or recognition accuracy. Two threads remains the conservative default. No replacement model was downloaded and no GPU recognition was enabled.

## Minimal-pill follow-up

- Idle footprint **96 by 22 DIPs** before scale, with a static neutral waveform. Hover grows to **280 by 44 DIPs**, revealing icon-only configured actions; right-click retains labeled choices. Recording/processing keep finish/cancel accessible.
- Growth lasts 130 ms with easing and respects disabled Windows animations/high contrast. Its timer stops at completion; the separate gesture watchdog runs only while a real mouse press is active.
- Fixed stale right-click actions on the next hover, caption/state changes beneath a pressed control, frozen-frame off-screen dragging and release jumps from clamped edges. Normalized resting anchors only after a real drag begins.
- **85 hidden Windows checks passed**, including 62 bar cases and 32 edge/corner/content combinations. These test actual components and product resources without displaying windows, acquiring native handles, or sending input.
- Light/dark idle, hover, context and recording resource renders were generated and visually inspected. The final self-contained build/publish passed. Its physical on-screen drag/hover behavior remains unverified after the computer-use stop.

## Screen-test interruption and limits

Browser automation rejected the local-file test page because file URLs are blocked. A public HTTPS accessibility example was opened as a safer field with no local-file access. Windows computer use then stopped for the turn because it could not determine the current browser URL confidently enough to enforce its policy. No native browser shortcut/input was sent after that stop, and no browser paste success is claimed. The test processes were cleaned up without operating other applications.

Remaining checks: browser fields, physical hold keys, actual mic indicator/device release, calls/Bluetooth playback, final pill hover/drag across displays, DPI transitions and authenticated private-provider quality. Keep these marked incomplete until observed; see the [remaining checklist](DEFERRED_DESKTOP_TESTS.md).
