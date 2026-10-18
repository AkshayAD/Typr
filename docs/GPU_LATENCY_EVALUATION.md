# GPU and streaming latency evaluation — 2026-10-18

An isolated test on this machine found a useful GPU path: NVIDIA's native **NeMo-Speech.cpp 0.2.0**, the official English Nemotron Q8_0 model, and **560 ms streaming chunks**. On the pinned synthetic phrase it decoded **5.83× faster** than Typr's current CPU model and produced its first partial about **697 ms earlier** in a paced feed. A native adapter has been implemented and checked with real GPU replay and unload/reopen tests. These measurements describe the isolated engine, not the installed app's complete microphone-to-paste latency.

The existing int8 ONNX model remains a poor CUDA candidate. The previous probe showed expensive integer operations staying on the CPU while tensors crossed between CPU and GPU. A second test with a GPU-friendly float32 export did execute on CUDA, but consumed most of this GPU's memory and did not beat the existing CPU configuration in throughput.

## Machine and scope

- Six-core/twelve-thread CPU; NVIDIA Turing SM 7.5 GPU with 4 GiB VRAM and a compatible CUDA 13 driver.
- Sherpa-onnx 1.13.8 / ONNX Runtime 1.28.2; portable CUDA 12 package with existing process-local CUDA/cuDNN dependencies.
- NeMo-Speech.cpp stable 0.2.0 Windows CUDA package, published 2026-10-02. Its release workflow includes native SM 7.5 code, matching this GTX 1650. The portable `doctor` command reported CUDA available, a compatible driver, and the correct GPU.
- All downloads, benchmark executables, native libraries, intermediate files, and detailed results were placed under `D:\Projects\Typr-gpu-latency-probe-20261018`. Benchmarks did not replace production DLLs, change global PATH, or install a GPU toolkit. The subsequent adapter implementation is described below.
- Only `artifacts/synthetic-test.wav` was read: 5.915 seconds, SHA-256 `71EF05827093D1BE995C395A1EE7798E1A31EE13025382FDE8AD42D8000B366C`. No microphone, user recordings, or dictation history were accessed.

The Windows CUDA build configuration and package checks are in NVIDIA's [release workflow](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/.github/workflows/release.yml) and [portable packaging script](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/scripts/windows/package-release.ps1).

## Measured results

Each configuration used one recognizer, one discarded warmup stream, two warm throughput streams, and one stream fed on a real-time schedule. Input was submitted in 320-sample/20 ms blocks. Endpointing was disabled; finalization added one second of zero samples and finished the stream. All child processes ran below normal priority, sequentially, with a 90-second timeout and a low-memory stop guard. Heavy builds and installer compression were paused during measurement.

| Configuration | Mean warm decode for 5.915 s | RTF¹ | Audio received at first partial² | Paced first partial³ | Peak total GPU memory |
|---|---:|---:|---:|---:|---:|
| Current Sherpa CPU, int8, 560 ms | 1,554 ms | 0.263 | 1,240 ms | 1,374 ms | 202 MiB baseline |
| Sherpa CPU, float32, 160 ms | 6,789 ms | 1.148 | 920 ms | 1,094 ms | 202 MiB baseline |
| Sherpa CUDA, float32, 160 ms | 2,266 ms | 0.383 | 920 ms | 991 ms | 3,526 MiB |
| NeMo-Speech.cpp CPU, Q8_0, 160 ms | 2,591 ms | 0.438 | 660 ms | 718 ms | 155 MiB baseline |
| **NeMo-Speech.cpp CUDA, Q8_0, 160 ms** | **479 ms** | **0.081** | **660 ms** | **671 ms** | **1,122 MiB** |
| **NeMo-Speech.cpp CUDA, Q8_0, 560 ms** | **267 ms** | **0.045** | **660 ms** | **678 ms** | **1,124 MiB** |

¹ RTF is compute wall time divided by audio duration; lower is faster. These are unpaced throughput measurements, including finalization. Native CUDA measurements were 480.1 and 478.5 ms at 160 ms chunks, and 267.1 and 266.2 ms at 560 ms chunks; float32 CUDA varied from 2,002.8 to 2,530.0 ms.

