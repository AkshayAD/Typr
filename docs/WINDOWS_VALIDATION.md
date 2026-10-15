# Windows validation checklist

Cross-compilation and pure tests do not verify Windows runtime behavior. Do not drive the keyboard while someone is using the computer; use a clearly agreed idle test window and disposable local text.

1. Check the OS, CPU, available memory and audio device. Do not require administrator elevation.
2. Test model download, pause/resume, corruption detection, loading failures and responsive cancellation.
3. Test the communications-default microphone, 300 ms pre-roll and 300 ms release tail. Verify beginning/end words are preserved.
4. Exercise shortcut order, left/right modifiers, Shift added mid-hold, cancellation, hands-free, repeated starts and close-mic behavior.
5. Use `AvaVoice.exe --paste-target` for a separate-process disposable text field. Test changed focus, password and read-only fields, blocked/elevated targets and unresponsive accessibility providers. Do not test against active messages or private documents.
6. Verify no automatic retry after uncertain paste; no bare Enter submits text. Unknown targets must keep the result available for explicit copying.
7. Test plaintext clipboard restoration, rich clipboard preservation, a newer copy during delivery, and cancellation during the first 400 ms and later cleanup window. Ensure cancelled operations never paste later.
8. Test 100%, 125%, 150% and mixed-monitor DPI. Use layered-window-aware screenshots for overlays.
9. For deterministic replay, set `AVA_TEST_WAV` to a local PCM16 mono 16 kHz WAV, then launch. Clear this variable for normal microphone use. It does not grant permission for keyboard automation.
10. Benchmark 2 and 4 CPU threads on representative, locally held audio. Measure key-release, final transcript and actual insertion separately. Never label unpaced file-decode time as live dictation latency.

Known limitations: focus and SendInput cannot be atomic; some UI Automation checks can outlive their deadline, although cancelled/deadline-expired checks must not inject; unsupported clipboard formats disable automatic paste; Unicode typing fallback is intentionally not connected to avoid duplicates.

Stop release preparation if tests produce wrong-field insertion, delayed input after cancellation, clipboard loss, accidental submission, sticky modifiers or silent audio loss. Record findings without committing private text/audio or device identifiers.
