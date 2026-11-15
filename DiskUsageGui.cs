using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// ---------------------------------------------------------------------------
// Native WinForms front-end. Everything is owner-drawn so the dark palette and
// the dense table layout from the prototype survive the move off the browser.
// ---------------------------------------------------------------------------

internal static class Win32
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point p);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Dark title bar. Needs the OS manifest, otherwise Windows shims the process
    // as a Windows 7 app and the compositor ignores the request.
    public static void DarkFrame(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            int hr = DwmSetWindowAttribute(hwnd, 20, ref on, 4);
            if (hr == 0) return;
            int hr2 = DwmSetWindowAttribute(hwnd, 19, ref on, 4);
            if (hr2 != 0)
                Ui.Report(new Exception("dark frame: 20=0x" + hr.ToString("X8") + " 19=0x" + hr2.ToString("X8")));
        }
        catch { }
    }
}

// Lets the mouse wheel scroll whatever list is under the cursor, not just the
// focused one. Without this a table only scrolls after you click it.
internal interface IWheelTarget
{
    void Wheel(int delta);
}

internal sealed class WheelRouter : IMessageFilter
{
    private const int WM_MOUSEWHEEL = 0x020A;

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;

        long lp = m.LParam.ToInt64();
        Point screen = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));

        Control c = Control.FromHandle(Win32.WindowFromPoint(screen));
        while (c != null && !(c is IWheelTarget)) c = c.Parent;

        IWheelTarget t = c as IWheelTarget;
        if (t == null) return false;

        t.Wheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
        return true;
    }
}

internal abstract class DarkControl : Control
{
    protected DarkControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Bg;
        ForeColor = Ui.TextCol;
    }

    // A paint bug should leave a blank panel and a log line, not a red cross
    // over the whole window.
    protected sealed override void OnPaint(PaintEventArgs e)
    {
        try { PaintSelf(e.Graphics); }
        catch (Exception ex) { Ui.Report(ex); }
    }

    protected abstract void PaintSelf(Graphics g);

    // DrawToBitmap and print preview send WM_PRINTCLIENT instead of WM_PAINT.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0318)
        {
            using (Graphics g = Graphics.FromHdc(m.WParam))
            using (PaintEventArgs pe = new PaintEventArgs(g, ClientRectangle))
                OnPaint(pe);
            return;
        }
        base.WndProc(ref m);
    }
}

internal static class ScrollBar
{
    public static int Width { get { return Ui.Px(10); } }

    public static Rectangle Track(Control c)
    {
        return new Rectangle(c.Width - Width, 0, Width, c.Height);
    }

    public static Rectangle Thumb(Rectangle track, int value, int content, int page)
    {
        if (content <= page || page <= 0) return Rectangle.Empty;

        int inset = Ui.Px(2);
        int h = Math.Max(Ui.Px(24), (int)((double)page / content * track.Height));
        int max = content - page;
        int travel = track.Height - h;
        int y = track.Y + (max > 0 ? (int)((double)value / max * travel) : 0);
        return new Rectangle(track.X + inset, y, track.Width - inset * 2, h);
    }

    public static void Draw(Graphics g, Rectangle track, int value, int content, int page)
    {
        if (content <= page) return;

        g.FillRectangle(Ui.BrushFor(Ui.C("#1a1a1f")), track);

        Rectangle th = Thumb(track, value, content, page);
        if (th.IsEmpty) return;

        Ui.Rounded(g, new RectangleF(th.X, th.Y, th.Width, th.Height), th.Width / 2f, Ui.C("#383843"));
    }

    public static int ValueFromY(Rectangle track, int y, int content, int page)
    {
        int h = Math.Max(Ui.Px(24), (int)((double)page / content * track.Height));
        int travel = track.Height - h;
        if (travel <= 0) return 0;
        double t = (double)(y - track.Y - h / 2) / travel;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return (int)(t * (content - page));
    }
}

// ---------------------------------------------------------------------------
// Toolbar
// ---------------------------------------------------------------------------
internal sealed class Toolbar : DarkControl
{
    private readonly TextBox _path;
    private Rectangle _pathRect;
    private Rectangle _chevRect;
    private Rectangle _scanRect;
    private Rectangle _chipRect;
    private bool _scanHover, _scanDown;
    private bool _chevHover;
    private bool _scanning;
    private string _status = "";
    private string _drive = "";
    private PopupMenu _menu;

    public event EventHandler ScanClicked;
    public event Action<string> PathChosen;      // a drive was picked: scan it now
    public event EventHandler FolderPickRequested;
    public event EventHandler SkipRequested;

