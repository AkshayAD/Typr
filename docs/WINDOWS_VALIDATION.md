# Windows validation checklist

Cross-compilation and pure tests do not verify Windows runtime behavior. Do not drive the keyboard while someone is using the computer; use a clearly agreed idle test window and disposable local text.

The redesign has native WPF interaction tests and a real-recognizer synthetic replay check; [implementation evidence](IMPLEMENTATION_STATUS.md) records what actually passed. These do not replace the hardware, global-keyboard, external-app delivery and mixed-DPI checks below.

1. Check the OS, CPU, available memory and audio device. Do not require administrator elevation.
2. Test model download, pause/resume, corruption detection, loading failures and responsive cancellation.
3. Verify launch, model preparation and idle never open capture. Test the selected/default shared input, a 300 ms release tail, and immediate cancellation. No idle pre-roll remains; wait for Listening before speaking, especially on cold preparation.
4. Exercise shortcut order, left/right modifiers, Shift added mid-hold, cancellation, hands-free, repeated starts and unload behavior. Release/Esc during cold preparation must never open capture later. Disable keyboard shortcuts and verify the hold/repeat commands are released.
5. Use `Typr.exe --paste-target` for a separate-process disposable text field. Test changed focus, password and read-only fields, blocked/elevated targets and unresponsive accessibility providers. Do not test against active messages or private documents.
6. Verify no automatic retry after uncertain paste; no bare Enter submits text. Unknown targets must keep the result available for explicit copying.
7. Test plaintext clipboard restoration, rich clipboard preservation, a newer copy during delivery, and cancellation during the first 400 ms and later cleanup window. Ensure cancelled operations never paste later.
8. Test 100%, 125%, 150% and mixed-monitor DPI. Use layered-window-aware screenshots for overlays.
9. For deterministic replay, set `TYPR_REPLAY_WAV` to a local PCM16 mono 16 kHz WAV, then launch. Clear this variable for normal microphone use. It does not grant permission for keyboard automation.
10. Benchmark 2 and 4 CPU threads on representative, locally held audio. Measure key-release, final transcript and actual insertion separately. Never label unpaced file-decode time as live dictation latency.

Known limitations: focus and SendInput cannot be atomic; some UI Automation checks can outlive their deadline, although cancelled/deadline-expired checks must not inject. A busy clipboard or a format Typr cannot safely preserve requires Copy text recovery; automatic insertion sends no input in those cases. Batched Unicode key packets are disabled after the native fixture demonstrated character corruption. Delivery never retries after an ambiguous input attempt.

Stop release preparation if tests produce wrong-field insertion, delayed input after cancellation, clipboard loss, accidental submission, sticky modifiers or silent audio loss. Record findings without committing private text/audio or device identifiers.

## Companion, providers and history

11. Drag the bar from every control, including hold. Verify no click/paste on drag, hold cancellation, delayed-load release, hover anchoring and right-click choices. Hide/show/reset position and restart.
12. Check command-shortcut conflicts, prepare/unload with speech unloaded, shortcut-only defaults and explicit bar/button opt-in. Verify finish/cancel remain available, caption visibility, opacity/size/hover delay, high contrast and appearance reset.
13. Use each provider’s own official sign-in. Verify disconnected/expired/limited accounts preserve local output. Compare the same synthetic sample across advertised efforts. Cancel while a request starts; no late paste.
14. Check private CLI install/update, digest failures, managed-version selection and preserved global installations. Verify no API-credit substitution or dictated-content telemetry.
15. Enable opt-in retention in an isolated test profile. Search original/final text, play/stop synthetic audio, preview re-polish/re-transcription, discard/apply, restore previous metadata, remove and restart. Test missing audio and missing/corrupt local models.
16. Check a non-destructive old-profile migration, an existing destination and failed staging. Never test destructive recovery against personal data.
17. Verify a concurrent call retains its own mute/input state and video/game playback continues. Test a built-in/USB Typr input first; separately observe the documented Bluetooth Classic voice-profile transition when the headset microphone is deliberately selected. Record actual compatibility, CPU and memory observations without promising zero impact.

The current screen-use restriction and precise remaining checks are tracked in [Deferred desktop tests](DEFERRED_DESKTOP_TESTS.md). Do not launch them until the user resumes screen testing.
