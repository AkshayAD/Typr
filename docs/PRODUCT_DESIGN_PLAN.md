# Typr: product, UI and UX implementation plan

Prepared 18 October 2026. This is the design and acceptance specification; completion evidence belongs in `IMPLEMENTATION_STATUS.md`. The plan precedes implementation. A planned feature is not a shipped feature.

## 1. Product ambition and success criteria

Make Typr a desktop tool people can understand in a minute, use without interrupting their writing, and trust with their words. The core promise is **your voice, at home on your PC**. Retain the existing native Windows/WPF foundation and local recognition: an Electron rewrite would add migration and distribution work without fixing dictation quality.

“At least as good as Wispr” is a product benchmark, not a claim we can establish from screenshots. Separate visual quality, workflow completeness, transcription accuracy, latency, app compatibility and release reliability. This work delivers a substantial native experience foundation, then records the remaining evidence needed for parity.

Release acceptance:

- A coherent Typr identity throughout the visible interface, with no engineering-pilot screen as the home page.
- A first-run user can find model setup, understand the one-time download, and reach a practice field without reading developer documentation.
- Every recording state has explicit text and an available next action; stopping/cancelling must never silently discard a recoverable completed result.
- Appearance, output behavior, captions and retention controls actually change behavior and persist across restart.
- Vocabulary and spoken snippets operate on completed text with predictable, bounded replacements; Raw mode bypasses these transformations.
- Locally retained transcript history can be searched, copied and explicitly cleared. Retention is disclosed and controllable.
- Existing focus checks, password-field restrictions, clipboard preservation and cancellation safeguards remain intact.
- Automated core/infrastructure checks and the Windows build pass; actual native smoke testing checks visible pages and interactions.

## 2. Existing product audit

The repository already has streaming local English speech recognition, pre-roll, Ctrl+Alt hold-to-talk, hands-free dictation, a non-activating caption overlay, resumable/hash-verified model download, and conservative clipboard delivery. These are worth retaining.

Current experience problems:

1. The window says Ava, while the repository/product says Typr. There is no consistent identity.
2. All controls sit in one long dark settings screen. Setup, practice, results and diagnostics compete for attention.
3. The main actions use engineering language: CPU threads, model folders and pilot phases are more prominent than speaking.
4. First-run has no clear progress, recommended path or completion milestone.
5. There is no usable history, vocabulary, snippet editor or appearance customization.
6. Caption and retention settings exist in the data model but are not connected to runtime behavior.
7. Raw mode still goes through cleanup. Audio is unconditionally written, with a fixed 200-file limit.
8. Esc cancellation ends at recording state; processing and delivery need explicit cancellation handling too.
9. Empty, loading, error, unavailable and success states are not designed as a system.
10. Accuracy, latency, real microphone behavior, target-app coverage and multi-monitor layout have not been demonstrated.

## 3. Competitive benchmark and differentiation

Research uses official Wispr sources retrieved 18 October 2026:

