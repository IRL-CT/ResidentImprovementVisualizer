using UnityEngine;

// Draws floor-plan fixtures for the detector tests, in code, so the tests need no image assets and
// every pixel is accounted for.
//
// COORDINATES ARE TOP-DOWN (origin top-left, y down): the frame the detector and the spec think in,
// so a fixture reads like the plan it draws. The one flip lives in Pixels, which hands the buffer
// back in Texture2D order (bottom row first), exactly the way UnderlayTool's texture would arrive.
public sealed class SketchTestImages
{
    private readonly byte[] _gray;   // top-down
    public int Width { get; }
    public int Height { get; }

    public SketchTestImages(int width, int height, byte paper = 255)
    {
        Width = width;
        Height = height;
        _gray = new byte[width * height];
        for (int i = 0; i < _gray.Length; i++) _gray[i] = paper;
    }

    /// <summary>The image in Texture2D order (bottom row first), ready for SketchPlanDetector.Detect.</summary>
    public Color32[] Pixels
    {
        get
        {
            var px = new Color32[Width * Height];
            for (int y = 0; y < Height; y++)
            {
                int src = y * Width;
                int dst = (Height - 1 - y) * Width;
                for (int x = 0; x < Width; x++)
                {
                    byte v = _gray[src + x];
                    px[dst + x] = new Color32(v, v, v, 255);
                }
            }
            return px;
        }
    }

    public void FillRect(int x, int yTop, int w, int h, byte ink = 0)
    {
        int x1 = Mathf.Min(Width, x + w);
        int y1 = Mathf.Min(Height, yTop + h);
        for (int yy = Mathf.Max(0, yTop); yy < y1; yy++)
            for (int xx = Mathf.Max(0, x); xx < x1; xx++)
                _gray[yy * Width + xx] = ink;
    }

    /// <summary>Four wall bands drawn inward from the given bounds: the outline of a building.</summary>
    public void RectOutline(int x, int yTop, int w, int h, int stroke, byte ink = 0)
    {
        FillRect(x, yTop, w, stroke, ink);                    // north
        FillRect(x, yTop + h - stroke, w, stroke, ink);       // south
        FillRect(x, yTop, stroke, h, ink);                    // west
        FillRect(x + w - stroke, yTop, stroke, h, ink);       // east
    }

    /// <summary>Back to paper: how a doorway or a window break is punched into a wall.</summary>
    public void Erase(int x, int yTop, int w, int h) => FillRect(x, yTop, w, h, 255);