    public Toolbar()
    {
        _path = new TextBox();
        _path.BorderStyle = BorderStyle.None;
        _path.BackColor = Ui.C("#131317");
        _path.ForeColor = Ui.C("#c9c9d4");
        _path.Font = Ui.F(12.5f);
        _path.Text = "";
        _path.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Fire(); }
        };
        Controls.Add(_path);
    }

    public string PathText
    {
        get { return _path.Text; }
        set { _path.Text = value; }
    }

    public void SetDrive(string d) { _drive = d; Invalidate(); }

    public void SetStatus(string s) { _status = s; Invalidate(); }

    public void SetScanning(bool on)
    {
        _scanning = on;
        Invalidate();
    }

    public void FocusPath()
    {
        _path.Focus();
        _path.SelectAll();
    }

    public void OpenMenuNow()
    {
        OpenMenu();
    }

    private void Fire()
    {
        if (ScanClicked != null) ScanClicked(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        int pad = Ui.Px(12);
        int brandW = Ui.Px(96);
        int chipW = Ui.Px(42);
        int btnW = Ui.Px(84);
        int gap = Ui.Px(10);

        int x = pad + brandW + gap;
        _chipRect = new Rectangle(x, (Height - Ui.Px(24)) / 2, chipW, Ui.Px(24));
        x += chipW + gap;

        int scanX = Width - pad - btnW;
        _scanRect = new Rectangle(scanX, (Height - Ui.Px(28)) / 2, btnW, Ui.Px(28));

        // Reserve enough room for the longest status: files, seconds, threads
        // and the skipped count all at once.
        int pathRight = scanX - gap - Ui.Px(268);
        int pathW = Math.Max(Ui.Px(160), pathRight - x);

        _pathRect = new Rectangle(x, (Height - Ui.Px(26)) / 2, pathW, Ui.Px(26));
        _chevRect = new Rectangle(_pathRect.Right - Ui.Px(25), _pathRect.Y + 1, Ui.Px(24), _pathRect.Height - 2);

        // Poppins has a taller line box than Segoe UI, so the native text box
        // needs more than the old 12px of inner height or it clips the baseline.
        _path.SetBounds(_pathRect.X + Ui.Px(9), _pathRect.Y + Ui.Px(4),
                        _pathRect.Width - Ui.Px(18) - Ui.Px(24), _pathRect.Height - Ui.Px(8));
    }

    protected override void PaintSelf(Graphics g)
    {
        using (LinearGradientBrush bg = new LinearGradientBrush(
            new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
            Ui.C("#232330"), Ui.C("#1c1c24"), 90f))
            g.FillRectangle(bg, ClientRectangle);

            g.DrawLine(Ui.PenFor(Ui.Line, 1), 0, Height - 1, Width, Height - 1);

        int pad = Ui.Px(12);

        // brand
        Rectangle brand = new Rectangle(pad, 0, Ui.Px(96), Height);
        Ui.DrawText(g, "Disk", Ui.FB(12.5f), Ui.C("#e8e8ef"), brand, Ui.LeftFlags);
        Size diskW = TextRenderer.MeasureText("Disk", Ui.FB(12.5f), Size.Empty, TextFormatFlags.NoPadding);
        Ui.DrawText(g, "Usage", Ui.FB(12.5f), Ui.Accent,
            new Rectangle(brand.X + diskW.Width, 0, Ui.Px(60), Height), Ui.LeftFlags);

        // drive chip
        Ui.Rounded(g, _chipRect, Ui.Px(5), Ui.C("#2a2a35"));
        using (GraphicsPath p = Ui.RoundPath(_chipRect, Ui.Px(5)))
            g.DrawPath(Ui.PenFor(Ui.C("#35353f"), 1), p);
        Ui.DrawText(g, _drive, Ui.FB(11.5f), Ui.TextCol, _chipRect, Ui.CenterFlags);

        // path field
        Ui.Rounded(g, _pathRect, Ui.Px(5), Ui.C("#131317"));
        using (GraphicsPath p = Ui.RoundPath(_pathRect, Ui.Px(5)))
            g.DrawPath(Ui.PenFor(Ui.C("#34343f"), 1), p);

        // dropdown affordance: divider plus a chevron, drawn rather than typed
        // so it does not depend on the typeface having the glyph
        if (_chevHover)
            g.FillRectangle(Ui.BrushFor(Ui.C("#1c1c24")), _chevRect);
        g.DrawLine(Ui.PenFor(Ui.C("#2b2b35"), 1),
            _chevRect.X, _chevRect.Y + Ui.Px(5), _chevRect.X, _chevRect.Bottom - Ui.Px(5));
        DrawChevron(g, _chevRect, _chevHover ? Ui.TextCol : Ui.Dim);

        // scan button
        bool hot = _scanHover && !_scanning;
        Color top = _scanning ? Ui.C("#3a3a46") : (hot ? Ui.C("#5b98ff") : Ui.C("#4c8dff"));
        Color bot = _scanning ? Ui.C("#32323c") : (hot ? Ui.C("#4483ea") : Ui.C("#3b78e0"));
        using (LinearGradientBrush bg = new LinearGradientBrush(
            new Rectangle(_scanRect.X, _scanRect.Y, _scanRect.Width, Math.Max(1, _scanRect.Height)),
            top, bot, 90f))
        {
            using (GraphicsPath p = Ui.RoundPath(_scanRect, Ui.Px(5)))
                g.FillPath(bg, p);
        }
        Ui.DrawText(g, _scanning ? "Cancel" : "Scan", Ui.FB(12f),
            _scanning ? Ui.C("#c8c8d2") : Color.White, _scanRect, Ui.CenterFlags);

        // status
        int statusX = _pathRect.Right + Ui.Px(12);
        int statusW = _scanRect.X - Ui.Px(10) - statusX;
        if (statusW > Ui.Px(40))
            Ui.DrawText(g, _status, Ui.F(11.5f), Ui.Dim,
                new Rectangle(statusX, 0, statusW, Height), Ui.LeftFlags);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool h = _scanRect.Contains(e.Location);
        if (h != _scanHover) { _scanHover = h; Invalidate(); }

        bool c = _chevRect.Contains(e.Location);
        if (c != _chevHover) { _chevHover = c; Invalidate(); }

        Cursor = (h || c) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_scanHover || _chevHover)
        {
            _scanHover = false;
            _chevHover = false;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // The menu opens on mouse-up: opening on mouse-down leaves the popup
        // active while the matching mouse-up is still heading for this window,
        // which deactivates it and closes it again.
        if (_chevRect.Contains(e.Location)) return;
        if (_scanRect.Contains(e.Location)) { _scanDown = true; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_chevRect.Contains(e.Location)) { OpenMenu(); return; }

        if (_scanDown)
        {
            _scanDown = false;
            Invalidate();
            if (_scanRect.Contains(e.Location)) Fire();
        }
    }

    private static void DrawChevron(Graphics g, Rectangle r, Color c)
    {
        int w = Ui.Px(9);
        int h = Ui.Px(5);
        int cx = r.X + r.Width / 2;
        int cy = r.Y + r.Height / 2 - h / 2;

        g.DrawLines(Ui.PenFor(c, 1.6f), new Point[] {
            new Point(cx - w / 2, cy),
            new Point(cx, cy + h),
            new Point(cx + w / 2, cy)
        });
    }

    // Drives first, matching the source list people expect, then the two
    // folder actions. Picking a drive scans it straight away.
    private void OpenMenu()
    {
        if (_menu != null) return;

        PopupMenu pop = new PopupMenu();
        MenuList list = pop.List;

        MenuEntry head = new MenuEntry();
        head.Header = true;
        head.Text = "SCAN";
        list.Entries.Add(head);

        List<DriveEntry> drives = Drives.List();
        for (int i = 0; i < drives.Count; i++)
        {
            DriveEntry d = drives[i];

            MenuEntry e = new MenuEntry();
            e.Text = "[" + d.Letter + "] " + d.Label;
            e.Value = d.Root;
            e.Enabled = d.Ready;

            if (d.Ready && d.Total > 0)
            {
                long used = d.Total - d.Free;
                e.Sub = Ui.Fmt(used) + " used  \u00B7  " + Ui.Fmt(d.Free) + " free  \u00B7  " +
                        Ui.Pct(used * 100.0 / d.Total);
            }
            else e.Sub = "Not ready";

            list.Entries.Add(e);
        }

        MenuEntry other = new MenuEntry();
        other.Header = true;
        other.Text = "OTHER";
        list.Entries.Add(other);

        MenuEntry pick = new MenuEntry();
        pick.Text = "<Select folder\u2026>";
        pick.Action = 1;
        list.Entries.Add(pick);

        MenuEntry skip = new MenuEntry();
        skip.Text = "<Folders to skip\u2026>";
        skip.Sub = "Leave parts of the folder out of the scan";
        skip.Action = 2;
        skip.Enabled = _path.Text.Trim().Length > 0;
        list.Entries.Add(skip);

        list.Picked += delegate(MenuEntry picked)
        {
            pop.Close();

            if (picked.Action == 1)
            {
                if (FolderPickRequested != null) FolderPickRequested(this, EventArgs.Empty);
            }
            else if (picked.Action == 2)
            {
                if (SkipRequested != null) SkipRequested(this, EventArgs.Empty);
            }
            else if (!string.IsNullOrEmpty(picked.Value))
            {
                _path.Text = picked.Value;
                if (PathChosen != null) PathChosen(picked.Value);
            }
        };

        pop.FormClosed += delegate { _menu = null; };
        _menu = pop;

        pop.ShowUnder(RectangleToScreen(_pathRect), FindForm());
    }
}

// ---------------------------------------------------------------------------
// Stats strip
// ---------------------------------------------------------------------------
internal sealed class StatsStrip : DarkControl
{
    private string _selection = "";
    private long _total, _used, _free, _scanned;
    private long _files;
    private double _scanSeconds;

    public void SetData(ScanResultData d)
    {
        _selection = d.Root != null ? d.Root.Path : d.Path;
        _scanned = d.TotalSize;
        _files = d.FileCount;
        _scanSeconds = d.Seconds;

        _total = 0; _used = 0; _free = 0;
        try
        {
            string root = System.IO.Path.GetPathRoot(d.Path);
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo di = new DriveInfo(root);
                if (di.IsReady)
                {
                    _total = di.TotalSize;
                    _free = di.AvailableFreeSpace;
                    _used = _total - _free;
                }
            }
        }
        catch { }

        Invalidate();
    }

    public void Clear()
    {
        _selection = "";
        _total = _used = _free = _scanned = _files = 0;
        Invalidate();
    }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Panel);
        g.DrawLine(Ui.PenFor(Ui.Line, 1), 0, Height - 1, Width, Height - 1);

        int selW = Ui.Px(300);
        int barW = Ui.Px(220);
        int pad = Ui.Px(14);

        // selection block
        Rectangle sel = new Rectangle(0, 0, selW, Height);
        Ui.DrawText(g, "SELECTION", Ui.FB(10f), Ui.Dimmer,
            new Rectangle(pad, Ui.Px(8), selW - pad * 2, Ui.Px(14)), Ui.LeftFlags);
        Ui.DrawText(g, _selection.Length == 0 ? "\u2014" : _selection, Ui.F(13f), Ui.C("#cfcfda"),
            new Rectangle(pad, Ui.Px(24), selW - pad * 2, Ui.Px(20)), Ui.LeftFlags);

        g.DrawLine(Ui.PenFor(Ui.Line, 1), selW, 0, selW, Height);

        // stat cells
        int gridX = selW;
        int gridW = Width - selW - barW;
        if (gridW < Ui.Px(80)) return;
        int cellW = gridW / 4;

        Stat(g, gridX + cellW * 0, cellW, "TOTAL SPACE", Ui.Fmt(_total), null, Ui.TextCol);
        Stat(g, gridX + cellW * 1, cellW, "SPACE USED", Ui.Fmt(_used),
            _total > 0 ? Ui.Pct(_used * 100.0 / _total) : null, Ui.Amber);
        Stat(g, gridX + cellW * 2, cellW, "SPACE FREE", Ui.Fmt(_free), null, Ui.TextCol);
        Stat(g, gridX + cellW * 3, cellW, "SCANNED", Ui.Fmt(_scanned),
            _files > 0 ? Ui.Num(_files) + " files" : null, Ui.C("#e2e2ea"));

        for (int i = 1; i < 4; i++)
        {
            int x = gridX + cellW * i;
            g.DrawLine(Ui.PenFor(Ui.C("#24242c"), 1), x, Ui.Px(10), x, Height - Ui.Px(10));
        }

        // usage bar
        int barX = Width - barW + pad;
        int barY = (Height - Ui.Px(9)) / 2;
        int bw = barW - pad * 2;
        Ui.Rounded(g, new RectangleF(barX, barY, bw, Ui.Px(9)), Ui.Px(4), Ui.Track);

        double pct = _total > 0 ? _used * 100.0 / _total : 0;
        int fill = (int)(bw * pct / 100.0);
        if (fill > 0)
        {
            Color c = pct >= 90 ? Ui.Red : (pct >= 75 ? Ui.Amber : Ui.Green);
            Ui.Rounded(g, new RectangleF(barX, barY, fill, Ui.Px(9)), Ui.Px(4), c);
        }

        g.DrawLine(Ui.PenFor(Ui.C("#24242c"), 1), Width - barW, Ui.Px(10), Width - barW, Height - Ui.Px(10));
    }

    private void Stat(Graphics g, int x, int w, string key, string value, string sub, Color vc)
    {
        int pad = Ui.Px(14);
        Rectangle r = new Rectangle(x + pad, Ui.Px(8), w - pad * 2, Ui.Px(14));
        Ui.DrawText(g, key, Ui.FB(10f), Ui.Dimmer, r, Ui.LeftFlags);

        Rectangle vr = new Rectangle(x + pad, Ui.Px(23), w - pad * 2, Ui.Px(20));
        Ui.DrawText(g, value, Ui.F(13.5f), vc, vr, Ui.LeftFlags);

        if (!string.IsNullOrEmpty(sub))
        {
            Size vs = TextRenderer.MeasureText(value, Ui.F(13.5f), Size.Empty, TextFormatFlags.NoPadding);
            Ui.DrawText(g, sub, Ui.F(11f), Ui.Dim,
                new Rectangle(x + pad + vs.Width + Ui.Px(6), Ui.Px(23), w - pad * 2, Ui.Px(20)), Ui.LeftFlags);
        }
    }
}

