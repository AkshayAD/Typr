# Dictation reliability and bar refinement

18 October 2026. Follow-up to the user's physical hold-to-talk test: text appeared, but releasing before the partial transcript caught up lost the ending; recognition felt slow and inaccurate, and the bar needed more refined motion and hints.

## Release means finish the captured words

The display is a partial transcript, not the recording boundary. Releasing the shortcut closes capture after the existing 300 ms ending tail, admits the converter's final samples, drains all admitted audio, finishes recognition, optionally polishes the final text, and then inserts once. Users should not have to keep holding until text appears. Escape remains an explicit cancellation operation.

Reproducible defects and repairs:

- Audio callbacks previously shared a 256-item work queue with inference/control actions. A blocked decoder plus 300 short callbacks exhausted that queue. Captured PCM now has its own FIFO, bounded to 120 seconds of *pending* audio, with one scheduled drain wake. Each callback-sized decode yields to control work. Stop closes capture before draining the remaining backlog; cancellation invalidates the generation and clears it.
- Stop waits for the source's recording thread and flushes converter samples before retiring the input epoch. The recognizer drains all admitted samples before `InputFinished`. Cancellation never revives a previous recording.
- Packetized resampling accumulated extra samples: a one-second 44.1/48 kHz input could become 16,086/16,117 samples. Cumulative duration accounting plus final converter flushing now produces exactly 16,000 output samples for a one-second input. Whole and irregularly packetized inputs were compared separately.
- A new rejected physical chord during finalization could subsequently cancel the previous recording. Cancelled/mode updates from that rejected chord now leave the previous finalization alone. The separate Escape path can still cancel it deliberately.
- Prepared capture no longer waits for asynchronous UIA destination discovery. Delivery retains the original key-down capture task; it does not silently retarget later.
- RMS level feedback comes from admitted audio, throttled to 20 Hz, rather than waiting for recognition to catch up. The bar distinguishes Recognizing, Polishing and Placing your words.

The 120-second bound limits queued memory, not the total length of a continuously decoded dictation. There is still no interruption inside an already executing native decode call. These fixes do not establish human accent accuracy or guarantee a particular latency on another computer.

## Shared bar improvements implemented before design selection

- Keep the existing 96 by 22 DIP resting footprint, 280 by 44 DIP expanded footprint, saved anchor, user opacity, scale, hover delay, captions and action configuration.
- Use a 220 ms eased geometry transition and a short delayed control fade. Repeated identical state refreshes no longer stop the growth animation. Glyph hover scales gently over 120 ms.
- Replace font-dependent action glyphs with consistent vector icons and a dedicated rounded button template. Spread actions across the expanded surface.
- Replace platform-looking hints with product surfaces, short action labels and relevant shortcut chips. A bar-owned 450 ms delay and five-second dismissal work on the nonactivating window, including disabled shortcut-only controls. Revalidate the hovered button before opening; dismiss on departure, rebuilding, dragging, hiding and closing.
- Animate actual microphone-level marks while listening and small dots while processing. No animation clock runs for hidden/idle marks. Honor reduced animations and high contrast.
- Preserve click-versus-drag behavior and defer action replacement while a pointer gesture is active. Dragging an action must move the bar without invoking it.

## Visual choices prepared for selection

Three interactive conversation previews expose Rest, Hover, Listening and Finishing states, local controls, surface opacity and optional live words. They simulate the appearance; they do not open the microphone or represent three shipped native modes.

| Direction | Rest and expansion | Recommendation |
| --- | --- | --- |
| Silk | Small translucent capsule, quiet waveform, rounded expansion and restrained hints | Preferred daily-use direction: minimal visual weight and clear controls |
| Halo | Small microphone orb expanding into a capsule, subtle active halo | More visible recording presence |
| Underline | Thin resting line expanding into a compact tool strip | Lowest resting visual footprint, with less immediate discoverability |

The previews compiled through the conversation renderer; browser inspection of a local-file preview was blocked and was not bypassed through another origin. The user subsequently selected Silk, with the additional requirements below.

## Selected Silk design

