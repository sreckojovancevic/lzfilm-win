# LzFilm for Windows

A desktop archiver with two modes:

- **Files and folders** — the usual "add files and folders, press Pack", like 7-Zip: one solid LZMA (xz)
  stream, split volumes, self-extracting `.exe`, resume, SHA-256 per file.
- **Disk image versions** — successive versions of large disk images (VM disks, raw backups) in one
  compact archive. Each version is coded against the previous one, block by block, and the residual is
  compressed with LZMA. When versions differ little, ten snapshots of a disk cost not much more than one.

This is the Windows application of the [lzfilm](https://github.com/sreckojovancevic/lzfilm) research
prototype. The engine is rewritten in C# as a library (`LzFilm.Core.dll`) that the GUI calls directly —
no command-line tool is started in the background.

## Features

- **Files and folders** — solid compression (similar files are placed next to each other), empty files
  and folders, modification times and attributes (read-only, hidden, system, archive) are kept;
  hidden and system files are included; folder links (junctions) are stored but not followed.
  Extract everything or only selected files/folders — only the segments that contain them are decoded.
- **Version chain** (disk images) — version *i* is predicted from version *i−1* per 4 KB block: unchanged, changed in
  place (XOR residual), **moved** (block found elsewhere in the previous version) or new data.
- **Split volumes** — `name.lzs.001`, `name.lzs.002`, … of any size (10 MB, 100 MB, 4095 MB for FAT32, custom).
- **Self-extracting archives** — `name.exe` (+ `name.lzs.002`, …). Double-click, choose a folder, done.
  No installation and no .NET needed on the target machine.
- **Resume** — an interrupted or cancelled backup continues where it stopped.
- **Integrity** — every 32 MB segment is an independent xz stream (CRC64) with its own SHA-256; every
  version has a SHA-256. Damage is reported per volume and per segment.
- **Verify after packing** — the archive is decoded and compared with the original files.
- **Multi-threaded** — the output is byte-identical for any number of threads.
- **Progress, speed, ETA and cancel** for every operation.
- **Unicode names** — names are stored as UTF-8, so Cyrillic, Latin with diacritics etc. survive on any
  system code page (unlike Explorer's "Compressed (zipped) folder", which uses the OEM code page).

## When to use it (and when not)

Good fit: several full images of the **same** disk taken at different times — raw `.img`, flat
`-flat.vmdk`, fixed `.vhd`. Blocks line up between versions, so only what changed is stored.

Poor fit: sparse/dynamic container formats (sparse VMDK, dynamic VHDX, QCOW2, dynamic VDI). Their
internal layout shifts between versions. The app detects these and tells you how to convert them, e.g.
`vmware-vdiskmanager -r disk.vmdk -t 2 disk-flat.vmdk` or `qemu-img convert -O raw disk.qcow2 disk.raw`.
For ordinary files and folders use the *Pack files* tab. Compression is about the same as 7-Zip's LZMA
(it is the same algorithm); what LzFilm adds is the volume / self-extracting / resume / verification
machinery and exact Unicode names.

Benchmark (4 × 512 MB ext4 images, Linux, liblzma 5.4.5): 2048 MiB → 402.6 MiB, 228 s with 1 thread,
114 s with 2 threads — the same ratio as the Rust prototype (401.7 MiB).

## Build

Requirements: Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0),
git, and Visual Studio with the C++ workload (for liblzma; or see the alternatives in
`tools\build-liblzma.ps1`).

```powershell
.\build.ps1
```

This will

1. build `native\liblzma.dll` from the official xz sources if it is missing (`tools\build-liblzma.ps1`),
2. publish the self-extractor stub to `artifacts\sfx\LzFilm.Sfx.exe`,
3. publish the app to **`artifacts\app\LzFilm.exe`** — one self-contained file with the stub and
   liblzma inside,
4. run the tests, including a real self-extracting archive.

Opening `LzFilm.sln` in Visual Studio works too; publish `LzFilm.Sfx` before `LzFilm.App` if you want the
self-extracting option in your build.

If PowerShell refuses to run the script: `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

## Usage

**Pack files tab** — add files and folders (or drag them in), choose the archive name, optionally split
into volumes and/or make it self-extracting, press *Pack*. Pressing *Pack* again after an interruption,
with the same files and settings, offers to continue.

**Disk image versions tab** — add the versions (oldest first; drag and drop works, "Sort by date" helps), choose the
archive name, optionally split into volumes and/or make it self-extracting, press *Start backup*.
Starting again with the same name and settings after an interruption offers to resume.

**Extract / Test tab** — open `name.lzs`, `name.lzs.001` (any part works) or `name.exe`. For a files
archive tick files and folders in the tree; for disk images tick the versions (versions that are only
needed as a reference are decoded to a temporary file and deleted). *Test integrity* checks every
segment without writing anything.

**Self-extracting archive** — keep all parts in one folder and run `name.exe`. Options for scripts:

```
name.exe [/dest <folder>] [/all | /latest | /versions 1,3] [/test] [/yes]
```

`/yes` runs without questions. A files archive is extracted completely (default: into `<name>` next to the
exe); for disk images the default is the latest version into `<name>-restored`.

## Notes and limitations

- Files mode does not store NTFS permissions (ACLs) or alternate data streams. A file that another
  program keeps exclusively locked (e.g. an open Outlook `.pst`) stops packing with a clear message —
  close the program or leave the file out; the archive can then be resumed only if the file list is unchanged.

- Unsigned self-extracting `.exe` files may be flagged by SmartScreen or antivirus heuristics. Signing
  the stub, or sending the plain `.lzs` volumes together with `LzFilm.exe`, avoids that.
- The stub adds roughly 10–20 MB to the first volume (it contains its own .NET runtime).
- Memory: about 0.5–1 GB for a typical backup (shown in the app before you start); mostly the
  moved-block index, which is limited to 1 GB per version.
- Archive format **v3** (`.lzs`) is not readable by the Rust prototype CLI (format v2); updating the CLI
  is on the list.

## Project layout

```
src/LzFilm.Core    engine (DLL): format, volumes, version coding, xz binding;
                   SnapArchiver (disk image versions), FolderArchiver (files and folders)
src/LzFilm.App     WinForms desktop app (self-contained)
src/LzFilm.Sfx     self-extractor stub (trimmed single-file console program)
tests/LzFilm.Tests test runner: dotnet run --project tests/LzFilm.Tests -c Release
tools/             build-liblzma.ps1
```

The engine and its tests also run on Linux with the system liblzma (`liblzma.so.5`).

## Roadmap

- Files mode: deduplication of identical files; adding files to an existing archive; optional AES encryption.
- Update the Rust CLI to read format v3.
- Optional code signing of the stub.

## Authors

- **Srećko Jovančević** — idea, design, testing

## Acknowledgments

- **Claude (Anthropic)** — code elaboration and codebase extension
- **Claude (Anthropic)** — icon design (`src/LzFilm.App/lzfilm.ico`, drawn by `tools/make-icon.py`)
- [xz-utils / liblzma](https://tukaani.org/xz/) by Lasse Collin and contributors — LZMA compression (0BSD)

## License

MIT — see [LICENSE](LICENSE).
