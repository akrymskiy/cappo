namespace CapPo.App.Tray;

internal enum TrayGlyph
{
    /// <summary>Online: plain Wi-Fi arcs.</summary>
    Online,
    /// <summary>A portal wants a sign-in: an amber badge with "!".</summary>
    Portal,
    /// <summary>Connected but no internet: outer arcs faded.</summary>
    Offline,
    /// <summary>Not connected to any network: all faded, with a cross.</summary>
    Disconnected,
    /// <summary>Not checked yet, or a Store trial has ended: all faded.</summary>
    Idle,
}

/// <summary>
/// Draws the tray icon at any size as straight-alpha BGRA pixels (0xAARRGGBB, rows from the top), the way Firmly's
/// battery icon is drawn: each pixel sampled 4×4 times against a few shapes designed on a 16×16 grid.
/// </summary>
internal static class TrayIconRenderer
{
    private const int Samples = 4;

    // Wi-Fi: a dot and three arcs around a point near the bottom, within 45° either side of straight up.
    private const double CenterX = 8, CenterY = 13.25;
    private static readonly (double Inner, double Outer)[] Arcs = [(3.5, 5.25), (6.75, 8.5), (10, 11.75)];
    private const double DotRadius = 1.6;
    private const double HalfAngle = Math.PI / 4;

    // The badge, bottom right, with a cleared ring around it so it reads over the arcs.
    private const double BadgeX = 12.25, BadgeY = 12.25, BadgeRadius = 3.6, Halo = 1.1;

    /// <param name="lightTaskbar">Draw dark on light; otherwise light on dark.</param>
    public static uint[] Render(TrayGlyph glyph, int size, bool lightTaskbar)
    {
        var foreground = lightTaskbar ? 0xFF1A1A1Au : 0xFFFFFFFFu;
        var amber = lightTaskbar ? 0xFFB86E00u : 0xFFFFC233u;
        var mark = 0xFF1A1A1Au;
        var badge = glyph == TrayGlyph.Portal;
        var cross = glyph == TrayGlyph.Disconnected;

        var pixels = new uint[size * size];
        var scale = 16.0 / size;
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                double arcs = 0, faded = 0, badgeFill = 0, markFill = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var x = (px + (sx + 0.5) / Samples) * scale;
                        var y = (py + (sy + 0.5) / Samples) * scale;

                        if (badge && InCircle(x, y, BadgeX, BadgeY, BadgeRadius))
                        {
                            if (InExclamation(x, y))
                                markFill++;
                            else
                                badgeFill++;
                            continue;
                        }
                        if (badge && InCircle(x, y, BadgeX, BadgeY, BadgeRadius + Halo))
                            continue;
                        if (cross && InCross(x, y))
                        {
                            arcs++;
                            continue;
                        }
                        if (cross && InCross(x, y, Halo))
                            continue;

                        var part = WifiPart(x, y);
                        if (part < 0)
                            continue;
                        // Offline keeps the dot and the inner arc; Idle and Disconnected fade everything.
                        var dim = glyph is TrayGlyph.Idle or TrayGlyph.Disconnected || (glyph == TrayGlyph.Offline && part >= 2);
                        if (dim)
                            faded++;
                        else
                            arcs++;
                    }
                }

                const double total = Samples * Samples;
                pixels[(py * size) + px] = Blend(
                    (foreground, arcs / total),
                    (foreground, faded / total * 0.4),
                    (amber, badgeFill / total),
                    (mark, markFill / total));
            }
        }
        return pixels;
    }

    // -1 outside; 0 the dot; 1..3 the arcs from the inside out.
    private static int WifiPart(double x, double y)
    {
        var dx = x - CenterX;
        var dy = CenterY - y;
        var r = Math.Sqrt((dx * dx) + (dy * dy));
        if (r <= DotRadius)
            return 0;
        if (dy <= 0 || Math.Abs(Math.Atan2(dx, dy)) > HalfAngle)
            return -1;
        for (var i = 0; i < Arcs.Length; i++)
        {
            if (r >= Arcs[i].Inner && r <= Arcs[i].Outer)
                return i + 1;
        }
        return -1;
    }

    private static bool InExclamation(double x, double y) =>
        (Math.Abs(x - BadgeX) <= 0.6 && y >= BadgeY - 2.3 && y <= BadgeY + 0.5)
        || InCircle(x, y, BadgeX, BadgeY + 1.65, 0.65);

    // An × in the badge's place: two strokes along the diagonals, widened by margin for the cleared ring.
    private static bool InCross(double x, double y, double margin = 0)
    {
        const double Arm = 2.6, HalfWidth = 0.8;
        var dx = x - BadgeX;
        var dy = y - BadgeY;
        var reach = Arm + margin;
        var width = (HalfWidth + margin) * Math.Sqrt(2);
        return Math.Abs(dx) <= reach && Math.Abs(dy) <= reach
            && (Math.Abs(dx - dy) <= width || Math.Abs(dx + dy) <= width);
    }

    private static bool InCircle(double x, double y, double cx, double cy, double radius) =>
        ((x - cx) * (x - cx)) + ((y - cy) * (y - cy)) <= radius * radius;

    // The layers cover separate samples, so their colours add up, weighted by coverage.
    private static uint Blend(params (uint Color, double Coverage)[] layers)
    {
        double a = 0, r = 0, g = 0, b = 0;
        foreach (var (color, coverage) in layers)
        {
            if (coverage <= 0)
                continue;
            a += coverage;
            r += ((color >> 16) & 0xFF) * coverage;
            g += ((color >> 8) & 0xFF) * coverage;
            b += (color & 0xFF) * coverage;
        }
        if (a <= 0)
            return 0;
        var alpha = Math.Min(1, a);
        return ((uint)Math.Round(alpha * 255) << 24)
            | ((uint)Math.Round(r / a) << 16)
            | ((uint)Math.Round(g / a) << 8)
            | (uint)Math.Round(b / a);
    }
}