// ---------------------------------------------------------------------------
// Folder tree table
// ---------------------------------------------------------------------------
internal sealed class TreeTable : DarkControl, IWheelTarget
{
    private struct Row
    {
        public Node Node;
        public int Depth;
    }

    private readonly List<Row> _rows = new List<Row>();
    private Node _root;
    private Node _selected;
    private Node _hover;
    private int _scroll;
    private bool _dragBar;

    public event EventHandler SelectionChanged;

    public TreeTable()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    private int RowH { get { return Ui.Px(22); } }
    private int HeadH { get { return Ui.Px(24); } }

    public Node SelectedNode { get { return _selected; } }

    public void SetRoot(Node root)
    {
        _root = root;
        _selected = root;
        _scroll = 0;
        Rebuild();
        Raise();
    }

    public void SelectNode(Node n, bool reveal)
    {
        if (n == null) return;

        if (reveal)
        {
            // expand every ancestor so the row is actually visible
            Node p = n.Parent;
            while (p != null) { p.Expanded = true; p = p.Parent; }
            Rebuild();
        }

        _selected = n;
        EnsureVisible();
        Invalidate();
        Raise();
    }

    private void Raise()
    {
        if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        _rows.Clear();
        if (_root != null) Add(_root, 0);
        ClampScroll();
        Invalidate();
    }

    private void Add(Node n, int depth)
    {
        Row r = new Row();
        r.Node = n;
        r.Depth = depth;
        _rows.Add(r);

        if (!n.Expanded) return;
        for (int i = 0; i < n.Children.Count; i++) Add(n.Children[i], depth + 1);
    }

    private int ContentH { get { return _rows.Count * RowH; } }
    private int PageH { get { return Math.Max(0, Height - HeadH); } }

    private void ClampScroll()
    {
        int max = Math.Max(0, ContentH - PageH);
        if (_scroll > max) _scroll = max;
        if (_scroll < 0) _scroll = 0;
    }

    private void EnsureVisible()
    {
        if (_selected == null) return;

        int idx = -1;
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Node == _selected) { idx = i; break; }
        if (idx < 0) return;

        int top = idx * RowH;
        int bottom = top + RowH;
        if (top < _scroll) _scroll = top;
        else if (bottom > _scroll + PageH) _scroll = bottom - PageH;
        ClampScroll();
    }

    public void Wheel(int delta)
    {
        _scroll -= (delta / 120) * RowH * 3;
        ClampScroll();
        Invalidate();
    }

    private int NameW { get { return Width - Ui.Px(92) - Ui.Px(88) - Ui.Px(76) - ScrollBar.Width; } }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Panel);

        int nameW = NameW;
        int pctX = nameW;
        int sizeX = pctX + Ui.Px(92);
        int itemsX = sizeX + Ui.Px(88);

        // header
        g.FillRectangle(Ui.BrushFor(Ui.Thead), 0, 0, Width, HeadH);
        g.DrawLine(Ui.PenFor(Ui.Line, 1), 0, HeadH - 1, Width, HeadH - 1);

        Font hf = Ui.FB(10f);
        Ui.DrawText(g, "FOLDER", hf, Ui.Dimmer, new Rectangle(Ui.Px(12), 0, nameW, HeadH), Ui.LeftFlags);
        Ui.DrawText(g, "% OF PARENT", hf, Ui.Dimmer, new Rectangle(pctX, 0, Ui.Px(84), HeadH), Ui.RightFlags);
        Ui.DrawText(g, "SIZE", hf, Ui.Dimmer, new Rectangle(sizeX, 0, Ui.Px(80), HeadH), Ui.RightFlags);
        Ui.DrawText(g, "ITEMS", hf, Ui.Dimmer, new Rectangle(itemsX, 0, Ui.Px(68), HeadH), Ui.RightFlags);

        if (_rows.Count == 0)
        {
            Ui.DrawText(g, "Scan a folder to see its contents", Ui.F(12f), Ui.Dimmer,
                new Rectangle(0, HeadH, Width, PageH), Ui.CenterFlags);
            return;
        }

        g.SetClip(new Rectangle(0, HeadH, Width, PageH));

        int first = Math.Max(0, _scroll / RowH);
        int last = Math.Min(_rows.Count - 1, (_scroll + PageH) / RowH);

        Font nf = Ui.F(12f);
        Font sf = Ui.F(11.5f);

        for (int i = first; i <= last; i++)
        {
            Row row = _rows[i];
            int y = HeadH + i * RowH - _scroll;
            Rectangle rr = new Rectangle(0, y, Width, RowH);

            if (row.Node == _selected) g.FillRectangle(Ui.BrushFor(Ui.Sel), rr);
            else if (row.Node == _hover) g.FillRectangle(Ui.BrushFor(Ui.RowHover), rr);

            g.DrawLine(Ui.PenFor(Ui.RowLine, 1), 0, y + RowH - 1, Width, y + RowH - 1);

            int indent = Ui.Px(8) + row.Depth * Ui.Px(12);

            // chevron
            bool hasKids = !row.Node.IsFile && row.Node.Children.Count > 0;
            if (hasKids)
            {
                Rectangle chev = new Rectangle(indent, y, Ui.Px(13), RowH);
                Ui.DrawText(g, row.Node.Expanded ? "\u25BC" : "\u25B6", Ui.F(9f), Ui.Dimmer, chev, Ui.CenterFlags);
            }

            // colour dot
            int dotX = indent + Ui.Px(14);
            Rectangle dot = new Rectangle(dotX, y + (RowH - Ui.Px(7)) / 2, Ui.Px(7), Ui.Px(7));
            Ui.Rounded(g, dot, Ui.Px(2), Ui.ExtColor(row.Node.TopExt));

            // name
            int labelX = dotX + Ui.Px(12);
            Color nameCol = row.Node == _selected ? Color.White : Ui.C("#d2d2dc");
            Ui.DrawText(g, row.Node.Name, nf, nameCol,
                new Rectangle(labelX, y, Math.Max(0, nameW - labelX - Ui.Px(6)), RowH), Ui.LeftFlags);

            // share of parent, with an inline bar behind it
            double share = 100;
            if (row.Node.Parent != null && row.Node.Parent.Size > 0)
                share = row.Node.Size * 100.0 / row.Node.Parent.Size;

            int barW = (int)(Ui.Px(84) * Math.Min(1.0, share / 100.0));
            if (barW > 0)
                Ui.Rounded(g, new RectangleF(pctX + Ui.Px(84) - barW, y + Ui.Px(4), barW, RowH - Ui.Px(8)),
                    Ui.Px(2), row.Node == _selected ? Ui.C("#3d5f9e") : Ui.C("#3a3a47"));

            Ui.DrawText(g, Ui.Pct(share), sf, Ui.C("#b4b4c2"),
                new Rectangle(pctX, y, Ui.Px(84), RowH), Ui.RightFlags);

            Ui.DrawText(g, Ui.Fmt(row.Node.Size), sf, Ui.C("#cfcfda"),
                new Rectangle(sizeX, y, Ui.Px(80), RowH), Ui.RightFlags);

            Ui.DrawText(g, Ui.Num(row.Node.Items), sf, Ui.C("#a9a9b8"),
                new Rectangle(itemsX, y, Ui.Px(68), RowH), Ui.RightFlags);
        }

        g.ResetClip();
        ScrollBar.Draw(g, ScrollBar.Track(this), _scroll, ContentH, PageH);
    }

    private int IndexAt(Point p)
    {
        if (p.Y < HeadH) return -1;
        int y = p.Y - HeadH + _scroll;
        int i = y / RowH;
        return (i >= 0 && i < _rows.Count) ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragBar)
        {
            _scroll = ScrollBar.ValueFromY(ScrollBar.Track(this), e.Y, ContentH, PageH);
            ClampScroll();
            Invalidate();
            return;
        }

        int i = IndexAt(e.Location);
        Node n = i >= 0 ? _rows[i].Node : null;
        if (n != _hover) { _hover = n; Invalidate(); }
        Cursor = Cursors.Default;
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
            ClampScroll();
            Invalidate();
            return;
        }

        int i = IndexAt(e.Location);
        if (i < 0) return;

        Row row = _rows[i];
        int indent = Ui.Px(8) + row.Depth * Ui.Px(12);

        // chevron zone toggles expansion without changing the selection
        if (!row.Node.IsFile && row.Node.Children.Count > 0 &&
            e.X >= indent && e.X <= indent + Ui.Px(15))
        {
            row.Node.Expanded = !row.Node.Expanded;
            Rebuild();
            return;
        }

        _selected = row.Node;
        Invalidate();
        Raise();
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

        Node n = _rows[i].Node;
        if (n.IsFile || n.Children.Count == 0) return;
        n.Expanded = !n.Expanded;
        Rebuild();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0) return;

        int idx = 0;
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Node == _selected) { idx = i; break; }

        if (e.KeyCode == Keys.Down && idx + 1 < _rows.Count)
        {
            _selected = _rows[idx + 1].Node;
            EnsureVisible(); Invalidate(); Raise(); e.Handled = true;
        }
        else if (e.KeyCode == Keys.Up && idx > 0)
        {
            _selected = _rows[idx - 1].Node;
            EnsureVisible(); Invalidate(); Raise(); e.Handled = true;
        }
        else if (e.KeyCode == Keys.Right)
        {
            if (!_selected.Expanded && _selected.Children.Count > 0) { _selected.Expanded = true; Rebuild(); }
            else if (idx + 1 < _rows.Count) { _selected = _rows[idx + 1].Node; EnsureVisible(); Invalidate(); Raise(); }
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Left)
        {
            if (_selected.Expanded && _selected.Children.Count > 0) { _selected.Expanded = false; Rebuild(); }
            else if (_selected.Parent != null) { _selected = _selected.Parent; EnsureVisible(); Invalidate(); Raise(); }
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
        {
            if (!_selected.IsFile && _selected.Children.Count > 0) { _selected.Expanded = !_selected.Expanded; Rebuild(); }
            e.Handled = true;
        }
    }
}

