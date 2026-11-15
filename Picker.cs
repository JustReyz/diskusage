using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
// Source picker: the drive dropdown on the toolbar, and the folder tree used to
// include or exclude parts of a scan before it runs.
// ---------------------------------------------------------------------------

internal sealed class DriveEntry
{
    public string Root;     // C:\
    public string Letter;   // C:
    public string Label;    // volume label, or a name for the drive type
    public long Total;
    public long Free;
    public bool Ready;
}

internal static class Drives
{
    public static List<DriveEntry> List()
    {
        List<DriveEntry> list = new List<DriveEntry>();

        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); }
        catch { return list; }

        for (int i = 0; i < all.Length; i++)
        {
            DriveInfo d = all[i];
            DriveEntry e = new DriveEntry();

            try
            {
                e.Root = d.RootDirectory.FullName;
                e.Letter = d.Name.TrimEnd('\\');
                e.Label = TypeName(d.DriveType);
                e.Ready = d.IsReady;

                if (e.Ready)
                {
                    e.Total = d.TotalSize;
                    e.Free = d.AvailableFreeSpace;

                    string vol = "";
                    try { vol = d.VolumeLabel; } catch { }
                    if (!string.IsNullOrEmpty(vol)) e.Label = vol;
                }
            }
            catch { continue; }

            list.Add(e);
        }

        return list;
    }

    private static string TypeName(DriveType t)
    {
        switch (t)
        {
            case DriveType.Fixed: return "Local Disk";
            case DriveType.Removable: return "Removable Disk";
            case DriveType.CDRom: return "CD Drive";
            case DriveType.Network: return "Network Drive";
            case DriveType.Ram: return "RAM Disk";
            default: return "Drive";
        }
    }

    // First ready local drive, used when the window opens with no arguments.
    public static string DefaultRoot()
    {
        List<DriveEntry> list = List();

        for (int i = 0; i < list.Count; i++)
            if (list[i].Ready && !list[i].Root.StartsWith(@"\\")) return list[i].Root;

        try
        {
            string sys = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(sys)) return sys;
        }
        catch { }

        return null;
    }
}

// ---------------------------------------------------------------------------
// One row in the dropdown.
// ---------------------------------------------------------------------------
internal sealed class MenuEntry
{
    public string Text;
    public string Sub;
    public bool Header;     // section label, not clickable
    public bool Enabled = true;
    public string Value;    // path to scan, when this entry picks a source
    public int Action;      // 0 none, 1 choose folder, 2 choose folders to skip
}

internal sealed class MenuList : DarkControl
{
    public readonly List<MenuEntry> Entries = new List<MenuEntry>();

    private int _hover = -1;

    public event Action<MenuEntry> Picked;

    private int RowH { get { return Ui.Px(38); } }
    private int HeadH { get { return Ui.Px(24); } }

    public int ContentHeight
    {
        get
        {
            int h = 0;
            for (int i = 0; i < Entries.Count; i++)
                h += Entries[i].Header ? HeadH : RowH;
            return h;
        }
    }

