# Hodozip

**Unpack your archives.**

[Japanese README](README.md)

Hodozip is a free, open-source archive extractor for Windows that carries the usability of classic Japanese extraction tools into a modern implementation. Drag and drop ZIP, 7z, RAR, MSI, TAR, GZ, BZ2, XZ, and ZST archives; Hodozip chooses the destination folder automatically and extracts them.

- Product name: Hodozip
- Version: 0.4.0
- License: MIT
- Supported OS: Windows 11 (primary) / Windows 10 version 19041 or later
- Implementation: C# / .NET 8 / WinUI 3 (Windows App SDK)
- Distribution: an installer (setup.exe), a portable unpackaged zip, and an MSIX package with file associations and context-menu integration

## Download

Download the latest release from [GitHub Releases](https://github.com/Ren9618/Hodozip/releases).

- Latest: [Hodozip v0.4.0](https://github.com/Ren9618/Hodozip/releases/tag/v0.4.0)
- `Hodozip-0.4.0-setup.exe` (installer, recommended): installs per user into `%LOCALAPPDATA%\Programs\Hodozip` without administrator rights. It creates a Start menu shortcut and optionally registers the legacy context menu and "Open with" candidates. See [Installer installation](#installer-installation-recommended).
- `Hodozip-0.4.0-win-x64.zip` (unpackaged): unzip it and run `Hodozip.exe`. No installation is required.
- `Hodozip-0.4.0.msix` (MSIX, experimental): registers Hodozip as a candidate for supported archive types and adds Windows 11 context-menu entries. It uses a self-signed certificate, so the certificate must be trusted before installation. See [MSIX installation](#msix-installation-advanced-experimental).

Note for the installer and unpackaged build: they are unsigned, so Windows SmartScreen may show a warning. Choose "More info" → "Run" to start them.

The unpackaged build also includes `Hodozip.ContextMenu.dll`. You can register the legacy context menu from the Settings window or from the command line.

## Changes in v0.4.0

- Added a Japanese / English UI (Settings → Language). The default is "Follow system settings"; changes take effect on the next launch.
- The legacy context menu now switches item labels according to the OS display language (Japanese: 「Hodozip で解凍」/「Hodozip でフォルダに解凍」, English: "Extract with Hodozip" / "Extract to folder with Hodozip").
- Added entry-level parallel ZIP extraction. Parallelism is capped at the CPU core count and can be set in Settings to Auto / 1 / 2 / 4 / 8 / 16 / 32 threads.
- Measured 5,336 ms → 806 ms (about 6.6x) for a large ZIP with 1 GiB of expanded data, and 28,577 ms → 8,210 ms (about 3.5x) for a 10,000-file ZIP. See [Performance](#performance-phase-4) for details.
- Evaluated cozip (Rust + WebGPU) as a GPU extraction backend. Its ZIP decompression is CPU-only, and even including the helper process startup it did not reach the 2x adoption gate, so the GPU engine was not adopted. The evaluation is recorded in [GPU extraction evaluation](#gpu-extraction-evaluation-phase-4).
- Added `README.en.md` and an English website.

## Changes in v0.3.1

- Added MSI (Windows Installer) extraction (embedded and external cabinets verified with fixtures).
- Shortened the folder-collision dialog button labels so long labels are not clipped.

## Phase 3 features (v0.3.0)

- Added extraction for 7z / RAR / TAR / TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST / GZ / BZ2 / XZ / ZST (each verified with fixtures).
- Added destination-folder collision policies: ask every time / merge into the existing folder / create a numbered folder.
- Wired the same-name overwrite dialog into the normal flow: Overwrite / Skip / Save with a new name / Overwrite all / Skip all / Cancel.
- Redesigned the progress window as a fixed-size, compact layout with middle ellipsis.
- Added a legacy `IContextMenu` handler for the unpackaged build, with registration and removal from Settings.
- Reflected supported extensions in file associations and the Windows 11 context menu.
- Unified the in-app version display to 0.3.0 (MSIX package 0.3.0.0).

## Supported formats

Only formats that have been verified with generated fixtures in unit tests and E2E are listed as supported.

| Format | Extensions | Fixture verification |
| --- | --- | --- |
| ZIP | `.zip` | Yes (including ZipCrypto / AES password-protected) |
| 7z | `.7z` | Yes (including password-protected) |
| RAR | `.rar` | Yes (RAR5 test archive) |
| MSI | `.msi` | Yes (embedded cabinet / Windows Installer database) |
| TAR | `.tar` | Yes |
| TAR.GZ | `.tar.gz`, `.tgz` | Yes (both extensions) |
| TAR.BZ2 | `.tar.bz2`, `.tbz2` | Yes (both extensions) |
| TAR.XZ | `.tar.xz`, `.txz` | Yes (both extensions) |
| TAR.ZST | `.tar.zst`, `.tzst` | Yes (both extensions) |
| GZ | `.gz` | Yes (single file inside) |
| BZ2 | `.bz2` | Yes (single stream) |
| XZ | `.xz` | Yes (single stream) |
| ZST | `.zst` | Yes (single stream) |

- `.cab` and `.lzh` are not included because they have not been verified.
- MSI never runs the installer. It reads the Windows Installer database (`msi.dll`) and expands cabinets with `expand.exe` to extract only the files in the package. Standard folders such as `ProgramFiles64Folder` are replaced with display names such as "Program Files".
- Single-stream GZ / BZ2 / XZ / ZST archives are extracted to a file named after the archive without its extension.
- If the extension is unknown, Hodozip still tries to process the file when SharpCompress can open it as an archive. Only the verified formats above are declared as supported.
- Encrypted archives have been tested for ZIP (ZipCrypto / AES) and 7z. Encryption in RAR and other formats is not declared as supported.

## Performance (Phase 4)

The benchmark harness is in `tests/perf`. It generates the following fixtures and measures sequential and parallel extraction under the same conditions.

1. Large, highly compressible ZIP (16 × 64 MiB = 1 GiB of expanded data)
2. Many small files (10,000 files)
3. Store-heavy ZIP (128 × 2 MiB uncompressed entries)
4. 7z (80 files)

```powershell
# Phase 3 equivalent (sequential)
dotnet run --project tests\perf\Hodozip.Perf.csproj -c Release -- --mode sequential --iterations 3 --warmup 1 --label phase3-v031-sequential

# Phase 4 (parallel)
dotnet run --project tests\perf\Hodozip.Perf.csproj -c Release -- --mode parallel --iterations 3 --warmup 1 --label phase4-parallel

# Comparison report
dotnet run --project tests\perf\Hodozip.Perf.csproj -c Release -- --compare artifacts\perf\reports\phase3-v031-sequential.json artifacts\perf\reports\phase4-parallel.json --out tests\perf\reports\comparison.md
```

Results are kept in `tests/perf/reports/`. Actual values vary with the machine and disk-cache state.

Final measurement (16 logical processors, median of 3 runs per fixture):

| Fixture | Sequential (ms) | Parallel (ms) | Speedup |
| --- | ---: | ---: | ---: |
| Large, highly compressible ZIP (1 GiB expanded, 16 entries) | 5,336 | 806 | 6.62x |
| Many small files (10,000 entries) | 28,577 | 8,210 | 3.48x |
| Store-heavy ZIP (256 MiB, 128 entries) | 944 | 184 | 5.14x |
| 7z (80 files, sequential only) | 3,104 | 3,111 | 1.00x |

7z / RAR / TAR / MSI and encrypted ZIP are not parallelized and remain sequential. The 7z difference is run-to-run noise.

## GPU extraction evaluation (Phase 4)

We evaluated [bea4dev/cozip](https://github.com/bea4dev/cozip) (MIT License) as a candidate GPU extraction engine. The helper source is in `tools/cozip-spike/` and is not included in the distributed application.

The conclusion is **not adopted**. Reasons:

- As of November 2026, cozip's ZIP decompression is CPU-only (the upstream README says "decompression is CPU-only now"). It does not satisfy the purpose of GPU-accelerated extraction.
- As a CPU-parallel ZIP decompressor it is fast: the helper's internal measurement for the 1 GiB fixture had a median of 256 ms (SharpCompress parallel: 806 ms). However, including process startup and GPU initialization, the wall-clock median was 1,235 ms (0.65x), below the 2x adoption gate. CPU-only mode without GPU initialization took 320 ms (2.52x), but that is not GPU acceleration.
- The store-heavy ZIP regressed to 0.58x in CPU-only mode, so the improvement was not stable across fixtures.
- Normal ZIP and Shift_JIS filename output matched SharpCompress, but adding and maintaining another engine that is not GPU extraction is not worth the cost.

See [`tests/perf/reports/gpu-cozip-evaluation.md`](tests/perf/reports/gpu-cozip-evaluation.md) for details. We also considered sending encrypted ZIP files to SharpCompress as a fallback instead of cozip, but did not implement it because the engine was not adopted.

## Features

### Japanese / English UI

Settings → Language offers `Follow system settings` (default) / `日本語` / `English`. Language changes take effect on the next launch. Context-menu item labels follow the OS display language, not the app setting.

### Parallel extraction (ZIP)

ZIP archives are extracted in parallel at entry level. Parallelism is capped at the CPU core count and can be changed in Settings under "Parallel extraction" (default: Auto). Overwrite confirmations, folder-collision dialogs, and password prompts are resolved before parallel workers start, so multiple dialogs never appear at once. Formats that cannot open an independent stream per entry, such as 7z / RAR / TAR, remain sequential.

### Extract immediately by double-clicking

Pass an archive path to `Hodozip.exe` to start extraction immediately. If you install the MSIX build and set Hodozip as the default app for supported extensions, double-clicking extracts the archive.

When launched without arguments, the main window appears and accepts dropped archives.

### Automatic destination selection

In `Auto` mode, the destination is chosen from the archive contents:

| Archive contents | Result |
| --- | --- |
| Exactly one file at the root | Write the file next to the archive without creating a folder |
| All entries under a single top-level folder | Extract that top-level folder (no doubled folder) |
| Anything else (multiple files at various levels) | Create a folder named after the archive (without extension) and extract into it |

`ForceArchiveFolder` mode always creates a folder named after the archive (without extension) and extracts into it, regardless of contents. Compound and short extensions such as `.tar.gz` and `.tgz` are removed together from the archive name.

### Destination-folder collision

You can configure what happens when the destination folder already exists.

| Setting | Behavior |
| --- | --- |
| Ask every time (`Ask`, default) | Show a dialog with the target folder name and path before extraction |
| Extract into the existing folder (`Merge`) | Merge into the existing folder |
| Create a new folder (`AutoRename`) | Create a numbered folder such as `Name (2)`, `Name (3)` |

The `Ask` dialog offers "Extract into existing", "New folder", and "Cancel". Enabling "Make this the default" saves the choice. If the existing same-name path is a file rather than a folder, "Extract into existing" is unavailable.

This dialog is not shown for patterns that do not create a folder, such as extracting a single file directly. Same-name files are handled by the per-file overwrite dialog.

### Same-name overwrite confirmation

When extracting into an existing folder with `Merge`, or when extracting a single file whose name already exists, Hodozip shows an overwrite dialog if the setting is `Ask`. It compares the size and modification time of the existing and new files and offers the same six choices as Lhaplus:

- **Overwrite**: overwrite this file
- **Skip**: keep this file and skip
- **Save with a new name**: save as `Name (2)`
- **Overwrite all**: overwrite every collision in the current extraction session
- **Skip all**: skip every collision in the current extraction session
- **Cancel**: abort the whole extraction

"Overwrite all" and "Skip all" apply only to the current extraction session and are not saved to settings. Esc or closing the dialog is treated as Cancel. Cancel deletes temporary files being written and does not leave partial files behind.

In Settings, the overwrite policy can be set to `Ask` / `Overwrite` / `Skip` / `AutoRename`.

### Progress window

The progress window has a fixed size of 480×240 and cannot be resized. It does not change size with file names or progress values.

- Title bar: progress percentage and archive name, such as `58% / sample.zip`
- Destination path: one line with middle ellipsis (keeps the beginning and end)
- Overall progress bar and percentage
- Current file name (middle ellipsis) and its per-file progress bar
- Status text appears in exactly one place
- Completion, errors, and cancellation appear in the same fixed window

Long paths and file names use middle ellipsis instead of tail truncation, and tooltips show the full value.

### Windows 11 context menu

Installing the MSIX build adds two entries to the context menu for supported extensions:

- **Extract with Hodozip**: extracts in `Auto` mode. Single-folder archives are not doubled.
- **Extract to folder with Hodozip**: extracts in `ForceArchiveFolder` mode, always creating a folder named after the archive.

If the OS display language is English, the labels are "Extract with Hodozip" / "Extract to folder with Hodozip".

Supported extensions are `.zip` `.7z` `.rar` `.msi` `.tar` `.gz` `.tgz` `.bz2` `.tbz2` `.xz` `.txz` `.zst` `.tzst`. If the entries do not appear in the new Windows 11 menu, choose "Show more options" or press `Shift` + `F10` to open the legacy menu.

### Legacy context menu (unpackaged build)

With the unpackaged build, you can add the same two entries to the Windows 10-style legacy context menu (the "Show more options" side on Windows 11). They are shown flat, not in a submenu.

To register:

1. Start Hodozip and open Settings.
2. Turn on "Register in legacy context menu" under "Legacy context menu".
3. Confirm that the status shows "Registered in the legacy context menu."
4. Turn the same toggle off to unregister.

Registration is per-user under HKCU and does not require administrator rights.

- `HKCU\Software\Classes\SystemFileAssociations\<extension>\shellex\ContextMenuHandlers\Hodozip`
- `HKCU\Software\Classes\CLSID\{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}\InprocServer32`

You can also register and unregister from the command line (for E2E and troubleshooting).

```powershell
# Register
.\Hodozip.exe --register-context-menu

# Unregister
.\Hodozip.exe --unregister-context-menu
```

Registration stores the absolute path to the bundled `Hodozip.ContextMenu.dll`. If you move the unpackaged folder, unregister the old path and register again from the new location. Unregister before deleting the folder.

### Why the legacy menu is unavailable in the MSIX build

The MSIX (packaged) build cannot register the legacy `IContextMenu` handler. The legacy-menu toggle is therefore disabled in the MSIX Settings window, and only the new Windows 11 context menu is available.

Reasons:

- Legacy shell extensions are loaded in-process by Explorer. Packaged apps have HKCU `Software\Classes` writes virtualized, and there is no official way to make the real shell load `Hodozip.ContextMenu.dll` as an in-proc handler.
- The only context-menu extension Windows offers to packaged apps is `windows.fileExplorerContextMenus` (`IExplorerCommand`), which appears in the new Windows 11 menu.
- Even if the app wrote to HKCU directly, Explorer would not recognize it. The MSIX build therefore offers only the new menu.

### Japanese filename handling

ZIP entry UTF-8 flags are respected.

- UTF-8 flag set: decode as UTF-8
- UTF-8 flag not set: decode as CP932 (Shift_JIS)

CP932 decoding registers .NET's `CodePagesEncodingProvider` and sets SharpCompress's `ArchiveEncoding.Default` to CP932. This Shift_JIS fallback is ZIP-specific. 7z / RAR / TAR and other formats use UTF-8 and do not mis-decode Unicode names as CP932. See `src/Hodozip.Core/EncodingSetup.cs` and `CreateReaderOptions` in `src/Hodozip.Core/SharpCompressExtractor.cs`.

### Password-protected archives

Hodozip supports ZipCrypto / AES password-protected ZIP and password-protected 7z. When an encrypted entry is detected, the UI shows a password dialog and resumes extraction after input. A wrong password can be retried up to five times.

### Security

- Path traversal: rejects entries containing `../`, absolute paths, or drive letters. Applies to all supported formats, not just ZIP; rejection is tested with a TAR `../` entry.
- Symbolic links: neither links nor regular files are extracted; Hodozip warns and skips them.
- Long paths: supports paths over 260 characters with the `\\?\` prefix.
- Windows reserved device names (CON, PRN, AUX, NUL, COM1, etc.) are sanitized.
- Zip-bomb protection: warns when the expanded size is at least 1 GiB and at least 1000 times the compressed size.

### No network communication

There is no telemetry. The application itself does not communicate over the network. The only exception is the "Support development" button in About and Settings, which opens the donation page in the default browser. No donation pop-ups appear at startup or after extraction.

## Usage

### Extract with the app

1. Start `Hodozip.exe`.
2. Drag a supported archive into the window, or choose "Select file".
3. Progress is displayed. When finished, "Open folder" shows the destination.

### Launch from the command line

```powershell
# Auto mode (path only)
.\Hodozip.exe "C:\path\to\archive.zip"
.\Hodozip.exe "C:\path\to\archive.7z"

# Explicit Auto mode
.\Hodozip.exe --extract-here "C:\path\to\archive.tar.gz"

# Always create a folder named after the archive
.\Hodozip.exe --extract-folder "C:\path\to\archive.rar"
```

| Argument | Extraction mode | Behavior |
| --- | --- | --- |
| `<archive>` | `Auto` | Choose automatically from the archive contents |
| `--extract-here <archive>` | `Auto` | Same as above (explicit form for the context menu) |
| `--extract-folder <archive>` | `ForceArchiveFolder` | Always create a folder named after the archive |
| `--register-context-menu` | - | Register the legacy context menu and exit (unpackaged only) |
| `--unregister-context-menu` | - | Unregister the legacy context menu and exit (unpackaged only) |

Hodozip extracts next to the archive according to the selected mode. If "Close automatically after command-line extraction" is enabled in Settings, the app exits after completion when launched from the command line (it stays open on errors).

## Installer installation (recommended)

`Hodozip-0.4.0-setup.exe` is an Inno Setup installer. It does not require administrator rights and installs for the current user.

1. Download and run `Hodozip-0.4.0-setup.exe`. It is unsigned; if Windows SmartScreen shows a warning, choose "More info" → "Run".
2. The install location is fixed to `%LOCALAPPDATA%\Programs\Hodozip`; no directory page is shown.
3. Under "Shell integration" you can optionally enable (both are checked by default):
   - Register the classic right-click menu (current user only)
   - Add supported archives to "Open with"
4. "Create a desktop icon" is off by default.
5. After installation, "Hodozip" appears in the Start menu.

The installer only adds Hodozip to the "Open with" candidates; it does not change the default app. To make it the default, open Windows Settings → Apps → Default apps and choose Hodozip, or right-click a file → "Open with" → "Choose another app" → "Hodozip" → "Always use this app to open these files".

To uninstall, open Settings → Apps → Installed apps and select Hodozip. User settings (`%LOCALAPPDATA%\Hodozip\settings.json`) are preserved.

## MSIX installation (advanced, experimental)

The MSIX build is experimental. It is signed with a local test self-signed certificate (`CN=Ren9618`), not a certificate reviewed by the Microsoft Store. Proceed at your own risk.

### 1. Install the certificate

Download [`packaging/certs/Hodozip-Test.cer`](https://github.com/Ren9618/Hodozip/raw/v0.4.0/packaging/certs/Hodozip-Test.cer) from the repository. This is the public certificate only; it contains no private key.

In an administrator PowerShell, add the downloaded CER to `LocalMachine\TrustedPeople`.

```powershell
certutil -addstore -f TrustedPeople .\Hodozip-Test.cer
```

The certificate thumbprint is `09F6216808D67CCAF8686609F375EC7BFF837284`. You can verify it with `Get-PfxCertificate` or similar.

> Note: registering only in `CurrentUser\TrustedPeople` may cause `Add-AppxPackage` to fail with `0x800B0109`. In that case, register in `LocalMachine\TrustedPeople` as shown above.

### 2. Install the MSIX

Double-click `Hodozip-0.4.0.msix`, or run it from PowerShell.

```powershell
Add-AppxPackage -Path .\Hodozip-0.4.0.msix
```

After installation, "Hodozip" appears in the Start menu. The package ID is `Ren9618.Hodozip_0.4.0.0_x64__ex19z75j0wj0g`, and the application ID is `Ren9618.Hodozip_ex19z75j0wj0g!Hodozip`.

### 3. Set Hodozip as the default app for supported extensions

Windows does not allow apps to force file associations. Installing the MSIX build registers Hodozip as an "Open with" candidate for supported extensions, but the user must set it as the default.

1. Open Windows Settings → Apps → Default apps.
2. Search for an extension such as `.zip`, or select it from the list.
3. Choose "Hodozip".

Alternatively, right-click a file → "Open with" → "Choose another app" → "Hodozip" → "Always use this app to open these files".

### 4. Use the context menu

Right-click a supported archive and choose "Extract with Hodozip" or "Extract to folder with Hodozip". If the entries do not appear in the new Windows 11 menu, choose "Show more options".

The MSIX build cannot register the legacy `IContextMenu`. See [Why the legacy menu is unavailable in the MSIX build](#why-the-legacy-menu-is-unavailable-in-the-msix-build).

After installation or an update, restarting Explorer helps the menu appear.

### 5. Uninstall

Uninstall Hodozip from Settings → Apps → Installed apps. From PowerShell:

```powershell
Remove-AppxPackage Ren9618.Hodozip_0.4.0.0_x64__ex19z75j0wj0g
```

If you no longer need the certificate, remove it from an administrator PowerShell:

```powershell
certutil -delstore TrustedPeople 09F6216808D67CCAF8686609F375EC7BFF837284
```

## Building

### Requirements

- Windows 10 version 19041 or later / Windows 11
- .NET SDK 8.0 or later (able to build `net8.0`)
- Visual Studio 2022 or later, or Build Tools (WinUI 3 / Windows App SDK / C++ build environment)
- Windows 10/11 SDK (`makeappx.exe` / `signtool.exe`) to build the MSIX
- Inno Setup 6.7 or later (`ISCC.exe`) to build the installer
- x64

### Build the app

```powershell
dotnet build Hodozip.sln -c Release -p:Platform=x64
```

Build output:

```text
src\Hodozip\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\Hodozip.exe
```

Windows App SDK is output self-contained, so the unpackaged build does not require an MSIX installation.

### Publish the unpackaged build and create a zip

To use the legacy context menu, `Hodozip.ContextMenu.dll` must be included in the publish output. The MSIX build script performs both the publish and the COM DLL copy, so its output can be zipped.

```powershell
pwsh -File packaging\build-msix.ps1
New-Item -ItemType Directory -Path artifacts\release\Hodozip -Force | Out-Null
Copy-Item -Path artifacts\msix\publish\* -Destination artifacts\release\Hodozip -Recurse -Force
Compress-Archive -Path artifacts\release\Hodozip -DestinationPath Hodozip-0.4.0-win-x64.zip
```

To create only the zip build without the MSIX, publish and then build the C++ project to copy the DLL.

```powershell
dotnet publish src\Hodozip\Hodozip.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o publish\Hodozip
msbuild src\Hodozip.ContextMenu\Hodozip.ContextMenu.vcxproj /p:Configuration=Release /p:Platform=x64
Copy-Item src\Hodozip.ContextMenu\bin\x64\Release\Hodozip.ContextMenu.dll publish\Hodozip\
Compress-Archive -Path publish\Hodozip -DestinationPath Hodozip-0.4.0-win-x64.zip
```

The zip contains exactly one top-level `Hodozip` folder.

### Build the installer

With Inno Setup 6 installed, run:

```powershell
pwsh -File packaging\build-installer.ps1
```

The script publishes the app, builds the COM DLL, generates the installer icon, and compiles the Inno Setup script. The output is `artifacts\installer\Hodozip-0.4.0-setup.exe` plus its SHA256 file.

- `-SkipPublish` / `-SkipComBuild` reuse an existing publish output or COM DLL.
- `-InnoCompilerPath` specifies the location of `ISCC.exe` (by default the per-user Inno Setup 6 installation is used).
- The installer is defined in `packaging\Hodozip.iss`. Change the install location, optional tasks, and registry entries there.

### Build the MSIX

```powershell
pwsh -File packaging\build-msix.ps1
```

The script performs the following:

1. `dotnet publish` to create the unpackaged publish output
2. Build the COM DLL in `src\Hodozip.ContextMenu` for x64 Release
3. Stage the publish output, COM DLL, and generated asset images in `artifacts\msix\staging`
4. Generate `AppxManifest.xml` from `packaging\Package.appxmanifest.template`
5. Create `artifacts\msix\Hodozip-0.4.0.msix` with `makeappx.exe`
6. Sign with `signtool.exe` using the self-signed certificate

The certificate is created at `artifacts\msix\certs\Hodozip-Test.pfx`. The PFX contains a private key and is never committed or included in a release.

To automatically add the test certificate to `LocalMachine\TrustedPeople`, run the following as administrator:

```powershell
pwsh -File packaging\build-msix.ps1 -InstallTrustedCertificate
```

Use `-RecreateCertificate` to recreate the certificate. The Publisher (`CN=Ren9618`) must match the certificate Subject.

### Versioning

The version is managed by `HodozipVersion` in `Directory.Build.props`. The app's `AssemblyVersion` / `FileVersion` and the MSIX `PackageVersion` (`0.4.0` → `0.4.0.0`) are generated from this value.

## Testing

### Core unit tests

```powershell
dotnet test tests\Hodozip.Core.Tests\Hodozip.Core.Tests.csproj -c Release
```

The 76 tests verify:

- Automatic destination selection (single file / single folder / scattered files)
- Forced folder creation in `ForceArchiveFolder` mode (single file / single folder / scattered files)
- Folder collision policies (Merge / AutoRename / Ask) and cancellation of Ask
- Overwrite callback invocation during `Merge`, automatic renaming, and no leftover files on cancel
- Size and modification-time comparison in the overwrite confirmation
- Extraction of fixtures for every supported format (ZIP / 7z / RAR / MSI / TAR / TAR.GZ / TGZ / TAR.BZ2 / TBZ2 / TAR.XZ / TXZ / TAR.ZST / TZST / GZ / BZ2 / XZ / ZST)
- Password-protected ZIP (ZipCrypto / AES) and password-protected 7z, including retry and cancel
- Rejection of TAR `../` entries
- Shift_JIS and UTF-8 filenames (Shift_JIS only for ZIP)
- Zip Slip rejection
- Reserved device names, symbolic links, zip-bomb warnings, long paths
- Overall and per-file progress byte reporting
- Parallel/sequential output equivalence (normal ZIP / Shift_JIS / Merge / overwrite pre-resolution)
- Zip Slip rejection during parallel extraction and temp-file cleanup on cancel

The password for `tests/Hodozip.Core.Tests/Fixtures/password-*.zip` and `Fixtures/formats/password.7z` is `secret`. The ZIP fixtures are generated with 7-Zip.

Fixture generation is documented in `tests/Hodozip.Core.Tests/Fixtures/formats/generate-fixtures.ps1`. It requires a 7-Zip-compatible CLI and an internet connection to obtain the RAR fixture. The MSI fixture is generated from WiX Toolset (`wix.exe`) with `generate-msi-fixture.ps1`. Generated files are committed to the repository.

### App E2E

Run after a Release build or publish.

```powershell
# Use the build output
pwsh -File tests\e2e\run-e2e.ps1

# Use the publish output
pwsh -File tests\e2e\run-e2e.ps1 -ExePath publish\Hodozip\Hodozip.exe
```

E2E creates ZIPs in a temporary folder and verifies five patterns:

1. Extract a single-folder archive by path (Auto) without doubling the folder
2. Extract a scattered archive by path (Auto) and create an archive-name folder
3. Extract a single-folder archive with `--extract-here` without doubling the folder
4. Extract a single-folder archive with `--extract-folder` and always create an archive-name folder
5. Launch without arguments and show the main window

### Phase 3 E2E

```powershell
# Overwrite flow (Merge + Overwrite / Merge + Skip / Ask + UI Automation)
pwsh -File tests\e2e\run-overwrite-e2e.ps1

# Multi-format extraction (ZIP / 7z / RAR / TAR / compression variants + short extensions)
pwsh -File tests\e2e\run-formats-e2e.ps1

# Legacy context menu (registration, menu items, InvokeCommand, unregistration)
pwsh -File tests\e2e\run-legacy-menu-e2e.ps1
```

- `run-overwrite-e2e.ps1` verifies three cases. The third detects the `OverwriteDialog` with UI Automation and presses the Cancel button.
- `run-formats-e2e.ps1` verifies 17 format/extension cases (ZIP, 7z, TAR, RAR, MSI, TAR.GZ/TGZ, TAR.BZ2/TBZ2, TAR.XZ/TXZ, TAR.ZST/TZST, GZ, BZ2, XZ, ZST).
- `run-legacy-menu-e2e.ps1` restarts itself in STA, verifies HKCU registration, handlers for 13 extensions, the two items from `IContextMenu::QueryContextMenu`, extraction via `InvokeCommand` in both modes, and unregistration. It restores the pre-run registration state and settings file on exit.
- E2E temporarily modifies `%LOCALAPPDATA%\Hodozip\settings.json`. Do not run the scripts concurrently.

### MSIX installation validation

MSIX validation installs the built package for real.

```powershell
# Add the certificate to LocalMachine\TrustedPeople (administrator)
certutil -addstore -f TrustedPeople artifacts\msix\certs\Hodozip-Test.cer

# Install
Add-AppxPackage -Path artifacts\msix\Hodozip-0.4.0.msix

# Check the Start menu and association candidates
Get-StartApps | Where-Object { $_.Name -eq 'Hodozip' }
Get-ItemProperty 'HKCU:\Software\Classes\.zip\OpenWithProgids'

# Uninstall
Remove-AppxPackage Ren9618.Hodozip_0.4.0.0_x64__ex19z75j0wj0g
```

Automated post-install E2E is also available. The two scripts share the settings file, so do not run them concurrently.

```powershell
# Packaged launch and --extract-here / --extract-folder behavior
pwsh -File tests\e2e\run-packaged-e2e.ps1

# Association candidates for supported extensions, COM registration, context-menu titles, and verb launch
pwsh -File tests\e2e\run-contextmenu-e2e.ps1
```

Context-menu COM registration can be checked under `HKLM\SOFTWARE\Classes\PackagedCom\ClassIndex` by CLSID.

- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C01` (Extract with Hodozip)
- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C02` (Extract to folder with Hodozip)
- `4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03` (legacy menu in the unpackaged build)

### Installer installation validation

Install the built installer and verify the registrations and cleanup after uninstallation.

```powershell
# Install (enable the legacy menu and "Open with" candidates)
Start-Process artifacts\installer\Hodozip-0.4.0-setup.exe -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/TASKS="contextmenu,associate"' -Wait

# Check the registrations
Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\Hodozip.exe'
Get-ItemProperty 'HKCU:\Software\Classes\.zip\OpenWithProgids' -Name 'Hodozip.Archive'
Get-ItemProperty 'HKCU:\Software\Classes\CLSID\{4F4C6D2A-71B9-4E4E-9A7D-8D9A0B5E6C03}\InprocServer32'

# Uninstall (%LOCALAPPDATA%\Programs\Hodozip\unins000.exe)
& "$env:LOCALAPPDATA\Programs\Hodozip\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

After uninstallation, confirm that the install folder, the `Hodozip.Archive` ProgID, the `OpenWithProgids` values, App Paths, the Start menu shortcut, and the legacy menu CLSID / handlers are removed. `%LOCALAPPDATA%\Hodozip\settings.json` remains.

## Project structure

```text
Hodozip.sln
Directory.Build.props      central version management
src/
  Hodozip.Core/            extraction logic (UI-independent)
    ArchiveFormatCatalog.cs  supported-format list and detection
    MsiArchiveReader.cs      MSI table/stream reader
    DestinationPlanner.cs    destination-folder selection
    SharpCompressExtractor.cs extraction engine
  Hodozip/                 WinUI 3 app
    Dialogs/               overwrite, folder collision, password dialogs
    Services/              settings, legacy menu registration, middle ellipsis
  Hodozip.ContextMenu/     COM DLL for Windows 11 context menu (IExplorerCommand / IContextMenu)
packaging/
  build-msix.ps1           MSIX build and signing
  generate-assets.ps1      generate MSIX asset images
  Package.appxmanifest.template
  certs/                   public certificate (no private key)
tests/
  Hodozip.Core.Tests/      xUnit unit tests and fixtures
  e2e/                     E2E scripts
  perf/                    performance harness and reports
tools/
  cozip-spike/             cozip helper for GPU evaluation (not distributed)
```

Core uses SharpCompress through the `IArchiveExtractor` interface. Format detection prefers the extension and, when unknown, asks SharpCompress whether it can open the file as an archive. TAR.GZ / TAR.BZ2 / TAR.XZ / TAR.ZST expand the single stream to a temporary TAR and then process it as TAR. MSI reads the `File` / `Media` / `Component` / `Directory` tables with `msi.dll`, expands embedded cabinets with `expand.exe`, and then feeds them into the same extraction pipeline as other formats.

The COM DLL is a thin server implementing `IExplorerCommand` (new Windows 11 menu) and `IContextMenu` (legacy menu); it contains no extraction logic. On `Invoke` / `InvokeCommand`, it launches `Hodozip.exe` with `--extract-here` or `--extract-folder` and the selected file path. The MSIX `IExplorerCommand` runs as a `com:SurrogateServer` inside `dllhost.exe`.

## Libraries

| Library | Version | License | Purpose |
| --- | --- | --- | --- |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.50.8 | MIT | ZIP / 7z / RAR / TAR / GZ / BZ2 / XZ / ZST extraction engine |
| Microsoft Windows App SDK | 1.8.260921001 | MIT | WinUI 3 runtime |
| .NET | 8.0 | MIT | Runtime |
| xUnit | 2.5.3 | Apache-2.0 | Tests (not distributed) |

## Supporting development

Hodozip is free and open source. Support is used for ongoing development and maintenance. You can support it from the "Support development" button in the app or from the links below.

- GitHub Sponsors: <https://github.com/sponsors/Ren9618>
- Stripe: <https://buy.stripe.com/5kQ9AU8Xva8T7tAbnj67S00>

## Known limitations (v0.4.0)

- The MSIX build cannot register the legacy context menu because of Windows packaged-app shell-extension restrictions. The MSIX build only has the new Windows 11 context menu. See [Why the legacy menu is unavailable in the MSIX build](#why-the-legacy-menu-is-unavailable-in-the-msix-build).
- The new Windows 11 context menu does not appear on Windows 10. Use the legacy menu in the unpackaged build on Windows 10.
- Windows decides file associations by the final extension. `.tar.gz` is associated as `.gz` and `.tar.bz2` as `.bz2`. Hodozip itself understands compound extensions, so drag-and-drop and command-line extraction still handle `.tar.gz` correctly.
- RAR / 7z / TAR extraction depends on SharpCompress. Only fixtures verified as described are declared supported; unusual compression options or future format variants may fail. `.cab` and `.lzh` are unsupported.
- Only ZIP and 7z encryption are declared supported. Encrypted RAR and other formats are unverified.
- MSI extraction does not run the installer; it expands the files in the package. External source files outside the MSI (for example `Compressed="no"`) cannot be extracted. Custom actions that create files or modify the registry are not reproduced.
- Per-format icons are not implemented; all supported formats use a common icon.
- The MSIX build is an experimental self-signed distribution. The certificate must be trusted, and SmartScreen or organizational policy may block installation.
- The user must choose the default app for supported extensions; the app cannot force it.
- Legacy menu registration stores the absolute path to `Hodozip.ContextMenu.dll`. Re-register after moving the unpackaged folder.
- The app has not been submitted to the Microsoft Store. A Store submission would need a Store package ID and a trusted code-signing certificate, plus `runFullTrust` justification, a privacy policy, age ratings, and other Store application information.
- Compression, archive-content preview, and automatic updates are not implemented.
- The UI supports Japanese / English. Language changes take effect on the next launch. Legacy context-menu labels follow the OS display language, not the app setting.
- Parallel extraction applies only to normal ZIP archives. Encrypted ZIP and 7z / RAR / TAR / MSI remain sequential, so speedups vary by format.
- Automatic numbering uses the `Name (2)` format and tries up to 10,000.
- Zip-bomb warnings do not cancel extraction; they are reported as warnings in the result.

## License

MIT License. Copyright (c) 2026 Ren9618. See [LICENSE](LICENSE) for details.
