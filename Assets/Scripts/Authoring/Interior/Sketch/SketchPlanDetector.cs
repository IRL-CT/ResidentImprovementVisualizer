using System.Collections.Generic;
using UnityEngine;

// Reads a floor-plan sketch into a SketchPlanSpec on this machine, with no network and no model.
//
// THIS IS THE OFFLINE SIBLING OF SketchPlanGenerator. Both stop at the same seam: a SketchPlanSpec in
// normalised image coordinates (0..1000, origin top-left, y DOWN), which SketchPlanCompiler turns
// into a LevelDef by driving PlanBuilder. Nothing here emits a wall id or a world coordinate, for the
// same reason the model is never asked for one: OpeningDef.offset does not exist until the wall graph
// has been derived.
//
// GRAPH FIRST, CELLS SECOND. The pixels are read exactly once, into measured wall segments
// (SketchWallSegments); everything after that happens on snapped wall LINES. Repairing that graph
// (SketchWallGraph) is also what finds the doorways: a doorway IS a gap between collinear wall
// segments, and a corner pen lift IS an endpoint that missed a perpendicular line, so one pass owns
// both and tolerates the few-pixel misalignments a photographed hand sketch always has. The rooms
// are then the bounded cells of the closed arrangement (SketchCellMap), already on wall centerlines,
// so adjacent rooms share their wall's line by construction and no face-pushing stage exists to
// leave slivers. Openings are verified against the mask before they are believed
// (SketchOpeningReader). Line extraction (Hough and friends) is still refused on principle: its
// accumulator binning is a determinism hazard.
//
// DETERMINISM IS A CONTRACT HERE: the same pixels and the same calibration produce the same spec,
// byte for byte. Every scan is row-major, every median is the lower median of a sorted copy, every
// tie breaks on the smaller index or the smaller coordinate, nothing iterates a Dictionary, nothing
// runs in parallel, and nothing draws a random number.
//
// THE ONE FLIP: Texture2D.GetPixels32 hands rows BOTTOM-UP; the spec reads the image the way a
// person does, origin top-left, y down. ToGrayTopDown converts exactly once, at the door, mirroring
// the discipline SketchFrame.ToWorld keeps on the way out.
public sealed class SketchDetectOptions
{
    /// <summary>Analysis resolution. Downscaling normalises stroke widths and bounds every pass.</summary>
    public int workingLongEdge = 1200;

    /// <summary>A standard interior door leaf: the scale anchor when the sketch is uncalibrated.</summary>
    public float assumedDoorMeters = 0.813f;

    public bool detectWindows = true;
    public bool correctSkew = true;

    /// <summary>Anything smaller once the scale is known is a symbol or noise, and dropped,
    /// unless a verified door opens into it (see closetMaxAreaMeters).</summary>
    public float minRoomAreaMeters = 1.0f;

    /// <summary>A room at most this big whose door was verified is a closet: kept, typed storage
    /// and named Closet. 1.5 m2 is a reach-in closet up to about 0.75 x 2.0 m, under a small WC.</summary>
    public float closetMaxAreaMeters = 1.5f;

    /// <summary>The adaptive threshold's offset below the local mean. Higher ignores fainter marks.</summary>
    public int adaptiveC = 12;
}

public sealed class SketchDetectResult
{
    public SketchPlanSpec spec;                          // null when refused
    public string refusal;                               // one sentence, shown in the rail
    public List<string> warnings = new List<string>();

    /// <summary>Metres per SOURCE pixel: the calibration echoed back, or the estimate made here.</summary>
    public float metersPerPixel;

    /// <summary>True when metersPerPixel was estimated from doorways and should be written back.</summary>
    public bool scaleEstimated;

    /// <summary>How many doorway gaps the estimate rested on. For the outcome line.</summary>
    public int scaleDoorways;

    // Diagnostics.
    public float skewDegrees;
    public float strokeWidthPx;

    public bool Ok => spec != null && refusal == null;
}

public static class SketchPlanDetector
{
    // How many rectangles a room may be said with. PlanBuilder.RoomPart is one level deep and every
    // extra piece is another chance to mint a sliver, so the budget is small: two say an L, three a
    // U or a T, four a plus; a shape needing more stands as its bounding box, with a warning.
    private const int ROOM_RECT_BUDGET = 4;