    /// <summary>
    /// The hand of a person: every pixel displaced by a smooth low-frequency wobble. Fixed functions
    /// of the seed, so the same call always draws the same shaky plan.
    /// </summary>
    public void Jitter(int seed, float amplitudePx)
    {
        var src = (byte[])_gray.Clone();
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float dx = amplitudePx * Mathf.Sin(0.10f * y + seed);
                float dy = amplitudePx * Mathf.Cos(0.09f * x + 2f * seed);
                int sx = Mathf.Clamp(Mathf.RoundToInt(x + dx), 0, Width - 1);
                int sy = Mathf.Clamp(Mathf.RoundToInt(y + dy), 0, Height - 1);
                _gray[y * Width + x] = src[sy * Width + sx];
            }
        }
    }

    /// <summary>The corners of a photographed page: darker the further from the middle.</summary>
    public void Vignette(float strength)
    {
        float cx = 0.5f * (Width - 1), cy = 0.5f * (Height - 1);
        float maxD = Mathf.Sqrt(cx * cx + cy * cy);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD;
                int i = y * Width + x;
                _gray[i] = (byte)Mathf.RoundToInt(_gray[i] * (1f - strength * d));
            }
    }

    /// <summary>Sensor grain, from a fixed xorshift stream so a seed always means the same speckle.</summary>
    public void Noise(int seed, byte amplitude)
    {
        uint state = (uint)(seed * 2654435761u + 1u);
        for (int i = 0; i < _gray.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            int delta = (int)(state % (2u * amplitude + 1u)) - amplitude;
            _gray[i] = (byte)Mathf.Clamp(_gray[i] + delta, 0, 255);
        }
    }

    /// <summary>A straight stroke between two points: a dimension line, a diagonal, an annotation.</summary>
    public void Line(int x0, int y0, int x1, int y1, int stroke, byte ink = 0)
    {
        int steps = Mathf.Max(1, Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)));
        for (int i = 0; i <= steps; i++)
        {
            int px = x0 + Mathf.RoundToInt((x1 - x0) * (float)i / steps);
            int py = y0 + Mathf.RoundToInt((y1 - y0) * (float)i / steps);
            FillRect(px, py, stroke, stroke, ink);
        }
    }

    /// <summary>
    /// A handwritten word: a cluster of short strokes from a fixed stream, so the same seed always
    /// writes the same scrawl. Nothing in it is long enough to read as wall.
    /// </summary>
    public void Squiggle(int x, int yTop, int seed, byte ink = 0)
    {
        uint state = (uint)(seed * 2654435761u + 12345u);
        for (int s = 0; s < 10; s++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            int sx = x + (int)(state % 50u);
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            int sy = yTop + (int)(state % 22u);
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            bool vertical = (state & 1u) == 0u;
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            int len = 6 + (int)(state % 10u);
            FillRect(sx, sy, vertical ? 2 : len, vertical ? len : 2, ink);
        }
    }

    /// <summary>A furniture symbol: a thin closed outline with one diagonal across it.</summary>
    public void FurnitureSymbol(int x, int yTop, int w, int h, byte ink = 0)
    {
        RectOutline(x, yTop, w, h, 2, ink);
        Line(x, yTop, x + w - 2, yTop + h - 2, 2, ink);
    }

    /// <summary>
    /// A circular arc about a centre: a door swing. Angles are degrees in the image frame (0 points
    /// east, 90 points DOWN because y runs down). Stepped densely enough to leave no holes.
    /// </summary>
    public void Arc(int cx, int cy, float radius, float startDeg, float endDeg, int stroke, byte ink = 0)
    {
        int steps = Mathf.Max(8, Mathf.CeilToInt(Mathf.Abs(endDeg - startDeg) * Mathf.Deg2Rad * radius * 2f));
        for (int i = 0; i <= steps; i++)
        {
            float deg = startDeg + (endDeg - startDeg) * i / steps;
            int px = Mathf.RoundToInt(cx + radius * Mathf.Cos(deg * Mathf.Deg2Rad));
            int py = Mathf.RoundToInt(cy + radius * Mathf.Sin(deg * Mathf.Deg2Rad));
            FillRect(px, py, stroke, stroke, ink);
        }
    }

    /// <summary>
    /// A zigzag panel line between two points: the bifold closet convention. Odd vertices are
    /// offset by the amplitude along the perpendicular (-dy, dx); pass a negative amplitude for
    /// the other side. teeth is how many peaks the zigzag has.
    /// </summary>
    public void Zigzag(int x0, int y0, int x1, int y1, int amplitudePx, int teeth, int stroke, byte ink = 0)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len = Mathf.Max(1f, Mathf.Sqrt(dx * dx + dy * dy));
        float nx = -dy / len, ny = dx / len;

        int points = 2 * Mathf.Max(1, teeth) + 1;
        int px = x0, py = y0;
        for (int i = 1; i < points; i++)
        {
            float t = i / (float)(points - 1);
            float off = (i & 1) == 1 ? amplitudePx : 0f;
            int qx = Mathf.RoundToInt(x0 + dx * t + nx * off);
            int qy = Mathf.RoundToInt(y0 + dy * t + ny * off);
            Line(px, py, qx, qy, stroke, ink);
            px = qx; py = qy;
        }
    }

    /// <summary>The whole drawing turned about the image centre: a page photographed off square.</summary>
    public void Rotate(float degrees, byte paper = 255)
    {
        var src = (byte[])_gray.Clone();
        float cx = 0.5f * (Width - 1), cy = 0.5f * (Height - 1);
        // Inverse mapping: each destination pixel asks where it came from.
        float rad = -degrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

        for (int y = 0; y < Height; y++)
        {
            float dy = y - cy;
            for (int x = 0; x < Width; x++)
            {
                float dx = x - cx;
                int sx = Mathf.RoundToInt(cx + dx * cos - dy * sin);
                int sy = Mathf.RoundToInt(cy + dx * sin + dy * cos);
                _gray[y * Width + x] = sx >= 0 && sx < Width && sy >= 0 && sy < Height
                    ? src[sy * Width + sx]
                    : paper;
            }
        }
    }
}