// ---------------------------------------------------------------------------
// Extension table
// ---------------------------------------------------------------------------
internal sealed class ExtTable : DarkControl, IWheelTarget
{
    private List<ExtStat> _exts = new List<ExtStat>();
    private long _total = 1;
    private string _isolated;
    private string _hover;
    private int _scroll;
    private bool _dragBar;

    public event EventHandler IsolationChanged;

    private int StripTop { get { return Ui.Px(8); } }
    private int StripH { get { return Ui.Px(22); } }
    private int HeadH { get { return Ui.Px(46); } }
    private int RowH { get { return Ui.Px(23); } }

    public string Isolated { get { return _isolated; } }

    public void SetData(List<ExtStat> exts, long total)
    {
        _exts = exts;
        _total = Math.Max(1, total);
        _scroll = 0;
        _isolated = null;
        Invalidate();
    }

    public void Clear()
    {
        _exts = new List<ExtStat>();
        _isolated = null;
        Invalidate();
    }

    private int ContentH { get { return _exts.Count * RowH; } }
    private int PageH { get { return Math.Max(0, Height - HeadH); } }

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

    private void Raise()
    {
        if (IsolationChanged != null) IsolationChanged(this, EventArgs.Empty);
    }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Panel);

        int pad = Ui.Px(12);
        int right = Width - pad - ScrollBar.Width;

        // composition strip
        Rectangle strip = new Rectangle(pad, StripTop, Math.Max(0, right - pad), StripH);
        Ui.Rounded(g, strip, Ui.Px(4), Ui.Track);

        if (_exts.Count > 0)
        {
            g.SetClip(strip);
            float x = strip.X;
            long shown = 0;
            int cap = Math.Min(_exts.Count, 26);

            for (int i = 0; i < cap; i++)
            {
                float w = (float)((double)_exts[i].Size / _total * strip.Width);
                if (w < 0.5f) { shown += _exts[i].Size; continue; }
                    g.FillRectangle(Ui.BrushFor(Ui.ExtColor(_exts[i].Ext)), x, strip.Y, w + 0.5f, strip.Height);
                x += w;
                shown += _exts[i].Size;
            }

            if (shown < _total)
            {
                float w = (float)((double)(_total - shown) / _total * strip.Width);
                    g.FillRectangle(Ui.BrushFor(Ui.C("#3a3a45")), x, strip.Y, w + 0.5f, strip.Height);
            }
            g.ResetClip();
        }

        // header
        int nameX = pad + Ui.Px(20);
        int shareX = right - Ui.Px(62) - Ui.Px(80) - Ui.Px(64);
        int sizeX = right - Ui.Px(80) - Ui.Px(64);
        int filesX = right - Ui.Px(64);

        Font hf = Ui.FB(10f);
        Ui.DrawText(g, "EXTENSION", hf, Ui.Dimmer,
            new Rectangle(nameX, StripTop + StripH, shareX - nameX, HeadH - StripTop - StripH), Ui.LeftFlags);
        Ui.DrawText(g, "SHARE", hf, Ui.Dimmer,
            new Rectangle(shareX, StripTop + StripH, Ui.Px(54), HeadH - StripTop - StripH), Ui.RightFlags);
        Ui.DrawText(g, "SIZE", hf, Ui.Dimmer,
            new Rectangle(sizeX, StripTop + StripH, Ui.Px(72), HeadH - StripTop - StripH), Ui.RightFlags);
        Ui.DrawText(g, "FILES", hf, Ui.Dimmer,
            new Rectangle(filesX, StripTop + StripH, Ui.Px(56), HeadH - StripTop - StripH), Ui.RightFlags);

        g.DrawLine(Ui.PenFor(Ui.Line, 1), 0, HeadH - 1, Width, HeadH - 1);

        if (_exts.Count == 0)
        {
            Ui.DrawText(g, "No extensions yet", Ui.F(12f), Ui.Dimmer,
                new Rectangle(0, HeadH, Width, PageH), Ui.CenterFlags);
            return;
        }

        g.SetClip(new Rectangle(0, HeadH, Width, PageH));

        int first = Math.Max(0, _scroll / RowH);
        int last = Math.Min(_exts.Count - 1, (_scroll + PageH) / RowH);
        long maxSize = Math.Max(1, _exts[0].Size);

        Font ef = Ui.F(11.5f);
        Font sf = Ui.F(11.5f);

        for (int i = first; i <= last; i++)
        {
            ExtStat x2 = _exts[i];
            int y = HeadH + i * RowH - _scroll;
            Rectangle rr = new Rectangle(0, y, Width, RowH);

            if (x2.Ext == _isolated) g.FillRectangle(Ui.BrushFor(Ui.Sel), rr);
            else if (x2.Ext == _hover) g.FillRectangle(Ui.BrushFor(Ui.RowHover), rr);

            g.DrawLine(Ui.PenFor(Ui.RowLine, 1), 0, y + RowH - 1, Width, y + RowH - 1);

            Ui.Rounded(g, new Rectangle(pad, y + (RowH - Ui.Px(10)) / 2, Ui.Px(10), Ui.Px(10)),
                Ui.Px(2), Ui.ExtColor(x2.Ext));

            Ui.DrawText(g, x2.Ext, ef, Ui.C("#cfcfda"),
                new Rectangle(nameX, y, Math.Max(0, shareX - nameX - Ui.Px(8)), RowH), Ui.LeftFlags);

            // Share column doubles as a heat bar, scaled against the biggest
            // extension. Anything too small to read as a bar is skipped rather
            // than drawn as a one-pixel sliver that looks like a rendering bug.
            double share = (double)x2.Size / _total * 100.0;
            int barW = (int)(Ui.Px(54) * ((double)x2.Size / maxSize));
            if (barW >= Ui.Px(9))
                Ui.Rounded(g, new RectangleF(shareX + Ui.Px(54) - barW, y + Ui.Px(4), barW, RowH - Ui.Px(8)),
                    Ui.Px(2), x2.Ext == _isolated ? Ui.C("#3d5f9e") : Ui.C("#3a3a47"));

            Ui.DrawText(g, Ui.Pct(share), sf, Ui.C("#b4b4c2"),
                new Rectangle(shareX, y, Ui.Px(54), RowH), Ui.RightFlags);

            Ui.DrawText(g, Ui.Fmt(x2.Size), sf, Ui.C("#cfcfda"),
                new Rectangle(sizeX, y, Ui.Px(72), RowH), Ui.RightFlags);

            Ui.DrawText(g, Ui.Num(x2.Count), sf, Ui.C("#a9a9b8"),
                new Rectangle(filesX, y, Ui.Px(56), RowH), Ui.RightFlags);
        }

        g.ResetClip();
        ScrollBar.Draw(g, ScrollBar.Track(this), _scroll, ContentH, PageH);
    }

    private int IndexAt(Point p)
    {
        if (p.Y < HeadH) return -1;
        int y = p.Y - HeadH + _scroll;
        int i = y / RowH;
        return (i >= 0 && i < _exts.Count) ? i : -1;
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
        string ext = i >= 0 ? _exts[i].Ext : null;
        if (ext != _hover) { _hover = ext; Invalidate(); }
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
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

        _isolated = _isolated == _exts[i].Ext ? null : _exts[i].Ext;
        Invalidate();
        Raise();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragBar = false;
    }

    public void ClearIsolation()
    {
        if (_isolated == null) return;
        _isolated = null;
        Invalidate();
        Raise();
    }

    public void Isolate(string ext)
    {
        _isolated = ext;
        Invalidate();
        Raise();
    }
}