- Silk is the native default for existing and new profiles, with a **96 by 16 DIP** resting capsule and **192 by 32 DIP** hover surface. The existing 85/100/115% sizes scale both footprints together, preserving the maximum two-times width and height.
- Shortcut start, recognition, polishing and live captions never force expansion. Resting marks respond to input level while listening, then become animated dots while finishing. The bar has no visible processing label; detailed status remains available in the workspace and accessibility help.
- Hover alone reveals the primary finish/cancel action, up to four configured icons and More. Additional configured hover actions remain reachable under More alongside the chosen right-click actions. Optional live text and the menu are separate nonactivating popups; they never enlarge the capsule.
- Existing background opacity (20–100%), scale, hover delay, captions, position and shortcut-only recording choices remain saved. The Appearance page names Silk and explains its behavior.
- Review repaired detached-popup gestures and menu toggle state. Menu controls enter the same click/hold/drag pipeline as bar controls; an actual drag closes the menu without invoking its button. A second menu press/release closes it.

Native checks observed **96 by 16** during listening with separate live text, **96 by 16** during finishing with animated dots, and the same resting geometry after insertion. The complete synthetic sentence, including its ending, appeared once in the original external field with **Pasted and verified**. Hover reached **192 by 32**. Two successive menu clicks opened/closed it, and dragging its Prepare speech control moved the saved anchor without preparing speech. No personal voice was used in these checks.

## Verification and limits

- Latest Infrastructure Release: **123 passed, zero skipped**, including real-model replay, native capture stop/drain, direct streaming, backlog/drain, sample counts, converter flush and cancellation/restart tests.
- Latest Windows Release: **122 passed, zero skipped**, including three readiness checks, nine recording-state regressions, compact/scaled activity geometry, popup overflow/menu toggle, bar gestures, native delivery/focus and nonactivation checks.
- Corrected the paced replay harness to follow absolute elapsed time. It now admits all **94,640 frames**; the old relative-delay harness could stop after about 77% of the fixture. Complete final words are now asserted on initial and recovery recordings.
- One complete synthetic stop observation was **843.6 ms**, including the 300 ms ending tail. It is not a controlled improvement percentage or a human dictation measurement.
- Native replay-to-external-field check pasted the entire synthetic sentence, including its last word, and reported **Pasted and verified** with AI off. The prior session also verified that sentence and paste-last in Edge.
- Actual new hover controls and a rounded Paste last hint were observed. Dragging the Paste icon moved the bar without firing Paste. Final native hint state confirmed an open hint after its delay. These same-display observations do not replace mixed-DPI/multi-display checks.
- Repeated actual microphone preparation/start/finish with the repaired build in a no-retention profile: preparation stayed off, deliberate start reached Listening with capture active, and finish closed capture while recognition was still busy. Silent finalization returned to microphone off with no speech detected. This verifies lifecycle, not spoken accuracy.
- Final self-contained x64 publish passed. Disposable fixtures were closed; the normal updated Typr app was opened and prepared, with **SPEECH READY / MICROPHONE OFF** observed. No recording was started in its normal profile.
- One small compatible English model was measured against the current recognizer. It saved resources but misheard the opening, so the default remains unchanged. See [model research](ENGLISH_MODEL_RESEARCH.md).

A fresh physical hold-release test with the user's natural voice is still needed to assess these fixes against the reported experience. No synthetic fixture can establish that their English/Hinglish accuracy now meets the desired standard. Calls, Bluetooth playback, sustained gaming impact, accessibility and mixed-DPI transitions retain their separate checklist in [deferred tests](DEFERRED_DESKTOP_TESTS.md).

## Follow-up: release still loses the ending

The user confirmed insertion works, but ending words still disappear when releasing before the partial transcript catches up. The prior replay fixture had approximately **790 ms of trailing quiet**, and its timed test waited a further 800 ms; that successful test did not establish the immediate-release case.

The new real-model regression removes that trailing cushion, blocks the first native decode, feeds all remaining speech, and requests Stop while **no partial text has appeared**. It requires all admitted frames and the exact complete sentence, including `ideas into words`. It passed before the new hardware fix, narrowing the confirmed remaining gap to capture rather than proving that recognition itself discards admitted audio.

