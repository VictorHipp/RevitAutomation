#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitAutomation.Addin.Commands.Patinillo;

public static class PatinilloGeometry
{
    public static PatinilloRect FromOutline(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 3) throw new ArgumentException("El contorno necesita al menos 3 puntos.", nameof(points));
        double area = 0, longest = 0, ux = 0, uy = 0;
        var anchor = points[0];
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (!double.IsFinite(a.X) || !double.IsFinite(a.Y))
                throw new ArgumentException("El contorno contiene coordenadas no válidas.", nameof(points));
            area += (a.X - anchor.X) * (b.Y - anchor.Y) - (b.X - anchor.X) * (a.Y - anchor.Y);
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > longest) { longest = length; ux = dx / length; uy = dy / length; }
        }
        if (!double.IsFinite(area) || area == 0 || longest == 0)
            throw new ArgumentException("El contorno tiene área nula o no válida.", nameof(points));
        PatinilloRect Bounds(double uX, double uY)
        {
            double vX = -uY, vY = uX;
            var xs = points.Select(p => (p.X - anchor.X) * uX + (p.Y - anchor.Y) * uY).ToArray();
            var ys = points.Select(p => (p.X - anchor.X) * vX + (p.Y - anchor.Y) * vY).ToArray();
            double x = xs.Min(), y = ys.Min();
            return new PatinilloRect(anchor.X + x * uX + y * vX, anchor.Y + x * uY + y * vY,
                uX, uY, vX, vY, xs.Max() - x, ys.Max() - y);
        }
        var r = Bounds(ux, uy);
        return r.Depth > r.Width ? Bounds(-uy, ux) : r;
    }

    public static PatinilloRect FromCenter(double cx, double cy, double width, double depth)
    {
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(width) ||
            !double.IsFinite(depth) || width <= 0 || depth <= 0)
            throw new ArgumentException("El centro y las dimensiones deben ser finitos; las dimensiones, mayores que 0.");
        double w = Math.Max(width, depth), d = Math.Min(width, depth);
        return new PatinilloRect(cx - w / 2, cy - d / 2, 1, 0, 0, 1, w, d);
    }

    public static PatinilloRect FromCorners(double x1, double y1, double x2, double y2)
    {
        double w = Math.Abs(x2 - x1), d = Math.Abs(y2 - y1);
        if (!double.IsFinite(x1) || !double.IsFinite(y1) || !double.IsFinite(x2) ||
            !double.IsFinite(y2) || w <= 0 || d <= 0)
            throw new ArgumentException("Las esquinas deben definir un rectángulo de área mayor que 0.");
        if (w >= d) return new PatinilloRect(Math.Min(x1, x2), Math.Min(y1, y2), 1, 0, 0, 1, w, d);
        // The left vertical side is the initial mouth for a tall rectangle.
        return new PatinilloRect(Math.Min(x1, x2), Math.Max(y1, y2), 0, -1, 1, 0, d, w);
    }

    public static PatinilloRect WithMouthNear(PatinilloRect r, double px, double py)
    {
        double y = (px - r.OriginX) * r.VX + (py - r.OriginY) * r.VY;
        if (Math.Abs(y) <= Math.Abs(y - r.Depth)) return r;
        var origin = r.ToWorld(r.Width, r.Depth);
        return new PatinilloRect(origin.X, origin.Y, -r.UX, -r.UY, -r.VX, -r.VY, r.Width, r.Depth);
    }
}
