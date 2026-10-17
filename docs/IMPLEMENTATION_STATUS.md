# Typr: implementation and verification

Date: 18 October 2026. Specifications: [Product/UI plan](PRODUCT_DESIGN_PLAN.md) and [daily dictation extension](COMPANION_AND_POLISH_PLAN.md). The current screen-use restriction and remaining checks are recorded in [deferred desktop tests](DEFERRED_DESKTOP_TESTS.md); efficient English alternatives are assessed in [model research](ENGLISH_MODEL_RESEARCH.md).

## Delivered

| Area | Behavior |
| --- | --- |
| Identity and requirements | Typr UI, projects, namespaces, executable and data folders; requirements in Settings/README; staged profile migration preserves originals and existing destinations |
| Native design | Workspace, History, Vocabulary, Snippets, Appearance and Settings; dynamic light/dark/system themes, three accents and keyboard focus |
| External insertion | Fixed clipboard sequence sampling after CloseClipboard; supported rich formats preserved; Copy recovery without input for busy/unsupported clipboard data; focus/password/read-only/cancellation guards; no retry after ambiguous input |
| Companion | Persistent nonactivating movable bar, draggable from controls, click and hold gestures, hover/right-click actions independently configurable, optional live words, hide/show/reset position; 20-100% background opacity, three sizes and four hover delays, solid high-contrast surface |
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
- Latest Infrastructure Release suite excluding real-model replay: **100 passed**. Includes 16 fake microphone lifecycle cases; no hardware, models or inference used.
- Latest hidden Windows Release checks: **39 passed**. Includes cold preparation release/Esc/bar-cancel, positive fake capture, consent guards, picker persistence, actual-closure feedback, empty-result recovery and bar appearance. Excludes the five native delivery cases and the native-handle style case while the user is gaming.
- Latest total: **327 headless checks passed**. The real-model replay and six native Windows cases are deferred, not counted as passes on the latest changes.
- Windows Release build: passed with zero warnings/errors.
- Self-contained Windows x64 publish: passed; build prepared without launching it. Local development preview, not a public signed release.
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
- Real microphone/device changes, physical global chords, calls/Bluetooth/game coexistence, multi-hour sessions, physical bar dragging, full mixed-DPI/multi-monitor and assistive-technology evaluation remain pending. New Typr has not been launched during the gaming restriction.
- Hardware recommendations are provisional. No certified minimum-device accuracy or memory study has been performed.
- A supported LTS runtime migration, complete binary license bundle, signing, installer and authenticated Typr updater are public-release prerequisites. CLI updates are implemented; they do not update Typr itself.
- No merge or production deployment is part of this change.

## Final check record

| Check | Result |
| --- | --- |
| Core |188 passed |
| Infrastructure, no real-model replay |100 passed |
| Hidden Windows checks, no native handles/input |39 passed |
| Latest headless total |**327 passed** |
| Real-model replay and six native Windows checks |Deferred on latest changes |
| Self-contained Windows x64 publish |Passed; not launched |
| Documentation links, branding scan and whitespace |Passed |


**Earlier native observations, before the on-demand/opacity follow-up:** validation used a separate synthetic profile. Observed model setup, practice start/finish, actual recognized text inserted in practice, latest-result recovery, original/final history metadata, and restoring both earlier text and reasoning. One AI-off practice run completed in972ms after stop; this is a single observation. The floating bar was visible at340×66 and its compact/hover WPF renderings were inspected; complete physical mouse/mixed-monitor validation remains pending.

The busy-clipboard test exposed actual Unicode corruption in an optional bulk typing fallback. That fallback was removed. Native tests now prove a busy clipboard sends no input and preserves existing data while providing Copy recovery. The standard Ctrl+V path and rich clipboard restoration pass. Presentation/bar tests were rerun after final layout/label changes. Diagnostics from isolated checks use isolated log directories.
