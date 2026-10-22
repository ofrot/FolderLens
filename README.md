# FolderLens

FolderLens is a lightweight, portable Windows storage analyzer. Pick a folder or scan every ready local drive to see exactly what is using your disk space.

## Download and run

1. Download `FolderLens-Windows.zip` from the [latest GitHub release](https://github.com/ofrot/FolderLens/releases/latest).
2. Extract the ZIP.
3. Double-click `FolderLens.exe`.

There is no installer and no package manager. FolderLens targets the Windows .NET Framework included with current Windows 10 and Windows 11 systems. Because community builds are not code-signed, Windows may show a SmartScreen warning on first launch; verify that the file came from this repository's Releases page before running it.

## Features

FolderLens shows:

- total space used, file count, folder count, and inaccessible items;
- the largest immediate subfolders as a chart and sortable-style table;
- a complete size-annotated folder tree;
- the 500 largest files, with filtering and CSV export;
- shortcuts that open folders or select files in File Explorer.
- a combined **All drives** scan that presents every ready local drive in one result.

The scan runs in the background, can be cancelled, ignores filesystem links to avoid loops, and continues when it encounters an inaccessible folder.

Every successful scan is saved automatically. When FolderLens opens again, it restores the complete last result—including the folder tree and largest-file list—and shows when that snapshot was created. The snapshot is stored at `%LocalAppData%\FolderLens\last-scan.fls` and is replaced only after a new scan has been saved successfully.

## Run from this repository

Double-click **Launch FolderLens.cmd**. A prebuilt `FolderLens.exe` is also included when the project has been compiled.

Use the **All drives** button inside the app, or start it directly in that mode with `FolderLens.exe --all-drives`.

## Build

Right-click `build.ps1`, choose **Run with PowerShell**, or run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

FolderLens uses the C# compiler included with the Windows .NET Framework. It needs no package manager or third-party dependencies. Run `package.ps1` to produce the portable ZIP and its SHA-256 checksum in `dist/`.

## Test

Run `test.ps1` to compile and execute scanner tests against a temporary folder tree. The test verifies byte totals, recursive folder totals, file ordering, progress reporting, and cancellation.
