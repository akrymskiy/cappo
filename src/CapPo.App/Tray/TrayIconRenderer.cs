using System.Globalization;

namespace CapPo.App.Tray;

internal enum TrayGlyph
{
    /// <summary>Online: the hat and the Wi-Fi.</summary>
    Online,
    /// <summary>A portal wants a sign-in: a feather in the hat.</summary>
    Portal,
    /// <summary>Connected but no internet: the arc faded.</summary>
    Offline,
    /// <summary>Not connected to any network: all faded.</summary>
    Disconnected,
    /// <summary>Not checked yet, or a Store trial has ended: all faded.</summary>
    Idle,
}

/// <summary>
/// Draws the tray icon at any size as straight-alpha BGRA pixels (0xAARRGGBB, rows from the top), the way Firmly's
/// battery icon is drawn: each pixel sampled 4×4 times against shapes designed on a 16×16 grid.
/// </summary>
/// <remarks>
/// The icon is a capo's fedora, cocked, over the bottom of Windows' Wi-Fi glyph (its dot and inner arc, as Segoe
/// Fluent Icons draws them). <c>tools/make-icons.cs</c> draws the app icon from the same shapes: keep the two in step.
/// </remarks>
internal static class TrayIconRenderer
{
    private const int Samples = 4;
    private const double Faded = 0.4;

    // The hat: crown and brim outlines (SVG path data, M/L/C/Z only), cocked 5° clockwise about (8, 5).
    internal const string CrownPath =
        "M 3.5,7.9 C 3.75,5.6 4.0,3.6 4.35,2.35 C 4.55,1.7 5.1,1.4 5.9,1.45 C 6.8,1.52 7.3,1.95 8,1.95 "
        + "C 8.7,1.95 9.2,1.52 10.1,1.45 C 10.9,1.4 11.45,1.7 11.65,2.35 C 12,3.6 12.25,5.6 12.5,7.9 Z";
    internal const string BrimPath =
        "M 0.35,6.2 C 0.6,6.05 0.95,6.05 1.3,6.2 C 3.3,6.95 5.2,7.15 8,7.15 C 10.8,7.15 12.7,6.95 14.7,6.2 "
        + "C 15.05,6.05 15.4,6.05 15.65,6.2 C 14.9,7.95 12,9.05 8,9.05 C 4,9.05 1.1,7.95 0.35,6.2 Z";
    internal const double Tilt = 5, TiltX = 8, TiltY = 5;

    // Wi-Fi as Windows draws it: a dot, and an arc centred on it, 64.7° either side of straight up, with round ends.
    internal const double DotX = 8, DotY = 14.6, DotRadius = 1.25;
    internal const double ArcRadius = 3.5, ArcWidth = 1.02, ArcHalfAngle = 64.7;

    // The feather: an ellipse turned 30° and tucked into the right of the crown (before the tilt), with a gap cut
    // around it in the hat.
    internal const double FeatherX = 12.7, FeatherY = 3.7, FeatherRx = 1.05, FeatherRy = 3.3, FeatherAngle = 30, FeatherGap = 0.65;

    private static readonly (double X, double Y)[][] Hat = [Outline(CrownPath), Outline(BrimPath)];

