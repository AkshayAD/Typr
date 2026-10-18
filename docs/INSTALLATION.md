# Install Typr on Windows

Typr's installer supports Windows 10 and 11 on an x64 PC. The application includes its .NET runtime. Speech models and optional provider CLIs are downloaded separately from inside Typr.

Run `Typr-0.1.4-win-x64-Setup.exe` as your normal Windows user. Setup installs into `%LOCALAPPDATA%\Programs\Typr`, adds Typr to Start and Windows Settings > Apps > Installed apps, and creates a desktop shortcut by default. Clear **Create a desktop shortcut** if you do not want it. No administrator permission is requested, and Typr is not added to Windows startup. The final **Launch Typr** option is off by default.

This package is **version 0.1.4**, with executable file version `0.1.4.0`. Check Typr's visible version number to confirm which installed build you opened. Installing it over the earlier 0.1.0 pilot keeps the same upgrade identity and profile locations.

For every user-facing update, increment `Version`, `AssemblyVersion` and `FileVersion` in `src/Typr.Windows/Typr.Windows.csproj` together. The window title/sidebar read the assembly version; the installer reads the project version. Keep the Inno fallback values synchronized and record the changes in [the changelog](CHANGELOG.md).

This local development installer is unsigned. Windows may identify its publisher as unknown. Verify the source and the adjacent SHA256 file before running a copy; a checksum checks file integrity and is not a publisher signature. Public distribution still requires signing and the complete binary license/notice review described in [third-party notices](../THIRD_PARTY_NOTICES.md).

## Updating and removing

Close Typr before installing another version. Setup and uninstall check Typr's instance mutex and ask you to close it; they do not automatically close an active dictation. Run the new installer as the same Windows user and keep the existing destination. The stable application identity reuses the installed location and the same uninstall entry. Settings and previously downloaded models remain available.

Remove Typr through Windows Settings > Apps > Installed apps > Typr > Uninstall. Uninstall removes installed application files and the shortcuts it created. It deliberately preserves:

- `%APPDATA%\Typr`: settings, vocabulary and snippets.
- `%LOCALAPPDATA%\Typr`: models, provider tools, history, retained audio and diagnostics.

Those folders can contain personal data. Removing Typr does not erase that data. Back them up before any separate manual cleanup. The installer does not bundle or replace a settings file, provider credentials, models or history.

CPU and GPU model folders can share a central parent directory on any writable drive. `%LOCALAPPDATA%\Typr\model-locations.json` records their verified locations independently of settings. Startup first uses a ready selected folder, then the recorded shared folder, then the existing default location. This avoids another download when settings point to an old build; it does not duplicate models or scan your drives. Preparation still verifies their pinned hashes. An unreadable catalog is preserved. Opening Typr prepares an existing model once without opening the microphone; explicit Unload remains effective until another prepare/recording or app restart, and gaming pauses preparation.

## Runtime dependencies

The installed application is a self-contained multi-file `win-x64` publish. It pins .NET and Windows Desktop runtime 8.0.32, the current .NET 8 patch in [Microsoft's official release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json) on October 18, 2026. Recheck that metadata when preparing later builds. Keep the files together; launch the installed `Typr.exe` or its shortcuts. The runtime and native speech libraries are installed directly, without relying on a single-file bundle's extraction cache.

The pinned sherpa-onnx 1.13.8 Windows x64 package supplies `onnxruntime.dll` and `sherpa-onnx-c-api.dll`. Their PE import tables reference each other and Windows system libraries, without imports of `MSVCP140.dll` or `VCRUNTIME140.dll`. This package therefore does not require a separate Visual C++ redistributable installation. Recheck native imports when upgrading sherpa-onnx; [generic ONNX Runtime packages have different prerequisites](https://onnxruntime.ai/docs/install/).

Optional GPU speech needs an NVIDIA GPU and a driver compatible with CUDA 13. It downloads a separate [NVIDIA NeMo-Speech.cpp 0.2.0 CUDA bundle](https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.2.0) and pinned Nemotron Q8_0 model: 802,056,425 bytes downloaded, with about 1 GB of free staging space needed for a fresh setup. Its selected ASR DLLs, private Visual C++ runtime DLLs and upstream notices remain in the chosen GPU model folder. You can choose a private folder on another drive, such as `D:\Applications\Typr\speech-gpu`; the default is `%LOCALAPPDATA%\Typr\models\nemotron-cuda`. Typr does not install a global CUDA toolkit or change PATH. Downloads are resumable and verified against pinned SHA256 hashes before use; CPU speech remains a separate setup. Keep the default **Quality (560 ms)** mode recommended for this pilot; the shorter Fast context was not faster in the local short-clip comparison.

## Build the installer

Use Windows, a compatible .NET SDK with desktop targeting support, and Inno Setup 7.1.0. Run these commands from the repository root. `BuildRoot` should have enough space for a self-contained publish, compression staging and the resulting installer. A new staging subdirectory is created for each build, so previous publishes cannot leak into a new package.

```powershell
# Prepare a verified, portable official compiler without changing global tools.
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Get-InnoSetup.ps1 `
  -ToolRoot D:\Projects\Typr-installer-build-20261018\tools

powershell -NoProfile -ExecutionPolicy Bypass -File tools/Build-Installer.ps1 `
  -IsccPath D:\Projects\Typr-installer-build-20261018\tools\inno-7.1.0\ISCC.exe `
  -BuildRoot D:\Projects\Typr-installer-build-20261018
```

Use another writable build/tool path as needed. Compiler preparation verifies both the SHA256 from the [official immutable release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0) and its valid Pyrsys B.V. Authenticode signature before execution, following the [official verification guidance](https://jrsoftware.org/isdl-verify.php). Its [portable mode](https://jrsoftware.org/ishelp/topic_technotes.htm) avoids global registration and shortcuts.

The build publishes Release `win-x64`, self-contained, with single-file bundling and debug symbols disabled. Build and intermediate outputs stay under `BuildRoot\artifacts`, and compression temporary files stay on the build volume. It requires the expected .NET and speech DLLs and rejects foreign-platform native assets, then compiles `installer/Typr.iss`. Output appears under `BuildRoot\output`, with a `.sha256` sidecar. Existing portable-publish defaults in the application project remain available.

`Assets/Typr.ico` contains 16, 24, 32, 48, 64, 128 and 256 pixel versions of the existing WPF `TyprIcon`. Regenerate it after changing that resource:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/New-AppIcon.ps1
```

## Verification before a release

On a disposable standard-user Windows x64 profile or VM:

1. Install with the default options. Verify the install directory, Start and desktop shortcuts, icon, single Installed apps entry, and absence of a UAC request or startup entry.
2. Launch from each shortcut. Verify local speech loads and that models/settings remain in their documented data locations.
3. Save a distinct setting and download a model. Close Typr and install the next version over the first; verify the setting/model survives and there is still one Installed apps entry.
4. Uninstall. Verify shortcuts/application files are removed and the settings/model/history folders remain unchanged. Reinstall and verify the profile is reused.
5. Repeat with the desktop task cleared and a custom destination. Verify the task choice and destination survive an upgrade.

Build success does not establish those desktop, upgrade, data-preservation or clean-machine runtime behaviors. Record the checks actually performed separately from this checklist.

When a packaged development host launches setup or creates shortcuts, Windows can redirect newly created AppData files and HKCU registration into that host's private storage. A shortcut's apparent path and an installer exit code alone do not prove a normal Start launch works. Use a normal unpackaged installer launch, verify the real Start entry, and open Typr from it with its normal profile. See [Microsoft's description of AppData and registry redirection](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes).
