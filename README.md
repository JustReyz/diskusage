# DiskUsage

Fast disk usage analyzer for Windows. Single executable, no installation, no
runtime download. Requires .NET Framework 4.8 (included in Windows 10 and 11).

Two front-ends, one scan approach:

| Executable | What it is |
|---|---|
| `diskusage-gui.exe` | Windowed app: heatmap, folder table, per-extension breakdown |
| `diskusage.exe` | Console app: ranked folder table, largest files, HTML and JSON export |

Neither one ever deletes, moves or modifies a file. They only read sizes.

## The window

`diskusage-gui.exe` scans the root of the drive it sits on when started with no
arguments, or a folder passed on the command line:

```powershell
diskusage-gui.exe
diskusage-gui.exe "G:\Games"
```

```
┌───────────────────────────────────────────────────────────────────────┐
│ DiskUsage  [G:]  G:\Games                              [ Scan ]       │
├───────────────────────────────────────────────────────────────────────┤
│ SELECTION   G:\Games │ 315.71 GB │ 211.91 GB 67.1% │ 103.80 GB │ ...  │
├───────────────────────────────────┬───────────────────────────────────┤
│ Folders                           │ Extensions                        │
│  ▼ Games      100.0%  221.39 GB   │  ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬        │
│    Endfield    60.3%  133.53 GB   │  .chk   29.5%   65.25 GB   1,266  │
│    Data        18.2%   40.20 GB   │  .mp4    7.3%   16.15 GB     340  │
│    Discord      3.5%    7.83 GB   │  .zip    2.4%    5.37 GB     173  │
├───────────────────────────────────┴───────────────────────────────────┤
│ Heatmap                                          Games › Endfield     │
│  ████████████████ ████████ ███ ██ █ █ █ █ █ █ █ █ █ █ █ █ █ █ █ █ █  │
├───────────────────────────────────────────────────────────────────────┤
│ NAME  Endfield │ FULL PATH  G:\Games\Endfield │ SIZE 133.53 GB │ ...  │
└───────────────────────────────────────────────────────────────────────┘
```

**Folders** — one row per folder, biggest first. Click the chevron to expand,
click the row to select it. The bar behind the percentage is that folder's share
of its parent, so the shape of the disk is readable without reading numbers.

**Extensions** — every extension found, with a stacked strip showing how the
whole scan divides up. The bar behind each share figure is a heat bar scaled
against the largest extension. **Click a row to isolate that extension in the
heatmap**; everything else dims. Click it again, or press Esc, to clear.

**Heatmap** — squarified treemap of the selection. Area is size, colour is the
dominant extension of the block. Click a folder block to drill into it, use the
breadcrumbs to walk back out. Hover anything for a tooltip with the full path,
size, share of the scan and file type.

**Details bar** — name, full path, size, share of parent, item count and type for
whatever is selected, whether that came from the tree, the heatmap or a
breadcrumb.

The two gutters between the panels are draggable if you want more room for one
of them.

| Key | Action |
|---|---|
| `F5` | Rescan |
| `Esc` | Clear the extension isolation |
| `Ctrl+O` | Jump to the path box |
| `↑ ↓ ← →` | Move around the folder tree |
| `Space` / `Enter` | Expand or collapse the selected folder |

## The console

```
  Drive G:  ===============-------  209.58 GB used of 315.71 GB   (66.4% full, 106.13 GB free)
  Scanning G:\

  TOP FOLDERS  G:\
  738,230 files in 8.0s   (8 threads)
  --------------------------------------------------------------------------------
  SIZE        SHARE   BAR                     NAME
   112.11 GB   54.3%  ======================  Games
    28.90 GB   14.0%  ======----------------  Kuliah
    14.79 GB    7.2%  ===-------------------  VID
  --------------------------------------------------------------------------------
   206.37 GB  100.0%                          TOTAL
  31 more folders not shown (14.25 GB), raise -Top to see them
```