// ---------------------------------------------------------------------------
// Treemap
// ---------------------------------------------------------------------------
internal sealed class TreemapView : DarkControl, IWheelTarget
{
    private struct Block
    {
        public Node Node;
        public RectangleF Rect;
        public int Depth;
    }

    private struct Outline
    {
        public RectangleF Rect;
        public string Label;
    }

    private Node _root;
    private Node _mapRoot;
    private Node _selected;
    private Node _hover;
    private string _isolated;
    private Point _mouse;
    private bool _mouseIn;
    private readonly List<Block> _blocks = new List<Block>();
    private readonly List<Outline> _outlines = new List<Outline>();
    private bool _dirty = true;

    private const int MaxBlocks = 6000;

    public event EventHandler SelectionChanged;
    public event EventHandler RootChanged;

    private int CrumbH { get { return Ui.Px(24); } }

    public TreemapView()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    public Node MapRoot { get { return _mapRoot; } }
    public Node SelectedNode { get { return _selected; } }

    public void SetRoot(Node root)
    {
        _root = root;
        _mapRoot = root;
        _selected = root;
        _dirty = true;
        Invalidate();
    }

    public void SetSelected(Node n)
    {
        _selected = n;
        Invalidate();
    }

    public void SetIsolation(string ext)
    {
        _isolated = ext;
        Invalidate();
    }

    public void DrillTo(Node n)
    {
        if (n == null) return;
        _mapRoot = n;
        _dirty = true;
        Invalidate();
        if (RootChanged != null) RootChanged(this, EventArgs.Empty);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _dirty = true;
    }

    private Rectangle MapArea
    {
        get { return new Rectangle(0, CrumbH, Width, Math.Max(0, Height - CrumbH)); }
    }

    private void Build()
    {
        _dirty = false;
        _blocks.Clear();
        _outlines.Clear();

        if (_mapRoot == null) return;

        Rectangle a = MapArea;
        if (a.Width <= 0 || a.Height <= 0) return;

        Place(_mapRoot, a.X, a.Y, a.Width, a.Height, 0);
    }

