using System.Text;
using Mio.Core;
using Mio.Css;
using Mio.Layout;
using SkiaSharp;
using Svg.Skia;

namespace Mio.Rendering;

/// <summary>
/// Walks the layout tree and paints each box onto a SkiaSharp canvas.
/// </summary>
public sealed class Painter
{
    private readonly Func<string, byte[]?>? _loadImage;
    private readonly Dictionary<string, SKBitmap?> _imageCache = [];

    /// <param name="loadImage">Optional delegate to load image bytes by URL path. Used for &lt;img&gt; rendering.</param>
    public Painter(Func<string, byte[]?>? loadImage = null)
    {
        _loadImage = loadImage;
    }

    public void Paint(SKCanvas canvas, LayoutBox root)
    {
        PaintBox(canvas, root);
    }

    private void PaintBox(SKCanvas canvas, LayoutBox box)
    {
        if (box.Style.Display == Display.None) return;

        var border  = box.BorderRect;
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
                Color       = ToSkia(box.Style.BorderColor),
                IsAntialias = true,
                IsStroke    = true,
                StrokeWidth = box.Style.BorderWidth.Top, // simplified: uniform border width
            };
            if (box.Style.BorderRadius > 0)
                canvas.DrawRoundRect(ToSkRect(border), box.Style.BorderRadius, box.Style.BorderRadius, borderPaint);
            else
                canvas.DrawRect(ToSkRect(border), borderPaint);
        }

        // SVG elements — rendered via Svg.Skia
        if (box.SvgContent != null && content.Width > 0 && content.Height > 0)
        {
            PaintSvg(canvas, box.SvgContent, content);
        }

        // Image (<img> elements)
        if (box.ImageSrc != null && _loadImage != null && content.Width > 0 && content.Height > 0)
        {
            if (!_imageCache.TryGetValue(box.ImageSrc, out var bitmap))
            {
                var bytes = _loadImage(box.ImageSrc);
                bitmap = bytes != null ? SKBitmap.Decode(bytes) : null;
                _imageCache[box.ImageSrc] = bitmap;
            }
            if (bitmap != null)
            {
                using var imgPaint = new SKPaint { IsAntialias = true };
                canvas.DrawBitmap(bitmap, ToSkRect(content), imgPaint);
            }
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
            TextAlign.Right  => box.ContentRect.Right,
            _                => box.ContentRect.X,
        };

        // Baseline offset: y is the top of content rect, text baseline is at ~85% of fontSize
        float y = box.ContentRect.Y + s.FontSize * 0.85f;

        var xAlign = s.TextAlign switch
        {
            TextAlign.Center => SKTextAlign.Center,
            TextAlign.Right  => SKTextAlign.Right,
            _                => SKTextAlign.Left,
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

    /// <summary>
    /// Parses and renders inline SVG markup into the given content rect using Svg.Skia.
    /// The SVG is scaled uniformly to fit, centred within the rect.
    /// </summary>
    private static void PaintSvg(SKCanvas canvas, string svgXml, Rect content)
    {
        try
        {
            using var svg    = new SKSvg();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgXml));
            var picture = svg.Load(stream);
            if (picture == null) return;

            var bounds = picture.CullRect;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            float scaleX = content.Width  / bounds.Width;
            float scaleY = content.Height / bounds.Height;
            float scale  = Math.Min(scaleX, scaleY);

            // Centre within the allocated rect
            float offsetX = content.X + (content.Width  - bounds.Width  * scale) / 2f;
            float offsetY = content.Y + (content.Height - bounds.Height * scale) / 2f;

            canvas.Save();
            canvas.Translate(offsetX, offsetY);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Restore();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SVG] {ex.Message}");
        }
    }

    private static SKRect ToSkRect(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static SKColor ToSkia(Color c) => new(c.R, c.G, c.B, c.A);
}
