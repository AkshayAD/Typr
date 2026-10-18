# Third-party components and models

This source repository references packages through NuGet; it does not vendor their code or binaries. It does not contain model weights, sample audio, SDKs, runtime packages or release executables.

- sherpa-onnx, `org.k2fsa.sherpa.onnx` 1.13.8: https://github.com/k2-fsa/sherpa-onnx — Apache-2.0. Native transitive dependencies have their own notices.
- NAudio.Core / NAudio.Wasapi 2.2.1: https://github.com/naudio/NAudio — MIT; includes the WDL-derived resampling implementation and its upstream attribution.
- .NET runtime / WPF: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf — MIT and applicable third-party notices.
- xUnit 2.9.2 and runner 2.8.2: https://github.com/xunit/xunit — Apache-2.0.
- Microsoft.NET.Test.Sdk 17.11.1: https://github.com/microsoft/vstest — MIT and applicable third-party notices.

## Speech model

The downloader fetches this public ONNX export:
https://huggingface.co/csukuangfj2/sherpa-onnx-nemotron-speech-streaming-en-0.6b-560ms-int8-2026-04-25

Pinned revision: `52056fdc070914a48dcd68b31b44d6a6f5b85902`. Expected file sizes and SHA256 digests are in `ModelDownload.cs` and checked before use.

Original model and governing terms:
https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b

The original model is governed by the NVIDIA Open Model License; it must not be described as Apache-licensed merely because the inference engine is Apache-licensed. Review those terms before use or redistribution.

## Optional NVIDIA speech acceleration

The optional downloader fetches the unmodified [NeMo-Speech.cpp 0.2.0 Windows CUDA package](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.2.0) and the official [Nemotron English Q8_0 model](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b). `NeMoSpeechDownload.cs` pins revisions, archive/model hashes and every extracted file. Neither is bundled in the installer or source repository.

The engine is Apache-2.0; the model retains the NVIDIA Open Model License. The private runtime folder includes upstream license texts and third-party notices for its native dependencies, including ggml and Microsoft runtime components, under `licenses/`. Review those notices and model terms before redistribution. Typr calls the documented stable C ABI and installs no system CUDA toolkit or driver.

## Optional multilingual re-transcription and providers

Whisper model exports are fetched separately from [Small](https://huggingface.co/csukuangfj/sherpa-onnx-whisper-small) and [Large v3](https://huggingface.co/csukuangfj/sherpa-onnx-whisper-large-v3). Pinned revisions, sizes and hashes are in `WhisperRetranscriber.cs`. Upstream [Whisper](https://github.com/openai/whisper) code and weights use the [MIT license](https://github.com/openai/whisper/blob/main/LICENSE); review the export and upstream attribution before redistribution. No weights are bundled here.

Optional polishing launches unmodified official [Codex](https://github.com/openai/codex) and [Claude Code](https://code.claude.com/docs/en/overview) executables installed on the user's device or downloaded to Typr's private provider folder. These binaries are not committed or bundled in the application. Their own licenses, service terms, subscription entitlements and authentication rules apply. Claude Code is not represented as open-source software. See its [integration and legal documentation](https://code.claude.com/docs/en/legal-and-compliance).

## Binary notice review

These are dependency/source references, not a complete binary redistribution notice bundle. Before publishing an executable or installer, inventory all resolved native/runtime/transitive packages and include their required license texts and notices. Do not bundle model weights without a separate license and distribution review. No third-party source code has been copied into this repository; integration calls use the dependencies' documented APIs.
