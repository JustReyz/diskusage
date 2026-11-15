using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// Shared look and feel: palette, DPI scale, cached fonts and brushes, number
// formatting, extension colours, and the squarified treemap layout.
internal static class Ui
{
    public static float S = 1f;      // DPI scale, 1.0 at 96 DPI

    public static readonly Color Bg = C("#16161a");
    public static readonly Color Panel = C("#1d1d22");
    public static readonly Color Panel2 = C("#22222a");
    public static readonly Color Head = C("#202027");
    public static readonly Color Thead = C("#232329");
    public static readonly Color Line = C("#2c2c36");
    public static readonly Color TextCol = C("#dcdce3");
    public static readonly Color Dim = C("#8a8a97");
    public static readonly Color Dimmer = C("#62626e");
    public static readonly Color Accent = C("#4c8dff");
    public static readonly Color Sel = C("#2a4a7f");
    public static readonly Color Red = C("#e0575b");
    public static readonly Color Amber = C("#f2a33c");
    public static readonly Color Green = C("#3fb98a");
    public static readonly Color RowHover = C("#26262f");
    public static readonly Color RowLine = C("#1f1f26");
    public static readonly Color Track = C("#2a2a33");

    public static readonly string[] Palette = new string[]
    {
        "#5b8def", "#e0575b", "#f2a33c", "#3fb98a", "#a86bd1", "#e07ab0",
        "#4bb8c4", "#c9d24a", "#8b7cf6", "#ef7d4b", "#2f9e6e", "#d9534f",
        "#7aa6e8", "#b58b3e", "#6fbf9a", "#c46a8a", "#5aa9d6", "#9aa53f",
        "#8e6fd0", "#d1785a", "#4fa3a0", "#c25b7c", "#7f9f4a", "#9c7bd0"
    };

    private static Dictionary<string, Color> _colorCache;
    private static readonly Dictionary<int, SolidBrush> _brushCache = new Dictionary<int, SolidBrush>();
    private static readonly Dictionary<long, Pen> _penCache = new Dictionary<long, Pen>();
    private static readonly Dictionary<string, Font> _fontCache = new Dictionary<string, Font>();
    // Body and emphasis are separate families rather than one family with
    // FontStyle.Bold, because Poppins ships real Medium/SemiBold weights and
    // GDI+ synthesises a fake bold when asked for a weight it does not have.
    private static string _uiFamily = "Segoe UI";
    private static string _uiStrong = "Segoe UI";
    private static string _monoFamily = "Consolas";
    private static bool _realWeights;

    public static int Px(int v) { return (int)Math.Round(v * S); }
    public static float Pxf(float v) { return v * S; }

    public static Color C(string hex)
    {
        if (_colorCache == null) _colorCache = new Dictionary<string, Color>();
        Color c;
        if (_colorCache.TryGetValue(hex, out c)) return c;
        c = ColorTranslator.FromHtml(hex);
        _colorCache[hex] = c;
        return c;
    }

