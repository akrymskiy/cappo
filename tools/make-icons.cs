#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property OutputType=Exe
#:property PublishAot=false

// Renders CapPo's app icon and MSIX logo assets from vector shapes (the same tool as Firmly's).
//   dotnet run tools/make-icons.cs            -> writes src/CapPo.App/Assets
//   dotnet run tools/make-icons.cs -- <dir>   -> writes a preview sheet and assets to <dir> instead
//
// The icon is Wi-Fi arcs on a blue tile with a green check: signed in to the network. Below 32 px the check is
// dropped to keep the arcs crisp.

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

    // MSIX logos: canvas size, icon size.
    WritePng(Path.Combine(outDir, "Square44x44Logo.scale-200.png"), 88, 88, 84);
    WritePng(Path.Combine(outDir, "Square44x44Logo.targetsize-24_altform-unplated.png"), 24, 24, 24);
    WritePng(Path.Combine(outDir, "Square44x44Logo.targetsize-48_altform-lightunplated.png"), 48, 48, 48);
    WritePng(Path.Combine(outDir, "Square150x150Logo.scale-200.png"), 300, 300, 192);
    WritePng(Path.Combine(outDir, "Wide310x150Logo.scale-200.png"), 620, 300, 192);
    WritePng(Path.Combine(outDir, "SplashScreen.scale-200.png"), 1240, 600, 320);
    WritePng(Path.Combine(outDir, "LockScreenLogo.scale-200.png"), 48, 48, 48);
    WritePng(Path.Combine(outDir, "StoreLogo.png"), 50, 50, 50);

    if (preview)
        WritePreview(Path.Combine(outDir, "preview.png"));

    Console.WriteLine($"Wrote icons to {outDir}");
}

static void DrawIcon(DrawingContext dc, double x, double y, double size)
{
    var detailed = size >= 32;
    dc.PushTransform(new TranslateTransform(x, y));
    dc.PushTransform(new ScaleTransform(size / 256, size / 256));

    // Soft contact shadow, then the tile.
    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)), null, new Rect(20, 30, 216, 216), 52, 52);
    dc.DrawRoundedRectangle(Gradient("#3A8DF0", "#1B4FA8"), new Pen(Gradient("#7DB7FF", "#163F86"), 3), new Rect(16, 16, 224, 224), 52, 52);

    // Wi-Fi: a dot and three arcs, 45° either side of straight up.
    var white = new SolidColorBrush(Hex("#FFFFFF"));
    var center = new Point(detailed ? 120 : 128, detailed ? 186 : 194);
    var stroke = detailed ? 22.0 : 30.0;
    double[] radii = detailed ? [40, 76, 112] : [50, 98];
    foreach (var radius in radii)
    {
        var start = new Point(center.X - (radius * Math.Sin(Math.PI / 4)), center.Y - (radius * Math.Cos(Math.PI / 4)));
        var end = new Point(center.X + (radius * Math.Sin(Math.PI / 4)), start.Y);
        var arc = new PathGeometry([new PathFigure(start, [new ArcSegment(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true)], false)]);
        dc.DrawGeometry(null, new Pen(white, stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
    }
    dc.DrawEllipse(white, null, center, stroke * 0.85, stroke * 0.85);

    if (detailed)
    {
        // The check: signed in. A ring the tile's colour keeps it apart from the arcs.
        var badge = new Point(190, 186);
        dc.DrawEllipse(new SolidColorBrush(Hex("#1D56B5")), null, badge, 44, 44);
        dc.DrawEllipse(Gradient("#4BE39A", "#13A35E"), null, badge, 34, 34);
        var check = new PathGeometry([new PathFigure(new Point(172, 187), [
            new LineSegment(new Point(185, 200), true),
            new LineSegment(new Point(208, 174), true)], false)]);
        dc.DrawGeometry(null, new Pen(white, 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, check);
    }

    dc.Pop();
    dc.Pop();
}

static void WritePng(string path, int width, int height, double iconSize)
{
    var bitmap = Render(width, height, dc => DrawIcon(dc, (width - iconSize) / 2, (height - iconSize) / 2, iconSize));
    using var file = File.Create(path);
    Encode(bitmap, file);
}

// ICO with PNG-compressed entries, which Windows Vista and later read at every size.
static void WriteIco(string path, int[] sizes)
{
    var images = sizes.Select(s =>
    {
        using var ms = new MemoryStream();
        Encode(Render(s, s, dc => DrawIcon(dc, 0, 0, s)), ms);
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

static void WritePreview(string path)
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    var width = sizes.Sum() + (24 * (sizes.Length + 1));
    var rowHeight = 256 + 48;
    var bitmap = Render(width, rowHeight * 2, dc =>
    {
        foreach (var (row, bg) in new[] { (0, "#F3F3F3"), (1, "#202020") })
        {
            dc.DrawRectangle(new SolidColorBrush(Hex(bg)), null, new Rect(0, row * rowHeight, width, rowHeight));
            double x = 24;
            foreach (var s in sizes)
            {
                var small = Render(s, s, c => DrawIcon(c, 0, 0, s));
                dc.DrawImage(small, new Rect(x, (row * rowHeight) + 24 + (256 - s), s, s));
                x += s + 24;
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

static LinearGradientBrush Gradient(string top, string bottom) =>
    new(Hex(top), Hex(bottom), new Point(0, 0), new Point(0, 1));

static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
