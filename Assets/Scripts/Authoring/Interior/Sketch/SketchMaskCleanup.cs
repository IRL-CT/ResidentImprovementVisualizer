using UnityEngine;

// Removes the marks on a sketch that are not walls: room labels, dimension text, arrows, door-swing
// arc fragments and small furniture symbols. They all land in the wall mask when the sketch is
// binarised, and left there they corrupt everything downstream: a label splits a room's rectangle
// decomposition, an arrow near a wall reads as a jamb, a symbol's box becomes a phantom room.
//
// THE TEST IS THE STRAIGHT RUN. Every real piece of wall carries a long straight run of dark pixels
// in its own direction, even a fragmented wobbly one; text, arcs and small symbols do not. A
// connected component is removed only when it has NO long straight run AND a small bounding box,
// which is deliberately conservative: a mark touching a wall shares the wall's component and always
// survives, and what slips through is caught by the metric filters later.
//
// Determinism: components are labelled in first-pixel row-major order, and nothing else here
// branches on anything but the mask.
public static class SketchMaskCleanup
{
    /// <summary>
    /// A fresh mask with the small, runless components removed. <paramref name="stroke"/> is the
    /// measured pen width; <paramref name="longEdge"/> the working image's long side in pixels.
    /// </summary>
    public static bool[] RemoveIsolatedMarks(bool[] wall, int w, int h, int stroke, int longEdge)
    {
        var visited = new bool[w * h];
        var queue = new int[w * h];
        var result = (bool[])wall.Clone();

        int minKeepRun = 8 * stroke;
        int minKeepBox = Mathf.Max(1, longEdge / 12);

        for (int i = 0; i < wall.Length; i++)
        {
            if (!wall[i] || visited[i]) continue;

            int head = 0, tail = 0;
            visited[i] = true;
            queue[tail++] = i;
            int x0 = w, x1 = 0, y0 = h, y1 = 0;
            while (head < tail)
            {
                int p = queue[head++];
                int px = p % w, py = p / w;
                if (px < x0) x0 = px;
                if (px > x1) x1 = px;
                if (py < y0) y0 = py;
                if (py > y1) y1 = py;
                if (px > 0 && wall[p - 1] && !visited[p - 1]) { visited[p - 1] = true; queue[tail++] = p - 1; }
                if (px < w - 1 && wall[p + 1] && !visited[p + 1]) { visited[p + 1] = true; queue[tail++] = p + 1; }
                if (py > 0 && wall[p - w] && !visited[p - w]) { visited[p - w] = true; queue[tail++] = p - w; }
                if (py < h - 1 && wall[p + w] && !visited[p + w]) { visited[p + w] = true; queue[tail++] = p + w; }
            }

            // A component with a big bounding box is kept without further looking: even a badly
            // fragmented wall earns its size, while text and symbols stay small.
            if (x1 - x0 + 1 >= minKeepBox || y1 - y0 + 1 >= minKeepBox) continue;

            // The bounded box makes this cheap. It may see runs of a NEIGHBOURING component, which
            // only ever errs toward keeping: the safe direction.
            if (MaxStraightRun(wall, w, x0, y0, x1, y1) >= minKeepRun) continue;

            for (int q = 0; q < tail; q++) result[queue[q]] = false;
        }

        return result;
    }

    /// <summary>The longest horizontal or vertical dark run inside the box, inclusive bounds.</summary>
    private static int MaxStraightRun(bool[] wall, int w, int x0, int y0, int x1, int y1)
    {
        int best = 0;
        for (int y = y0; y <= y1; y++)
        {
            int row = y * w, run = 0;
            for (int x = x0; x <= x1 + 1; x++)
            {
                if (x <= x1 && wall[row + x]) { run++; continue; }
                if (run > best) best = run;
                run = 0;
            }
        }
        for (int x = x0; x <= x1; x++)
        {
            int run = 0;
            for (int y = y0; y <= y1 + 1; y++)
            {
                if (y <= y1 && wall[y * w + x]) { run++; continue; }
                if (run > best) best = run;
                run = 0;
            }
        }
        return best;
    }
}