    private void Place(Node node, float x, float y, float w, float h, int depth)
    {
        if (_blocks.Count > MaxBlocks || w < 1.2f || h < 1.2f) return;

        // Folders holding a single child add a level without adding information,
        // and they hide the interesting name behind a chain of wrappers.
        while (!node.IsFile && node.Children.Count == 1) node = node.Children[0];

        bool canSplit = !node.IsFile && node.Children.Count > 0 && depth < 12 && w > Ui.Px(17) && h > Ui.Px(17);

        if (!canSplit)
        {
            Block b = new Block();
            b.Node = node;
            b.Rect = new RectangleF(x, y, w, h);
            b.Depth = depth;
            _blocks.Add(b);
            return;
        }

        List<Box> boxes = Treemap.Layout(node.Children, x, y, w, h);
        for (int i = 0; i < boxes.Count; i++)
        {
            Box bx = boxes[i];
            Place(bx.Node, bx.X + 0.5f, bx.Y + 0.5f, Math.Max(0.4f, bx.W - 1f), Math.Max(0.4f, bx.H - 1f), depth + 1);
        }

        if (depth > 0 && w > Ui.Px(54) && h > Ui.Px(30))
        {
            Outline o = new Outline();
            o.Rect = new RectangleF(x, y, w, h);
            o.Label = node.Name;
            _outlines.Add(o);
        }
    }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Bg);

        DrawCrumbs(g);

        if (_mapRoot == null)
        {
            Ui.DrawText(g, "Scan a folder to see its heatmap", Ui.F(12.5f), Ui.Dimmer, MapArea, Ui.CenterFlags);
            return;
        }

        if (_dirty) Build();

        Rectangle a = MapArea;
        g.SetClip(a);
        g.SmoothingMode = SmoothingMode.None;

        // Hoisted so the label font and its measured advance are resolved once
        // per paint rather than once per block.
        Font blockFont = Ui.F(10.5f);
        Font sizeFont = Ui.F(9.5f);
        float blockCharW = Ui.AvgCharW(blockFont);
        float chipCharW = Ui.AvgCharW(sizeFont);

        for (int i = 0; i < _blocks.Count; i++)
        {
            Block b = _blocks[i];
            if (b.Rect.Width < 1f || b.Rect.Height < 1f) continue;

            Color baseCol = Ui.ExtColor(b.Node.TopExt);
            bool dim = _isolated != null && b.Node.TopExt != _isolated;

            Color fill;
            if (dim) fill = Ui.Mix(baseCol, Ui.Bg, 0.72);
            else if (b.Node.IsFile) fill = baseCol;
            else fill = Ui.Mix(baseCol, Color.Black, 0.18);

            g.FillRectangle(Ui.BrushFor(fill), b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height);

            if (b.Node == _selected)
            {
                    g.DrawRectangle(Ui.PenFor(Color.White, 1.6f), b.Rect.X, b.Rect.Y, b.Rect.Width, b.Rect.Height);
            }

            if (b.Rect.Width > Ui.Px(62) && b.Rect.Height > Ui.Px(20))
            {
                int maxChars = (int)((b.Rect.Width - Ui.Px(8)) / blockCharW);
                if (maxChars > 2)
                {
                    Rectangle tr = new Rectangle((int)b.Rect.X + Ui.Px(4), (int)b.Rect.Y + Ui.Px(1),
                                                 (int)b.Rect.Width - Ui.Px(8), Ui.Px(16));
                    if (!dim)
                        Ui.DrawText(g, Clip(b.Node.Name, maxChars), blockFont, Ui.C("#1a1a1a"),
                            new Rectangle(tr.X + 1, tr.Y + 1, tr.Width, tr.Height), Ui.LeftFlags);
                    Ui.DrawText(g, Clip(b.Node.Name, maxChars), blockFont, dim ? Ui.C("#4e4e5a") : Color.White,
                        tr, Ui.LeftFlags);

                    if (b.Rect.Height > Ui.Px(32))
                        Ui.DrawText(g, Ui.Fmt(b.Node.Size), sizeFont, dim ? Ui.C("#43434e") : Ui.C("#d8d8e2"),
                            new Rectangle(tr.X, (int)b.Rect.Y + Ui.Px(15), tr.Width, Ui.Px(15)), Ui.LeftFlags);
                }
            }
        }

        // folder outlines and chips, drawn over the blocks
        for (int i = 0; i < _outlines.Count; i++)
        {
            Outline o = _outlines[i];
                g.DrawRectangle(Ui.PenFor(Color.FromArgb(140, 0, 0, 0), 1.4f), o.Rect.X, o.Rect.Y, o.Rect.Width, o.Rect.Height);

            float chipW = Math.Min(o.Rect.Width - Ui.Px(4),
                Math.Max(Ui.Px(34), o.Label.Length * chipCharW + Ui.Px(8)));
            RectangleF chip = new RectangleF(o.Rect.X + 1.5f, o.Rect.Y + 1.5f, chipW, Ui.Px(15));
            Ui.Rounded(g, chip, Ui.Px(3), Ui.C("#0d0d12"));

            int maxChars = (int)((chipW - Ui.Px(8)) / chipCharW);
            if (maxChars > 2)
                Ui.DrawText(g, Clip(o.Label, maxChars), sizeFont, Ui.C("#d8d8e2"),
                    new Rectangle((int)chip.X + Ui.Px(4), (int)chip.Y, (int)chip.Width - Ui.Px(6), (int)chip.Height),
                    Ui.LeftFlags);
        }

        g.ResetClip();

        if (_mouseIn && _hover != null) DrawTip(g);
    }

    private void DrawCrumbs(Graphics g)
    {
        g.FillRectangle(Ui.BrushFor(Ui.Head), 0, 0, Width, CrumbH);
        g.DrawLine(Ui.PenFor(Ui.Line, 1), 0, CrumbH - 1, Width, CrumbH - 1);

        if (_mapRoot == null) return;

        List<Node> chain = new List<Node>();
        Node n = _mapRoot;
        while (n != null) { chain.Insert(0, n); n = n.Parent; }

        int x = Ui.Px(12);
        Font f = Ui.F(11.5f);

        for (int i = 0; i < chain.Count; i++)
        {
            if (x > Width - Ui.Px(20)) break;

            if (i > 0)
            {
                Ui.DrawText(g, "\u203A", f, Ui.C("#45454f"),
                    new Rectangle(x, 0, Ui.Px(10), CrumbH), Ui.LeftFlags);
                x += Ui.Px(11);
            }

            bool last = i == chain.Count - 1;
            Color c = last ? Ui.C("#d6d6e0") : Ui.Dim;
            Size sz = TextRenderer.MeasureText(chain[i].Name, f, Size.Empty, TextFormatFlags.NoPadding);
            Ui.DrawText(g, chain[i].Name, f, c, new Rectangle(x, 0, sz.Width + Ui.Px(4), CrumbH), Ui.LeftFlags);
            x += sz.Width + Ui.Px(4);
        }
    }

    private static string Clip(string s, int max)
    {
        if (max < 3) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "\u2026";
    }

    private void DrawTip(Graphics g)
    {
        Node n = _hover;
        string ext = n.IsFile ? PathUtil.ExtOf(n.Name) : n.TopExt;
        string line2 = Ui.Fmt(n.Size) + "  \u00B7  " + Ui.Pct(n.Size * 100.0 / Math.Max(1, _root.Size)) +
                       " of scan  \u00B7  " + (n.IsFile ? "file" : Ui.Num(n.Items) + " items") +
                       "  \u00B7  " + Ui.TypeOf(ext, n.IsFile);

        Font nf = Ui.FB(12f);
        Font pf = Ui.MF(10.5f);
        Font sf = Ui.F(11f);

        int maxW = Ui.Px(420);
        Size ns = TextRenderer.MeasureText(n.Name, nf, new Size(maxW, 0), TextFormatFlags.WordBreak);
        Size ps = TextRenderer.MeasureText(n.Path, pf, new Size(maxW, 0), TextFormatFlags.WordBreak);
        Size ss = TextRenderer.MeasureText(line2, sf, new Size(maxW, 0), TextFormatFlags.WordBreak);

        int w = Math.Min(maxW, Math.Max(ns.Width, Math.Max(ps.Width, ss.Width))) + Ui.Px(20);
        int h = ns.Height + ps.Height + ss.Height + Ui.Px(22);

        int x = _mouse.X + Ui.Px(14);
        int y = _mouse.Y + Ui.Px(14);
        if (x + w > Width - Ui.Px(4)) x = _mouse.X - w - Ui.Px(14);
        if (y + h > Height - Ui.Px(4)) y = _mouse.Y - h - Ui.Px(14);
        if (x < 0) x = 0;
        if (y < 0) y = 0;

        Rectangle box = new Rectangle(x, y, w, h);
        Ui.Rounded(g, box, Ui.Px(6), Ui.C("#0d0d11"));
        using (GraphicsPath p = Ui.RoundPath(box, Ui.Px(6)))
            g.DrawPath(Ui.PenFor(Ui.C("#3b3b48"), 1), p);

        int pad = Ui.Px(10);
        int cy = y + Ui.Px(7);
        Ui.DrawText(g, n.Name, nf, Color.White, new Rectangle(x + pad, cy, w - pad * 2, ns.Height), Ui.LeftFlags);
        cy += ns.Height + Ui.Px(2);
        Ui.DrawText(g, n.Path, pf, Ui.C("#9a9aab"), new Rectangle(x + pad, cy, w - pad * 2, ps.Height), Ui.LeftFlags);
        cy += ps.Height + Ui.Px(2);
        Ui.DrawText(g, line2, sf, Ui.C("#cfcfda"), new Rectangle(x + pad, cy, w - pad * 2, ss.Height), Ui.LeftFlags);
    }

    private Block? HitBlock(Point p)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
            if (_blocks[i].Rect.Contains(p.X, p.Y)) return _blocks[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        _mouseIn = true;

        Block? hit = HitBlock(e.Location);
        Node n = hit.HasValue ? hit.Value.Node : null;
        if (n != _hover) { _hover = n; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouseIn = false;
        if (_hover != null) { _hover = null; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.Y < CrumbH)
        {
            Node hit = CrumbAt(e.X);
            if (hit != null && hit != _mapRoot)
            {
                _mapRoot = hit;
                _dirty = true;
                Invalidate();
                if (RootChanged != null) RootChanged(this, EventArgs.Empty);
            }
            return;
        }

        Block? hitB = HitBlock(e.Location);
        if (!hitB.HasValue) return;

        Node node = hitB.Value.Node;
        _selected = node;

        if (!node.IsFile && node.Children.Count > 0)
        {
            _mapRoot = node;
            _dirty = true;
            if (RootChanged != null) RootChanged(this, EventArgs.Empty);
        }

        Invalidate();
        if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
    }

    private Node CrumbAt(int x)
    {
        if (_mapRoot == null) return null;

        List<Node> chain = new List<Node>();
        Node n = _mapRoot;
        while (n != null) { chain.Insert(0, n); n = n.Parent; }

        int cx = Ui.Px(12);
        Font f = Ui.F(11.5f);

        for (int i = 0; i < chain.Count; i++)
        {
            if (i > 0) cx += Ui.Px(11);
            Size sz = TextRenderer.MeasureText(chain[i].Name, f, Size.Empty, TextFormatFlags.NoPadding);
            if (x >= cx && x <= cx + sz.Width) return chain[i];
            cx += sz.Width + Ui.Px(4);
        }
        return null;
    }

    public void Wheel(int delta) { }
}

// ---------------------------------------------------------------------------
// Details bar
// ---------------------------------------------------------------------------
internal sealed class DetailsBar : DarkControl
{
    private Node _node;

    public void SetNode(Node n)
    {
        _node = n;
        Invalidate();
    }

    public void Clear()
    {
        _node = null;
        Invalidate();
    }

    protected override void PaintSelf(Graphics g)
    {
        g.Clear(Ui.Head);
        g.DrawLine(Ui.PenFor(Ui.Line, 1), 0, 0, Width, 0);

        if (_node == null)
        {
            Ui.DrawText(g, "Select a folder, a file or a heatmap block to see its details.",
                Ui.F(11.5f), Ui.Dimmer, new Rectangle(Ui.Px(14), 0, Width - Ui.Px(28), Height), Ui.LeftFlags);
            return;
        }

        string ext = _node.IsFile ? PathUtil.ExtOf(_node.Name) : _node.TopExt;
        double share = 100;
        if (_node.Parent != null && _node.Parent.Size > 0)
            share = _node.Size * 100.0 / _node.Parent.Size;

        int x = Ui.Px(14);

        // swatch
        Ui.Rounded(g, new Rectangle(x, (Height - Ui.Px(12)) / 2, Ui.Px(12), Ui.Px(12)), Ui.Px(3), Ui.ExtColor(ext));
        x += Ui.Px(12) + Ui.Px(12);

        // The path takes whatever is left after the fixed columns.
        int fixedW = Ui.Px(230) + Ui.Px(84) + Ui.Px(66) + Ui.Px(74) + Ui.Px(150);
        int gaps = Ui.Px(16) * 5;
        int pathW = Math.Max(Ui.Px(120), Width - x - fixedW - gaps - Ui.Px(14));

        x = Detail(g, x, "NAME", _node.Name, Ui.F(12f), Ui.TextCol, Ui.Px(230));
        x = Detail(g, x, "FULL PATH", _node.Path, Ui.MF(11f), Ui.C("#9c9cab"), pathW);
        x = Detail(g, x, "SIZE", Ui.Fmt(_node.Size), Ui.F(12f), Ui.TextCol, Ui.Px(84));
        x = Detail(g, x, "SHARE", Ui.Pct(share), Ui.F(12f), Ui.TextCol, Ui.Px(66));
        x = Detail(g, x, "ITEMS", Ui.Num(_node.Items), Ui.F(12f), Ui.TextCol, Ui.Px(74));
        Detail(g, x, "TYPE", Ui.TypeOf(ext, _node.IsFile), Ui.F(12f), Ui.TextCol, Math.Max(Ui.Px(60), Width - x - Ui.Px(14)));
    }

