# Speech resources and GPU acceleration

18 October 2026. The user's priority is fast, accurate dictation during ordinary work and minimal resource competition during gaming. This extends the [recognition research](ENGLISH_MODEL_RESEARCH.md). The bounded measurements below favor efficient CPU execution over CUDA for the current quantized model.

## Separate the delays

1. Shortcut dispatch and microphone initialization. GPU inference cannot capture speech spoken before the microphone starts. The latest no-retention device probe observed approximately 568 ms in source construction, 7 ms in native Start and 58 ms before the first converted PCM, about 642 ms overall. This is one local observation. Opening and Listening must have distinct signals.
2. Local streaming recognition. The current Nemotron int8 model uses CPU execution, four threads in the user's saved configuration, and its own 560 ms model window. The redundant application batching has been removed. GPU acceleration can help only if this model's expensive operators execute on the GPU efficiently.
3. Final recognition and text polish. Luna runs through the selected provider after recognition; local GPU acceleration cannot accelerate that cloud request. Keep Raw available for the shortest delivery path.

## GPU feasibility gate

- Check the installed adapter, driver, free VRAM and actual runtime execution providers without changing drivers or system settings.
- Test the exact existing model and pinned synthetic sentence in an isolated process. The CPU baseline and CUDA run must use the same native engine version, waveform and result checks.
- Verify the provider really activates. Sherpa can log a warning and fall back to CPU when CUDA support is absent; choosing the string `cuda` does not prove acceleration. Check runtime/node assignment or profiling as needed.
- Measure model preparation, warm streaming/first partial, finalization, CPU seconds and peak GPU memory. Record exact transcript equality. GPU presence alone does not establish lower latency or improved accuracy.
- Keep the working CPU path and the microphone lifecycle independent. Do not silently select a smaller or different recognizer merely to report a GPU speedup.
- Use a portable, hash-verified runtime in an isolated location for experiments. The public Windows CUDA archive is hundreds of megabytes, and CUDA/cuDNN dependencies can be larger. Respect free disk space; do not install a toolkit or driver as a side effect of probing.

Sherpa publishes Windows NVIDIA GPU builds. ONNX Runtime documents CUDA/cuDNN version requirements. DirectML is another possible Windows backend, but it is not the same as CUDA and a separate supported native build is needed. It should not be offered as an interchangeable option without tests. [Sherpa Windows CUDA builds](https://k2-fsa.github.io/sherpa/onnx/install/windows/build-cuda.html), [pinned provider selection and CPU fallback](https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.8/sherpa-onnx/csrc/session.cc), [ONNX Runtime CUDA requirements](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html), [DirectML support](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html).

## Everyday and gaming behavior

Everyday mode should keep the chosen speech model ready while leaving the microphone closed. Use a GPU only when the validated backend improves latency and has sufficient memory headroom. A CPU fallback should report the effective backend honestly. Keep inference below interactive foreground work in scheduling priority; no background re-transcription or benchmark should run opportunistically while the user is busy.

The user chose to **pause speech entirely during gaming**. A manual override is necessary: fullscreen status and high GPU load are signals, not reliable proof that a game is running. Windowed games and GPU-heavy creative work also need protection.

Do not switch backends in the middle of a recording or discard queued PCM. Apply changes at an idle boundary and preserve the microphone stop/drain fence. Do not keep two large models resident merely to make switching look instantaneous. If native CUDA keeps a context resident after sessions close, isolate GPU recognition in a worker process so stopping it releases the context; confirm with GPU memory measurements.

## Measured choice and implementation

A portable, hash-verified Sherpa 1.13.8 / ONNX Runtime 1.28.2 CUDA build was tested in isolated processes against the existing 5.915-second synthetic sentence and Nemotron int8 weights. The unprofiled CPU/CUDA pair both used four threads, disabled thread spinning and BelowNormal process priority. Both produced identical complete text. CPU decode was **1.6 seconds**, CUDA **2.9 seconds** (rounded CLI observations). Preparation was 4.06 versus 5.86 seconds. A separate profiled run verified real CUDA operator execution, with 36,982 encoder CUDA execution events and 14,410 CPU events: expensive `MatMulInteger` and `ConvInteger` remained on CPU and transfers added overhead. CUDA was not selected for production.

The GPU probe did not measure live first-partial or stop-to-paste latency. Its process exited and sampled total GPU memory returned below its starting observation. No driver/toolkit installation, application DLL replacement, microphone capture or personal audio was involved. Portable dependencies stayed outside the application; Typr has no dependency on those probe files. Current-model results do not establish that a GPU-friendly floating-point model would also be slower.

A separate CPU comparison used the same pinned sentence and native CPU runtime, direct 320-frame admission, one discarded warmup and two measured streams per configuration:

| Configuration | Mean decode wall | Mean CPU time | Real-time factor |
| --- | ---: | ---: | ---: |
| Four threads, default spinning | 2.200 s | 11.742 CPU-s | 0.372 |
| Four threads, spinning disabled | 1.787 s | 4.641 CPU-s | 0.302 |
| Two threads, default spinning | 2.359 s | 5.477 CPU-s | 0.399 |
| Two threads, spinning disabled | 2.581 s | 4.469 CPU-s | 0.436 |

All twelve streams, including warmups, retained the exact sentence. First meaningful partial stayed at 19,840 input frames / 1,240 ms. Four-thread disabled spinning used approximately **60.5% less CPU time** and **18.8% less wall time** than four-thread defaults in this bounded comparison. These are unpaced throughput observations, not game FPS, energy or general latency guarantees. First-model preparation differed with process/JIT/cache order and is not an attributed improvement.

Typr now disables ONNX Runtime's intra/inter-operation thread spinning through a unique temporary provider configuration. The verified native parser reads the options during construction; the file is removed afterward in success and failure paths. Existing user thread settings and model weights remain intact. [ONNX Runtime thread behavior and supported keys](https://onnxruntime.ai/docs/performance/tune-performance/threading.html).

Settings offers **Automatic**, **Everyday** and **Gaming**. Automatic listens to the read-only Windows effective power mode V2 notification, including its Game Mode state; it does not poll processes or change Windows settings. Everyday is a manual override. Gaming prevents new speech starts, cancels pending preparation/download/reprocessing/comparison, then unloads the model at an idle boundary. It hides the bar, releases dictation/command shortcuts and stops foreground-target polling. Current dictation can finish before release; cancellation also clears actual input feedback after disposal. Leaving gaming prepares a previously prepared model without opening input; a previously unloaded model stays unloaded.

Automatic detection follows Windows' recognized Game Mode, so the manual Gaming option remains necessary for unrecognized games or other demanding work. [Microsoft notification contract](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powerregisterforeffectivepowermodenotifications).

## Acceptance checks

- Idle/prepared has no microphone capture and no inference loop.
- Gaming selection releases verified GPU allocations; returning to everyday mode gives explicit preparation feedback.
- A pending start cancelled during initialization cannot open input later.
- No backend/profile change interrupts an active dictation; exact first/last PCM and transcription checks still pass.
- A failed or unavailable accelerator preserves usable CPU dictation and states the actual backend.
- Compare latency and transcript quality before changing the default. Record unmeasured game frame-time, thermal and battery behavior as pending; a thread count is not a whole-process CPU cap.
