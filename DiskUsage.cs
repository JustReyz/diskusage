using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: System.Reflection.AssemblyTitle("DiskUsage")]
[assembly: System.Reflection.AssemblyDescription("Fast disk usage analyzer")]
[assembly: System.Reflection.AssemblyProduct("DiskUsage")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

internal sealed class FileRec
{
    public string Path;
    public long Size;
}

internal sealed class Entry
{
    public string Name;
    public long Size;
    public bool IsLink;
}

internal sealed class ScanResult
{
    public long Size;
    public long Errors;
    public readonly List<FileRec> Big = new List<FileRec>();
}

internal static class Program
{
    private const long BigThreshold = 25L * 1024 * 1024;
    private const int BarWidth = 22;
    private const int NameWidth = 34;
    private const int TableWidth = 80;

    private static long _fileCount;
    private static long _errors;
    private static long _dirsDone;
    private static long _dirsTotal;
    private static bool _noColor;
    private static bool _showProgress;
    private static string _progressLabel = "";
    private static DateTime _lastDraw = DateTime.MinValue;
    private static readonly object DrawLock = new object();
    private static readonly List<FileRec> BigFiles = new List<FileRec>();

    private static int Main(string[] args)
    {
        string path = ".";
        int top = 15;
        int files = 10;
        double minMB = 0;
        int threads = Math.Min(Environment.ProcessorCount, 8);
        string export = null;
        bool standalone = IsStandaloneLaunch();

        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-h" || a == "--help" || a == "/?")
                {
                    PrintHelp();
                    PauseIfStandalone(standalone);
                    return 0;
                }
                else if (a == "-top") { top = int.Parse(args[++i], CultureInfo.InvariantCulture); }
                else if (a == "-files") { files = int.Parse(args[++i], CultureInfo.InvariantCulture); }
                else if (a == "-minmb") { minMB = double.Parse(args[++i], CultureInfo.InvariantCulture); }
                else if (a == "-threads") { threads = int.Parse(args[++i], CultureInfo.InvariantCulture); }
                else if (a == "-export") { export = args[++i]; }
                else if (a == "-nocolor") { _noColor = true; }
                else if (a.StartsWith("-"))
                {
                    Console.Error.WriteLine("Unknown option: " + args[i]);
                    PauseIfStandalone(standalone);
                    return 1;
                }
                else { path = args[i]; }
            }
        }
        catch (IndexOutOfRangeException)
        {
            Console.Error.WriteLine("Missing value after an option.");
            PauseIfStandalone(standalone);
            return 1;
        }
        catch (FormatException)
        {
            Console.Error.WriteLine("Options -Top, -Files, -MinMB and -Threads expect a number.");
            PauseIfStandalone(standalone);
            return 1;
        }

        if (threads < 1) threads = 1;

        // Double-clicked from Explorer: analyze the drive root, not the exe's own folder.
        if (standalone && path == ".")
        {
            string rootOfExe = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
            if (!string.IsNullOrEmpty(rootOfExe)) path = rootOfExe;
        }

        string resolved;
        try { resolved = Path.GetFullPath(path); }
        catch
        {
            Console.Error.WriteLine("Cannot find path: " + path);
            PauseIfStandalone(standalone);
            return 1;
        }

        if (!Directory.Exists(resolved))
        {
            Console.Error.WriteLine("Not a folder: " + path);
            PauseIfStandalone(standalone);
            return 1;
        }

        _noColor = _noColor || Console.IsOutputRedirected;
        _showProgress = !_noColor;

        Console.WriteLine();

        // Drive summary, when the target lives on a local volume.
        string rootPath = Path.GetPathRoot(resolved);
        if (!string.IsNullOrEmpty(rootPath))
        {
            try
            {
                DriveInfo drive = new DriveInfo(rootPath);
                if (drive.IsReady)
                {
                    long total = drive.TotalSize;
                    long free = drive.AvailableFreeSpace;
                    long used = total - free;
                    double pct = total > 0 ? (double)used / total * 100.0 : 0;

                    WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  Drive {0}  {1}  {2} used of {3}   ({4:N1}% full, {5} free)",
                        drive.Name.TrimEnd('\\'), Bar((double)used / total),
                        Fmt(used).Trim(), Fmt(total).Trim(), pct, Fmt(free).Trim()),
                        DriveColor(pct));
                }
            }
            catch { }
        }

        _progressLabel = DisplayPath(resolved);
        WriteLine("  Scanning " + _progressLabel, ConsoleColor.DarkGray);

        Stopwatch watch = Stopwatch.StartNew();

        DirectoryInfo root = new DirectoryInfo(ToLongPath(resolved));
        long rootFileSize = 0;

        try
        {
            foreach (FileInfo fi in root.EnumerateFiles())
            {
                try
                {
                    rootFileSize += fi.Length;
                    Interlocked.Increment(ref _fileCount);
                    if (fi.Length >= BigThreshold)
                        BigFiles.Add(new FileRec { Path = fi.FullName, Size = fi.Length });
                }
                catch { _errors++; }
            }
        }
        catch { _errors++; }

        List<DirectoryInfo> subs = new List<DirectoryInfo>();
        try
        {
            foreach (DirectoryInfo sd in root.EnumerateDirectories()) subs.Add(sd);
        }
        catch { _errors++; }

        bool[] isLink = new bool[subs.Count];
        ScanResult[] results = new ScanResult[subs.Count];
        _dirsTotal = subs.Count;

        ParallelOptions options = new ParallelOptions();
        options.MaxDegreeOfParallelism = threads;

        Parallel.For(0, subs.Count, options, i =>
        {
            DirectoryInfo sd = subs[i];
            bool link = false;

            try { link = (sd.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { }

            isLink[i] = link;
            results[i] = link ? new ScanResult() : MeasureTree(sd.FullName);

            Interlocked.Increment(ref _dirsDone);
            MaybeDrawProgress();
        });

        if (_showProgress) ClearProgress();

        watch.Stop();

        // Merge per-folder results on the main thread.
        List<Entry> entries = new List<Entry>();
        long totalSize = rootFileSize;

        for (int i = 0; i < subs.Count; i++)
        {
            ScanResult r = results[i] != null ? results[i] : new ScanResult();
            entries.Add(new Entry { Name = subs[i].Name, Size = r.Size, IsLink = isLink[i] });
            _errors += r.Errors;
            BigFiles.AddRange(r.Big);
            if (!isLink[i]) totalSize += r.Size;
        }

        long minBytes = (long)(minMB * 1024.0 * 1024.0);
        List<Entry> ranked = new List<Entry>();
        for (int i = 0; i < entries.Count; i++)
            if (!entries[i].IsLink && entries[i].Size >= minBytes) ranked.Add(entries[i]);

        ranked.Sort(delegate(Entry a, Entry b) { return b.Size.CompareTo(a.Size); });

        List<Entry> shown = ranked;
        if (top > 0 && ranked.Count > top) shown = ranked.GetRange(0, top);

        long hiddenSize = 0;
        for (int i = shown.Count; i < ranked.Count; i++) hiddenSize += ranked[i].Size;

        // Folder table
        Console.WriteLine();
        WriteLine("  TOP FOLDERS  " + DisplayPath(resolved), ConsoleColor.Cyan);
        WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0:N0} files in {1:N1}s   ({2} threads)",
            _fileCount, watch.Elapsed.TotalSeconds, threads), ConsoleColor.DarkGray);
        WriteLine("  " + new string('-', TableWidth), ConsoleColor.DarkGray);
        WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-10}  {1,-6}  {2,-22}  {3}",
            "SIZE", "SHARE", "BAR", "NAME"), ConsoleColor.DarkGray);

        long maxSize = 0;
        for (int i = 0; i < shown.Count; i++)
            if (shown[i].Size > maxSize) maxSize = shown[i].Size;

        for (int i = 0; i < shown.Count; i++)
        {
            Entry e = shown[i];
            double pct = totalSize > 0 ? (double)e.Size / totalSize * 100.0 : 0;
            double ratio = maxSize > 0 ? (double)e.Size / maxSize : 0;

            WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0}  {1,5:N1}%  {2}  {3}",
                Fmt(e.Size), pct, Bar(ratio), TruncateRight(e.Name, NameWidth)), SizeColor(pct));
        }

        WriteLine("  " + new string('-', TableWidth), ConsoleColor.DarkGray);
        WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0}  {1,5:N1}%  {2}  {3}",
            Fmt(totalSize), 100.0, new string(' ', BarWidth), "TOTAL"), ConsoleColor.White);

        if (hiddenSize > 0)
        {
            WriteLine(string.Format(CultureInfo.InvariantCulture,
                "  {0} more folders not shown ({1}), raise -Top to see them",
                ranked.Count - shown.Count, Fmt(hiddenSize).Trim()), ConsoleColor.DarkGray);
        }

        int links = 0;
        for (int i = 0; i < entries.Count; i++) if (entries[i].IsLink) links++;
        if (links > 0)
            WriteLine(string.Format("  {0} junction/symlink entries skipped", links), ConsoleColor.DarkGray);

        // Largest files
        if (files > 0 && BigFiles.Count > 0)
        {
            BigFiles.Sort(delegate(FileRec a, FileRec b) { return b.Size.CompareTo(a.Size); });
            int take = Math.Min(files, BigFiles.Count);

            Console.WriteLine();
            WriteLine("  LARGEST FILES  (over " + Fmt(BigThreshold).Trim() + ")", ConsoleColor.Cyan);
            WriteLine("  " + new string('-', TableWidth), ConsoleColor.DarkGray);
            WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-10}  {1}", "SIZE", "FILE"), ConsoleColor.DarkGray);

            for (int i = 0; i < take; i++)
            {
                WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0}  {1}",
                    Fmt(BigFiles[i].Size), CompactPath(DisplayPath(BigFiles[i].Path), TableWidth - 14)), ConsoleColor.Gray);
            }
        }

        // Footer
        Console.WriteLine();
        string skipped = _errors > 0
            ? string.Format(CultureInfo.InvariantCulture, "   {0:N0} paths skipped (access denied)", _errors)
            : "";
        WriteLine(string.Format(CultureInfo.InvariantCulture, "  Total: {0}   in {1:N1}s{2}",
            Fmt(totalSize), watch.Elapsed.TotalSeconds, skipped), ConsoleColor.DarkGray);
        Console.WriteLine();

        if (!string.IsNullOrEmpty(export))
        {
            try
            {
                string full = Path.GetFullPath(export);
                File.WriteAllText(full, BuildHtml(DisplayPath(resolved), shown, totalSize, maxSize, files), Encoding.UTF8);
                WriteLine("  Report written to " + full, ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not write report: " + ex.Message);
                PauseIfStandalone(standalone);
                return 1;
            }
        }

        PauseIfStandalone(standalone);
        return 0;
    }

    private static ScanResult MeasureTree(string directory)
    {
        ScanResult result = new ScanResult();
        Stack<string> stack = new Stack<string>();
        stack.Push(directory);
        long sinceDraw = 0;

        while (stack.Count > 0)
        {
            string current = stack.Pop();
            DirectoryInfo di;

            try { di = new DirectoryInfo(current); }
            catch { result.Errors++; continue; }

            try
            {
                foreach (FileInfo fi in di.EnumerateFiles())
                {
                    try
                    {
                        long len = fi.Length;
                        result.Size += len;
                        Interlocked.Increment(ref _fileCount);

                        if (len >= BigThreshold)
                            result.Big.Add(new FileRec { Path = fi.FullName, Size = len });

                        if (++sinceDraw >= 1000)
                        {
                            sinceDraw = 0;
                            MaybeDrawProgress();
                        }
                    }
                    catch { result.Errors++; }
                }
            }
            catch { result.Errors++; }

            try
            {
                foreach (DirectoryInfo sd in di.EnumerateDirectories())
                {
                    try
                    {
                        if ((sd.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push(sd.FullName);
                    }
                    catch { result.Errors++; }
                }
            }
            catch { result.Errors++; }
        }

        return result;
    }

    private static string Fmt(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;
        const double TB = GB * 1024.0;
        double b = bytes;

        if (b >= TB) return string.Format(CultureInfo.InvariantCulture, "{0,7:N2} TB", b / TB);
        if (b >= GB) return string.Format(CultureInfo.InvariantCulture, "{0,7:N2} GB", b / GB);
        if (b >= MB) return string.Format(CultureInfo.InvariantCulture, "{0,7:N1} MB", b / MB);
        if (b >= KB) return string.Format(CultureInfo.InvariantCulture, "{0,7:N0} KB", b / KB);
        return string.Format(CultureInfo.InvariantCulture, "{0,7:N0}  B", b);
    }

    private static string Bar(double ratio)
    {
        int filled = (int)Math.Round(ratio * BarWidth);
        if (filled < 0) filled = 0;
        if (filled > BarWidth) filled = BarWidth;
        return new string('=', filled) + new string('-', BarWidth - filled);
    }

    private static ConsoleColor SizeColor(double percent)
    {
        if (percent >= 25) return ConsoleColor.Red;
        if (percent >= 10) return ConsoleColor.Yellow;
        return ConsoleColor.Green;
    }

    private static ConsoleColor DriveColor(double percent)
    {
        if (percent >= 90) return ConsoleColor.Red;
        if (percent >= 75) return ConsoleColor.Yellow;
        return ConsoleColor.Green;
    }

    private static string TruncateRight(string s, int max)
    {
        if (s == null) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max - 3) + "...";
    }

    // Keeps the file name readable by shortening the parent folder instead of the tail.
    private static string CompactPath(string full, int max)
    {
        if (string.IsNullOrEmpty(full)) return "";
        if (full.Length <= max) return full;

        string file = Path.GetFileName(full);
        string dir = Path.GetDirectoryName(full);
        string parent = string.IsNullOrEmpty(dir) ? "" : Path.GetFileName(dir);

        if (!string.IsNullOrEmpty(parent))
        {
            string candidate = parent + "\\" + file;
            if (candidate.Length <= max - 3) return "..." + candidate;
        }

        if (file.Length <= max - 3) return "..." + file;
        return "..." + file.Substring(file.Length - (max - 3));
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        if (_noColor)
        {
            Console.WriteLine(text);
        }
        else
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    private static void MaybeDrawProgress()
    {
        if (!_showProgress) return;

        lock (DrawLock)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastDraw).TotalMilliseconds < 120) return;
            _lastDraw = now;
            DrawProgress();
        }
    }

    private static void DrawProgress()
    {
        string line = string.Format(CultureInfo.InvariantCulture,
            "\r  scanning {0,-26} {1,10:N0} files   {2}/{3} folders ",
            TruncateRight(_progressLabel, 26), _fileCount, _dirsDone, _dirsTotal);
        Console.Write(line);
    }

    private static void ClearProgress()
    {
        Console.Write("\r" + new string(' ', 82) + "\r");
    }

    private static string ToLongPath(string p)
    {
        if (p.StartsWith(@"\\?\")) return p;
        if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
        return @"\\?\" + p;
    }

    private static string DisplayPath(string p)
    {
        if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
        if (p.StartsWith(@"\\?\")) return p.Substring(4);
        return p;
    }

    private static string BuildHtml(string rootLabel, List<Entry> shown, long totalSize, long maxSize, int files)
    {
        StringBuilder sb = new StringBuilder();
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        sb.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>Disk usage - " + System.Net.WebUtility.HtmlEncode(rootLabel) + "</title>");
        sb.AppendLine(@"<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin:0; padding:32px; background:#0f1115; color:#e6e8ee;
         font:14px/1.5 'Segoe UI',system-ui,sans-serif; }
  h1 { font-size:20px; margin:0 0 4px; font-weight:600; }
  .meta { color:#8b93a7; font-size:13px; margin-bottom:24px; }
  .cards { display:flex; gap:12px; flex-wrap:wrap; margin-bottom:28px; }
  .card { background:#171a21; border:1px solid #232833; border-radius:10px; padding:14px 18px; min-width:150px; }
  .card .k { color:#8b93a7; font-size:11px; text-transform:uppercase; letter-spacing:.06em; }
  .card .v { font-size:20px; font-weight:600; margin-top:4px; font-variant-numeric:tabular-nums; }
  table { width:100%; border-collapse:collapse; margin-bottom:32px; }
  th { text-align:left; color:#8b93a7; font-size:11px; text-transform:uppercase;
       letter-spacing:.06em; padding:0 10px 8px; border-bottom:1px solid #232833; }
  td { padding:7px 10px; border-bottom:1px solid #1b1f28; vertical-align:middle; }
  td.num { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; }
  .barwrap { width:100%; min-width:120px; }
  .bar { height:9px; border-radius:5px; background:#232833; overflow:hidden; }
  .bar i { display:block; height:100%; border-radius:5px; }
  .p-red { background:#e5484d; } .p-yel { background:#f5a524; } .p-grn { background:#30a46c; }
  .path { font-family:ui-monospace,Consolas,monospace; font-size:12px; color:#b9c0d4; }
</style>");
        sb.AppendLine("</head><body>");
        sb.AppendLine("<h1>Disk usage</h1>");
        sb.AppendLine("<div class='meta'>" + System.Net.WebUtility.HtmlEncode(rootLabel) + " &middot; generated " + stamp + "</div>");
        sb.AppendLine("<div class='cards'>");
        sb.AppendLine("<div class='card'><div class='k'>Scanned</div><div class='v'>" + Fmt(totalSize).Trim() + "</div></div>");
        sb.AppendLine("<div class='card'><div class='k'>Files</div><div class='v'>" + _fileCount.ToString("N0", CultureInfo.InvariantCulture) + "</div></div>");
        sb.AppendLine("<div class='card'><div class='k'>Skipped</div><div class='v'>" + _errors.ToString("N0", CultureInfo.InvariantCulture) + "</div></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<table><thead><tr><th>Folder</th><th class='num'>Size</th><th class='num'>Share</th><th></th></tr></thead><tbody>");

        for (int i = 0; i < shown.Count; i++)
        {
            Entry e = shown[i];
            double pct = totalSize > 0 ? (double)e.Size / totalSize * 100.0 : 0;
            double ratio = maxSize > 0 ? (double)e.Size / maxSize : 0;
            string cls = pct >= 25 ? "p-red" : (pct >= 10 ? "p-yel" : "p-grn");
            int width = (int)Math.Round(ratio * 100);

            sb.AppendLine("<tr><td>" + System.Net.WebUtility.HtmlEncode(e.Name) + "</td><td class='num'>"
                + Fmt(e.Size).Trim() + "</td><td class='num'>"
                + pct.ToString("N1", CultureInfo.InvariantCulture) + "%</td><td class='barwrap'><div class='bar'><i class='"
                + cls + "' style='width:" + width + "%'></i></div></td></tr>");
        }

        sb.AppendLine("</tbody></table>");

        if (files > 0 && BigFiles.Count > 0)
        {
            int take = Math.Min(files, BigFiles.Count);
            sb.AppendLine("<table><thead><tr><th>Largest file</th><th class='num'>Size</th></tr></thead><tbody>");
            for (int i = 0; i < take; i++)
            {
                sb.AppendLine("<tr><td class='path'>" + System.Net.WebUtility.HtmlEncode(DisplayPath(BigFiles[i].Path))
                    + "</td><td class='num'>" + Fmt(BigFiles[i].Size).Trim() + "</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    // True when the exe owns a fresh console, i.e. it was double-clicked rather
    // than started from an existing shell. Redirected output is never standalone.
    private static bool IsStandaloneLaunch()
    {
        try
        {
            if (Console.IsOutputRedirected || Console.IsInputRedirected) return false;
            uint[] list = new uint[8];
            return GetConsoleProcessList(list, (uint)list.Length) <= 1;
        }
        catch { return false; }
    }

    private static void PauseIfStandalone(bool standalone)
    {
        if (!standalone) return;
        Console.WriteLine();
        Console.WriteLine("  Press any key to close...");
        try { Console.ReadKey(true); } catch { }
    }

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  DiskUsage - fast disk usage analyzer");
        Console.WriteLine();
        Console.WriteLine("  diskusage.exe [path] [-Top N] [-Files N] [-MinMB N] [-Threads N] [-Export file] [-NoColor]");
        Console.WriteLine();
        Console.WriteLine("    path         folder to analyze (default: current folder)");
        Console.WriteLine("    -Top N       child folders to show, 0 = all (default 15)");
        Console.WriteLine("    -Files N     largest files to show, 0 = none (default 10)");
        Console.WriteLine("    -MinMB N     hide folders smaller than N megabytes");
        Console.WriteLine("    -Threads N   parallel scan workers (default: cores, max 8)");
        Console.WriteLine("    -Export f    write an HTML report");
        Console.WriteLine("    -NoColor     plain output, no progress line");
        Console.WriteLine();
        Console.WriteLine("  Examples:");
        Console.WriteLine("    diskusage.exe C:\\Users\\Pans");
        Console.WriteLine("    diskusage.exe G:\\ -Top 8");
        Console.WriteLine("    diskusage.exe G:\\ -Threads 1          (slower, for spinning disks)");
        Console.WriteLine("    diskusage.exe C:\\Users\\Pans -MinMB 500 -Export report.html");
        Console.WriteLine();
    }
}
