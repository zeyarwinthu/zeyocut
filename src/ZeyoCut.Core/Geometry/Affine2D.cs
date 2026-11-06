// SPDX-License-Identifier: MIT

namespace ZeyoCut.Core.Geometry;

// Maps (x, y) to (A*x + C*y + E, B*x + D*y + F). m * n applies n first, then m.
public readonly record struct Affine2D(double A, double B, double C, double D, double E, double F)
{
    public static readonly Affine2D Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine2D Translation(double x, double y) => new(1, 0, 0, 1, x, y);

    public static Affine2D Scaling(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    public static Affine2D Rotation(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new Affine2D(cos, sin, -sin, cos, 0, 0);
    }

    public static Affine2D operator *(Affine2D m, Affine2D n) => new(
        m.A * n.A + m.C * n.B,
        m.B * n.A + m.D * n.B,
        m.A * n.C + m.C * n.D,
        m.B * n.C + m.D * n.D,
        m.A * n.E + m.C * n.F + m.E,
        m.B * n.E + m.D * n.F + m.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    // Null when the matrix squashes everything onto a line or a point.
    public Affine2D? Inverse()
    {
        var determinant = A * D - B * C;
        if (Math.Abs(determinant) < 1e-12 || !double.IsFinite(determinant))
        {
            return null;
        }

        var a = D / determinant;
        var b = -B / determinant;
        var c = -C / determinant;
        var d = A / determinant;
        return new Affine2D(a, b, c, d, -(a * E + c * F), -(b * E + d * F));
    }
}
