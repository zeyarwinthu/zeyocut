// SPDX-License-Identifier: MIT

using SkiaSharp;
using ZeyoCut.Render;

namespace ZeyoCut.Mcp.Demo;

public static class DemoArt
{
    private static readonly SKColor Fur = new(0xF2, 0x8C, 0x28);
    private static readonly SKColor Stripe = new(0x8A, 0x45, 0x14);
    private static readonly SKColor Cream = new(0xFF, 0xE9, 0xC6);
    private static readonly SKColor Ink = new(0x3B, 0x2A, 0x1A);
    private static readonly SKColor Pink = new(0xF7, 0x9F, 0xA6);

    public static string Body(string path) => Draw(path, 220, 240, canvas =>
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(110, 16);
        builder.CubicTo(168, 16, 204, 112, 200, 166);
        builder.CubicTo(197, 210, 160, 224, 110, 224);
        builder.CubicTo(60, 224, 23, 210, 20, 166);
        builder.CubicTo(16, 112, 52, 16, 110, 16);
        builder.Close();
        using var body = builder.Detach();

        using var fur = Fill(Fur);
        using var cream = Fill(Cream);
        using var stripe = Stroke(Stripe, 9);
        using var outline = Stroke(Ink, 5);
        canvas.DrawPath(body, fur);
        canvas.Save();
        canvas.ClipPath(body, antialias: true);
        canvas.DrawOval(new SKRect(62, 96, 158, 222), cream);
        foreach (var (y1, y2) in new[] { (88f, 96f), (124f, 128f), (160f, 158f) })
        {
            canvas.DrawLine(14, y1, 46, y2, stripe);
            canvas.DrawLine(206, y1, 174, y2, stripe);
        }

        canvas.Restore();
        canvas.DrawPath(body, outline);

        using var toe = Stroke(Ink, 3);
        foreach (var left in new[] { 52f, 114f })
        {
            var foot = new SKRect(left, 200, left + 54, 234);
            canvas.DrawOval(foot, fur);
            canvas.DrawOval(foot, outline);
            canvas.DrawLine(left + 20, 222, left + 20, 230, toe);
            canvas.DrawLine(left + 34, 222, left + 34, 230, toe);
        }

        foreach (var (center, angle) in new[] { (46f, 20f), (174f, -20f) })
        {
            canvas.Save();
            canvas.RotateDegrees(angle, center, 142);
            var arm = new SKRect(center - 14, 116, center + 14, 170);
            canvas.DrawOval(arm, fur);
            canvas.DrawOval(arm, outline);
            canvas.Restore();
        }
    });

    public static string Head(string path, bool happy) => Draw(path, 240, 208, canvas =>
    {
        using var fur = Fill(Fur);
        using var cream = Fill(Cream);
        using var pink = Fill(Pink);
        using var ink = Fill(Ink);
        using var white = Fill(SKColors.White);
        using var outline = Stroke(Ink, 5);
        using var thin = Stroke(Ink, 3);
        using var stripe = Stroke(Stripe, 7);

        foreach (var mirror in new[] { false, true })
        {
            float X(float x) => mirror ? 240 - x : x;
            using var ear = Path(b =>
            {
                b.MoveTo(X(36), 96);
                b.QuadTo(X(36), 30, X(54), 12);
                b.QuadTo(X(84), 30, X(110), 52);
                b.Close();
            });
            using var inner = Path(b =>
            {
                b.MoveTo(X(52), 78);
                b.QuadTo(X(53), 44, X(60), 32);
                b.QuadTo(X(78), 46, X(90), 56);
                b.Close();
            });
            canvas.DrawPath(ear, fur);
            canvas.DrawPath(ear, outline);
            canvas.DrawPath(inner, pink);
        }

        var face = new SKRect(18, 36, 222, 200);
        using var head = Path(b => b.AddOval(face, SKPathDirection.Clockwise));
        canvas.DrawPath(head, fur);
        canvas.Save();
        canvas.ClipPath(head, antialias: true);
        canvas.DrawLine(120, 42, 120, 68, stripe);
        canvas.DrawLine(96, 46, 102, 68, stripe);
        canvas.DrawLine(144, 46, 138, 68, stripe);
        canvas.DrawLine(16, 108, 42, 114, stripe);
        canvas.DrawLine(18, 130, 42, 130, stripe);
        canvas.DrawLine(224, 108, 198, 114, stripe);
        canvas.DrawLine(222, 130, 198, 130, stripe);
        canvas.DrawOval(new SKRect(80, 126, 160, 184), cream);
        canvas.Restore();
        canvas.DrawPath(head, outline);

        using var cheek = Fill(Pink.WithAlpha(150));
        canvas.DrawOval(new SKRect(46, 136, 76, 152), cheek);
        canvas.DrawOval(new SKRect(164, 136, 194, 152), cheek);

        if (happy)
        {
            using var lid = Stroke(Ink, 6);
            using var eyes = Path(b =>
            {
                b.MoveTo(70, 120);
                b.QuadTo(84, 96, 98, 120);
                b.MoveTo(142, 120);
                b.QuadTo(156, 96, 170, 120);
            });
            canvas.DrawPath(eyes, lid);
        }
        else
        {
            canvas.DrawOval(new SKRect(72, 96, 96, 128), ink);
            canvas.DrawOval(new SKRect(144, 96, 168, 128), ink);
            canvas.DrawCircle(88, 106, 4.5f, white);
            canvas.DrawCircle(160, 106, 4.5f, white);
        }

        if (happy)
        {
            using var mouth = Path(b =>
            {
                b.MoveTo(104, 150);
                b.QuadTo(120, 184, 136, 150);
                b.Close();
            });
            using var red = Fill(new SKColor(0x8E, 0x2B, 0x2B));
            canvas.DrawPath(mouth, red);
            canvas.DrawOval(new SKRect(113, 161, 127, 171), pink);
            canvas.DrawPath(mouth, thin);
        }
        else
        {
            using var mouth = Path(b =>
            {
                b.MoveTo(120, 145);
                b.QuadTo(120, 156, 108, 158);
                b.MoveTo(120, 145);
                b.QuadTo(120, 156, 132, 158);
            });
            canvas.DrawPath(mouth, thin);
        }

        using var nose = Path(b =>
        {
            b.MoveTo(111, 134);
            b.LineTo(129, 134);
            b.LineTo(120, 145);
            b.Close();
        });
        canvas.DrawPath(nose, pink);
        canvas.DrawPath(nose, thin);

        canvas.DrawLine(52, 148, 8, 140, thin);
        canvas.DrawLine(52, 156, 10, 164, thin);
        canvas.DrawLine(188, 148, 232, 140, thin);
        canvas.DrawLine(188, 156, 230, 164, thin);
    });

    public static string Tail(string path) => Draw(path, 180, 92, canvas =>
    {
        using var tail = Path(b =>
        {
            b.MoveTo(164, 72);
            b.CubicTo(122, 84, 54, 78, 32, 22);
        });
        using var outline = Stroke(Ink, 30);
        using var fur = Stroke(Fur, 21);
        using var stripes = Stroke(Stripe, 21);
        stripes.StrokeCap = SKStrokeCap.Butt;
        stripes.PathEffect = SKPathEffect.CreateDash([8, 16], 4);
        canvas.DrawPath(tail, outline);
        canvas.DrawPath(tail, fur);
        canvas.DrawPath(tail, stripes);
        using var tip = Fill(Stripe);
        canvas.DrawCircle(32, 22, 10.5f, tip);
    });

    public static string Shadow(string path) => Draw(path, 240, 50, canvas =>
    {
        using var paint = Fill(SKColors.Black);
        paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5);
        canvas.DrawOval(new SKRect(14, 9, 226, 41), paint);
    });

    public static string Ground(string path) => Draw(path, 1280, 140, canvas =>
    {
        using var grass = new SKPaint();
        grass.IsAntialias = true;
        grass.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 10), new SKPoint(0, 140),
            [new SKColor(0x8A, 0xD1, 0x6C), new SKColor(0x5B, 0xA5, 0x4C)],
            null, SKShaderTileMode.Clamp);
        canvas.DrawRect(new SKRect(0, 10, 1280, 140), grass);
        using var edge = Stroke(new SKColor(0x4A, 0x8C, 0x3E), 6);
        canvas.DrawLine(-10, 10, 1290, 10, edge);

        using var tuft = Stroke(new SKColor(0x4A, 0x8C, 0x3E), 3);
        var random = new Random(12);
        for (var i = 0; i < 46; i++)
        {
            var x = (float)(random.NextDouble() * 1260 + 10);
            var y = (float)(random.NextDouble() * 100 + 32);
            canvas.DrawLine(x, y, x - 5, y - 10, tuft);
            canvas.DrawLine(x, y, x + 1, y - 13, tuft);
            canvas.DrawLine(x, y, x + 6, y - 9, tuft);
        }
    });

    public static string Clouds(string path) => Draw(path, 1400, 240, canvas =>
    {
        using var paint = Fill(new SKColor(0xFF, 0xFF, 0xFF, 0xEB));
        foreach (var (x, y, scale) in new[] { (220f, 110f, 1.0f), (760f, 70f, 0.8f), (1180f, 130f, 1.1f) })
        {
            canvas.Save();
            canvas.Translate(x, y);
            canvas.Scale(scale);
            canvas.DrawCircle(-42, 10, 30, paint);
            canvas.DrawCircle(0, -10, 42, paint);
            canvas.DrawCircle(46, 8, 32, paint);
            canvas.DrawRoundRect(new SKRect(-80, 8, 90, 40), 16, 16, paint);
            canvas.Restore();
        }
    });

    private static string Draw(string path, int width, int height, Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        Png.Save(image, path);
        return path;
    }

    private static SKPath Path(Action<SKPathBuilder> build)
    {
        using var builder = new SKPathBuilder();
        build(builder);
        return builder.Detach();
    }

    private static SKPaint Fill(SKColor color)
    {
        var paint = new SKPaint();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = color;
        return paint;
    }

    private static SKPaint Stroke(SKColor color, float width)
    {
        var paint = new SKPaint();
        paint.IsAntialias = true;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = width;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        paint.Color = color;
        return paint;
    }
}
