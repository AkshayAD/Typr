# Typr versions

## 0.1.4 — 18 October 2026

- Reuse verified CPU/GPU model folders through a separate per-user location catalog, including when settings are missing, unreadable or point to an older installation. Model preparation still verifies pinned hashes; unreadable user files remain intact.
- Prepare existing speech models once when Typr opens, without opening the microphone. Gaming defers preparation; explicit Unload remains effective for the session.
- Prevent rapid consecutive dictations from sharing their audio. Starting the next recording ends the previous release tail and waits only for its microphone's stop/drain/disposal, while recognition and output can continue independently. An ordinary release still retains its 300 ms tail.
- Keep the visible application, installer and executable versions synchronized at 0.1.4 / 0.1.4.0.

Verification: 42 focused microphone/queue Infrastructure checks, 34 affected hidden Windows checks and five real CPU/CUDA recognition checks passed, with zero skips. Real-model checks retained the full synthetic sentence when released before any partial appeared, including GPU audio delivered only during driver stop. These are distinct from a physical spoken-voice/browser test. Model-catalog work separately passed 160 Infrastructure checks (six opt-in native checks excluded) and 197 hidden Windows checks before the rapid-restart repair.

Version 0.1.3 was installed during this repair to verify model-catalog packaging and preservation of all 110 profile files; 0.1.4 adds the final microphone handoff and startup preparation changes.

## 0.1.2 — 18 October 2026

- Start the next dictation while earlier speech is recognizing, polishing or waiting to paste. Each recording owns its audio, settings, destination, duration and cancellation; one model worker processes streams in order without delaying the next microphone.
- Temporarily show a hidden Silk bar during preparation, recording and pending output, then restore the saved hidden preference. The current listening waveform takes priority over older finishing work. Size, transparency and edge docking are unchanged.
- Keep insertion and clipboard restoration in speaking order. Wait while another recording is active; defer only before a native paste attempt, never repeat an ambiguous or partial insertion.
- Scope microphone errors and cancellation to their recording. A rejected physical chord cannot finish a bar-started recording, and a later error cannot cancel earlier valid output.
- Keep pending work protected through gaming transitions, unload and shutdown. A queued provider request's timeout starts when its polish turn begins.

Verified: 194 hidden Windows checks; 151 Infrastructure checks plus all six real CPU/GPU replay checks, including immediate release and capture-stop tail draining. Five host pipeline regressions and 18 affected bar/resource cases passed again after the final adjustments. The installed 0.1.2 binary matches its fresh publish; all 106 current profile files were byte-identical across the upgrade. Natural-voice rapid restart and browser insertion on this installed version still need a physical-keyboard check.

## 0.1.1 — 18 October 2026

- Show the installed version in the window title and sidebar. Installer and Windows' installed-apps entry report the same release number.
- Dock Silk anywhere along any screen edge. Left/right layouts are vertical with upright controls; top/bottom layouts are horizontal. Hover grows inward to at most twice the resting dimensions, while recording and finishing stay compact. Transparency remains customizable.
- Add optional verified NVIDIA CUDA speech with CPU fallback, silent preparation and the existing pause/unload behavior during gaming. Quality mode remains the default.
- Preserve capture draining and finalization after shortcut release. The CUDA regression retains the ending even when it arrives only as capture stops and no partial has appeared.
- Install per user with Start/Desktop shortcuts and preserved settings, models, connections and history.

Confirmed: 204 Core and 161 selected Windows checks for the bar/speech changes; prior complete Infrastructure suite 153 plus two strengthened CUDA checks; 0.1.1 publish/upgrade, visible version and real microphone start/cancel. The physical spoken-sentence/browser-paste result is pending.
