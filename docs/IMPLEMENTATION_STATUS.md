# Typr: implementation and verification

Date: 18 October 2026. Specifications: [Product/UI plan](PRODUCT_DESIGN_PLAN.md) and [daily dictation extension](COMPANION_AND_POLISH_PLAN.md). Resumed desktop results are recorded in [desktop verification](DESKTOP_VALIDATION_2026-10-18.md), with broader remaining checks in [deferred desktop tests](DEFERRED_DESKTOP_TESTS.md); efficient English alternatives are assessed in [model research](ENGLISH_MODEL_RESEARCH.md).

## Delivered

| Area | Behavior |
| --- | --- |
| Identity and requirements | Typr UI, projects, namespaces, executable and data folders; requirements in Settings/README; staged profile migration preserves originals and existing destinations |
| Native design | Workspace, History, Vocabulary, Snippets, Appearance and Settings; dynamic light/dark/system themes, three accents and keyboard focus |
| External insertion | Fixed clipboard sequence sampling after CloseClipboard; deadline-bounded off-dispatcher clipboard rendering, cancelled-read write revocation and explicit failed-restore recovery; supported rich formats preserved; foreground capture waits within its deadline for background polling; fresh keyboard paste-last target; focus/password/read-only/cancellation guards; no retry after ambiguous input |
| Companion | Default Silk capsule: 96 by 16 DIPs at rest and 192 by 32 on hover; listening waveform and finishing dots stay compact; nonactivating caption/menu popups, four hover icons plus configured overflow; finite eased growth, draggable controls, click/hold gestures, independent hover/right-click choices, hide/show/reset; 20-100% background opacity, three sizes and four hover delays, solid high-contrast surface |
| Keyboard | Cold-start physical hold plus configurable conflict-aware prepare/start-finish/paste shortcuts; shortcut-only recording by default, explicit bar/button opt-in and one master keyboard switch |
| Writing quality | Default exact GPT-6 Luna with low reasoning, optional ChatGPT/Claude subscription polishing, provider priority, strict text output, Roman Hindi/digits/lists/corrections prompt, local fallback and Raw bypass |
| Providers | Official CLI-owned login, status/models/efforts, cancellation, private install/update, integrity checks, isolated text-only requests and no API-credit fallback |
| Recovery | Original/final history search, real metadata and delivery outcome, retained-audio playback, editable reprocessing preview, apply/discard and ten prior versions with complete model/provider/effort restore |
| Microphone lifecycle | Prepare/idle never construct or enumerate capture; selected/default shared WASAPI only for deliberate recording; normal stop revokes pending starts; 300ms tail for active input; cancel/fault/unload/exit release input; actual closure feedback and no Windows mute/default/output changes |
| Audio | Optional local multilingual Small/Large v3 re-transcription, pinned downloads/hashes, bounded chunks and cancellation around native decoding |
| Storage | Independent opt-in history/audio retention, immutable history snapshots, atomic byte-bounded saves, corrupt/future-file preservation |

Speech recognition stays local. Enabling AI polish sends recognized text to the selected provider through its official CLI. No raw audio is sent for polishing. There is no mandatory account for local mode, telemetry backend or API-key billing fallback.

## Automated evidence

- Latest Core Release suite: **188 passed**.
- Infrastructure Release suite including real-model synthetic replay: **123 passed, zero skipped**. Includes unread native-packet stop/drain, packet ownership/thread and failure checks, immediate release before any partial with fixture silence removed, direct small-block streaming/caption deduplication, pending audio, converter duration/flush and cancellation/restart.
- Latest full Windows Release suite: **122 passed, zero skipped**, including three readiness checks, recording-state regressions, native focus/delivery cases, nonactivation, clipboard transactions, compact Silk activity states, scaled hover bounds, popup overflow and menu-toggle checks. One initial readiness fixture failed allocating dummy model files on a low-space drive; sparse fixtures repaired the test. The complete suite then passed.
- Actual shared microphone start/finish/cancel/unload and same-display bar hover/background/icon dragging were observed. Real recognizer replay automatically pasted the full sentence into Edge once; paste-last appended exactly one further copy. Both were verified by Typr and independent browser value reads. See the desktop evidence for scope.
- Windows Release build and self-contained x64 publish passed. This remains a development preview, not a public signed release.
- Focused security review after repairs: 30 provider tests passed; 13 prompt/output tests are included in the Core suite.
- History/revision checks include an actual 128 MiB serialized-budget rejection, preserved old state, reloadability, immutable revisions and cross-provider metadata restoration.
- Migration checks cover preservation, staging, locks/bounds, reparse rejection, existing-destination precedence and model-path confinement.