- [What is Wispr Flow?](https://docs.wisprflow.ai/articles/2772472373-what-is-flow): shortcut-driven dictation into the selected field, completed text after stopping, hands-free controls, recovery and permission setup.
- [Wispr Flow](https://wisprflow.ai/): cleanup, dictionary, snippets, writing styles and multilingual recognition.

These establish workflow expectations; marketing speed claims are not Typr measurements.

| Dimension | Typr target | First implementation | Evidence for full parity |
| --- | --- | --- | --- |
| Dictate anywhere | Shortcut, focus-safe insertion, clear fallback | Preserve existing delivery and improve recovery UX | Notepad, browser inputs, Office, chat, IDE and terminal matrix |
| Visual quality | Calm, consistent native workspace | Complete shell, typography, spacing and component states | Review at multiple resolutions, DPI and themes |
| Personalization | Vocabulary, snippets, output preferences | Explicit local replacement rules and Raw bypass | Accuracy corpus with names, punctuation and mixed cases |
| Writing styles | Preserve meaning while adapting tone | Honest Clean / Raw choices | Optional local polishing model and semantic guard tests |
| Languages | Accurate English, then Hindi/Hinglish | Clearly label English model | Evaluate multilingual models on representative speech |
| Privacy | Offline inference and understandable retention | Wired controls, local history, no cloud dependency | Storage audit, deletion checks, documented threat model |
| Speed | Immediate state feedback and fast completion | Keep existing engine; expose useful status | Measure first caption, stop-to-text and resource usage |
| Everyday reliability | Stable lifecycle, installer and updates | Build and native smoke evidence | Long-session tests, signing, update rollback and support |

Differentiation: no mandatory account, transparent local processing, deliberate retention controls, and personalization that users can inspect. Do not imply that deterministic replacements are adaptive AI or that English recognition handles 100+ languages.

## 4. Information architecture

Use a persistent left navigation rail and a vertically scrollable content area. Keep navigation and status visible while content scrolls. Target a 1180 × 820 desktop window, with usable minimum dimensions around 960 × 700 and appropriate scrolling at smaller work areas.

1. **Workspace** — readiness, start/stop practice dictation, editable practice field, latest recoverable result and useful shortcuts.
2. **History** — locally saved transcripts, text search, result selection, copy, individual removal and clear-all confirmation.
3. **Vocabulary** — “When I say” / “Write as” pairs for names and terminology; add, list and remove.
4. **Snippets** — spoken trigger phrases and reusable text, with multiline expansion, add/list/remove.
5. **Appearance** — Light, Dark and System themes; a restrained accent palette; live-caption visibility, background opacity and line count.
6. **Settings** — recommended local model setup, microphone lifecycle, Clean/Raw default, history and audio retention, keyboard reference and local diagnostics.

Keep advanced model path and CPU settings inside the setup area. Avoid account/profile placeholders, fabricated subscriptions, fake analytics and nonfunctional navigation.

## 5. Visual direction

### Identity

Typr should feel like a personal writing studio. Use a compact waveform mark, an expressive but simple wordmark, warm surfaces, crisp typography and one accent. Avoid excessive glass, gradients, card nesting and decorative dashboards.

### Tokens

- Light: warm near-white canvas, white surfaces, muted stone rail, near-black text, readable secondary text and subtle borders.
- Dark: charcoal canvas/surfaces, elevated panel separation, soft white text and lighter accents.
- Accent options: Iris, Forest and Ocean, each with corresponding readable foreground and subdued background.
- Semantic states: green for ready, amber for processing, red for failure, neutral for microphone closed; always pair with text.
- Typography: Segoe UI/Segoe UI Variable when present, 13–14 px body, 12 px labels, 22–30 px page titles and 36–42 px workspace statement. Use weight/size, not uppercase everywhere, to establish hierarchy.
- Spacing: 4/8/12/16/24/32 rhythm; 12–16 px card radii; 8–10 px control radii; 40–44 px minimum primary input height.
- Icons: native Segoe Fluent/MDL2 symbols with accessible text labels, or simple local vector geometry. No external font or image downloads required.
- Focus: visible high-contrast ring for keyboard navigation. Disabled actions remain readable and explain prerequisites nearby.

### Component system

Define shared WPF resource tokens and templates for primary/secondary/quiet/danger buttons, nav items, text inputs, combo boxes, toggles, cards, status pills, empty states and keyboard hints. Every template needs hover, pressed, focus, selected and disabled behavior. Dynamic resources allow immediate theme/accent updates without recreating the window.

## 6. Workspace specification

The top bar names the current section and states **Local processing · English**. The rail footer shows model/microphone readiness and links to setup.

The workspace begins with a small date-independent label and a clear statement: “Less typing. More you.” Supporting text explains holding Ctrl+Alt in any writable field. A compact primary action starts practice dictation when ready and opens setup otherwise.

Below it:

- Readiness banner: not downloaded / found / loading / microphone open / microphone closed / error.
- Main practice card: a labelled multiline input, Start/Finish action, Cancel action while active, elapsed listening time, microphone level and shortcut reminder.
- Latest result card: completed transcript, Copy, delivery status and pointer to History. Keep it visible when insertion fails.
- Compact shortcut guide: hold Ctrl+Alt; add Space for hands-free; Esc cancels; Ctrl+Shift+Z repeats the last result; add Shift for temporary Raw.

Avoid fake word/time-saved metrics. Any counts shown must derive from actual locally retained results and be clearly scoped.

## 7. First-run and setup flow

1. Explain local processing, English-only support and the approximate 632 MiB one-time download before starting it.
2. Provide **Download speech model** as the recommended route; disclose Hugging Face as download host. Retain advanced existing-folder input.
3. Show percentage and resumable cancellation. Pausing preserves partial download; integrity failure offers a retry without claiming success.
4. **Enable microphone** verifies and loads the model and opens capture; display loading state immediately. Prevent duplicate loads/downloads.
5. Focus the practice field after success. Use a suggested disposable sentence, never automatically start capturing on launch.
6. A completed practice result is available to copy even when target insertion is unavailable.
7. Keep **Close microphone** easy to find. Explain that warmed capture remains open for pre-roll until closed or the app exits.

Error recovery: missing files → download/select folder; microphone failure → clear error and retry after device correction; download interruption → resume; settings corruption → preserve original file and explain read-only fallback.

## 8. Dictation interaction and state model

| State | Visible feedback | Allowed actions | Exit condition |
| --- | --- | --- | --- |
| Setup needed | Model/mic prerequisites | Setup, edit preferences | Verified model loaded and microphone opened |
| Ready | Ready, shortcut hint | Hold or start practice | User starts recording |
| Listening | Listening + elapsed time + meter | Finish, Esc/Cancel | Release chord / hands-free finish / Cancel |
| Processing | Finishing your words | Esc/Cancel | Decoder result or cancellation |
| Delivering | Placing text | Cancel until delivery commits | Confirmed insertion or recoverable fallback |
| Complete | Result + delivery status | Copy, repeat, new recording | Next user action |
| Error | Specific failure + recovery | Retry, Copy available result | User chooses recovery |

Capture cancellation tokens and target identity per operation. Snapshot output preferences at recording start so editing settings cannot unpredictably change a recording already underway. Prevent overlapping capture/delivery. Do not announce a cancellation as undoing text that was already inserted.

For a clicked practice action, focus the practice field before capturing target identity. Do not refocus arbitrary external targets from a UI click. Global shortcuts retain the existing target validation.

## 9. Customization that works

### Output

- **Clean**: existing conservative cleanup plus explicit user vocabulary/snippet rules. Preserve numbers, negation and wording except the rule the user configured.
- **Raw**: recognized words without deterministic cleanup or personalization; no claim of verbatim audio fidelity beyond the recognizer.
- No “Professional”/“Casual” selector until an actual rewriting engine exists.

### Vocabulary

Fields: recognized phrase and preferred spelling. Trim/validate blank entries, reject duplicate source phrases case-insensitively, preserve explicit output capitalization and match whole phrases without corrupting substrings. Explain that this corrects completed text rather than retraining the speech model. Show a synthetic example such as “type er” → “Typr”.

### Snippets

Fields: spoken phrase and expansion. Support multiline text, literal punctuation and symbols. Match longer phrases first; avoid recursive expansion and collisions by using deterministic one-pass replacement. Explain that text is inserted literally, so “my signature” can expand to several lines. Raw mode bypasses expansion. Empty state shows examples without saving fake user data.

### Appearance/captions

Persist theme and accent immediately; System follows Windows appearance changes. Caption visibility, line count and background opacity affect the real overlay. Recording status remains discoverable even with live text disabled. Keep the overlay non-activating/click-through so it does not steal the user's writing focus.

### Retention

Make transcript-history retention explicit, bounded to 200 local entries, and controllable. New profiles do not save raw audio by default. If audio retention is enabled, enforce its configured limit rather than a hardcoded 200. Turning retention off stops future writes; clearing existing data requires an explicit action. Preserve legacy `%APPDATA%\Ava` and `%LOCALAPPDATA%\Ava` locations to avoid losing existing settings/model files during visual rebranding.

## 10. History and recovery

Store timestamp, completed text and output mode locally. Keep failed-to-insert results recoverable. Do not save partial captions or cancelled audio as successful transcripts. Use atomic file replacement and bounded storage. Handle malformed/inaccessible history without crashing or overwriting the original file.

History page: search by transcript text, show newest first with date/mode, open a selected full transcript, copy, remove one and clear all. Empty/search-empty states distinguish “nothing yet” from “no matches”. Use a confirmation for clearing all, with cancel as the safe default. Do not export or sync personal history automatically.

## 11. Accessibility and adaptation

- Use native WPF controls, proper labels and `AutomationProperties.Name` for unlabeled icon controls and editors.
- Make all navigation, editors and actions keyboard reachable in a sensible order.
- Preserve native window chrome/resizing and avoid a custom title bar with broken maximize/drag semantics.
- Provide text alongside state colors, visible focus and selectable transcripts.
- Wrap explanatory text and avoid fixed-height cards that clip at larger fonts/DPI.
- Test light/dark/accent states, minimum window size and keyboard-only navigation. High contrast should use system colors or be explicitly tracked if incomplete.
- Respect reduced-motion preference; no essential information depends on animation. Start with restrained static feedback and a functional level meter.

## 12. Engineering sequence and file ownership

1. Complete this plan and establish baseline test evidence.
2. Core/infrastructure work: typed appearance/retention settings, deterministic personalization, local history store, configurable audio retention, regression tests.
3. Native shell: WPF resources/templates, navigation, workspace and setup.
4. Connect vocabulary, snippets, history and preference controls to the real runtime and storage.
5. Update overlay and recording state feedback, repair Raw behavior and processing cancellation.
6. Build, run tests, start the native app, inspect screenshots and exercise representative UI flows; repair defects.
7. Perform independent source review and update the implementation evidence, README and validation checklist.
8. Publish the changes to a reviewable feature branch/PR; do not merge or release a signed installer in this task.

Keep original namespace/project/data-directory names where renaming would add migration risk. The visible product is Typr. Do not add a web dashboard, cloud service, telemetry or paid dependency to achieve the design.

## 13. Verification matrix

| Area | Automated checks | Native/manual evidence |
| --- | --- | --- |
| Baseline | Existing Core + Infrastructure suites | Existing behavior documented |
| Settings | Legacy migration, roundtrip, invalid enum/range, corruption | Restart restores theme/preferences |
| Personalization | Whole boundaries, longest match, literal symbols, non-recursion, Raw bypass | Add/remove entries and inspect feedback |
| History | Order/capacity, atomic save/load, opt-out, invalid JSON | Search, select, copy, empty state, clear confirmation |
| Audio | No write when disabled; bounded retention policy | Synthetic WAV recognition where feasible |
| Cancellation | Existing generation tests plus new processing/Esc logic coverage | Record/cancel/finish; no late insertion |
| UI | Windows Release compilation; testable presentation helpers | Every page, light/dark, accent, resize, keyboard focus |
| Clipboard | Existing eligibility/output guards | Disposable target tests; inspect restoration |
| Lifecycle | Load/close error paths reviewed | Close while idle/loading/recording and second-instance behavior |

Use synthetic phrases and fixtures. Never commit recordings, personal settings, real transcripts, clipboard contents or diagnostic logs. A build is not proof of microphone, recognition or external-app delivery correctness.

## 14. Measuring a Wispr-quality release

Treat these as proposed acceptance targets, to be measured on a named baseline Windows device:

- UI state feedback: within 100 ms of the user action under normal load.
- Final text: report median/p95 stop-to-text and delivery times separately; target median under 1 s after stop, then assess feasibility with the current 300 ms tail/model.
- Accuracy: score a versioned, consented English corpus; separately score proper names, punctuation, numbers and self-corrections. Measure word-error rate and edits needed before sending.
- Safety: zero unwanted insertion in password/read-only/changed-focus fixtures; cancelled operations must not produce a new paste.
- Recovery: completed text remains available after failed insertion; no repeated automatic paste after ambiguous verification.
- Onboarding: at least five first-time testers; >= 4/5 reach a correct practice result without assistance after model download.
- Reliability: multi-hour idle/active sessions, mic disconnect/reconnect, default-device changes, clipboard contention and 100+ consecutive captures.
- Resource use: report idle/loaded/recording memory and CPU, download size, disk retention and supported hardware.

Do not display unmeasured speed/accuracy claims in the product.

## 15. Follow-on milestones

**Milestone A — native experience foundation (this work):** real navigable UI, setup/practice, theme/accent/captions, dictionary/snippets/history and regression coverage.

**Milestone B — dependable daily dictation:** configurable conflict-aware global shortcuts; input-device picker/hot-swap; tray with clear microphone state; startup opt-in; stronger automated native delivery/cancellation tests; performance and accuracy corpus; long-session validation.

**Milestone C — writing quality:** evaluate local punctuation and polishing models; visible Clean/Raw/style comparison; review-before-apply edits; preservation checks for names/numbers/negation; optional per-app rules. Never silently add cloud processing.

**Milestone D — multilingual and distribution:** evaluate Hindi/Hinglish models rather than relabeling the English engine; migrate to a supported .NET LTS before public release; finish dependency/license notices; signed per-user installer, uninstall and authenticated updater with rollback; publish supported-app/hardware limitations.

**Milestone E — parity review:** run Typr and Wispr on the same consented tasks and hardware, compare time-to-result/edit burden/recovery, run usability sessions and close measured gaps. Only then make a defensible parity claim.
