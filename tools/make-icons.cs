#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false

// Renders CapPo's app icon and MSIX logo assets from vector shapes (the same tool as Firmly's).
//   dotnet run tools/make-icons.cs            -> writes src/CapPo.App/Assets
//   dotnet run tools/make-icons.cs -- <dir>   -> writes a preview sheet and assets to <dir> instead
//
// The icon is a capo's fedora, cocked, over the bottom of Windows' Wi-Fi glyph: its dot and inner arc. It's the tray
// icon's shape (src/CapPo.App/Tray/TrayIconRenderer.cs), in black and white with no tile:
//   - black on light and white on dark where Windows picks by theme (the MSIX "unplated" taskbar and Start icons);
//   - black with a white outline everywhere else (the .ico and the other logos), which holds up on either.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("src/CapPo.App/Assets");
var preview = args.Length > 0;

var thread = new Thread(() => Run(outDir, preview));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static void Run(string outDir, bool preview)
{
    Directory.CreateDirectory(outDir);

    WriteIco(Path.Combine(outDir, "AppIcon.ico"), [16, 20, 24, 32, 40, 48, 64, 128, 256]);

    // MSIX logos: canvas size, icon size, style.
    WritePng(Path.Combine(outDir, "Square44x44Logo.scale-200.png"), 88, 88, 84, Style.Outlined);
    foreach (var size in new[] { 16, 24, 32, 48, 256 })
    {
        WritePng(Path.Combine(outDir, $"Square44x44Logo.targetsize-{size}_altform-unplated.png"), size, size, size, Style.White);
        WritePng(Path.Combine(outDir, $"Square44x44Logo.targetsize-{size}_altform-lightunplated.png"), size, size, size, Style.Black);
    }
    WritePng(Path.Combine(outDir, "Square150x150Logo.scale-200.png"), 300, 300, 192, Style.Outlined);
    WritePng(Path.Combine(outDir, "Wide310x150Logo.scale-200.png"), 620, 300, 192, Style.Outlined);
    WritePng(Path.Combine(outDir, "SplashScreen.scale-200.png"), 1240, 600, 320, Style.Outlined);
    // The lock screen shows its badge logo in white.
    WritePng(Path.Combine(outDir, "LockScreenLogo.scale-200.png"), 48, 48, 48, Style.White);
    WritePng(Path.Combine(outDir, "StoreLogo.png"), 50, 50, 50, Style.Outlined);

    if (preview)
        WritePreview(Path.Combine(outDir, "preview.png"));

    Console.WriteLine($"Wrote icons to {outDir}");
}

static void DrawIcon(DrawingContext dc, double x, double y, double size, Style style)
{
    // The glyph is designed on a 16x16 grid; it fills the icon but for room for the outline.
    const double Scale = 14.5;
    dc.PushTransform(new TranslateTransform(x, y));
    dc.PushTransform(new ScaleTransform(size / 256, size / 256));
    dc.PushTransform(new TranslateTransform(128 - (8 * Scale), 128 - (8 * Scale)));
    dc.PushTransform(new ScaleTransform(Scale, Scale));

    var glyph = Glyph();
    var black = new SolidColorBrush(Hex("#141414"));
    switch (style)
    {
        case Style.Black:
            dc.DrawGeometry(black, null, glyph);
            break;
        case Style.White:
            dc.DrawGeometry(Brushes.White, null, glyph);
            break;
        default:
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.White, 0.8) { LineJoin = PenLineJoin.Round }, glyph);
            dc.DrawGeometry(black, null, glyph);
            break;
    }

    dc.Pop();
    dc.Pop();
    dc.Pop();
    dc.Pop();
}

