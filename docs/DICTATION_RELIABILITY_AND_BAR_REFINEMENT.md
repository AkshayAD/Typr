# Dictation reliability and bar refinement

18 October 2026. Follow-up to the user's physical hold-to-talk test: text appeared, but releasing before the partial transcript caught up lost the ending; recognition felt slow and inaccurate, and the bar needed more refined motion and hints.

## Release means finish the captured words

The display is a partial transcript, not the recording boundary. Releasing the shortcut closes capture after the existing 300 ms ending tail, admits the converter's final samples, drains all admitted audio, finishes recognition, optionally polishes the final text, and then inserts once. Users should not have to keep holding until text appears. Escape remains an explicit cancellation operation.

Reproducible defects and repairs:

- Audio callbacks previously shared a 256-item work queue with inference/control actions. A blocked decoder plus 300 short callbacks exhausted that queue. Captured PCM now has its own FIFO, bounded to 120 seconds of *pending* audio, with one scheduled drain wake. Each callback-sized decode yields to control work. Stop closes capture before draining the remaining backlog; cancellation invalidates the generation and clears it.
- Stop waits for the source's recording thread and flushes converter samples before retiring the input epoch. The recognizer drains all admitted samples and its partial block before `InputFinished`. Cancellation never revives a previous recording.
- Packetized resampling accumulated extra samples: a one-second 44.1/48 kHz input could become 16,086/16,117 samples. Cumulative duration accounting plus final converter flushing now produces exactly 16,000 output samples for a one-second input. Whole and irregularly packetized inputs were compared separately.
- A new rejected physical chord during finalization could subsequently cancel the previous recording. Cancelled/mode updates from that rejected chord now leave the previous finalization alone. The separate Escape path can still cancel it deliberately.
- Prepared capture no longer waits for asynchronous UIA destination discovery. Delivery retains the original key-down capture task; it does not silently retarget later.
- RMS level feedback comes from admitted audio, throttled to 20 Hz, rather than waiting for recognition to catch up. The bar distinguishes Recognizing, Polishing and Placing your words.

The 120-second bound limits queued memory, not the total length of a continuously decoded dictation. There is still no interruption inside an already executing native decode call. These fixes do not establish human accent accuracy or guarantee a particular latency on another computer.

## Shared bar improvements implemented

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

The shared native polish above is implemented. Final silhouette changes follow the user's visual preference. The previews compiled through the conversation renderer; browser inspection of a local-file preview was blocked and was not bypassed through another origin.

## Verification and limits

- Infrastructure Release: **113 passed, zero skipped**, including real-model replay, backlog/drain, sample counts, converter flush and cancellation/restart tests.
- Windows Release: **114 passed, zero skipped**, including nine recording-state regressions, bar resource/gesture checks, native delivery/focus and nonactivation checks.
- Corrected the paced replay harness to follow absolute elapsed time. It now admits all **94,640 frames**; the old relative-delay harness could stop after about 77% of the fixture. Complete final words are now asserted on initial and recovery recordings.
- One complete synthetic stop observation was **843.6 ms**, including the 300 ms ending tail. It is not a controlled improvement percentage or a human dictation measurement.
- Native replay-to-external-field check pasted the entire synthetic sentence, including its last word, and reported **Pasted and verified** with AI off. The prior session also verified that sentence and paste-last in Edge.
- Actual new hover controls and a rounded Paste last hint were observed. Dragging the Paste icon moved the bar without firing Paste. Final native hint state confirmed an open hint after its delay. These same-display observations do not replace mixed-DPI/multi-display checks.
- Repeated actual microphone preparation/start/finish with the repaired build in a no-retention profile: preparation stayed off, deliberate start reached Listening with capture active, and finish closed capture while recognition was still busy. Silent finalization returned to microphone off with no speech detected. This verifies lifecycle, not spoken accuracy.
- Final self-contained x64 publish passed. Disposable fixtures were closed; the normal updated Typr app was opened and prepared, with **SPEECH READY / MICROPHONE OFF** observed. No recording was started in its normal profile.
- One small compatible English model was measured against the current recognizer. It saved resources but misheard the opening, so the default remains unchanged. See [model research](ENGLISH_MODEL_RESEARCH.md).

A fresh physical hold-release test with the user's natural voice is still needed to assess these fixes against the reported experience. No synthetic fixture can establish that their English/Hinglish accuracy now meets the desired standard. Calls, Bluetooth playback, sustained gaming impact, accessibility and mixed-DPI transitions retain their separate checklist in [deferred tests](DEFERRED_DESKTOP_TESTS.md).