    public static Color Mix(Color a, Color b, double t)
    {
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    // Brushes and pens live for the life of the process. The palette is finite,
    // so the caches top out at a few hundred objects and never need eviction.
    // Callers must NOT wrap these in using(): disposing a cached handle leaves
    // a dead GDI+ object that breaks every later paint.
    public static SolidBrush BrushFor(Color c)
    {
        int key = c.ToArgb();
        SolidBrush b;
        if (_brushCache.TryGetValue(key, out b)) return b;
        b = new SolidBrush(c);
        _brushCache[key] = b;
        return b;
    }

    public static Pen PenFor(Color c, float w)
    {
        long key = ((long)c.ToArgb() << 8) | (long)(w * 4f);
        Pen p;
        if (_penCache.TryGetValue(key, out p)) return p;
        p = new Pen(c, w);
        _penCache[key] = p;
        return p;
    }

    public static Font UiFont(float px, bool bold)
    {
        if (_realWeights) return MakeFont(bold ? _uiStrong : _uiFamily, px, FontStyle.Regular);
        return MakeFont(_uiFamily, px, bold ? FontStyle.Bold : FontStyle.Regular);
    }

    public static Font MonoFont(float px, bool bold)
    {
        return MakeFont(_monoFamily, px, bold ? FontStyle.Bold : FontStyle.Regular);
    }

    // Sizes below are design pixels at 96 DPI; these helpers apply the scale.
    public static Font F(float px) { return UiFont(Pxf(px), false); }
    public static Font FB(float px) { return UiFont(Pxf(px), true); }
    public static Font MF(float px) { return MonoFont(Pxf(px), false); }

    private static Font MakeFont(string family, float px, FontStyle style)
    {
        string key = family + "|" + px.ToString("0.0", CultureInfo.InvariantCulture) + "|" + (int)style;
        Font f;
        if (_fontCache.TryGetValue(key, out f)) return f;
        f = new Font(family, px, style, GraphicsUnit.Pixel);
        _fontCache[key] = f;
        return f;
    }

    public static void InitFonts()
    {
        bool poppins = false, poppinsMedium = false, poppinsSemi = false;
        bool segoe = false, segoeVar = false, consolas = false, cascadia = false;

        FontFamily[] fams = FontFamily.Families;
        for (int i = 0; i < fams.Length; i++)
        {
            string n = fams[i].Name;
            if (n == "Poppins") poppins = true;
            if (n == "Poppins Medium") poppinsMedium = true;
            if (n == "Poppins SemiBold") poppinsSemi = true;
            if (n == "Segoe UI") segoe = true;
            if (n == "Segoe UI Variable Text") segoeVar = true;
            if (n == "Consolas") consolas = true;
            if (n == "Cascadia Mono") cascadia = true;
            fams[i].Dispose();
        }

        string body = segoeVar ? "Segoe UI Variable Text" : (segoe ? "Segoe UI" : "Tahoma");
        string strong = body;

        if (poppins)
        {
            body = poppinsMedium ? "Poppins Medium" : "Poppins";
            strong = poppinsSemi ? "Poppins SemiBold" : body;
            _realWeights = true;
        }

        _uiFamily = body;
        _uiStrong = strong;

        _monoFamily = cascadia ? "Cascadia Mono" : (consolas ? "Consolas" : "Courier New");
    }

    // Mean advance width of a font, measured once and cached. Treemap labels are
    // clipped by character count, and the count that fits depends on the
    // typeface: Poppins runs noticeably wider than Segoe UI at the same size.
    private static readonly Dictionary<string, float> _avgCache = new Dictionary<string, float>();

    public static float AvgCharW(Font f)
    {
        string key = f.Name + "|" + f.SizeInPoints.ToString("0.00", CultureInfo.InvariantCulture);
        float w;
        if (_avgCache.TryGetValue(key, out w)) return w;

        using (Bitmap bmp = new Bitmap(1, 1))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            Size s = TextRenderer.MeasureText(g, "abcdefghijklmnopqrstuvwxyz0123456789",
                f, Size.Empty, TextFormatFlags.NoPadding);
            w = Math.Max(1f, (float)s.Width / 36f);
        }

        _avgCache[key] = w;
        return w;
    }

    public static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
    {
        TextRenderer.DrawText(g, s, f, r, c, flags);
    }

    public static void Report(Exception ex)
    {
        try
        {
            string p = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diskusage-gui-error.log");
            System.IO.File.AppendAllText(p, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "  " + ex + "\r\n\r\n");
        }
        catch { }
    }

    public const TextFormatFlags LeftFlags =
        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    public const TextFormatFlags RightFlags =
        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    public const TextFormatFlags CenterFlags =
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;

