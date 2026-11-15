using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

// ---------------------------------------------------------------------------
// Scan engine shared by the console tool and the GUI.
// ---------------------------------------------------------------------------

internal sealed class FileRec
{
    public string Path;
    public long Size;
}

// A folder, or a file large enough to be worth its own node. Size and Items are
// recursive totals filled in by FinalizeTree. Folded counts items that belong to
// folders deeper than TreeDepth, or beyond MaxChildren, which were not given a
// node of their own; their bytes are still counted here so totals stay exact.
internal sealed class Node
{
    public string Name;
    public string Path;
    public long Size;
    public long Files;
    public long Folded;
    public long Items;
    public bool IsFile;
    public bool IsLink;

    // Largest single extension group in this subtree, used to colour blocks.
    public string TopExt;
    public long TopSize;

    // GUI state, never serialized.
    public Node Parent;
    public bool Expanded;

    public readonly List<Node> Children = new List<Node>();

    // Transient: extension -> bytes for files sitting directly in this folder.
    // Dropped by FinalizeTree once TopExt has been derived.
    internal Dictionary<string, long> DirectExt;
}

internal sealed class ExtAgg
{
    public long Size;
    public long Count;
}

internal sealed class ExtStat
{
    public string Ext;
    public long Size;
    public long Count;
}

internal sealed class ScanOptions
{
    public string Path;
    public int Threads = 8;

    // Depth and child caps keep the in-memory tree small enough to render.
    public int TreeDepth = 14;
    public int MaxChildren = 600;
    public long TreeFileThreshold = 1024L * 1024L;
    public long BigFileThreshold = 25L * 1024L * 1024L;

    // Folders the user excluded from the scan, as display paths. Matching is
    // case-insensitive and whole-subtree: a skipped folder is never descended
    // into, so nothing under it is counted or even opened.
    public HashSet<string> Skip;

    public bool IsSkipped(string fullPath)
    {
        if (Skip == null || Skip.Count == 0) return false;
        return Skip.Contains(PathUtil.DisplayPath(fullPath).TrimEnd('\\'));
    }
}

internal sealed class ScanProgress
{
    public long Files;
    public long Folders;
    public string Current;
}

internal sealed class ScanResultData
{
    public string Path;
    public Node Root;
    public long TotalSize;
    public long FileCount;
    public long Errors;
    public long SkippedFolders;
    public double Seconds;
    public int Threads;
    public bool Cancelled;
    public readonly List<ExtStat> Extensions = new List<ExtStat>();
    public readonly List<FileRec> BigFiles = new List<FileRec>();
}