    public int IndexAt(int y)
    {
        int top = 0;
        for (int i = 0; i < Entries.Count; i++)
        {
            int h = Entries[i].Header ? HeadH : RowH;
            if (y >= top && y < top + h) return i;
            top += h;
        }
        return -1;
    }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Panel2);

        int y = 0;
        Font tf = Ui.F(12f);
        Font sf = Ui.F(10.5f);
        Font hf = Ui.FB(9.5f);

        for (int i = 0; i < Entries.Count; i++)
        {
            MenuEntry e = Entries[i];
            int h = e.Header ? HeadH : RowH;
            Rectangle r = new Rectangle(0, y, Width, h);

            if (e.Header)
            {
                Ui.DrawText(g, e.Text, hf, Ui.Dimmer,
                    new Rectangle(Ui.Px(12), y, Width - Ui.Px(24), h), Ui.LeftFlags);
            }
            else
            {
                if (i == _hover && e.Enabled) g.FillRectangle(Ui.BrushFor(Ui.RowHover), r);

                Color c = e.Enabled ? Ui.TextCol : Ui.Dimmer;
                Ui.DrawText(g, e.Text, tf, c,
                    new Rectangle(Ui.Px(12), y + Ui.Px(5), Width - Ui.Px(24), Ui.Px(18)), Ui.LeftFlags);

                if (!string.IsNullOrEmpty(e.Sub))
                    Ui.DrawText(g, e.Sub, sf, Ui.Dimmer,
                        new Rectangle(Ui.Px(12), y + Ui.Px(21), Width - Ui.Px(24), Ui.Px(15)), Ui.LeftFlags);
            }

            y += h;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = IndexAt(e.Y);
        if (i >= 0 && (Entries[i].Header || !Entries[i].Enabled)) i = -1;
        if (i != _hover) { _hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        int i = IndexAt(e.Y);
        if (i < 0) return;
        if (Entries[i].Header || !Entries[i].Enabled) return;
        if (Picked != null) Picked(Entries[i]);
    }
}

// Borderless popup that hosts a MenuList and closes when it loses focus.
internal sealed class PopupMenu : Form
{
    private readonly MenuList _list = new MenuList();

    public PopupMenu()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Ui.Line;
        Padding = new Padding(1);
        DoubleBuffered = true;

        _list.Dock = DockStyle.Fill;
        Controls.Add(_list);
    }

    public MenuList List { get { return _list; } }

    public void ShowUnder(Rectangle screenRect, Form owner)
    {
        int h = _list.ContentHeight + 2;
        int w = Math.Max(screenRect.Width, Ui.Px(300));

        Point at = new Point(screenRect.Left, screenRect.Bottom + Ui.Px(3));
        Rectangle wa = Screen.FromPoint(new Point(screenRect.Left, screenRect.Top)).WorkingArea;

        if (at.Y + h > wa.Bottom) at.Y = screenRect.Top - h - Ui.Px(3);
        if (at.X + w > wa.Right) at.X = wa.Right - w;
        if (at.X < wa.Left) at.X = wa.Left;
        if (at.Y < wa.Top) at.Y = wa.Top;

        Size = new Size(w, h);
        Location = at;

        if (owner != null) Show(owner);
        else Show();

        BringToFront();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }
}

// ---------------------------------------------------------------------------
// Scope tree: a lazily loaded folder tree with check boxes. A checked folder is
// scanned, an unchecked one is skipped along with everything under it.
// ---------------------------------------------------------------------------
internal sealed class ScopeItem
{
    public string Path;
    public string Name;
    public int Depth;
    public bool Checked;
    public bool Expanded;
    public bool Loaded;
    public bool IsLink;
    public bool HasChildren = true;
    public ScopeItem Parent;
    public readonly List<ScopeItem> Children = new List<ScopeItem>();
}

internal sealed class ScopeTree : DarkControl, IWheelTarget
{
    private ScopeItem _root;
    private HashSet<string> _skipSet;
    private readonly List<ScopeItem> _rows = new List<ScopeItem>();
    private ScopeItem _hover;
    private int _scroll;
    private bool _dragBar;

    public event EventHandler Changed;

    private int RowH { get { return Ui.Px(24); } }

    public ScopeTree()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public void SetRoot(string path, HashSet<string> skip)
    {
        _skipSet = skip;
        _root = new ScopeItem();
        _root.Path = Clean(path);
        _root.Name = NameOf(path);
        _root.Checked = true;      // the scan root itself is always included
        _root.Expanded = true;
        _scroll = 0;

        Load(_root);
        Rebuild();
    }

    private static string Clean(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        return p.TrimEnd('\\').Length <= 2 ? p : p.TrimEnd('\\');
    }

    private static string NameOf(string path)
    {
        string p = Clean(path);
        string n = Path.GetFileName(p);
        return string.IsNullOrEmpty(n) ? p : n;
    }