    public static string Fmt(long b)
    {
        if (b >= 1099511627776L) return (b / 1099511627776.0).ToString("0.00", CultureInfo.InvariantCulture) + " TB";
        if (b >= 1073741824L) return (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        if (b >= 1048576L) return (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (b >= 1024L) return Math.Round(b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return b.ToString(CultureInfo.InvariantCulture) + " B";
    }

    public static string Num(long n)
    {
        return n.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string Pct(double v)
    {
        return v.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    public static uint Hash(string s)
    {
        uint h = 2166136261u;
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
        return h;
    }

    public static Color ExtColor(string ext)
    {
        if (string.IsNullOrEmpty(ext) || ext == "(none)") return C("#55555f");
        return C(Palette[Hash(ext) % (uint)Palette.Length]);
    }

    public static Color ShareColor(double pct)
    {
        if (pct >= 25) return Red;
        if (pct >= 10) return Amber;
        return Green;
    }

    public static void Rounded(Graphics g, RectangleF r, float radius, Color fill)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        using (GraphicsPath p = RoundPath(r, radius))
            g.FillPath(BrushFor(fill), p);
    }

    public static GraphicsPath RoundPath(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static readonly Dictionary<string, string> Types = BuildTypes();

    private static Dictionary<string, string> BuildTypes()
    {
        Dictionary<string, string> t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        t[".dll"] = "Application extension"; t[".exe"] = "Application"; t[".sys"] = "System file";
        t[".zip"] = "Compressed archive"; t[".rar"] = "Compressed archive"; t[".7z"] = "Compressed archive";
        t[".png"] = "PNG image"; t[".jpg"] = "JPEG image"; t[".jpeg"] = "JPEG image";
        t[".webp"] = "WebP image"; t[".gif"] = "GIF image"; t[".svg"] = "SVG image";
        t[".mp4"] = "MP4 video"; t[".mkv"] = "Matroska video"; t[".avi"] = "AVI video";
        t[".mov"] = "QuickTime video"; t[".mp3"] = "MP3 audio"; t[".wav"] = "Wave audio";
        t[".flac"] = "FLAC audio"; t[".ogg"] = "Ogg audio"; t[".pdf"] = "PDF document";
        t[".doc"] = "Word document"; t[".docx"] = "Word document"; t[".xls"] = "Excel sheet";
        t[".xlsx"] = "Excel sheet"; t[".ppt"] = "PowerPoint"; t[".pptx"] = "PowerPoint";
        t[".txt"] = "Text file"; t[".md"] = "Markdown"; t[".json"] = "JSON file";
        t[".xml"] = "XML file"; t[".html"] = "HTML document"; t[".css"] = "Stylesheet";
        t[".js"] = "JavaScript"; t[".ts"] = "TypeScript"; t[".py"] = "Python source";
        t[".cs"] = "C# source"; t[".jar"] = "Java archive"; t[".pak"] = "PAK archive";
        t[".bin"] = "Binary data"; t[".dat"] = "Data file"; t[".db"] = "Database";
        t[".iso"] = "Disc image"; t[".msi"] = "Windows installer"; t[".log"] = "Log file";
        t[".ttf"] = "Font"; t[".otf"] = "Font"; t[".woff"] = "Web font"; t[".woff2"] = "Web font";
        t[".ico"] = "Icon"; t[".psd"] = "Photoshop"; t[".blend"] = "Blender scene";
        t[".unity3d"] = "Unity asset"; t[".uasset"] = "Unreal asset"; t[".umap"] = "Unreal map";
        t[".bk2"] = "Bink video"; t[".pck"] = "Godot package"; t[".vpk"] = "Valve package";
        t[".bsa"] = "Bethesda archive"; t[".esm"] = "Bethesda plugin"; t[".esp"] = "Bethesda plugin";
        t[".nif"] = "NetImmerse mesh"; t[".dds"] = "DirectDraw surface"; t[".tga"] = "Targa image";
        t[".bmp"] = "Bitmap image"; t[".csv"] = "CSV data"; t[".ini"] = "Configuration";
        t[".cfg"] = "Configuration"; t[".conf"] = "Configuration"; t[".yml"] = "YAML";
        t[".yaml"] = "YAML"; t[".toml"] = "TOML"; t[".sh"] = "Shell script"; t[".bat"] = "Batch file";
        t[".ps1"] = "PowerShell"; t[".apk"] = "Android package"; t[".ipa"] = "iOS package";
        t[".dmg"] = "Disk image"; t[".pdb"] = "Debug symbols"; t[".lib"] = "Static library";
        t[".obj"] = "Object file"; t[".a"] = "Static library"; t[".so"] = "Shared object";
        t[".dylib"] = "Dynamic library"; t[".chk"] = "Game data chunk";
        return t;
    }

    public static string TypeOf(string ext, bool isFile)
    {
        if (!isFile) return "Folder";
        if (string.IsNullOrEmpty(ext)) return "File";
        string t;
        if (Types.TryGetValue(ext, out t)) return t;
        if (ext == "(none)") return "No extension";
        string bare = ext.Substring(1).ToUpperInvariant();
        return bare.Length <= 5 ? bare + " file" : "File";
    }
}

// One placed treemap block.
internal struct Box
{
    public Node Node;
    public float X, Y, W, H;
}

internal static class Treemap
{
    private struct Item
    {
        public Node Node;
        public double Area;
    }

    public static List<Box> Layout(List<Node> kids, float x, float y, float w, float h)
    {
        List<Box> outp = new List<Box>(kids.Count);

        double sum = 0;
        for (int i = 0; i < kids.Count; i++) sum += kids[i].Size;
        if (sum <= 0 || w <= 0 || h <= 0) return outp;

        double area = (double)w * h;
        List<Item> items = new List<Item>(kids.Count);
        for (int i = 0; i < kids.Count; i++)
        {
            if (kids[i].Size <= 0) continue;
            items.Add(new Item { Node = kids[i], Area = kids[i].Size / sum * area });
        }

        List<Item> row = new List<Item>();
        double rowSum = 0;

        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];
            if (row.Count == 0) { row.Add(it); rowSum += it.Area; continue; }

            double side = Math.Min(w, h);
            row.Add(it);
            double withIt = Worst(row, side);
            row.RemoveAt(row.Count - 1);

            if (withIt <= Worst(row, side))
            {
                row.Add(it);
                rowSum += it.Area;
            }
            else
            {
                Flush(row, ref rowSum, outp, ref x, ref y, ref w, ref h);
                row.Add(it);
                rowSum = it.Area;
            }
        }

        Flush(row, ref rowSum, outp, ref x, ref y, ref w, ref h);
        return outp;
    }

    private static double Worst(List<Item> row, double side)
    {
        double sum = 0, max = 0, min = double.MaxValue;
        for (int i = 0; i < row.Count; i++)
        {
            double a = row[i].Area;
            sum += a;
            if (a > max) max = a;
            if (a < min) min = a;
        }
        if (sum <= 0 || min <= 0) return double.MaxValue;
        return Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
    }

    private static void Flush(List<Item> row, ref double rowSum, List<Box> outp,
                              ref float x, ref float y, ref float w, ref float h)
    {
        if (row.Count == 0) return;

        if (w >= h)
        {
            double cw = rowSum / h;
            double cy = y;
            for (int i = 0; i < row.Count; i++)
            {
                double ch = row[i].Area / cw;
                outp.Add(new Box { Node = row[i].Node, X = x, Y = (float)cy, W = (float)cw, H = (float)ch });
                cy += ch;
            }
            x += (float)cw;
            w -= (float)cw;
        }
        else
        {
            double chh = rowSum / w;
            double cx = x;
            for (int i = 0; i < row.Count; i++)
            {
                double cw2 = row[i].Area / chh;
                outp.Add(new Box { Node = row[i].Node, X = (float)cx, Y = y, W = (float)cw2, H = (float)chh });
                cx += cw2;
            }
            y += (float)chh;
            h -= (float)chh;
        }

        row.Clear();
        rowSum = 0;
    }
}