Before the latest on-demand/transparency follow-up, a complete run passed 160 Core, 85 Infrastructure (including real replay), and 28 Windows tests (including native delivery). Those older runs do not establish runtime behavior of the latest changes.

The WPF presentation checks use actual product resources, controls and temporary profiles. Native delivery cases intentionally create a disposable separate-process text editor and use synthetic input/clipboard data; they require an idle desktop. Physical keyboard-hook behavior and real microphone quality are separate checks.

## Earlier engine, provider and maintenance checks

A synthetic 5.92-second PCM16 mono 16 kHz recording was replayed through the real Nemotron engine, including cancellation during finalization and a fresh generation. No personal voice was recorded.

The optional Small model was downloaded and hash-verified: 375,485,327 bytes in 51.6 seconds. Two-thread native re-transcription returned the expected synthetic English sentence in 8.2 seconds. These are single-device observations, not latency/accuracy guarantees. Large v3 and Hindi audio accuracy have not been benchmarked.

Before the final microphone/transparency follow-up, an initial subscription-authorized synthetic Luna comparison ran all five actual CLI-advertised efforts: low 6.10 s, medium 4.09 s, high 5.41 s, xhigh 6.17 s, max 4.60 s. Each preserved Roman Hindi, the explicit corrected time, numeric quantities and three list items without assistant commentary. This was one run per effort, affected by network/warm-up; it does not establish a speed ranking or a semantic guarantee. Low remains the default. Those observations preceded the final private-profile isolation; the final connection requires the user's own browser sign-in and has not yet been validated with authenticated inference after that final change.

The final native Codex protocol was probed offline using a local fake endpoint on both default 0.161.0 and managed stable 0.163.0: **zero advertised tools, zero outside instruction sources, no personal instruction content**. No cloud inference or credentials were involved. Inference uses a private CLI-managed home, ephemeral text-only model metadata, no environments/dynamic tools, explicit MCP disabling, and notification/telemetry restrictions. Cancellation while turn/start is pending disposes the dedicated child. Claude disables tools/customizations/session persistence and inherited telemetry collectors, validates exact supported model/effort selections and rejects model substitution.

Actual private CLI maintenance passed for **Codex 0.163.0** and **Claude Code 2.1.302**: fetch, digest verification, install, launch/version and a subsequent no-update check. Global installations and credential stores were unchanged. The test installs are ignored local artifacts.

## Independent review repairs

Review and regression checks repaired stale provider dropdowns, saved reasoning visibility, comparison-sample changes mid-run, history metadata restoration/oversize writes, late hold start after release, practice versus external target confusion, and missing personalization before re-transcription polish. On-demand review also repaired release during queued stream/input creation, zero-input recognition, input changes during preparation, premature Listening/off labels and delayed shutdown cancellation. Empty recordings preserve the last successful transcript. Provider review repaired file/tool/custom-instruction exposure, inherited prompt telemetry/notifications, pending-turn cancellation, sign-in process cleanup, relative executable lookup and unsupported model/effort substitution.

## Limits and remaining release work

- The live recognizer is English. Roman Hindi polish cannot recover Hindi speech that was missed; multilingual audio re-transcription requires opt-in retention. Full live bilingual capture remains work.
- Private ChatGPT sign-in and Claude sign-in require the human user's browser action. Final authenticated Claude quality has not been tested; initial Luna observations are described above with their scope.
- Native fixture success does not establish every text editor/browser/enterprise/elevated application. Focus changes and input injection cannot be atomic; ambiguous delivery is reported and not repeated.
- Physical hold/Raw/hands-free chords, real spoken-word quality/device changes, calls/Bluetooth/game coexistence, multi-hour sessions, physical bar dragging across displays, full mixed-DPI/multi-monitor and assistive-technology evaluation remain pending. Basic actual microphone release, same-display pill mouse interactions, browser dictation insertion and keyboard paste-last passed the resumed session.
- Hardware recommendations are provisional. No certified minimum-device accuracy or memory study has been performed.
- A supported LTS runtime migration, complete binary license bundle, signing, installer and authenticated Typr updater are public-release prerequisites. CLI updates are implemented; they do not update Typr itself.
- No merge or production deployment is part of this change.