    private void Load(ScopeItem it)
    {
        if (it.Loaded) return;
        it.Loaded = true;

        string[] dirs;
        try { dirs = Directory.GetDirectories(PathUtil.ToLongPath(it.Path)); }
        catch
        {
            it.HasChildren = false;
            return;
        }

        Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < dirs.Length; i++)
        {
            ScopeItem c = new ScopeItem();
            c.Path = PathUtil.DisplayPath(dirs[i]);
            c.Name = Path.GetFileName(c.Path);
            c.Depth = it.Depth + 1;
            c.Parent = it;
            // A folder is only in unless its parent is in and it was not
            // explicitly excluded last time.
            c.Checked = it.Checked && (_skipSet == null || !_skipSet.Contains(c.Path));

            try { c.IsLink = (new DirectoryInfo(dirs[i]).Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { }

            it.Children.Add(c);
        }

        if (it.Children.Count == 0) it.HasChildren = false;
    }

    private void Rebuild()
    {
        _rows.Clear();
        if (_root != null) Add(_root);
        Clamp();
        Invalidate();
        if (Changed != null) Changed(this, EventArgs.Empty);
    }

    private void Add(ScopeItem it)
    {
        _rows.Add(it);
        if (!it.Expanded) return;
        for (int i = 0; i < it.Children.Count; i++) Add(it.Children[i]);
    }

    private int ContentH { get { return _rows.Count * RowH; } }
    private int PageH { get { return Height; } }

    private void Clamp()
    {
        int max = Math.Max(0, ContentH - PageH);
        if (_scroll > max) _scroll = max;
        if (_scroll < 0) _scroll = 0;
    }

    public void Wheel(int delta)
    {
        _scroll -= (delta / 120) * RowH * 3;
        Clamp();
        Invalidate();
    }

    // Checked count and skipped count over what is currently loaded.
    public void Counts(out int total, out int skipped)
    {
        total = 0;
        skipped = 0;
        if (_root != null) Count(_root, ref total, ref skipped);
    }

    private void Count(ScopeItem it, ref int total, ref int skipped)
    {
        total++;
        if (!it.Checked) skipped++;
        for (int i = 0; i < it.Children.Count; i++) Count(it.Children[i], ref total, ref skipped);
    }

    public void SetAll(bool on)
    {
        if (_root == null) return;
        _root.Checked = true;   // the scan root itself always stays in
        for (int i = 0; i < _root.Children.Count; i++) SetSubtree(_root.Children[i], on);
        Rebuild();
    }

    private void SetSubtree(ScopeItem it, bool on)
    {
        it.Checked = on;
        for (int i = 0; i < it.Children.Count; i++) SetSubtree(it.Children[i], on);
    }

    public HashSet<string> Collect()
    {
        HashSet<string> skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_root != null) Collect(_root, skip);
        return skip;
    }

