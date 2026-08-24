using UnityEngine;

// Shrinking the sketch before it goes over the wire.
//
// WHY ON THE CPU RATHER THAN Graphics.Blit: the obvious way to resample a Texture2D is a blit into a
// RenderTexture and a ReadPixels back. That swaps the active render target, which is the first rule
// in ReportCapture's header ("never from OnGUI") and the reason ThumbnailCache queues its jobs
// instead of doing them where they are asked for. A pure array resample cannot be in the wrong phase
// of the frame, needs no temporary GPU resource to leak, and can be unit tested, which none of the
// blit path can. It runs once per generation on a texture that is already resident.
//
// A BOX FILTER RATHER THAN POINT SAMPLING, and that is not a nicety here. The content is line art:
// a wall is one or two dark pixels wide on a scanned sheet, and point-sampling a 4096 px scan down to
// 2576 drops entire walls: not blurs them, drops them. Averaging every source pixel that lands in a
// destination cell keeps a thin line as a grey line, which is still a line.
public static class SketchImageResample
{
    /// <summary>
    /// The longest edge sent to the API.
    ///
    /// 2576 px is the high-resolution tier's ceiling, and a floor plan is exactly the case that
    /// earns it: door swings, dimension strings and fixture symbols are the content, and they are
    /// small. It costs more per image than the older 1568 px cap and is worth it here. PdfRaster
    /// already caps its own rasters at 4096, so the worst case in is a 4096 px scan.
    /// </summary>
    public const int LongEdgeCap = 2576;

    /// <summary>
    /// The size to resample to, preserving aspect. Returns false when the image already fits, in
    /// which case the caller should send it untouched rather than round-trip it through a resample
    /// that can only lose information.
    /// </summary>
    public static bool Target(int width, int height, int cap, out int outWidth, out int outHeight)
    {
        outWidth = width;
        outHeight = height;
        if (width <= 0 || height <= 0) return false;

        int longest = Mathf.Max(width, height);
        if (longest <= cap) return false;

        float scale = (float)cap / longest;
        outWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
        outHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        return true;
    }

    /// <summary>
    /// Averages each destination pixel over the source pixels it covers. Row order is preserved, so
    /// a Texture2D's bottom-up rows stay bottom-up and the caller does not have to think about it.
    /// </summary>
    public static Color32[] Box(Color32[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new Color32[dstW * dstH];
        if (src == null || srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) return dst;

        for (int y = 0; y < dstH; y++)
        {
            int y0 = y * srcH / dstH;
            int y1 = Mathf.Max(y0 + 1, (y + 1) * srcH / dstH);

            for (int x = 0; x < dstW; x++)
            {
                int x0 = x * srcW / dstW;
                int x1 = Mathf.Max(x0 + 1, (x + 1) * srcW / dstW);

                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcW;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        var c = src[row + sx];
                        r += c.r; g += c.g; b += c.b; a += c.a;
                        n++;
                    }
                }

                if (n == 0) n = 1;
                dst[y * dstW + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
            }
        }

        return dst;
    }
}