## Desktop follow-up

The human hold-to-talk check returned text but exposed lost ending audio and poor perceived quality/latency. The subsequent [reliability and bar refinement](DICTATION_RELIABILITY_AND_BAR_REFINEMENT.md) fixes reproduced queue overflow, final converter samples, packetized resampling duration and rejected-chord cancellation. Complete-frame synthetic recognition/paste passed; fresh human accuracy remains unverified. The user selected Silk: the native default now has a thinner 96 by 16 DIP capsule, grows only on hover to 192 by 32, and conveys listening/finishing with waveform/dots. Captions and additional actions use separate popups. Native compact-state recognition/paste and menu/icon dragging passed.

The next user report confirmed paste works but immediate release still loses ending words. The stricter real-model test now removes the fixture's 790 ms trailing quiet and releases before any partial; it preserves the exact complete sentence. A separate native stop defect was repaired: Windows packets unread by NAudio's stopping loop are now drained on the capture thread before converter flush and session finalization. This closes a confirmed capture gap; human speech still needs a fresh same-phrase check. See the follow-up in the reliability document for evidence and scope.

The new native capture source passed an actual no-retention microphone start/finish check: preparation stayed off, Listening opened capture, and finish closed it before recognition completed. Direct recognizer feeding removes redundant 560 ms application batching. A bounded same-model four-thread replay produced its first meaningful partial at 1,240 versus 1,680 ms of input audio, retaining the exact sentence and the same native decode count. This is a 440 ms input-cadence improvement on one sample, not a general stop-to-paste guarantee. The workspace now offers Prepare speech for an installed/unloaded model; shortcut-only ready controls explain the recording chord. Unload speech is explained as freeing model memory without changing Windows mute. Model/provider defaults and user preferences remain intact.

The user resumed testing. The full native Windows suite, actual microphone lifecycle, same-display pill mouse interaction and a real recognizer-to-browser-paste flow passed. The session repaired background focus-capture contention, stale keyboard repeat-paste targets and dispatcher-blocking clipboard reads. See [desktop verification evidence](DESKTOP_VALIDATION_2026-10-18.md) for exact scope and remaining physical/hardware checks.

The bar was reduced from the original toolbar to a 96 by 22 DIP pill with 280 by 44 DIP hover controls. Review fixed stale context actions, layout changes under pressed controls, off-screen expanded dragging and edge-origin release jumps. A gesture-only watchdog cancels a missed release without synthesizing click/paste. No idle animation or polling is added. The earlier hidden suite passed 85 checks, including 32 edge/corner/content combinations and stationary-press coverage. The later resumed session included those checks in the full 102-test pass and verified actual same-display mouse input. The final self-contained executable was published, opened with its normal migrated profile, prepared successfully and left at SPEECH READY / MICROPHONE OFF.

**Earlier native observations, before the on-demand/opacity follow-up:** validation used a separate synthetic profile. Observed model setup, practice start/finish, actual recognized text inserted in practice, latest-result recovery, original/final history metadata, and restoring both earlier text and reasoning. One AI-off practice run completed in972ms after stop; this is a single observation. The floating bar was visible at340×66 and its compact/hover WPF renderings were inspected; complete physical mouse/mixed-monitor validation remains pending.

The busy-clipboard test exposed actual Unicode corruption in an optional bulk typing fallback. That fallback was removed. Native tests now prove a busy clipboard sends no input and preserves existing data while providing Copy recovery. The standard Ctrl+V path and rich clipboard restoration pass. Presentation/bar tests were rerun after final layout/label changes. Diagnostics from isolated checks use isolated log directories.