² Audio received at first partial describes model/runtime buffering on this phrase. It is not elapsed compute time or a general first-token guarantee. The runtimes expose different initial buffering behavior even at the same nominal 160 ms chunk setting.

³ Paced first partial is wall time from feeding the first audio block, excluding recognizer preparation, microphone startup, and Typr's application pipeline. One paced stream was measured per configuration. These numbers are not end-to-end measurements of the installed app. The separately measured microphone startup delay is still outside this experiment.

Sherpa used four CPU threads with both ONNX Runtime spinning settings disabled and greedy decoding. NeMo used its public C ABI's default CPU thread policy, `min(8, hardware threads)`, which is eight on this machine; that ABI exposes no thread override. Its CPU result therefore is not an equal-thread comparison. Native CUDA's paced stream used 1,031 ms of process CPU time, versus 4,297 ms for the current CPU baseline, a 76% reduction on this fixture. Native CPU's default worker behavior used substantially more CPU time and is not an efficiency improvement over the existing CPU backend.

All six configurations returned the same word sequence. The existing int8 model returned `Hello from TYPR today is a good day to turn ideas into words`; the float32 and native Q8_0 models returned `Hello from TYPR. Today is a good day to turn ideas into words`.

## Startup, memory, and release

| Configuration | Recognizer preparation | First unpaced stream | Explicit recognizer disposal |
|---|---:|---:|---:|
| Current Sherpa CPU int8 | 4,674 ms | 1,568 ms | 87 ms |
| Sherpa CPU float32 | 6,031 ms | 6,834 ms | 310 ms |
| Sherpa CUDA float32 | 8,113 ms | 4,415 ms | 260 ms |
| NeMo-Speech.cpp CPU Q8_0 | 57 ms | 3,045 ms | 107 ms |
| NeMo-Speech.cpp CUDA Q8_0 | 138 ms | 1,008 ms | 12 ms |
| NeMo-Speech.cpp CUDA Q8_0, 560 ms | 122 ms | 767 ms | 13 ms |

Preparation timings follow download and integrity checks, so files may already be cached. Native initialization also performs some work lazily on the first stream; the 138 ms constructor result must not be described as complete cold-start readiness. The first native CUDA stream took 1,008 ms versus 479 ms after warmup. A cold paced first-partial test was not performed.

The float32 CUDA model added about 3,324 MiB to total GPU usage, leaving only about 412 MiB available at the observed peak. The native Q8_0 model added about 970 MiB, leaving about 2,816 MiB available. GPU utilization reached 74% for float32 CUDA and 79% for native CUDA, confirming real GPU execution rather than CPU fallback alone.

After explicit native recognizer destruction, sampled GPU memory fell from 1,122 to 208 MiB. After process exit it returned to 145 MiB, close to the 152 MiB starting baseline; display activity can move that baseline. Float32 destruction similarly released its model allocation, with a small CUDA context allocation persisting until process exit. Neither result substitutes for testing Typr's Gaming mode lifecycle: a future adapter must release streams and model state on pause/unload, and avoid retaining the model while gaming.

## Model provenance and limits

The maintainer's pre-exported [160 ms float32 ONNX model](https://huggingface.co/csukuangfj2/sherpa-onnx-nemotron-speech-streaming-en-0.6b-160ms-2026-04-25) contains approximately 2.514 GB of files, including external encoder weights. All four binary files matched their published Hugging Face LFS SHA-256 values.

The native experiment used NVIDIA's official [English Nemotron Q8_0 GGUF](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b), pinned by the runtime's [0.2.0 model index](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/models/index.json) to revision `ebe59e5a817142986528bbbee5dba8db7b38ed50`. The 699,872,960-byte GGUF matched SHA-256 `d9a01898d2a611c8764e23a1c2f45e70bbd5a425dc4de93692ac951dd603812d`. The 102,183,465-byte Windows CUDA ZIP matched its published SHA-256 `9ffe5e842c39a7d3211f3b2c3b3aa1bca8c0b2455d96d961ed5f22ec5108f98d`. Total candidate downloads were about 3.316 GB, including the explicitly approved 802 MB native followup. The runtime code is Apache-2.0; the English model uses NVIDIA Open Model License terms as recorded in that index.