    private void Collect(ScopeItem it, HashSet<string> skip)
    {
        if (!it.Checked) { skip.Add(it.Path); return; }
        for (int i = 0; i < it.Children.Count; i++) Collect(it.Children[i], skip);
    }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Panel);

        if (_root == null)
        {
            Ui.DrawText(g, "Pick a folder to scan first.", Ui.F(12f), Ui.Dimmer,
                ClientRectangle, Ui.CenterFlags);
            return;
        }

        g.SetClip(ClientRectangle);

        int first = Math.Max(0, _scroll / RowH);
        int last = Math.Min(_rows.Count - 1, (_scroll + PageH) / RowH);

        Font nf = Ui.F(11.5f);

        for (int i = first; i <= last; i++)
        {
            ScopeItem it = _rows[i];
            int y = i * RowH - _scroll;
            Rectangle rr = new Rectangle(0, y, Width, RowH);

            if (it == _hover) g.FillRectangle(Ui.BrushFor(Ui.RowHover), rr);
            g.DrawLine(Ui.PenFor(Ui.RowLine, 1), 0, y + RowH - 1, Width, y + RowH - 1);

            int indent = Ui.Px(10) + it.Depth * Ui.Px(15);

            // expander
            if (it.HasChildren)
            {
                Rectangle chev = new Rectangle(indent, y, Ui.Px(14), RowH);
                Ui.DrawText(g, it.Expanded ? "\u25BC" : "\u25B6", Ui.F(8.5f), Ui.Dimmer, chev, Ui.CenterFlags);
            }

            // check box
            int bx = indent + Ui.Px(16);
            Rectangle box = new Rectangle(bx, y + (RowH - Ui.Px(14)) / 2, Ui.Px(14), Ui.Px(14));
            Ui.Rounded(g, box, Ui.Px(3), it.Checked ? Ui.Accent : Ui.C("#2a2a35"));

            using (GraphicsPath p = Ui.RoundPath(box, Ui.Px(3)))
                g.DrawPath(Ui.PenFor(it.Checked ? Ui.Accent : Ui.C("#3c3c48"), 1), p);

            if (it.Checked)
            {
                // tick
                g.DrawLines(Ui.PenFor(Color.White, 1.7f), new Point[] {
                    new Point(box.X + Ui.Px(3), box.Y + Ui.Px(7)),
                    new Point(box.X + Ui.Px(6), box.Y + Ui.Px(10)),
                    new Point(box.X + Ui.Px(11), box.Y + Ui.Px(4))
                });
            }

            // name
            int nameX = bx + Ui.Px(22);
            Color col = it.Checked ? Ui.C("#d2d2dc") : Ui.Dimmer;
            Ui.DrawText(g, it.Name, nf, col,
                new Rectangle(nameX, y, Math.Max(0, Width - nameX - ScrollBar.Width - Ui.Px(10)), RowH),
                Ui.LeftFlags);
        }

        g.ResetClip();
        ScrollBar.Draw(g, ScrollBar.Track(this), _scroll, ContentH, PageH);
    }

    private int IndexAt(Point p)
    {
        int y = p.Y + _scroll;
        int i = y / RowH;
        return (i >= 0 && i < _rows.Count) ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragBar)
        {
            _scroll = ScrollBar.ValueFromY(ScrollBar.Track(this), e.Y, ContentH, PageH);
            Clamp();
            Invalidate();
            return;
        }

        int i = IndexAt(e.Location);
        ScopeItem it = i >= 0 ? _rows[i] : null;
        if (it != _hover) { _hover = it; Invalidate(); }
        Cursor = it != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != null) { _hover = null; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        Rectangle track = ScrollBar.Track(this);
        if (track.Contains(e.Location) && ContentH > PageH)
        {
            _dragBar = true;
            _scroll = ScrollBar.ValueFromY(track, e.Y, ContentH, PageH);
            Clamp();
            Invalidate();
            return;
        }

        int i = IndexAt(e.Location);
        if (i < 0) return;

        ScopeItem it = _rows[i];
        int indent = Ui.Px(10) + it.Depth * Ui.Px(15);

        // chevron zone expands; anything else toggles the check box
        if (it.HasChildren && e.X >= indent && e.X <= indent + Ui.Px(14))
        {
            it.Expanded = !it.Expanded;
            if (it.Expanded) Load(it);
            Rebuild();
            return;
        }

        if (it.Parent == null) return;   // the scan root itself cannot be excluded

        it.Checked = !it.Checked;
        SetSubtree(it, it.Checked);
        Rebuild();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragBar = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = IndexAt(e.Location);
        if (i < 0) return;

        ScopeItem it = _rows[i];
        if (!it.HasChildren) return;

        it.Expanded = !it.Expanded;
        if (it.Expanded) Load(it);
        Rebuild();
    }
}

// ---------------------------------------------------------------------------
// Modal dialog wrapping the scope tree.
// ---------------------------------------------------------------------------
internal sealed class ScopeForm : Form
{
    private readonly ScopeTree _tree = new ScopeTree();
    private readonly Label _hint = new Label();
    private readonly Label _count = new Label();
    private readonly Button _ok = new Button();
    private readonly Button _cancel = new Button();
    private readonly Button _all = new Button();
    private readonly Button _none = new Button();

