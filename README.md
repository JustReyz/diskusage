# DiskUsage

Fast disk usage analyzer for Windows. Single executable, no installation, no runtime download.
Requires .NET Framework 4.8 (included in Windows 10 and 11).

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

## Usage

```powershell
diskusage.exe                          # current folder
diskusage.exe C:\Users\Pans            # a specific folder
diskusage.exe G:\ -Top 8               # top 8 folders on the drive
diskusage.exe G:\ -Top 0               # every child folder
diskusage.exe G:\ -MinMB 500           # hide anything under 500 MB
diskusage.exe G:\ -Files 20            # 20 largest files
diskusage.exe G:\ -Threads 1           # single thread, for spinning disks
diskusage.exe G:\ -Export report.html  # HTML report
```

### Double-clicking

Double-clicking `diskusage.exe` scans the root of the drive it sits on and then
waits for a keypress, so the window stays open. Results are printed to that
console window and are not saved anywhere.

To get a file you can open later, run it from a terminal with `-Export`:

```powershell
diskusage.exe G:\ -Export C:\Users\Pans\tools\DiskUsage\report.html
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `path` | `.` | Folder to analyze |
| `-Top N` | `15` | Child folders to show; `0` = all |
| `-Files N` | `10` | Largest files to show; `0` = none |
| `-MinMB N` | `0` | Hide folders below this size |
| `-Threads N` | cores, max 8 | Parallel scan workers |
| `-Export f` | — | Write a standalone HTML report |
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
- Files at the root of the target count toward the total and appear in the
  largest-files list, but not in the folder table.
- Long paths in the file list are shortened by dropping the parent folder, never
  the file name.

## Files

| File | Purpose |
|---|---|
| `diskusage.exe` | The program |
| `diskusage.exe.config` | Enables paths over 260 characters. Keep it next to the exe. |
| `DiskUsage.cs` | Source code |

## Build

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ `
  /target:exe /out:diskusage.exe DiskUsage.cs
```

## Performance

Measured on a 315 GB drive with 738,230 files:

| Version | Time |
|---|---|
| Sequential (v1.0) | 17.6s |
| Parallel, 8 threads (v1.1) | 8.0s |

## Notes

- The exe is unsigned. Windows SmartScreen may warn the first time it runs on a
  different machine. It is safe to allow on your own system.
- Without `-Export` the program writes nothing to disk. It only reads file sizes.
