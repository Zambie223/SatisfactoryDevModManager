#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
// Draws the Satisfactory Dev Mod Manager icon and writes sdmm.ico (16-256 px), sdmm-256.png and sdmm-512.png.
// Usage: dotnet run tools/icon/make-icon.cs -- <output folder> [--all-png] [--variant]
//   --all-png   also writes one PNG per icon size (for checking how each size looks)
//   --variant   the green variant (variant.ico, variant-256.png, variant-512.png)
// Needs the Bahnschrift font (included with Windows 10 1709 and later). The artwork is original: an industrial
// window frame with a "</>" dev mark and SDMM lettering. It is not derived from SMM's artwork.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "icon-out";
bool allPng = args.Contains("--all-png");
Directory.CreateDirectory(outDir);

bool variant = args.Contains("--variant");

var charcoal = Brush("#23272D");
var cream = Brush("#F6F1E7");
var grey = Brush("#8C949E");
// Default: orange body and accents. Variant: green body and accents.
var body = variant ? Brush("#2F5D3A") : Brush("#F39A2D");
var accent = variant ? Brush("#8BD450") : Brush("#F39A2D");
var orange = accent; // the title-bar mark, the "active" window button and the strip text
string mark = variant ? "$" : "</>";
string letters = variant ? "SBM" : "SDMM";
string line2 = variant ? "BLACK MARKET" : "DEV MOD MANAGER";
string prefix = variant ? "variant" : "sdmm";
var face = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed);
var faceSemi = new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.SemiCondensed);

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = new List<(int Size, byte[] Data)>();
foreach (var size in sizes)
{
    // Three levels of detail: the name strip is only legible from 128 px, and "SDMM" only from 32 px.
    Action<DrawingContext> draw = size >= 128 ? DrawLarge : size >= 32 ? DrawMedium : DrawSmall;
    var png = Render(size, draw);
    pngs.Add((size, png));
    if (allPng || size == 256) File.WriteAllBytes(Path.Combine(outDir, $"{prefix}-{size}.png"), png);
}
File.WriteAllBytes(Path.Combine(outDir, $"{prefix}-512.png"), Render(512, DrawLarge));
WriteIco(Path.Combine(outDir, $"{prefix}.ico"), pngs);
Console.WriteLine($"wrote {pngs.Count} icon sizes to {Path.GetFullPath(outDir)}");

// ---- drawing (design space: 256 x 256) ------------------------------------------------------------------

void DrawLarge(DrawingContext dc)
{
    var frame = new Rect(14, 24, 228, 216);
    // Body
    dc.DrawRoundedRectangle(body, null, frame, 22, 22);
    // Title bar with the mark and window buttons
    dc.DrawRoundedRectangle(charcoal, null, new Rect(14, 24, 228, 54), 22, 22);
    dc.DrawRectangle(charcoal, null, new Rect(14, 56, 228, 22));
    DrawText(dc, mark, face, 40, orange, null, 0, new Point(30, 50), TextAlignment.Left);
    for (int i = 0; i < 3; i++)
        dc.DrawRoundedRectangle(i == 2 ? orange : grey, null, new Rect(170 + i * 22, 43, 14, 14), 2, 2);
    // Lettering, cream with a charcoal outline
    DrawText(dc, letters, face, 94, cream, charcoal, 10, new Point(128, 130), TextAlignment.Center);
    // Name strip
    dc.DrawRoundedRectangle(charcoal, null, new Rect(28, 180, 200, 48), 8, 8);
    DrawText(dc, "SATISFACTORY", faceSemi, 19, orange, null, 0, new Point(128, 192), TextAlignment.Center);
    DrawText(dc, line2, faceSemi, 19, orange, null, 0, new Point(128, 214), TextAlignment.Center);
    // Outline on top so every edge gets the same thick border
    dc.DrawRoundedRectangle(null, new Pen(charcoal, 12), frame, 22, 22);
}

void DrawMedium(DrawingContext dc)
{
    // Frame, title bar and "SDMM" only; thicker strokes so edges survive downscaling.
    var frame = new Rect(12, 24, 232, 216);
    dc.DrawRoundedRectangle(body, null, frame, 24, 24);
    dc.DrawRoundedRectangle(charcoal, null, new Rect(12, 24, 232, 58), 24, 24);
    dc.DrawRectangle(charcoal, null, new Rect(12, 58, 232, 24));
    dc.DrawRectangle(orange, null, new Rect(190, 44, 28, 18));
    DrawText(dc, letters, face, variant ? 120 : 104, cream, charcoal, 16, new Point(128, 160), TextAlignment.Center);
    dc.DrawRoundedRectangle(null, new Pen(charcoal, 20), frame, 24, 24);
}

void DrawSmall(DrawingContext dc)
{
    // Same frame and title bar, bigger proportions, and only the "</>" mark: letters are unreadable this small.
    var frame = new Rect(16, 28, 224, 208);
    dc.DrawRoundedRectangle(body, null, frame, 28, 28);
    dc.DrawRoundedRectangle(charcoal, null, new Rect(16, 28, 224, 70), 28, 28);
    dc.DrawRectangle(charcoal, null, new Rect(16, 70, 224, 28));
    dc.DrawRectangle(orange, null, new Rect(170, 52, 34, 22));
    // No outline here: at 16-24 px an outline eats most of the glyph's pixels. DrawText centres on the glyph
    // bounds, so the slash's descender stays inside the frame.
    DrawText(dc, mark, face, variant ? 130 : 150, cream, null, 0, new Point(128, 166), TextAlignment.Center);
    dc.DrawRoundedRectangle(null, new Pen(charcoal, 24), frame, 28, 28);
}

/// <summary>Draws text with its baseline-centre at <paramref name="anchor"/> (vertical anchor is the middle of the cap height).</summary>
void DrawText(DrawingContext dc, string text, Typeface tf, double emSize, Brush fill, Brush? outline, double outlineWidth,
    Point anchor, TextAlignment align)
{
    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, emSize, fill, 1.0)
    {
        TextAlignment = align,
    };
    // Centre vertically on the visible glyphs, not the font's line box.
    var probe = ft.BuildGeometry(new Point(0, 0)).Bounds;
    var origin = new Point(anchor.X, anchor.Y - probe.Top - probe.Height / 2);
    var geometry = ft.BuildGeometry(origin);
    if (outline is not null)
        dc.DrawGeometry(null, new Pen(outline, outlineWidth) { LineJoin = PenLineJoin.Round }, geometry);
    dc.DrawGeometry(fill, null, geometry);
}

byte[] Render(int size, Action<DrawingContext> draw)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        draw(dc);
        dc.Pop();
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(visual);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

/// <summary>Writes a Windows .ico whose images are PNG-compressed (supported since Windows Vista).</summary>
static void WriteIco(string path, List<(int Size, byte[] Data)> images)
{
    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write(data.Length); w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
}

static SolidColorBrush Brush(string hex)
{
    var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    b.Freeze();
    return b;
}