    /// <param name="lightTaskbar">Draw dark on light; otherwise light on dark.</param>
    public static uint[] Render(TrayGlyph glyph, int size, bool lightTaskbar)
    {
        var foreground = lightTaskbar ? 0xFF1A1A1Au : 0xFFFFFFFFu;
        var pixels = new uint[size * size];
        var scale = 16.0 / size;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                double full = 0, faded = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var x = (px + (sx + 0.5) / Samples) * scale;
                        var y = (py + (sy + 0.5) / Samples) * scale;
                        switch (Part(glyph, x, y))
                        {
                            case Ink.Full:
                                full++;
                                break;
                            case Ink.Faded:
                                faded++;
                                break;
                        }
                    }
                }

                const double total = Samples * Samples;
                pixels[(py * size) + px] = Blend(foreground, full / total, faded / total * Faded);
            }
        }
        return pixels;
    }

    private enum Ink { None, Full, Faded }

    private static Ink Part(TrayGlyph glyph, double x, double y)
    {
        var dim = glyph is TrayGlyph.Disconnected or TrayGlyph.Idle;
        if (glyph == TrayGlyph.Portal)
        {
            if (InFeather(x, y, 0))
                return Ink.Full;
            if (InFeather(x, y, FeatherGap))
                return Ink.None;
        }
        if (InHat(x, y) || InDot(x, y))
            return dim ? Ink.Faded : Ink.Full;
        if (InArc(x, y))
            return dim || glyph == TrayGlyph.Offline ? Ink.Faded : Ink.Full;
        return Ink.None;
    }

    private static bool InHat(double x, double y)
    {
        // Undo the tilt, then test the upright outlines.
        var (ux, uy) = Rotate(x, y, -Tilt, TiltX, TiltY);
        return Hat.Any(polygon => InPolygon(polygon, ux, uy));
    }

    private static bool InDot(double x, double y) =>
        ((x - DotX) * (x - DotX)) + ((y - DotY) * (y - DotY)) <= DotRadius * DotRadius;

    private static bool InArc(double x, double y)
    {
        var dx = x - DotX;
        var dy = DotY - y;
        var half = ArcWidth / 2;
        var angle = Math.Atan2(dx, dy);
        var limit = ArcHalfAngle * Math.PI / 180;
        if (Math.Abs(angle) <= limit && Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - ArcRadius) <= half)
            return true;
        // The round ends.
        var ex = ArcRadius * Math.Sin(limit);
        var ey = DotY - (ArcRadius * Math.Cos(limit));
        return ((Math.Abs(dx) - ex) * (Math.Abs(dx) - ex)) + ((y - ey) * (y - ey)) <= half * half;
    }

    // Inside the feather's ellipse, grown by margin.
    private static bool InFeather(double x, double y, double margin)
    {
        var (ux, uy) = Rotate(x, y, -Tilt, TiltX, TiltY);
        var (fx, fy) = Rotate(ux - FeatherX, uy - FeatherY, -FeatherAngle, 0, 0);
        var rx = FeatherRx + margin;
        var ry = FeatherRy + margin;
        return ((fx * fx) / (rx * rx)) + ((fy * fy) / (ry * ry)) <= 1;
    }

    // Clockwise on screen (y down) by degrees about (cx, cy).
    private static (double X, double Y) Rotate(double x, double y, double degrees, double cx, double cy)
    {
        var a = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        return (cx + ((x - cx) * cos) - ((y - cy) * sin), cy + ((x - cx) * sin) + ((y - cy) * cos));
    }

    // Even-odd test against a closed polygon.
    private static bool InPolygon((double X, double Y)[] polygon, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
                inside = !inside;
        }
        return inside;
    }

    // Path data (M, L, C, Z with absolute coordinates) to a polygon, each curve cut into straight pieces.
    private static (double X, double Y)[] Outline(string path)
    {
        var tokens = path.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var points = new List<(double X, double Y)>();
        var i = 0;
        double Number() => double.Parse(tokens[i++], CultureInfo.InvariantCulture);
        while (i < tokens.Length)
        {
            switch (tokens[i++])
            {
                case "M" or "L":
                    points.Add((Number(), Number()));
                    break;
                case "C":
                    var (x0, y0) = points[^1];
                    var (x1, y1, x2, y2, x3, y3) = (Number(), Number(), Number(), Number(), Number(), Number());
                    for (var step = 1; step <= 24; step++)
                    {
                        var t = step / 24.0;
                        var u = 1 - t;
                        points.Add((
                            (u * u * u * x0) + (3 * u * u * t * x1) + (3 * u * t * t * x2) + (t * t * t * x3),
                            (u * u * u * y0) + (3 * u * u * t * y1) + (3 * u * t * t * y2) + (t * t * t * y3)));
                    }
                    break;
                case "Z":
                    break;
                default:
                    throw new FormatException($"Unexpected path command in {path}");
            }
        }
        return [.. points];
    }

    // Full and faded coverage of one colour: straight alpha, the colour unchanged.
    private static uint Blend(uint color, double full, double faded)
    {
        var alpha = Math.Min(1, full + faded);
        return alpha <= 0 ? 0 : ((uint)Math.Round(alpha * 255) << 24) | (color & 0x00FFFFFF);
    }
}
