using Mio.Core;
using Mio.Css;
using Mio.Layout;
using SkiaSharp;

namespace Mio.Rendering;

/// <summary>
/// Walks the layout tree and paints each box onto a SkiaSharp canvas.
/// </summary>
public sealed class Painter
{

    public void Paint(SKCanvas canvas, LayoutBox root)
    {
        PaintBox(canvas, root);
    }

    private void PaintBox(SKCanvas canvas, LayoutBox box)
    {
        if (box.Style.Display == Display.None) return;

        var border = box.BorderRect;
        var content = box.ContentRect;

        // Background
        if (box.Style.BackgroundColor.A > 0)
        {
            using var bgPaint = new SKPaint { Color = ToSkia(box.Style.BackgroundColor), IsAntialias = true };
            if (box.Style.BorderRadius > 0)
                canvas.DrawRoundRect(ToSkRect(border), box.Style.BorderRadius, box.Style.BorderRadius, bgPaint);
            else
                canvas.DrawRect(ToSkRect(border), bgPaint);
        }

        // Border
        if (box.Style.BorderWidth.Top > 0 || box.Style.BorderWidth.Right > 0 ||
            box.Style.BorderWidth.Bottom > 0 || box.Style.BorderWidth.Left > 0)
        {
            using var borderPaint = new SKPaint
            {
                Color = ToSkia(box.Style.BorderColor),
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = box.Style.BorderWidth.Top, // simplified: uniform border
            };
            if (box.Style.BorderRadius > 0)
                canvas.DrawRoundRect(ToSkRect(border), box.Style.BorderRadius, box.Style.BorderRadius, borderPaint);
            else
                canvas.DrawRect(ToSkRect(border), borderPaint);
        }

        // Text
        if (box.IsTextBox && !string.IsNullOrEmpty(box.TextContent))
        {
            PaintText(canvas, box);
        }

        // Children
        foreach (var child in box.Children)
            PaintBox(canvas, child);
    }

    private void PaintText(SKCanvas canvas, LayoutBox box)
    {
        var s = box.Style;
        using var font = new SKFont(
            SKTypeface.FromFamilyName(
                s.FontFamily,
                s.FontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                s.FontStyle == FontStyle.Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright),
            s.FontSize);

        using var paint = new SKPaint { Color = ToSkia(s.Color), IsAntialias = true };

        float x = s.TextAlign switch
        {
            TextAlign.Center => box.ContentRect.X + box.ContentRect.Width / 2,
            TextAlign.Right => box.ContentRect.Right,
            _ => box.ContentRect.X,
        };

        // Align baseline: y is the top of the content rect, baseline is at fontSize * 0.8 from top
        float y = box.ContentRect.Y + s.FontSize * 0.85f;

        var xAlign = s.TextAlign switch
        {
            TextAlign.Center => SKTextAlign.Center,
            TextAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Left,
        };

        canvas.DrawText(box.TextContent!, x, y, xAlign, font, paint);

        if (s.TextDecoration == TextDecoration.Underline)
        {
            float uy = y + 2;
            using var uPaint = new SKPaint { Color = ToSkia(s.Color), StrokeWidth = 1 };
            float tw = font.MeasureText(box.TextContent!);
            float lx = s.TextAlign == TextAlign.Center ? x - tw / 2 : x;
            canvas.DrawLine(lx, uy, lx + tw, uy, uPaint);
        }
    }

    private static SKRect ToSkRect(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static SKColor ToSkia(Color c) => new(c.R, c.G, c.B, c.A);
}
