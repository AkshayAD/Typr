# Typr redesign: delivered work and verification

Date: 18 October 2026. Specification: [Product and UI/UX plan](PRODUCT_DESIGN_PLAN.md).

## Delivered in this change

| Area | Implemented behavior |
| --- | --- |
| Native design | Typr identity/icon, warm light and charcoal dark surfaces, shared controls, persistent navigation, scrolling content and visible keyboard focus |
| Workspace | Clear setup/readiness, visible Start/Finish/Cancel practice controls, elapsed listening time, microphone meter, latest result and Copy recovery |
| Setup | Recommended model explanation, existing-folder advanced option, resumable verified download, explicit microphone enable/close and progress/error states |
| Appearance | System/Light/Dark, Iris/Forest/Ocean accents, immediate application and persistence, system-theme changes and high-contrast resource mapping |
| Captions | Real visibility/indicator/line/background controls; readable new default, minimum 60% black scrim for legacy low-opacity settings, no faded transcript foreground |
| Output | Clean uses conservative cleanup and explicit phrase rules; Raw bypasses cleanup, vocabulary and snippets |
| Vocabulary/snippets | Add/remove, literal whole-phrase matching, longest trigger first, snippet precedence, Unicode boundaries, no recursive expansion, duplicate/blank/size checks |
| History | Separately opt-in, newest 200 local entries, search, selection, full text, mode, Copy, removal and clear-all confirmation; disabling history prevents later commits even during a capture |
| Audio retention | New profiles default off; configured bound honored; aborted/faulted recordings discarded; enabling/disabling applies to subsequent recordings |
| Data resilience | Existing settings version/data paths preserved; atomic saves; unreadable/future-version files preserved; failed preference writes apply for the session with an explanation |
| Cancellation/lifecycle | Esc remains active through processing, claimed Esc key-up is suppressed consistently, recording generations/session identity guard captions, speech faults require recovery, idle-close reentrancy fixed |

No backend, telemetry, mandatory account, cloud transcription, API key or paid service was introduced. Namespace/project/executable names remain internally Ava for compatibility.

## Automated evidence

Original baseline: **98 Core + 9 Infrastructure tests passed** before implementation.

Current Windows Release checks:

| Check | Result |
| --- | --- |
| `dotnet test tests/Ava.Core.Tests -c Release` | 133 passed |
| `dotnet test tests/Ava.Infrastructure.Tests -c Release` with explicit synthetic replay configuration | 28 passed, 0 skipped |
| `dotnet test tests/Ava.Windows.Tests -c Release` | 9 passed |
| `dotnet build src/Ava.Windows -c Release -m:1 -p:BuildInParallel=false` | Passed, 0 warnings, 0 errors |
| Self-contained Windows x64 publish to `artifacts/windows` | Passed; local development build only |
| `git diff --check` | Passed |

Total: **170 passing tests**, including the opt-in real-model replay. Without the configured model/WAV, the Infrastructure suite reports 27 passed and one explicit skip.

The WPF suite loads actual product resources on an STA dispatcher and uses isolated temporary settings/history directories. It exercises navigation, theme/accent persistence and reopening, vocabulary/snippet buttons, history search/selection/removal, real caption overlay settings, corrupt settings/history preservation, failed preference writes and actual window-close completion. It never opens a microphone or changes the system clipboard.

The real-model test uses the pinned local Nemotron model and synthetic PCM16/mono/16 kHz speech. Audio feeds at recording pace through `SpeechSession`. It verifies meaningful recognition, cancellation during the 300 ms finalization tail, generation changes and identical recognition after a fresh start. This establishes a synthetic engine path, not real-microphone quality or global-hotkey correctness.

## Observed native Windows behavior

Using the actual running WPF application:

- Inspected the native workspace in light and dark themes and the appearance/setup pages. Corrected inherited button/navigation text colors, a Unicode-encoding regression and default workspace spacing based on rendered output.
- Changed System → Light using the native theme selector; the interface updated immediately and restored the choice on relaunch.
- Loaded verified local speech with `AVA_TEST_WAV` scoped to the test launch; no live voice was recorded.
- Chose Start → Finish in the practice card. The actual recognizer returned the synthetic sentence, and both the practice field and latest-result recovery displayed it.
- The observed run reported **794 ms** from stop/finalization to practice insertion, including the 300 ms audio tail. This is one synthetic run, not median/p95 latency or an accuracy comparison.
- Started another practice capture and pressed Esc. Listening stopped, controls returned to Ready, and the previous practice/latest transcript remained intact.
- Closed the speech session through Settings; the UI released recording controls and reported microphone/global-shortcut closure.

The model download encountered an interrupted response; the existing resume path completed the partial transfer and verified all pinned files. Model/WAV/log artifacts remain ignored and are not part of the source change.

## Independent review and fixes

Independent source review identified and the implementation repaired:

1. Preference-write failure showing a disabled retention control while continuing the old behavior.
2. Escape being unavailable during processing and externally focused button-started practice capture.
3. Caption callbacks accepting an older recording's output after cancellation/restart.
4. A failed speech session continuing to advertise readiness and a recovery button that did nothing.
5. Hidden external error feedback after cancellation hid the overlay.
6. Low-contrast captions and button text, including pressed-state opacity.
7. Radio selection/page mismatch during keyboard navigation.
8. A history opt-out during recording not being checked at commit time.

WPF integration testing additionally found the synchronous Close-inside-Closing crash. Closure is now deferred through the dispatcher and covered by a real Closed-event assertion.

## Remaining release work

This is Milestone A from the plan, not a declaration of Wispr parity. The following remain unverified or unimplemented:

- Real microphone hardware/permission/device changes and long-session resource behavior.
- Physical global-shortcut combinations, modifier ordering and cancellation while an external application has focus. Injected test keys are deliberately ignored by the global hook.
- External-app safe insertion, changed-focus/password/read-only cases, actual clipboard restoration/contention and elevated/unresponsive targets on this build. Existing guards are preserved; tests are not a substitute for native target-app validation.
- Multi-monitor/mixed-DPI and screen-reader/high-contrast usability validation. High-contrast resources exist, but full assistive-technology testing is pending.
- Dedicated microphone selection, customizable/conflict-aware global shortcuts, tray/startup controls and per-app behavior.
- AI punctuation/polishing/style adaptation, review-before-apply voice edits and Hindi/Hinglish/multilingual recognition.
- Measured speech corpus, comparative Wispr tasks and first-time-user usability sessions.
- Supported-LTS runtime migration, licensing/notice completion, signed installer, updater and public release.

See [Windows validation](WINDOWS_VALIDATION.md) and the plan's follow-on milestones. No production deployment or merge is part of this change.