NAudio 2.2.1's `WasapiCapture` stop loop exits before its next packet read, then stops the native client without draining unread packets. Joining that thread and flushing the converter cannot recover packets never delivered to it. This is a real gap, although ordinary short packet loss alone does not prove the cause of every reported missing word. [Pinned capture implementation](https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/NAudio.Wasapi/WasapiCapture.cs).

Typr now uses a small shared-mode capture loop through NAudio's public native interfaces. One capture thread creates, initializes, starts, stops, drains and disposes its source. Stop freezes the stream, drains its remaining buffers, releases every acquired packet, flushes conversion, and only then releases the session's stop fence. Start waits for the actual native start. Device choice, Windows defaults/mute and the existing 300 ms release tail are unchanged. [Microsoft Stop contract](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-stop), [packet ownership contract](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer).

Six new tests cover a final packet unread at stop, silent frames, final interpolation output, native thread ownership, actual-start synchronization and failure cleanup. The packet test requires the exact nonzero ending samples to reach the recognizer before `InputFinished`; earlier callback-level fakes could not detect this gap. A separate converter audit found at most two un-emitted samples across seven input rates, ruling out a multiword converter backlog in those cases. The combined Infrastructure Release suite passed **123 tests, zero skipped**, including both real-model replay tests and the streaming checks below. Independent review found no actionable issues.

The new native source also passed an actual microphone lifecycle check in a no-retention, AI-off profile: prepared and idle stayed off, deliberate start reached Listening with capture active, and finish closed capture while recognition was still busy. Silent finalization returned to idle/off without an input error. Listening and finishing both stayed 96 by 16 DIPs. This establishes basic capture lifecycle, not natural-voice accuracy or the user's exact physical hold/release case.

## Lower latency and clearer preparation

Typr previously accumulated 8,960 samples (560 ms) before feeding the recognizer, which already buffers its own model windows. Captured blocks now reach the recognizer directly in order. Identical caption events are suppressed until the text changes, with the cache reset for each recording. Final padding, `InputFinished`, saved PCM, cancellation and generation boundaries remain unchanged.

A bounded four-thread synthetic comparison used the same pinned Nemotron model and audio, with two baseline runs and two direct-feed runs. First meaningful text appeared after **26,880 input frames / 1,680 ms** with the old batching and **19,840 frames / 1,240 ms** with direct 320-frame feeding: **440 ms earlier in input-audio time**. The complete ending appeared at 5,600 versus 5,160 ms. Every run returned the exact complete sentence and made 12 native model decode calls. These input boundaries do not establish a 440 ms wall-clock gain for every microphone recording, accented-human quality, energy use or gaming impact. The ignored audit source is `artifacts/streaming-cadence-audit/Program.cs`.

Three new streaming tests verify immediate small-block admission, exact ending PCM before finalization, unchanged-caption suppression and cache reset between generations. No recognition model, thread setting or provider preference was changed.

The workspace now distinguishes an installed model from a prepared model. **Prepare speech** loads the model without opening input. Once prepared, the shortcut-only hero focuses practice and explains **Hold Ctrl + Alt**; optional button recording remains explicitly gated. Settings explains that **Unload speech** releases model memory, adds loading time to the next dictation and does not mute Windows. Three readiness checks cover installed/unloaded, prepared shortcut-only and missing-model paths. Their dummy model files are sparse to avoid allocating a real model's worth of disk space.

For daily use, keep the model prepared and use live captions for early feedback. AI polishing runs after local recognition; it adds provider/process/network time before the final insertion. The existing Raw chord bypasses AI for the fastest path. Keep the selected low-effort Luna default for polished output rather than silently lowering quality. Avoid extra model requests or explanatory output, consistent with [OpenAI's latency guidance](https://developers.openai.com/api/docs/guides/latency-optimization). Final authenticated provider latency and natural-voice accuracy still need a representative user test.

The combined self-contained build was published and opened with the normal profile. The installed/unloaded workspace visibly offered Prepare speech; clicking it reached **SPEECH READY / MICROPHONE OFF**, with the practice field focused. No recording was started in the normal profile. This leaves the physical immediate-release check ready for the user.
