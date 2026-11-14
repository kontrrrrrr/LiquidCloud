using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiquidCloud;

/// <summary>
/// Pre-blurred copies of pictures for full-window backgrounds (wallpaper, cover glow).
/// A live BlurEffect on a window-sized layer is recomputed only for the part of the window that changed
/// (the moving progress bar, a hover), which left visible rectangles of differently blurred or stale picture.
/// A small blurred bitmap stretched smoothly looks the same, costs nothing per frame and can't tear.
/// </summary>
public static class SoftBlur
{
    /// <param name="src">frozen picture</param>
    /// <param name="radius">blur radius in screen pixels (like BlurEffect.Radius)</param>
    /// <param name="displayWidth">roughly how wide it is shown</param>
    public static BitmapSource Make(BitmapSource src, double radius, double displayWidth)
    {
        // blur ≈ 2 px at the small size, so shrink by radius / 2
        int w = (int)Math.Clamp(displayWidth * 2 / Math.Max(radius, 1), 32, 640);
        if (w >= src.PixelWidth) w = src.PixelWidth;
        int h = Math.Max(1, (int)Math.Round(src.PixelHeight * (double)w / src.PixelWidth));
        BitmapSource small = new TransformedBitmap(src, new ScaleTransform((double)w / src.PixelWidth, (double)h / src.PixelHeight));
        small = new FormatConvertedBitmap(small, PixelFormats.Pbgra32, null, 0);
        w = small.PixelWidth; h = small.PixelHeight;
        int stride = w * 4;
        var px = new byte[stride * h];
        small.CopyPixels(px, stride, 0);
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 3; pass++) // three box blurs ≈ a gaussian
        {
            BoxH(px, tmp, w, h, 2);
            BoxV(tmp, px, w, h, 2);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    static void BoxH(byte[] src, byte[] dst, int w, int h, int r)
    {
        for (int y = 0; y < h; y++)
            for (int c = 0; c < 4; c++)
            {
                int row = y * w * 4 + c, sum = 0;
                for (int i = -r; i <= r; i++) sum += src[row + Math.Clamp(i, 0, w - 1) * 4];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * 4] = (byte)(sum / (2 * r + 1));
                    sum += src[row + Math.Min(x + r + 1, w - 1) * 4] - src[row + Math.Max(x - r, 0) * 4];
                }
            }
    }

    static void BoxV(byte[] src, byte[] dst, int w, int h, int r)
    {
        int stride = w * 4;
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 4; c++)
            {
                int col = x * 4 + c, sum = 0;
                for (int i = -r; i <= r; i++) sum += src[col + Math.Clamp(i, 0, h - 1) * stride];
                for (int y = 0; y < h; y++)
                {
                    dst[col + y * stride] = (byte)(sum / (2 * r + 1));
                    sum += src[col + Math.Min(y + r + 1, h - 1) * stride] - src[col + Math.Max(y - r, 0) * stride];
                }
            }
    }
}