    // ---------------------------------------------------------------------------------------------
    // The pipeline
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the sketch into a SketchPlanSpec. <paramref name="pixels"/> is exactly what
    /// Texture2D.GetPixels32 returns (bottom row first). <paramref name="metersPerPixel"/> is per
    /// SOURCE pixel; pass 0 to estimate the scale from the doorways found.
    /// </summary>
    public static SketchDetectResult Detect(Color32[] pixels, int width, int height,
                                            float metersPerPixel, SketchDetectOptions options = null)
    {
        var opt = options ?? new SketchDetectOptions();
        var result = new SketchDetectResult();

        if (pixels == null || width < 8 || height < 8 || pixels.Length < width * height)
        {
            result.refusal = "The sketch image could not be read.";
            return result;
        }

        // 1. Downscale, then the one flip into a top-down gray buffer.
        Color32[] work = pixels;
        int w = width, h = height;
        if (SketchImageResample.Target(width, height, opt.workingLongEdge, out int tw, out int th))
        {
            work = SketchImageResample.Box(pixels, width, height, tw, th);
            w = tw; h = th;
        }
        float scaleToSource = Mathf.Max(width, height) / (float)Mathf.Max(w, h);
        int longEdge = Mathf.Max(w, h);

        byte[] gray = ToGrayTopDown(work, w, h);

        // 2. Adaptive threshold. The large window doubles as the illumination pass a photo needs.
        bool[] wall = Binarize(gray, w, h, opt.adaptiveC, out float darkFraction);
        if (darkFraction > 0.45f || darkFraction < 0.001f)
        {
            result.refusal = "Could not find line work in this image.";
            return result;
        }

        // 3. A photographed sheet is rarely perfectly square. Straight walls make sharp peaks in the
        //    row and column sums exactly when they are axis aligned, so a small search finds the
        //    tilt. The GRAY image is rotated and thresholded again: rotating the binary mask would
        //    punch nearest-neighbour holes into one-pixel strokes, which read as pen lifts. The
        //    border ring is then blanked: the seam between the sheet and the rotation's paper fill
        //    binarises into long thin bands that read as walls, and everything within the ring is
        //    edge of page, never plan.
        if (opt.correctSkew)
        {
            result.skewDegrees = EstimateSkewDegrees(wall, w, h);
            if (Mathf.Abs(result.skewDegrees) >= 0.5f)
            {
                gray = RotateGray(gray, w, h, result.skewDegrees);
                wall = Binarize(gray, w, h, opt.adaptiveC, out _);
                float tilt = Mathf.Tan(Mathf.Abs(result.skewDegrees) * Mathf.Deg2Rad);
                BlankBorder(wall, w, h, 4 + 2 * Mathf.CeilToInt(tilt * 0.5f * longEdge));
            }
        }

        // 4. Seal one-or-two-pixel pen lifts and scan dropouts. ONE iteration, deliberately: closing
        //    any harder would seal the light channel inside a double-line window, and everything
        //    wider is the graph repair's job now. The stroke width for the cleanup is provisional,
        //    measured before the close inflates it.
        int stroke = EstimateStrokePx(wall, w, h);
        bool[] closed = Close(wall, w, h, 1);

        // 5. Drop the marks that are not walls (text, arrows, arc fragments, small symbols), before
        //    anything downstream can read them as jambs or rooms. Then measure the stroke AGAIN on
        //    the cleaned mask: grain and glyph fragments vote the first estimate down, and every
        //    later threshold scales with the stroke, so the honest number is the one measured after
        //    the marks that are not pen strokes are gone. A changed answer redoes the cleanup once.
        wall = SketchMaskCleanup.RemoveIsolatedMarks(closed, w, h, stroke, longEdge);
        int refined = EstimateStrokePx(wall, w, h);
        if (refined != stroke)
        {
            stroke = refined;
            wall = SketchMaskCleanup.RemoveIsolatedMarks(closed, w, h, stroke, longEdge);
        }
        result.strokeWidthPx = stroke;

        // 7. The wall segments, measured, then snapped onto shared wall lines.
        var segs = SketchWallSegments.Extract(wall, w, h, stroke);
        var grid = SketchWallSegments.Snap(segs, stroke);
        if (grid.hLines.Length < 2 || grid.vLines.Length < 2)
        {
            result.refusal = "No rooms could be found in this image.";
            return result;
        }

        // 8. Close the graph. This is also where every doorway candidate is found: a doorway is a
        //    gap in a wall line, whichever few-pixel offset its jambs were drawn at.
        var graph = SketchWallGraph.Repair(grid, stroke, longEdge);
        if (graph.cells.roomCount == 0)
        {
            result.refusal = "No rooms could be found in this image.";
            return result;
        }
        if (!graph.clean)
            result.warnings.Add("Some walls did not quite meet and were joined by best guess. "
                              + "Check the corners.");

        // 9. The rooms are the bounded cells, cut into rectangles on the wall centerlines.
        var rects = BuildRects(graph.cells, result.warnings);
        if (rects.Count == 0)
        {
            result.refusal = "No rooms could be found in this image.";
            return result;
        }

        // 10. Openings: candidates verified against the mask, windows from the double-line marks.
        var gaps = SketchOpeningReader.Read(graph, grid, rects, wall, w, h, stroke, opt.detectWindows);

        // 11. Resolve the scale, then judge everything in metres.
        float mppWorking;
        if (metersPerPixel > 0f)
        {
            mppWorking = metersPerPixel * scaleToSource;
            result.metersPerPixel = metersPerPixel;
        }
        else
        {
            float doorPx = EstimateDoorGapPx(gaps, out int doorways);
            if (doorPx <= 0f)
            {
                result.refusal = "No scale is set and no doorways were found to estimate one from. "
                               + "Set the scale first, then read again.";
                return result;
            }
            mppWorking = opt.assumedDoorMeters / doorPx;
            result.metersPerPixel = mppWorking / scaleToSource;
            result.scaleEstimated = true;
            result.scaleDoorways = doorways;

            // The walls are a free second opinion on the estimate: an interior wall is around a
            // tenth of a metre, so walls far past that mean the door anchor caught an archway.
            float thick = MedianMajorThickness(grid) * mppWorking;
            if (thick > 0.35f)
                result.warnings.Add("The walls came out about " + thick.ToString("0.00")
                                  + " m thick at the estimated scale, which is far thicker than a "
                                  + "real wall. Check the scale before trusting the sizes.");
        }

        result.spec = Assemble(rects, gaps, w, h, mppWorking, opt, result.warnings);
        if (result.spec == null)
            result.refusal = "No rooms could be found in this image.";

        return result;
    }