    private int Detail(Graphics g, int x, string key, string val, Font f, Color c, int w)
    {
        int remaining = Width - x - Ui.Px(14);
        if (w <= 0 || w > remaining) w = remaining;
        if (w < Ui.Px(24)) return x;

        Ui.DrawText(g, key, Ui.F(9.5f), Ui.Dimmer,
            new Rectangle(x, Ui.Px(7), w, Ui.Px(13)), Ui.LeftFlags);
        Ui.DrawText(g, val, f, c,
            new Rectangle(x, Ui.Px(21), w, Ui.Px(18)), Ui.LeftFlags);

        return x + w + Ui.Px(16);
    }
}

// ---------------------------------------------------------------------------
// Workspace with draggable splitters
// ---------------------------------------------------------------------------
internal sealed class BodyPanel : Panel
{
    public readonly TreeTable Tree = new TreeTable();
    public readonly ExtTable Ext = new ExtTable();
    public readonly TreemapView Map = new TreemapView();

    private double _vSplit = 0.44;   // top row height fraction
    private double _hSplit = 0.53;   // folder table width fraction
    private int _drag;               // 0 none, 1 horizontal gutter, 2 vertical gutter

    private const int Gutter = 5;

    public BodyPanel()
    {
        DoubleBuffered = true;
        BackColor = Ui.Line;
        Controls.Add(Tree);
        Controls.Add(Ext);
        Controls.Add(Map);
    }

    private int G { get { return Ui.Px(Gutter); } }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        int g = G;
        int w = Width;
        int h = Height;

        int topH = (int)((h - g) * _vSplit);
        int leftW = (int)((w - g) * _hSplit);

        Tree.SetBounds(0, 0, leftW, topH);
        Ext.SetBounds(leftW + g, 0, w - leftW - g, topH);
        Map.SetBounds(0, topH + g, w, h - topH - g);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.FillRectangle(Ui.BrushFor(Ui.Line), ClientRectangle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0318)
        {
            using (Graphics g = Graphics.FromHdc(m.WParam))
            using (PaintEventArgs pe = new PaintEventArgs(g, ClientRectangle))
                OnPaint(pe);
            return;
        }
        base.WndProc(ref m);
    }

    private Rectangle HGutter
    {
        get
        {
            int topH = (int)((Height - G) * _vSplit);
            return new Rectangle(0, topH, Width, G);
        }
    }

    private Rectangle VGutter
    {
        get
        {
            int leftW = (int)((Width - G) * _hSplit);
            return new Rectangle(leftW, 0, G, (int)((Height - G) * _vSplit));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_drag == 1)
        {
            _vSplit = Math.Max(0.15, Math.Min(0.85, (double)e.Y / Math.Max(1, Height)));
            PerformLayout();
            Invalidate();
            return;
        }
        if (_drag == 2)
        {
            _hSplit = Math.Max(0.2, Math.Min(0.8, (double)e.X / Math.Max(1, Width)));
            PerformLayout();
            Invalidate();
            return;
        }

        if (HGutter.Contains(e.Location)) Cursor = Cursors.SizeNS;
        else if (VGutter.Contains(e.Location)) Cursor = Cursors.SizeWE;
        else Cursor = Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (HGutter.Contains(e.Location)) _drag = 1;
        else if (VGutter.Contains(e.Location)) _drag = 2;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = 0;
    }
}

// ---------------------------------------------------------------------------
// Main window
// ---------------------------------------------------------------------------
internal sealed class MainForm : Form
{
    private Toolbar _bar;
    private StatsStrip _stats;
    private BodyPanel _body;
    private DetailsBar _details;

    private ScanResultData _data;

    // Folders the user excluded from the current root, and the root they were
    // chosen for. Cleared when the root changes.
    private HashSet<string> _skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string _lastRoot;
    private bool _scanning;
    private volatile bool _cancel;
    private bool _syncMode;
    private bool _closing;

    public MainForm(string startPath)
    {
        Ui.S = 1f;
        using (Graphics g = CreateGraphics()) Ui.S = Math.Max(1f, g.DpiX / 96f);
        Ui.InitFonts();

        // Built after Ui.S is known so every metric and font picks up the DPI scale.
        _bar = new Toolbar();
        _stats = new StatsStrip();
        _body = new BodyPanel();
        _details = new DetailsBar();

        Text = "DiskUsage";
        BackColor = Ui.Bg;
        ForeColor = Ui.TextCol;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(Ui.Px(1500), Ui.Px(950));
        MinimumSize = new Size(Ui.Px(1080), Ui.Px(680));
        KeyPreview = true;
        DoubleBuffered = true;

        _bar.Dock = DockStyle.Top;
        _bar.Height = Ui.Px(46);
        _stats.Dock = DockStyle.Top;
        _stats.Height = Ui.Px(56);
        _details.Dock = DockStyle.Bottom;
        _details.Height = Ui.Px(48);
        _body.Dock = DockStyle.Fill;

        Controls.Add(_body);
        Controls.Add(_details);
        Controls.Add(_stats);
        Controls.Add(_bar);

        _bar.ScanClicked += delegate { OnScanButton(); };
        _bar.PathChosen += delegate(string p)
        {
            _bar.PathText = p;
            StartScan(p);
        };
        _bar.FolderPickRequested += delegate { ChooseFolder(); };
        _bar.SkipRequested += delegate { ChooseSkip(); };

        _body.Tree.SelectionChanged += delegate
        {
            _details.SetNode(_body.Tree.SelectedNode);
            _body.Map.SetSelected(_body.Tree.SelectedNode);
        };
        _body.Ext.IsolationChanged += delegate { _body.Map.SetIsolation(_body.Ext.Isolated); };
        _body.Map.SelectionChanged += delegate
        {
            _details.SetNode(_body.Map.SelectedNode);
            _body.Tree.SelectNode(_body.Map.SelectedNode, true);
        };
        _body.Map.RootChanged += delegate
        {
            _body.Tree.SelectNode(_body.Map.MapRoot, true);
        };

        if (!string.IsNullOrEmpty(startPath)) _bar.PathText = startPath;
    }

    public void SetSyncMode() { _syncMode = true; }

    // Used by the capture modes to exercise the skip path without a mouse.
    public void SetSkip(HashSet<string> skip)
    {
        _skip = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
    }

    public void OpenSourceMenu()
    {
        _bar.OpenMenuNow();
    }

    // Exercises the interactive paths (tree selection, drill-down, extension
    // isolation) so a capture can prove they do not throw.
    public void DemoInteract()
    {
        if (_data == null || _data.Root == null) return;

        Node folder = _data.Root.Children.Count > 0 ? _data.Root.Children[0] : _data.Root;
        _body.Tree.SelectNode(folder, true);
        if (folder.Children.Count > 0) _body.Tree.SelectNode(folder.Children[0], true);

        // Isolate the extension that actually dominates the drilled folder, so
        // the capture shows a meaningful highlight instead of an all-dim map.
        string ext = folder.TopExt;
        if (string.IsNullOrEmpty(ext) && _data.Extensions.Count > 0) ext = _data.Extensions[0].Ext;
        if (!string.IsNullOrEmpty(ext)) _body.Ext.Isolate(ext);

        _body.Map.DrillTo(folder);
        _details.SetNode(folder);
        _body.Map.SetSelected(folder);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkFrame();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Windows recalculates the frame when the window is first shown, which
        // drops the attribute on some builds. Re-assert it here.
        ApplyDarkFrame();
    }

    // Asks the compositor for a dark title bar so the frame matches the app.
    private void ApplyDarkFrame()
    {
        Win32.DarkFrame(Handle);
    }