internal static class PathUtil
{
    public static string ToLongPath(string p)
    {
        if (p.StartsWith(@"\\?\")) return p;
        if (p.StartsWith(@"\\")) return @"\\?\UNC\" + p.Substring(2);
        return @"\\?\" + p;
    }

    public static string DisplayPath(string p)
    {
        if (p.StartsWith(@"\\?\UNC\")) return @"\\" + p.Substring(8);
        if (p.StartsWith(@"\\?\")) return p.Substring(4);
        return p;
    }

    // Matches Path.GetExtension semantics, lower-cased for grouping.
    public static string ExtOf(string fileName)
    {
        string e = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(e)) return "(none)";
        return e.ToLowerInvariant();
    }
}

internal static class Scanner
{
    // Shared mutable scan state. Counters are touched from every worker thread.
    private sealed class State
    {
        public ScanOptions Options;
        public readonly ConcurrentDictionary<string, ExtAgg> Ext =
            new ConcurrentDictionary<string, ExtAgg>(StringComparer.OrdinalIgnoreCase);
        public readonly List<FileRec> Big = new List<FileRec>();
        public long Files;
        public long Errors;
        public long Folders;
        public long Skipped;
        public volatile bool Cancelled;
        public string Current = "";
        public Action<ScanProgress> OnProgress;
        private long _lastTick;
        public readonly Stopwatch Clock = Stopwatch.StartNew();

        public void Report()
        {
            if (OnProgress == null) return;

            long now = Stopwatch.GetTimestamp();
            if (now - _lastTick < Stopwatch.Frequency / 10) return;   // 10 fps
            _lastTick = now;

            ScanProgress p = new ScanProgress();
            p.Files = Interlocked.Read(ref Files);
            p.Folders = Interlocked.Read(ref Folders);
            p.Current = Current;
            OnProgress(p);
        }
    }

    public static ScanResultData Run(ScanOptions o, Action<ScanProgress> onProgress, Func<bool> isCancelled)
    {
        ScanResultData data = new ScanResultData();
        data.Path = o.Path;
        data.Threads = Math.Max(1, o.Threads);

        State s = new State();
        s.Options = o;
        s.OnProgress = onProgress;

        DirectoryInfo rootDir;
        string longPath = PathUtil.ToLongPath(o.Path);
        try { rootDir = new DirectoryInfo(longPath); }
        catch (Exception ex)
        {
            throw new IOException("Cannot open [" + longPath + "] (from [" + o.Path + "])", ex);
        }

        Node root = new Node();
        root.Path = PathUtil.DisplayPath(o.Path);
        string rootName = Path.GetFileName(root.Path.TrimEnd('\\'));
        root.Name = string.IsNullOrEmpty(rootName) ? root.Path : rootName;
        root.Expanded = true;

        try
        {
            foreach (FileInfo fi in rootDir.EnumerateFiles())
            {
                try
                {
                    long len = fi.Length;
                    root.Size += len;
                    root.Files++;
                    Interlocked.Increment(ref s.Files);
                    AddExt(root, s, fi.Name, len);

                    if (len >= o.BigFileThreshold)
                        lock (s.Big) s.Big.Add(new FileRec { Path = PathUtil.DisplayPath(fi.FullName), Size = len });

                    if (len >= o.TreeFileThreshold)
                        root.Children.Add(new Node
                        {
                            Name = fi.Name,
                            Path = PathUtil.DisplayPath(fi.FullName),
                            Size = len,
                            IsFile = true,
                            Parent = root
                        });
                }
                catch { Interlocked.Increment(ref s.Errors); }
            }
        }
        catch { Interlocked.Increment(ref s.Errors); }

        List<DirectoryInfo> subs = new List<DirectoryInfo>();
        try
        {
            foreach (DirectoryInfo sd in rootDir.EnumerateDirectories())
            {
                if (o.IsSkipped(sd.FullName)) { Interlocked.Increment(ref s.Skipped); continue; }
                subs.Add(sd);
            }
        }
        catch { Interlocked.Increment(ref s.Errors); }

        Interlocked.Add(ref s.Folders, subs.Count);

        Node[] nodes = new Node[subs.Count];
        bool[] links = new bool[subs.Count];

        ParallelOptions po = new ParallelOptions();
        po.MaxDegreeOfParallelism = data.Threads;

        Parallel.For(0, subs.Count, po, delegate(int i)
        {
            if (s.Cancelled) return;
            if (isCancelled != null && isCancelled()) { s.Cancelled = true; return; }

            DirectoryInfo sd = subs[i];
            bool link = false;
            try { link = (sd.Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { }

            links[i] = link;

            Node child = new Node();
            child.Name = sd.Name;
            child.Path = PathUtil.DisplayPath(sd.FullName);
            child.Parent = root;
            child.IsLink = link;
            nodes[i] = child;

            if (link) return;

            s.Current = child.Path;
            WalkTree(sd, child, 1, false, s);
            s.Report();
        });

        if (s.Cancelled)
        {
            data.Cancelled = true;
            return data;
        }

        for (int i = 0; i < subs.Count; i++)
            if (nodes[i] != null) root.Children.Add(nodes[i]);

        FinalizeTree(root);
        SortTree(root);

        data.Root = root;
        data.TotalSize = root.Size;
        data.FileCount = Interlocked.Read(ref s.Files);
        data.Errors = Interlocked.Read(ref s.Errors);
        data.SkippedFolders = Interlocked.Read(ref s.Skipped);
        data.Seconds = s.Clock.Elapsed.TotalSeconds;

        List<KeyValuePair<string, ExtAgg>> exts = new List<KeyValuePair<string, ExtAgg>>(s.Ext);
        exts.Sort(delegate(KeyValuePair<string, ExtAgg> a, KeyValuePair<string, ExtAgg> b)
        {
            return b.Value.Size.CompareTo(a.Value.Size);
        });
        for (int i = 0; i < exts.Count; i++)
            data.Extensions.Add(new ExtStat { Ext = exts[i].Key, Size = exts[i].Value.Size, Count = exts[i].Value.Count });

        s.Big.Sort(delegate(FileRec a, FileRec b) { return b.Size.CompareTo(a.Size); });
        data.BigFiles.AddRange(s.Big);

        return data;
    }

    // Depth-first walk. Past TreeDepth, or once a node is full, folders keep
    // walking but credit their bytes to the nearest ancestor instead of
    // creating nodes, which bounds memory without losing any size information.
    private static void WalkTree(DirectoryInfo dir, Node owner, int depth, bool folded, State s)
    {
        if (s.Cancelled) return;

        try
        {
            foreach (FileInfo fi in dir.EnumerateFiles())
            {
                if (s.Cancelled) return;
                try
                {
                    long len = fi.Length;
                    owner.Size += len;
                    if (folded) owner.Folded++; else owner.Files++;
                    Interlocked.Increment(ref s.Files);
                    AddExt(owner, s, fi.Name, len);

                    if (len >= s.Options.BigFileThreshold)
                        lock (s.Big) s.Big.Add(new FileRec { Path = PathUtil.DisplayPath(fi.FullName), Size = len });

                    if (!folded && len >= s.Options.TreeFileThreshold && owner.Children.Count < s.Options.MaxChildren)
                        owner.Children.Add(new Node
                        {
                            Name = fi.Name,
                            Path = PathUtil.DisplayPath(fi.FullName),
                            Size = len,
                            IsFile = true,
                            Parent = owner
                        });
                }
                catch { Interlocked.Increment(ref s.Errors); }
            }
        }
        catch { Interlocked.Increment(ref s.Errors); }

        try
        {
            foreach (DirectoryInfo sd in dir.EnumerateDirectories())
            {
                if (s.Cancelled) return;
                try
                {
                    if ((sd.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    // Excluded by the user: do not open it at all. Counting it as
                    // skipped rather than as a folder keeps the folder total honest.
                    if (s.Options.IsSkipped(sd.FullName))
                    {
                        Interlocked.Increment(ref s.Skipped);
                        continue;
                    }

                    Interlocked.Increment(ref s.Folders);

                    if (!folded && depth < s.Options.TreeDepth && owner.Children.Count < s.Options.MaxChildren)
                    {
                        Node child = new Node();
                        child.Name = sd.Name;
                        child.Path = PathUtil.DisplayPath(sd.FullName);
                        child.Parent = owner;
                        owner.Children.Add(child);
                        WalkTree(sd, child, depth + 1, false, s);
                    }
                    else
                    {
                        WalkTree(sd, owner, depth + 1, true, s);
                    }
                }
                catch { Interlocked.Increment(ref s.Errors); }
            }
        }
        catch { Interlocked.Increment(ref s.Errors); }
    }

    // Rolls direct and folded bytes up the tree, and derives the dominant
    // extension for each node from the biggest extension group below it.
    private static void FinalizeTree(Node node)
    {
        if (node.IsFile)
        {
            node.Items = 1;
            node.TopExt = PathUtil.ExtOf(node.Name);
            node.TopSize = node.Size;
            return;
        }

        long size = node.Size;
        long items = node.Files + node.Folded;
        string topExt = null;
        long topSize = 0;

        if (node.DirectExt != null)
        {
            foreach (KeyValuePair<string, long> kv in node.DirectExt)
                if (kv.Value > topSize) { topSize = kv.Value; topExt = kv.Key; }

            node.DirectExt = null;   // release; only needed for the colouring pass
        }

        for (int i = 0; i < node.Children.Count; i++)
        {
            Node c = node.Children[i];
            FinalizeTree(c);

            // File nodes are only in the tree for display: their bytes were
            // already added to this node's Size and Files while walking, so
            // rolling them up again would count them twice.
            if (!c.IsFile)
            {
                size += c.Size;
                items += c.Items;
            }

            if (c.TopSize > topSize) { topSize = c.TopSize; topExt = c.TopExt; }
        }

        node.Size = size;
        node.Items = items;
        node.TopExt = topExt;
        node.TopSize = topSize;
    }

    // Biggest child first, recursively. The tables and the treemap both expect
    // it, and doing it once beats re-sorting on every expand.
    public static void SortTree(Node node)
    {
        if (node.IsFile || node.Children.Count == 0) return;

        node.Children.Sort(delegate(Node a, Node b) { return b.Size.CompareTo(a.Size); });
        for (int i = 0; i < node.Children.Count; i++) SortTree(node.Children[i]);
    }

    private static void AddExt(Node owner, State s, string fileName, long len)
    {
        string e = PathUtil.ExtOf(fileName);

        if (owner.DirectExt == null)
            owner.DirectExt = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        long cur;
        owner.DirectExt.TryGetValue(e, out cur);
        owner.DirectExt[e] = cur + len;

        ExtAgg agg = s.Ext.GetOrAdd(e, delegate(string k) { return new ExtAgg(); });
        Interlocked.Add(ref agg.Size, len);
        Interlocked.Increment(ref agg.Count);
    }
}