    public HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public ScopeForm(string root, HashSet<string> existing)
    {
        Text = "Folders to scan";
        BackColor = Ui.Bg;
        ForeColor = Ui.TextCol;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(Ui.Px(560), Ui.Px(580));
        MinimumSize = new Size(Ui.Px(420), Ui.Px(360));
        ShowInTaskbar = false;
        KeyPreview = true;
        Font = Ui.F(11.5f);
        DoubleBuffered = true;

        Panel head = new Panel();
        head.Dock = DockStyle.Top;
        head.Height = Ui.Px(46);
        head.BackColor = Ui.Bg;

        _hint.Text = "Uncheck a folder to leave it out. Skipped folders are never opened.";
        _hint.ForeColor = Ui.Dim;
        _hint.Font = Ui.F(11f);
        _hint.AutoSize = false;
        _hint.SetBounds(Ui.Px(14), Ui.Px(6), Ui.Px(520), Ui.Px(18));
        _hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        head.Controls.Add(_hint);

        _count.ForeColor = Ui.Dimmer;
        _count.Font = Ui.F(10.5f);
        _count.SetBounds(Ui.Px(14), Ui.Px(24), Ui.Px(520), Ui.Px(16));
        _count.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        head.Controls.Add(_count);

        Panel foot = new Panel();
        foot.Dock = DockStyle.Bottom;
        foot.Height = Ui.Px(50);
        foot.BackColor = Ui.Head;

        _all.Text = "All";
        Style(_all, 2);
        _none.Text = "None";
        Style(_none, 3);

        _cancel.Text = "Cancel";
        Style(_cancel, 1);
        _ok.Text = "Scan";
        Style(_ok, 0);

        _all.Click += delegate { _tree.SetAll(true); UpdateCount(); };
        _none.Click += delegate { _tree.SetAll(false); UpdateCount(); };
        _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        _ok.Click += delegate { Skip = _tree.Collect(); DialogResult = DialogResult.OK; Close(); };

        foot.Controls.Add(_all);
        foot.Controls.Add(_none);
        foot.Controls.Add(_cancel);
        foot.Controls.Add(_ok);

        _tree.Dock = DockStyle.Fill;
        _tree.Changed += delegate { UpdateCount(); };

        Controls.Add(_tree);
        Controls.Add(head);
        Controls.Add(foot);

        LayoutButtons(foot);

        _tree.SetRoot(root, existing);
        UpdateCount();

        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        };
    }

    private void Style(Button b, int slot)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Ui.Line;
        b.FlatAppearance.MouseOverBackColor = Ui.RowHover;
        b.BackColor = slot == 0 ? Ui.C("#3b78e0") : Ui.Panel2;
        b.ForeColor = slot == 0 ? Color.White : Ui.TextCol;
        b.Font = Ui.FB(11.5f);
        b.Tag = slot;
        b.Height = Ui.Px(30);
        b.Width = Ui.Px(88);
    }

    private void LayoutButtons(Panel foot)
    {
        int y = (foot.Height - Ui.Px(30)) / 2;
        int right = foot.Width - Ui.Px(14);

        _ok.SetBounds(right - Ui.Px(88), y, Ui.Px(88), Ui.Px(30));
        right -= Ui.Px(88) + Ui.Px(8);
        _cancel.SetBounds(right - Ui.Px(88), y, Ui.Px(88), Ui.Px(30));

        _all.SetBounds(Ui.Px(14), y, Ui.Px(62), Ui.Px(30));
        _none.SetBounds(Ui.Px(14) + Ui.Px(62) + Ui.Px(8), y, Ui.Px(62), Ui.Px(30));
    }

    private void UpdateCount()
    {
        int total, skipped;
        _tree.Counts(out total, out skipped);

        if (skipped == 0) _count.Text = "All " + Ui.Num(total) + " folders will be scanned.";
        else _count.Text = Ui.Num(skipped) + " of " + Ui.Num(total) + " folders skipped";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.DarkFrame(Handle);
    }
}