NVIDIA's [model card](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b) reports average WER of 7.07% at 560 ms, 7.67% at 160 ms, and 8.43% at 80 ms. Those are NVIDIA's evaluations of the underlying model, not measurements of this particular Q8_0 runtime. Shorter chunks trade accuracy for response time. One clean synthetic phrase cannot establish quality on Akshay's accent, background noise, names, numbers, longer dictation, or punctuation. No full quality equivalence is claimed.

NVIDIA documents a [stable native C ABI for streaming recognition](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/docs/sdk.md). Benchmarks called that ABI directly through isolated Python bindings, without launching a local server. Native logs confirmed right context 1/160 ms and right context 6/560 ms for the respective runs. The default adapter uses right context 6, retaining the current model's 560 ms geometry rather than accepting the shorter-chunk accuracy tradeoff.

## Native adapter verification

`NeMoSpeechStream.cs` implements `NeMoSpeechEngine`, model/stream safe handles, serialized native access, independent recording streams, idempotent completion and disposal, and a one-time 200 ms silence warmup on a separate stream. `NeMoSpeechApi.cs` binds the pinned stable C ABI, copies UTF-8 results before freeing them, and scopes absolute-directory DLL searches without changing PATH. CUDA initialization is required when requested: the adapter checks the CUDA device-count export, and the pinned backend implementation throws if the requested GPU cannot be selected or initialized; see [backend.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/v0.2.0/src/runtime/ggml/backend.cpp).

An actual unload/reopen test exposed an upstream ggml DLL limitation: unloading and reloading its code can abort because its [static initializer installs a C++ terminate handler](https://github.com/ggml-org/llama.cpp/blob/bd4f514/ggml/src/ggml.cpp) that remains registered after unloading. The adapter therefore caches code modules until process exit. Model weights, stream state, and GPU model allocations still close on engine disposal; the cached code is separate from keeping a loaded model. DLL search-directory registrations are removed after their load/create scope.

The D-only adapter test project passed all five focused tests, including an opt-in real CUDA test that warmed, decoded two independent synthetic recordings, disposed the engine, and repeated the full cycle in the same process. Both cycles logged CUDA0 and a 560 ms step. The other tests cover partial draining, fresh recording state, idempotent finish/disposal, stream-before-model release, rejection after disposal, an in-flight decode racing disposal, and initialization-failure cleanup. The real test requires `TYPR_TEST_NEMO_DIR` and `TYPR_TEST_WAV`; it expects `model.gguf` and the `native` directory from `NeMoSpeechDownload`, with an optional `TYPR_TEST_NEMO_NATIVE_DIR` override. It skips explicitly when those inputs are unavailable and verifies the pinned fixture hash before reading samples. A second successful run used the verified production-layout payload under `D:\Applications\Typr\speech-gpu`, with zero skips. No microphone was opened. The initial test-host abort was fixed by the code-module lifetime policy before the successful runs.

## Decision and reproducibility

Do not enable CUDA for the existing int8 ONNX files. Do not ship the float32 CUDA candidate on this 4 GiB GPU: it is slower than the current CPU baseline and uses most available VRAM.

Keep the current CPU backend as the established fallback. The native Q8_0 CUDA adapter defaults to the verified 560 ms profile and supports model disposal for the existing Gaming mode unload policy. A broader authorized speech corpus, cold and warm end-to-end dictation, application pause/unload/resume, and GPU contention still require application-level verification. One phrase establishes this machine's performance opportunity, not comprehensive transcription quality.

Detailed evidence is in `D:\Projects\Typr-gpu-latency-probe-20261018\summary.json`, the six `*.jsonl` result files and `*-monitor.json` GPU/RAM traces, `model-hashes.json`, and `nemo-download-manifest.json`. `Program.cs`/`LatencyProbe.csproj` implement the isolated Sherpa harness; `nemo-probe.py` implements the native C ABI harness; `run-latency-probe.py` provides the guarded runner. `summarize.py` regenerates the result summary. All six completed measurement processes exited with code zero. An initial native harness configuration with a zero chunk size was rejected before inference; it was corrected to 0.16 seconds before recording the successful results. The adapter's isolated project is `adapter-tests\AdapterTests.csproj`, built against the production adapter and its focused tests using linked source files.