    // ---------------------------------------------------------------------------------------------
    // Stage 1: gray, top-down
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Integer luma, written top-down. This is the ONE place the bottom-up rows of GetPixels32 are
    /// flipped into the picture-reading frame the spec uses; every later stage works top-down.
    /// </summary>
    public static byte[] ToGrayTopDown(Color32[] pixels, int w, int h)
    {
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int src = y * w;
            int dst = (h - 1 - y) * w;
            for (int x = 0; x < w; x++)
            {
                var c = pixels[src + x];
                gray[dst + x] = (byte)((299 * c.r + 587 * c.g + 114 * c.b) / 1000);
            }
        }
        return gray;
    }

    // ---------------------------------------------------------------------------------------------
    // Stage 2: adaptive threshold
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Dark-below-local-mean thresholding over a summed-area table. The window is large (about a
    /// sixteenth of the long edge) so a lighting gradient across a photographed page moves the local
    /// mean with it: that IS the illumination normalisation, with no separate pass to disagree with.
    /// </summary>
    public static bool[] Binarize(byte[] gray, int w, int h, int c, out float darkFraction)
    {
        // Summed-area table, one row and column of zero padding so no branch runs per pixel.
        var sat = new long[(w + 1) * (h + 1)];
        for (int y = 0; y < h; y++)
        {
            long rowSum = 0;
            int row = y * w;
            int satRow = (y + 1) * (w + 1);
            int satPrev = y * (w + 1);
            for (int x = 0; x < w; x++)
            {
                rowSum += gray[row + x];
                sat[satRow + x + 1] = sat[satPrev + x + 1] + rowSum;
            }
        }

        int window = Mathf.Max(15, Mathf.Max(w, h) / 16) | 1;
        int r = window / 2;

        var wallMask = new bool[w * h];
        int dark = 0;
        for (int y = 0; y < h; y++)
        {
            int y0 = Mathf.Max(0, y - r), y1 = Mathf.Min(h - 1, y + r);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Mathf.Max(0, x - r), x1 = Mathf.Min(w - 1, x + r);
                long sum = sat[(y1 + 1) * (w + 1) + x1 + 1] - sat[y0 * (w + 1) + x1 + 1]
                         - sat[(y1 + 1) * (w + 1) + x0] + sat[y0 * (w + 1) + x0];
                int n = (x1 - x0 + 1) * (y1 - y0 + 1);
                if (gray[row + x] < sum / n - c) { wallMask[row + x] = true; dark++; }
            }
        }

        darkFraction = (float)dark / (w * h);
        return wallMask;
    }

    // ---------------------------------------------------------------------------------------------
    // Stage 3: skew
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The tilt of the drawing: a coarse sweep over -4 to +4 degrees in one-degree steps, then a
    /// fine sweep in quarter-degree steps around the winner. Scored by how sharply the wall pixels
    /// pile into rows and columns when the mask is rotated by the candidate angle: axis-aligned
    /// walls make spiky projections, tilted ones smear. Ties go to the angle nearest zero, then the
    /// earlier candidate, so the answer is stable.
    /// </summary>
    public static float EstimateSkewDegrees(bool[] wall, int w, int h)
    {
        float coarse = SweepSkew(wall, w, h, 0f, 1.0f, 4);
        return SweepSkew(wall, w, h, coarse, 0.25f, 3);
    }

    private static float SweepSkew(bool[] wall, int w, int h, float center, float step, int halfSteps)
    {
        float cx = 0.5f * (w - 1), cy = 0.5f * (h - 1);

        float bestAngle = center;
        double bestScore = double.MinValue;

        var rows = new int[h];
        var cols = new int[w];

        for (int s = -halfSteps; s <= halfSteps; s++)
        {
            float angle = center + s * step;
            float rad = angle * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);

            System.Array.Clear(rows, 0, h);
            System.Array.Clear(cols, 0, w);

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float dy = y - cy;
                for (int x = 0; x < w; x++)
                {
                    if (!wall[row + x]) continue;
                    float dx = x - cx;
                    int xr = Mathf.RoundToInt(cx + dx * cos + dy * sin);
                    int yr = Mathf.RoundToInt(cy - dx * sin + dy * cos);
                    if (xr >= 0 && xr < w) cols[xr]++;
                    if (yr >= 0 && yr < h) rows[yr]++;
                }
            }

            double score = Variance(rows) + Variance(cols);
            bool better = score > bestScore
                       || (score == bestScore && Mathf.Abs(angle) < Mathf.Abs(bestAngle));
            if (better) { bestScore = score; bestAngle = angle; }
        }

        return bestAngle;
    }

    private static double Variance(int[] values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        double mean = sum / values.Length;

        double v = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            v += d * d;
        }
        return v / values.Length;
    }

    /// <summary>Clears a ring of the given width around the mask's border, in place.</summary>
    private static void BlankBorder(bool[] wall, int w, int h, int margin)
    {
        int m = Mathf.Min(margin, Mathf.Min(w, h) / 2);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            if (y < m || y >= h - m)
            {
                for (int x = 0; x < w; x++) wall[row + x] = false;
                continue;
            }
            for (int x = 0; x < m; x++) wall[row + x] = false;
            for (int x = w - m; x < w; x++) wall[row + x] = false;
        }
    }

    /// <summary>
    /// Nearest-neighbour rotation of the gray image about its centre, paper white where the sheet
    /// rotates out of frame. The angle means what EstimateSkewDegrees measured: passing its result
    /// straight back squares the drawing up.
    /// </summary>
    public static byte[] RotateGray(byte[] gray, int w, int h, float degrees)
    {
        var outGray = new byte[w * h];
        float cx = 0.5f * (w - 1), cy = 0.5f * (h - 1);
        float rad = degrees * Mathf.Deg2Rad;
        // Inverse mapping: each destination pixel asks where it came from.
        float cos = Mathf.Cos(-rad), sin = Mathf.Sin(-rad);

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                int sx = Mathf.RoundToInt(cx + dx * cos + dy * sin);
                int sy = Mathf.RoundToInt(cy - dx * sin + dy * cos);
                outGray[row + x] = sx >= 0 && sx < w && sy >= 0 && sy < h
                    ? gray[sy * w + sx]
                    : (byte)255;
            }
        }
        return outGray;
    }

    // ---------------------------------------------------------------------------------------------
    // Stage 4: stroke width
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The pen's width in pixels: the lower median of every short dark run along rows and columns.
    /// Runs longer than a cap are wall-length rather than wall-thickness and are left out, which also
    /// keeps filled symbols from voting.
    /// </summary>
    public static int EstimateStrokePx(bool[] wall, int w, int h)
    {
        int cap = Mathf.Max(8, Mathf.Max(w, h) / 30);
        var runs = new List<int>();

        for (int y = 0; y < h; y++)
        {
            int row = y * w, run = 0;
            for (int x = 0; x <= w; x++)
            {
                if (x < w && wall[row + x]) { run++; continue; }
                if (run > 0 && run <= cap) runs.Add(run);
                run = 0;
            }
        }
        for (int x = 0; x < w; x++)
        {
            int run = 0;
            for (int y = 0; y <= h; y++)
            {
                if (y < h && wall[y * w + x]) { run++; continue; }
                if (run > 0 && run <= cap) runs.Add(run);
                run = 0;
            }
        }

        if (runs.Count == 0) return 3;
        runs.Sort();
        return Mathf.Max(1, runs[(runs.Count - 1) / 2]);
    }

    // ---------------------------------------------------------------------------------------------
    // Stage 5: morphological close
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Dilate then erode, 3x3, the given number of times each. Seals gaps up to twice that. The
    /// input array is left untouched; the result is always a fresh buffer.
    /// </summary>
    public static bool[] Close(bool[] wall, int w, int h, int iterations)
    {
        var a = (bool[])wall.Clone();
        var b = new bool[w * h];
        for (int i = 0; i < iterations; i++) { Morph(a, b, w, h, dilate: true); var t = a; a = b; b = t; }
        for (int i = 0; i < iterations; i++) { Morph(a, b, w, h, dilate: false); var t = a; a = b; b = t; }
        return a;
    }

    private static void Morph(bool[] src, bool[] dst, int w, int h, bool dilate)
    {
        for (int y = 0; y < h; y++)
        {
            int y0 = Mathf.Max(0, y - 1), y1 = Mathf.Min(h - 1, y + 1);
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = Mathf.Max(0, x - 1), x1 = Mathf.Min(w - 1, x + 1);
                bool hit = !dilate;
                for (int yy = y0; yy <= y1 && hit != dilate; yy++)
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        bool v = src[yy * w + xx];
                        if (dilate && v) { hit = true; break; }
                        if (!dilate && !v) { hit = false; break; }
                    }
                dst[row + x] = hit;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Stage 9: rooms from cells
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every room's rectangles from the cell partition, root first (the largest by area, ties to
    /// the earliest cut), parts after it in sweep order. A room needing more rectangles than the
    /// budget stands as its bounding box, with a warning.
    /// </summary>
    private static List<SketchRoomRect> BuildRects(SketchCellMap cells, List<string> warnings)
    {
        var cut = cells.Partition();
        var byRoom = new List<int>[cells.roomCount];
        for (int i = 0; i < cut.Count; i++)
        {
            var list = byRoom[cut[i].room];
            if (list == null) byRoom[cut[i].room] = list = new List<int>();
            list.Add(i);
        }

        var rects = new List<SketchRoomRect>();
        for (int room = 0; room < cells.roomCount; room++)
        {
            var list = byRoom[room];
            if (list == null) continue;

            if (list.Count > ROOM_RECT_BUDGET)
            {
                int i0 = cells.nx, j0 = cells.ny, i1 = -1, j1 = -1;
                foreach (int k in list)
                {
                    if (cut[k].i0 < i0) i0 = cut[k].i0;
                    if (cut[k].j0 < j0) j0 = cut[k].j0;
                    if (cut[k].i1 > i1) i1 = cut[k].i1;
                    if (cut[k].j1 > j1) j1 = cut[k].j1;
                }
                rects.Add(PxRect(cells, room, -1, i0, j0, i1, j1));
                warnings.Add("One room had a shape that could not be traced exactly and was kept as "
                           + "a rectangle. Check its walls.");
                continue;
            }

            int rootK = 0;
            float best = -1f;
            for (int k = 0; k < list.Count; k++)
            {
                var c = cut[list[k]];
                float area = (cells.xs[c.i1 + 1] - cells.xs[c.i0]) * (cells.ys[c.j1 + 1] - cells.ys[c.j0]);
                if (area > best) { best = area; rootK = k; }
            }

            int rootIndex = rects.Count;
            var rc = cut[list[rootK]];
            rects.Add(PxRect(cells, room, -1, rc.i0, rc.j0, rc.i1, rc.j1));
            for (int k = 0; k < list.Count; k++)
            {
                if (k == rootK) continue;
                var c = cut[list[k]];
                rects.Add(PxRect(cells, room, rootIndex, c.i0, c.j0, c.i1, c.j1));
            }
        }
        return rects;
    }

    private static SketchRoomRect PxRect(SketchCellMap cells, int room, int parent,
                                         int i0, int j0, int i1, int j1)
        => new SketchRoomRect
        {
            room = room, parent = parent,
            cx0 = cells.xs[i0], cy0 = cells.ys[j0],
            cx1 = cells.xs[i1 + 1], cy1 = cells.ys[j1 + 1],
        };

    // ---------------------------------------------------------------------------------------------
    // Stage 11: scale, then the spec
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The doorway width to anchor the scale on, in working pixels. Interior non-closet doorways
    /// are the honest sample: an exterior gap can be a whole missing porch wall, and a closet door
    /// is real but narrow (0.61 m sits inside 1.5x of 0.813 m) and would drag the median. The floor
    /// is the smallest width another width supports within 1.5x, so one spurious small gap cannot
    /// shrink the scale; a lone gap still anchors alone. Wider gaps are archways and double doors
    /// that would drag the estimate up. Zero when there is nothing to stand on.
    /// </summary>
    private static float EstimateDoorGapPx(List<SketchGap> gaps, out int doorways)
    {
        doorways = 0;

        var widths = new List<float>();
        foreach (var g in gaps) if (g.rectB >= 0 && !g.window && !g.closet) widths.Add(g.widthPx);
        if (widths.Count == 0)
            foreach (var g in gaps) if (g.rectB >= 0 && !g.window) widths.Add(g.widthPx);
        if (widths.Count == 0)
            foreach (var g in gaps) if (!g.window) widths.Add(g.widthPx);
        if (widths.Count == 0) return 0f;

        widths.Sort();
        int f = -1;
        for (int i = 0; i + 1 < widths.Count; i++)
            if (widths[i + 1] <= 1.5f * widths[i]) { f = i; break; }
        if (f < 0) f = 0;
        float floor = widths[f];
        var doors = new List<float>();
        foreach (var v in widths) if (v >= floor && v <= 1.5f * floor) doors.Add(v);

        doorways = doors.Count;
        return doors[(doors.Count - 1) / 2];
    }

    /// <summary>The lower-median thickness of the major wall segments, px; 0 when there are none.</summary>
    private static float MedianMajorThickness(SketchWallGrid grid)
    {
        var thicknesses = new List<int>();
        foreach (var s in grid.segs) if (s.major) thicknesses.Add(s.thickness);
        if (thicknesses.Count == 0) return 0f;
        thicknesses.Sort();
        return thicknesses[(thicknesses.Count - 1) / 2];
    }

    private static SketchPlanSpec Assemble(List<SketchRoomRect> rects, List<SketchGap> gaps,
                                           int w, int h, float mppWorking, SketchDetectOptions opt,
                                           List<string> warnings)
    {
        // Which rooms have a verified door, and how big each room is in metres over ALL its rects:
        // what separates a closet (small, doored, kept) from a symbol (small, doorless, dropped).
        int maxRoom = -1;
        for (int i = 0; i < rects.Count; i++) if (rects[i].room > maxRoom) maxRoom = rects[i].room;
        var roomHasDoor = new bool[maxRoom + 1];
        var roomAreaM2 = new float[maxRoom + 1];
        foreach (var g in gaps)
        {
            if (g.window) continue;
            roomHasDoor[rects[g.rectA].room] = true;
            if (g.rectB >= 0) roomHasDoor[rects[g.rectB].room] = true;
        }
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            roomAreaM2[r.room] += (r.cx1 - r.cx0) * (r.cy1 - r.cy0) * mppWorking * mppWorking;
        }

        // Judge the rectangles in metres: a "room" smaller than a bed or thinner than a passage is a
        // symbol or a wall channel, unless its door was verified and the whole room stays inside the
        // closet band. The per-side floor is absolute: the regularizer drops thinner rooms anyway.
        // A dropped root orphans its parts, which then stand as rooms of their own if they are big
        // enough: still floor, just no longer an L.
        float minSide = SketchRegularizer.MinRoomSide;
        var keep = new bool[rects.Count];
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            float wm = (r.cx1 - r.cx0) * mppWorking;
            float dm = (r.cy1 - r.cy0) * mppWorking;
            bool closetRescue = roomHasDoor[r.room] && roomAreaM2[r.room] < opt.closetMaxAreaMeters;
            keep[i] = wm >= minSide && dm >= minSide
                   && (wm * dm >= opt.minRoomAreaMeters || closetRescue);
        }

        // Roots in reading order (top of the image first, then left to right), which is what makes
        // "Room 1" the one a person would point at first.
        var roots = new List<int>();
        for (int i = 0; i < rects.Count; i++)
            if (keep[i] && (!rects[i].IsPart || !keep[rects[i].parent])) roots.Add(i);
        roots.Sort((a, b) =>
        {
            int byY = rects[a].cy0.CompareTo(rects[b].cy0);
            if (byY != 0) return byY;
            int byX = rects[a].cx0.CompareTo(rects[b].cx0);
            return byX != 0 ? byX : a.CompareTo(b);
        });
        if (roots.Count == 0) return null;

        var keyOf = new string[rects.Count];
        var spec = new SketchPlanSpec { rooms = new List<SketchRoom>(), openings = new List<SketchOpening>() };

        // A rescued small room is a closet: typed storage, named by its own counter so the person
        // reads "Closet" where the plan drew one. Keys stay room1..roomN by root index throughout.
        int closets = 0;
        for (int n = 0; n < roots.Count; n++)
        {
            int i = roots[n];
            keyOf[i] = "room" + (n + 1);
            bool isCloset = roomHasDoor[rects[i].room]
                         && roomAreaM2[rects[i].room] < opt.closetMaxAreaMeters;
            string name = isCloset
                ? (++closets == 1 ? "Closet" : "Closet " + closets)
                : "Room " + (n + 1);
            string type = isCloset ? RoomType.Storage : RoomType.Untyped;
            spec.rooms.Add(Room(rects[i], keyOf[i], name, null, w, h, mppWorking, type));
        }

        // Parts, keyed after their room, in rect order under each root so re-runs agree.
        var partCount = new int[rects.Count];
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            if (!keep[i] || !r.IsPart || keyOf[i] != null) continue;
            if (keyOf[r.parent] == null) continue;

            string key = keyOf[r.parent] + (char)('b' + partCount[r.parent]++);
            keyOf[i] = key;
            spec.rooms.Add(Room(r, key, null, keyOf[r.parent], w, h, mppWorking));
        }

        foreach (var g in gaps)
        {
            float widthM = g.widthPx * mppWorking;
            var a = rects[g.rectA];
            string keyA = RoomKey(keyOf, rects, g.rectA);
            if (keyA == null) continue;

            if (g.window)
            {
                if (widthM < 0.4f) continue;
                spec.openings.Add(new SketchOpening
                {
                    kind = OpeningKind.Window,
                    room = keyA,
                    edge = EdgeWord(g.edge),
                    alongFraction = Along(a, g.edge, g.center),
                    widthMeters = widthM,
                    sillMeters = 0.9f,
                });
                continue;
            }

            if (widthM < 0.45f) continue;
            string kind = widthM > 1.6f ? OpeningKind.CasedOpening : OpeningKind.Door;

            if (g.rectB >= 0)
            {
                string keyB = RoomKey(keyOf, rects, g.rectB);
                if (keyB == null || keyB == keyA) continue;

                var b = rects[g.rectB];
                float s0, s1, along;
                if (!g.horizontal)
                {
                    // South to north on a vertical wall: image y runs the other way, so flip.
                    s0 = Mathf.Max(a.cy0, b.cy0);
                    s1 = Mathf.Min(a.cy1, b.cy1);
                    along = s1 > s0 ? (s1 - g.center) / (s1 - s0) : 0.5f;
                }
                else
                {
                    s0 = Mathf.Max(a.cx0, b.cx0);
                    s1 = Mathf.Min(a.cx1, b.cx1);
                    along = s1 > s0 ? (g.center - s0) / (s1 - s0) : 0.5f;
                }

                spec.openings.Add(new SketchOpening
                {
                    kind = kind,
                    between = new List<string> { keyA, keyB },
                    alongFraction = Mathf.Clamp01(along),
                    widthMeters = widthM,
                });
            }
            else
            {
                spec.openings.Add(new SketchOpening
                {
                    kind = kind,
                    room = keyA,
                    edge = EdgeWord(g.edge),
                    alongFraction = Along(a, g.edge, g.center),
                    widthMeters = widthM,
                });
            }
        }

        return spec;
    }

    /// <summary>The room key an opening should name: a part's opening belongs to the whole room.</summary>
    private static string RoomKey(string[] keyOf, List<SketchRoomRect> rects, int i)
    {
        string key = keyOf[i];
        if (key == null) return null;
        var r = rects[i];
        return r.IsPart && keyOf[r.parent] != null ? keyOf[r.parent] : key;
    }

    private static SketchRoom Room(SketchRoomRect r, string key, string name, string partOf,
                                   int w, int h, float mpp, string roomType = RoomType.Untyped)
    {
        // Normalised to the image span the way the spec reads it: 0..1000, y down.
        int x = Mathf.RoundToInt(r.cx0 * 1000f / w);
        int y = Mathf.RoundToInt(r.cy0 * 1000f / h);
        return new SketchRoom
        {
            key = key,
            name = name ?? key,
            roomType = roomType,
            partOf = partOf ?? "",
            x = x,
            y = y,
            w = Mathf.Max(1, Mathf.RoundToInt(r.cx1 * 1000f / w) - x),
            h = Mathf.Max(1, Mathf.RoundToInt(r.cy1 * 1000f / h) - y),
            widthMeters = (r.cx1 - r.cx0) * mpp,
            depthMeters = (r.cy1 - r.cy0) * mpp,
        };
    }

    /// <summary>
    /// Image edges as compass words. Image y runs DOWN, world z runs up, so the image's top edge is
    /// the building's north; the flip in SketchFrame.ToWorld is what makes this true.
    /// </summary>
    private static string EdgeWord(SketchWallSide edge)
    {
        switch (edge)
        {
            case SketchWallSide.North: return "north";
            case SketchWallSide.South: return "south";
            case SketchWallSide.East: return "east";
            default: return "west";
        }
    }

    /// <summary>
    /// Where along its wall an opening sits, 0..1 from the MINIMUM world coordinate: the west end of
    /// a horizontal wall, the SOUTH end of a vertical one. South is the larger image y, so the
    /// vertical case measures from the bottom of the span. This is the alongFraction trap the schema
    /// documents, paid once, here.
    /// </summary>
    private static float Along(SketchRoomRect r, SketchWallSide edge, float centerPx)
    {
        bool vertical = edge == SketchWallSide.East || edge == SketchWallSide.West;
        float s0 = vertical ? r.cy0 : r.cx0;
        float s1 = vertical ? r.cy1 : r.cx1;
        if (s1 <= s0) return 0.5f;
        return Mathf.Clamp01(vertical ? (s1 - centerPx) / (s1 - s0) : (centerPx - s0) / (s1 - s0));
    }
}