```powershell
diskusage.exe                          # current folder
diskusage.exe C:\Users\Pans            # a specific folder
diskusage.exe G:\ -Top 8               # top 8 folders on the drive
diskusage.exe G:\ -Top 0               # every child folder
diskusage.exe G:\ -MinMB 500           # hide anything under 500 MB
diskusage.exe G:\ -Files 20            # 20 largest files
diskusage.exe G:\ -Threads 1           # single thread, for spinning disks
diskusage.exe G:\ -Export report.html  # HTML report
diskusage.exe G:\ -ExportJson d.json   # raw scan data, for the interactive UI
```

Double-clicking `diskusage.exe` scans the root of the drive it sits on and then
waits for a keypress, so the window stays open.

## Options

| Option | Default | Meaning |
|---|---|---|
| `path` | `.` | Folder to analyze |
| `-Top N` | `15` | Child folders to show; `0` = all |
| `-Files N` | `10` | Largest files to show; `0` = none |
| `-MinMB N` | `0` | Hide folders below this size |
| `-Threads N` | cores, max 8 | Parallel scan workers |
| `-Export f` | — | Write a standalone HTML report |
| `-ExportJson f` | — | Write scan data as JSON |
| `-NoColor` | off | Plain output, no progress line |
| `-h` | — | Show help |

## How it works

- Sizes are **recursive totals** for each immediate child of the target folder.
- **Junctions and symlinks are skipped**, so `AppData\Local Settings` and similar
  reparse points don't double-count. The count of skipped entries is shown.
- **Top-level folders are scanned in parallel.** `-Threads 1` forces sequential
  scanning, which is often faster on a spinning disk where parallel seeks thrash.
- Uses the `\\?\` long-path prefix, so deep `node_modules` trees work.
- Unreadable paths are counted and reported instead of aborting the scan.
- The window builds a tree to a bounded depth and folds anything deeper into its
  nearest ancestor, so memory stays flat on huge drives while totals stay exact.
- Extension colours come from a hash of the extension name, so `.mp4` is always
  the same colour from one scan to the next.

## Files

| File | Purpose |
|---|---|
| `diskusage-gui.exe` | The windowed app |
| `diskusage-gui.exe.config` | Enables paths over 260 characters. Keep it next to the exe. |
| `diskusage.exe` | The console app |
| `diskusage.exe.config` | Same, for the console app |
| `Scanner.cs` | Scan engine used by the windowed app |
| `GuiTheme.cs` | Palette, fonts, treemap layout |
| `DiskUsageGui.cs` | Window, panels and controls |
| `DiskUsage.cs` | Console app source |
| `app.manifest` | OS support declarations; enables the dark title bar |
| `build.cmd` | Builds both executables |

## Build

```powershell
build.cmd
```

Or by hand:

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

& $csc /nologo /optimize+ /target:exe /platform:anycpu /win32manifest:app.manifest `
    /out:diskusage.exe DiskUsage.cs

& $csc /nologo /optimize+ /target:winexe /platform:anycpu /win32manifest:app.manifest `
    /out:diskusage-gui.exe Scanner.cs GuiTheme.cs DiskUsageGui.cs
```

`DiskUsageGui.cs` uses `--shot <png> [folder]` to render the window to an image
without interaction, which is how the layout gets checked from a terminal.

## Performance

Measured on a 315 GB drive with 738,230 files:

| Version | Time |
|---|---|
| Sequential (v1.0) | 17.6s |
| Parallel, 8 threads (v1.1) | 8.0s |

## Notes

- The exes are unsigned. Windows SmartScreen may warn the first time they run on
  a different machine. They are safe to allow on your own system.
- The window uses **Poppins** (Medium for body text, SemiBold for emphasis). If
  Poppins is not installed it falls back to Segoe UI, then Tahoma, so the layout
  still holds on a machine without it. The full path in the details bar and the
  hover tooltip use Cascadia Mono, falling back to Consolas, because a
  proportional face makes long paths hard to scan.
- The console app still carries its own copy of the scan loop from before the
  window existed. Both produce the same numbers; unifying them is outstanding.
- Without `-Export` / `-ExportJson` the console writes nothing to disk. The
  window writes nothing to disk at all.