    private void OnScanButton()
    {
        if (_scanning)
        {
            _cancel = true;
            _bar.SetStatus("Cancelling\u2026");
            return;
        }

        string path = _bar.PathText.Trim().Trim('"');
        if (path.Length == 0) return;

        try { path = System.IO.Path.GetFullPath(path); }
        catch { _bar.SetStatus("Bad path"); return; }

        if (!Directory.Exists(path)) { _bar.SetStatus("Not a folder: " + path); return; }

        _bar.PathText = path;
        StartScan(path);
    }

    public void StartScan(string path)
    {
        // Exclusions belong to one root. Moving to a different root drops them
        // rather than silently carrying stale paths along.
        string root = path.TrimEnd('\\');
        if (_lastRoot != null && !string.Equals(_lastRoot, root, StringComparison.OrdinalIgnoreCase))
            _skip.Clear();
        _lastRoot = root;

        StartScanCore(path);
    }

    private void StartScanCore(string path)
    {
        if (_scanning) return;

        _scanning = true;
        _cancel = false;
        _bar.SetScanning(true);
        _bar.SetStatus("Scanning\u2026");
        _details.Clear();

        if (_syncMode) { RunScan(path); return; }

        Thread t = new Thread(delegate() { RunScan(path); });
        t.IsBackground = true;
        t.Priority = ThreadPriority.BelowNormal;
        t.Start();
    }

    private void RunScan(string path)
    {
        try
        {
            ScanOptions o = new ScanOptions();
            o.Path = path;
            o.Threads = Math.Min(Environment.ProcessorCount, 8);
            o.Skip = new HashSet<string>(_skip, StringComparer.OrdinalIgnoreCase);

            ScanResultData d = Scanner.Run(o,
                delegate(ScanProgress p) { Post(delegate { OnProgress(p); }); },
                delegate { return _cancel; });

            Post(delegate { OnScanned(d); });
        }
        catch (Exception ex)
        {
            Ui.Report(ex);
            Post(delegate
            {
                _scanning = false;
                _bar.SetScanning(false);
                _bar.SetStatus("Scan failed: " + ex.Message);
            });
        }
    }

    // Pick a folder with the shell dialog, then scan it.
    private void ChooseFolder()
    {
        using (FolderBrowserDialog dlg = new FolderBrowserDialog())
        {
            dlg.Description = "Folder to scan";
            dlg.ShowNewFolderButton = false;

            string cur = _bar.PathText.Trim().Trim('"');
            if (Directory.Exists(cur)) dlg.SelectedPath = cur;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _bar.PathText = dlg.SelectedPath;
            StartScan(dlg.SelectedPath);
        }
    }

    // Choose which subfolders of the current root are left out, then rescan.
    private void ChooseSkip()
    {
        string path = _bar.PathText.Trim().Trim('"');
        if (path.Length == 0) { _bar.SetStatus("Pick a folder first"); return; }

        try { path = System.IO.Path.GetFullPath(path); }
        catch { _bar.SetStatus("Bad path"); return; }

        if (!Directory.Exists(path)) { _bar.SetStatus("Not a folder: " + path); return; }

        using (ScopeForm f = new ScopeForm(path, _skip))
        {
            if (f.ShowDialog(this) != DialogResult.OK) return;
            _skip = f.Skip;
        }

        _lastRoot = path.TrimEnd('\\');
        _bar.PathText = path;
        StartScanCore(path);
    }

    private void Post(Action a)
    {
        if (_syncMode) { a(); return; }
        if (_closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void OnProgress(ScanProgress p)
    {
        if (!_scanning) return;
        _bar.SetStatus(Ui.Num(p.Files) + " files \u00B7 " + Ui.Num(p.Folders) + " folders");
    }

    private void OnScanned(ScanResultData d)
    {
        _scanning = false;
        _bar.SetScanning(false);

        if (d.Cancelled || d.Root == null)
        {
            _bar.SetStatus("Cancelled");
            return;
        }

        _data = d;
        _bar.SetDrive(DriveOf(d.Path));

        string status = Ui.Num(d.FileCount) + " files \u00B7 " +
                        d.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s \u00B7 " +
                        d.Threads + " threads";
        if (d.SkippedFolders > 0) status += " \u00B7 " + Ui.Num(d.SkippedFolders) + " skipped";
        _bar.SetStatus(status);

        _stats.SetData(d);
        _body.Ext.SetData(d.Extensions, d.TotalSize);
        _body.Map.SetIsolation(null);
        _body.Map.SetRoot(d.Root);
        _body.Tree.SetRoot(d.Root);
        _details.SetNode(d.Root);
        _body.Map.SetSelected(d.Root);
    }

    private static string DriveOf(string path)
    {
        try
        {
            string root = System.IO.Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? "" : root.TrimEnd('\\');
        }
        catch { return ""; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.F5) { OnScanButton(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { _body.Ext.ClearIsolation(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.O) { _bar.FocusPath(); e.Handled = true; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _cancel = true;
        base.OnFormClosing(e);
    }
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------
internal static class GuiProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        try { Win32.SetProcessDPIAware(); } catch { }

        string shot = null;
        string path = null;
        bool demo = false;
        bool scope = false;
        bool menu = false;
        List<string> skipPaths = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--shot" && i + 1 < args.Length) shot = args[++i];
            else if (a == "--demo") demo = true;
            else if (a == "--scope") scope = true;
            else if (a == "--menu") menu = true;
            else if (a == "--skip" && i + 1 < args.Length)
            {
                string[] parts = args[++i].Split(';');
                for (int k = 0; k < parts.Length; k++)
                    if (parts[k].Trim().Length > 0) skipPaths.Add(parts[k].Trim());
            }
            else if (a == "--help" || a == "/?")
            {
                MessageBox.Show(
                    "DiskUsage\r\n\r\nUsage:\r\n  diskusage-gui.exe [folder]\r\n\r\n" +
                    "Scans a folder and shows a treemap, a folder table and a per-extension breakdown.",
                    "DiskUsage", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            else if (!a.StartsWith("-")) path = a;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.AddMessageFilter(new WheelRouter());

        if (path == null)
        {
            // Double-clicked from Explorer: start on the first local drive and
            // scan it straight away, the way the drive dropdown does.
            path = Drives.DefaultRoot();
        }

        if (shot != null) return CaptureMode(path, shot, demo, scope, menu, skipPaths);

        using (MainForm f = new MainForm(path))
        {
            if (path != null)
            {
                MainForm form = f;
                string root = path;
                f.BeginInvoke((Action)delegate { form.StartScan(root); });
            }
            Application.Run(f);
        }
        return 0;
    }

    // Renders the window to a PNG without a visible message loop. Used to check
    // the layout from a terminal, and handy for docs screenshots.
    private static int CaptureMode(string path, string file, bool demo, bool scope, bool menu,
                                   List<string> skipPaths)
    {
        HashSet<string> skip = new HashSet<string>(skipPaths, StringComparer.OrdinalIgnoreCase);

        if (scope)
        {
            using (ScopeForm sf = new ScopeForm(path, skip))
            {
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(Ui.Px(40), Ui.Px(40));
                sf.TopMost = true;
                sf.Show();
                Application.DoEvents();
                Thread.Sleep(400);
                Application.DoEvents();
                Grab(sf.Bounds, file);
                sf.Close();
            }
            return 0;
        }

        MainForm f = new MainForm(path);
        f.SetSyncMode();
        if (skip.Count > 0) f.SetSkip(skip);

        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        int w = Math.Min(Ui.Px(1500), wa.Width - Ui.Px(40));
        int h = Math.Min(Ui.Px(950), wa.Height - Ui.Px(40));
        f.Size = new Size(w, h);
        f.Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2);

        f.TopMost = true;
        f.Show();
        f.Activate();
        Application.DoEvents();

        if (path != null) f.StartScan(path);
        Application.DoEvents();

        if (demo) { f.DemoInteract(); Application.DoEvents(); }

        if (menu)
        {
            f.OpenSourceMenu();
            Application.DoEvents();
        }

        Thread.Sleep(700);
        Application.DoEvents();
        f.Refresh();
        Application.DoEvents();

        // The dropdown is a separate top-level window, so capture the whole
        // working area when it is open or it gets cut off.
        Rectangle grab = menu ? wa : f.Bounds;
        Grab(grab, file);

        f.Close();
        return 0;
    }

    private static void Grab(Rectangle b, string file)
    {
        try
        {
            using (Bitmap bmp = new Bitmap(b.Width, b.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                bmp.Save(file, ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("capture failed: " + ex.Message);
        }
    }
}