// The tray icon's shapes, as in TrayIconRenderer: crown and brim cocked 5 degrees about (8, 5); a dot, and an arc around
// it 64.7 degrees either side of straight up, 1.02 wide, with round ends.
static Geometry Glyph()
{
    const string Crown = "M 3.5,7.9 C 3.75,5.6 4.0,3.6 4.35,2.35 C 4.55,1.7 5.1,1.4 5.9,1.45 C 6.8,1.52 7.3,1.95 8,1.95 "
        + "C 8.7,1.95 9.2,1.52 10.1,1.45 C 10.9,1.4 11.45,1.7 11.65,2.35 C 12,3.6 12.25,5.6 12.5,7.9 Z";
    const string Brim = "M 0.35,6.2 C 0.6,6.05 0.95,6.05 1.3,6.2 C 3.3,6.95 5.2,7.15 8,7.15 C 10.8,7.15 12.7,6.95 14.7,6.2 "
        + "C 15.05,6.05 15.4,6.05 15.65,6.2 C 14.9,7.95 12,9.05 8,9.05 C 4,9.05 1.1,7.95 0.35,6.2 Z";
    var hat = new GeometryGroup { FillRule = FillRule.Nonzero, Children = { Geometry.Parse(Crown), Geometry.Parse(Brim) } };
    hat.Transform = new RotateTransform(5, 8, 5);

    var dot = new Point(8, 14.6);
    const double Radius = 3.5, Half = 64.7 * Math.PI / 180;
    var start = new Point(dot.X - (Radius * Math.Sin(Half)), dot.Y - (Radius * Math.Cos(Half)));
    var end = new Point(dot.X + (Radius * Math.Sin(Half)), start.Y);
    var arc = new PathGeometry([new PathFigure(start, [new ArcSegment(end, new Size(Radius, Radius), 0, false, SweepDirection.Clockwise, true)], false)])
        .GetWidenedPathGeometry(new Pen(Brushes.Black, 1.02) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, 0.001, ToleranceType.Absolute);

    return new GeometryGroup { FillRule = FillRule.Nonzero, Children = { hat, new EllipseGeometry(dot, 1.25, 1.25), arc } };
}

static void WritePng(string path, int width, int height, double iconSize, Style style)
{
    var bitmap = Render(width, height, dc => DrawIcon(dc, (width - iconSize) / 2, (height - iconSize) / 2, iconSize, style));
    using var file = File.Create(path);
    Encode(bitmap, file);
}

// ICO with PNG-compressed entries, which Windows Vista and later read at every size.
static void WriteIco(string path, int[] sizes)
{
    var images = sizes.Select(s =>
    {
        using var ms = new MemoryStream();
        Encode(Render(s, s, dc => DrawIcon(dc, 0, 0, s, Style.Outlined)), ms);
        return ms.ToArray();
    }).ToArray();

    using var file = File.Create(path);
    using var w = new BinaryWriter(file);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)sizes.Length);
    var offset = 6 + (16 * sizes.Length);
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(images[i].Length);
        w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var image in images)
        w.Write(image);
}

// Each style at each size, on light and on dark.
static void WritePreview(string path)
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    Style[] styles = [Style.Outlined, Style.Black, Style.White];
    var width = sizes.Sum() + (24 * (sizes.Length + 1));
    var rowHeight = 256 + 48;
    var bitmap = Render(width, rowHeight * styles.Length * 2, dc =>
    {
        var row = 0;
        foreach (var style in styles)
        {
            foreach (var bg in new[] { "#F3F3F3", "#202020" })
            {
                dc.DrawRectangle(new SolidColorBrush(Hex(bg)), null, new Rect(0, row * rowHeight, width, rowHeight));
                double x = 24;
                foreach (var s in sizes)
                {
                    var small = Render(s, s, c => DrawIcon(c, 0, 0, s, style));
                    dc.DrawImage(small, new Rect(x, (row * rowHeight) + 24 + (256 - s), s, s));
                    x += s + 24;
                }
                row++;
            }
        }
    });
    using var file = File.Create(path);
    Encode(bitmap, file);
}

static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
{
    var visual = new DrawingVisual();
    RenderOptions.SetEdgeMode(visual, EdgeMode.Unspecified);
    using (var dc = visual.RenderOpen())
        draw(dc);
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

static void Encode(BitmapSource bitmap, Stream stream)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    encoder.Save(stream);
}

static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

enum Style
{
    /// <summary>Black with a white outline: for places that may be light or dark.</summary>
    Outlined,
    Black,
    White,
}
