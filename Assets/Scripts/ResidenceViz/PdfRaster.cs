using System;
using System.Runtime.InteropServices;
using UnityEngine;

// Turns a page of a PDF into a Texture2D, so a floor plan that arrives as a PDF can be traced by the
// same UnderlayTool that traces a photograph.
//
// PDF IS NOT AN IMAGE FORMAT. Texture2D.LoadImage decodes PNG and JPG and nothing else, so before this
// file the only route from a paper plan into the model was a raster the user had converted somewhere
// else. A managed parse-only library (PdfPig and friends) can pull EMBEDDED images out of a scan but
// renders nothing, and a floor plan out of any CAD package or estate agent's toolchain is vector, so
// half of all real plans would have imported as a blank page. PDFium is the renderer Chrome, Edge and
// every browser's Save-as-PDF preview use; it draws vector and raster alike.
//
// This is a native dependency, and the only one in the project. It is worth being clear about what it
// does and does not cost:
//   * Assets/Plugins/x86_64/pdfium.dll, ~7 MB, BSD-3-Clause. It ships inside the build.
//   * Windows x64 only, which is the only platform ResidenceViz builds for (Build -> ResidenceViz (PC, Windows)).
//     IsAvailable is false anywhere else and the import says so rather than throwing.
//   * NOTHING here touches the network, a server or Python: a bundled decoder is not a service.
//     (The one place in ResidenceViz that does reach the network is SketchPlanGenerator, which is opt-in
//     and per-press. Rasterizing a PDF is still entirely local, and remains so if that is never used.)
//
// Note this is the opposite direction to the argument in HtmlReportWriter, which declines to add a PDF
// WRITER for the before/after report. Writing a PDF means laying out a document and embedding fonts,
// a large library for something a browser's Save-as-PDF already does. Reading one means rasterizing a
// page. They are different problems and the answers do not have to match.
public static class PdfRaster
{
    private const string DLL = "pdfium";

    // 150 dpi renders a Letter or A4 plan at 1275x1650. Enough to trace a door jamb against, and
    // cheap. Anything larger is capped by MaxRasterSide instead.
    public const float TargetDpi = 150f;

    // An architectural sheet is big: ARCH D (24x36") at 150 dpi is 3600x5400, and RGBA32 at 4096 on a
    // side is already 67 MB of texture that stays resident under the traced quad for as long as the
    // sketch is on screen. So the dpi is lowered until the longest side fits this, rather than letting
    // a full-size E sheet allocate a third of a gigabyte.
    public const int MaxRasterSide = 4096;

    private const int FPDFBitmap_BGRA = 4;
    private const int FPDF_ANNOT = 0x01;      // dimension strings and callouts on a plan are annotations

    // ---------------------------------------------------------------------------------------
    // The C API. Only the dozen entry points a rasterizer needs.
    // ---------------------------------------------------------------------------------------

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_InitLibrary();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadMemDocument64(IntPtr dataBuf, UIntPtr size, IntPtr password);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint FPDF_GetLastError();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageWidthF(IntPtr page);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern float FPDF_GetPageHeightF(IntPtr page);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format,
                                                     IntPtr firstScan, int stride);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern bool FPDFBitmap_FillRect(IntPtr bitmap, int left, int top,
                                                   int width, int height, uint color);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY,
                                                     int sizeX, int sizeY, int rotate, int flags);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    // ---------------------------------------------------------------------------------------
    // Library lifetime
    // ---------------------------------------------------------------------------------------

    private static bool _initTried;
    private static bool _available;

    /// <summary>
    /// Whether PDFs can be read on this machine. False when the native plugin is missing or is the
    /// wrong architecture; every entry point here checks it, so a missing DLL produces a sentence in
    /// the rail rather than an exception inside a FileBrowser callback where nothing would surface it.
    /// </summary>
    public static bool IsAvailable
    {
        get { EnsureInit(); return _available; }
    }

    // Initialised once and DELIBERATELY NEVER DESTROYED. FPDF_DestroyLibrary tears down process-wide
    // native state, but a domain reload in the Editor throws away the managed half only: the DLL stays
    // loaded for the life of the process. Pairing a destroy with the reload would leave the next
    // FPDF_InitLibrary re-initialising over a torn-down library, which crashes the Editor rather than
    // failing. Leaking one initialised library per process is the correct trade.
    private static void EnsureInit()
    {
        if (_initTried) return;
        _initTried = true;
        try
        {
            FPDF_InitLibrary();
            _available = true;
        }
        catch (DllNotFoundException)        { _available = false; }
        catch (EntryPointNotFoundException) { _available = false; }
        catch (BadImageFormatException)     { _available = false; }
        catch (Exception e)
        {
            _available = false;
            Debug.LogWarning("[PdfRaster] PDFium could not be initialized: " + e.Message);
        }
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>True when the path looks like a PDF. Extension only. Open is what actually decides.</summary>
    public static bool LooksLikePdf(string path)
        => !string.IsNullOrEmpty(path) &&
           path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a PDF. Returns false with a sentence fit to show the user; never throws. The caller owns
    /// the returned document and must Dispose it.
    /// </summary>
    public static bool Open(string path, out PdfDocument doc, out string error)
    {
        doc = null;
        error = null;

        if (!IsAvailable)
        {
            error = "PDFs cannot be read on this machine.";
            return false;
        }

        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(path); }
        catch (Exception e) { error = "That file could not be read: " + e.Message; return false; }

        return PdfDocument.TryCreate(bytes, out doc, out error);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// One open PDF. Pages are 1-based to the outside world, because that is how a page is referred to
    /// in every other context; the native API is 0-based and the conversion happens here.
    /// </summary>
    public sealed class PdfDocument : IDisposable
    {
        private IntPtr _doc;
        // FPDF_LoadMemDocument does NOT copy. It reads out of this buffer for the document's whole
        // lifetime. A pinned managed array would work but would pin a multi-megabyte block across
        // every GC for as long as the picker is open, so the bytes are held unmanaged instead and
        // freed in Dispose alongside the document itself.
        private IntPtr _bytes;

        public int PageCount { get; private set; }

        /// <summary>Each page's size in POINTS (1/72"), 1-based; index 0 unused.</summary>
        private Vector2[] _sizes;

        internal static bool TryCreate(byte[] bytes, out PdfDocument doc, out string error)
        {
            doc = null;
            error = null;

            IntPtr buf = IntPtr.Zero;
            IntPtr handle = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, buf, bytes.Length);

                handle = FPDF_LoadMemDocument64(buf, (UIntPtr)(ulong)bytes.Length, IntPtr.Zero);
                if (handle == IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buf);
                    error = DescribeError(FPDF_GetLastError());
                    return false;
                }

                int count = FPDF_GetPageCount(handle);
                if (count <= 0)
                {
                    FPDF_CloseDocument(handle);
                    Marshal.FreeHGlobal(buf);
                    error = "That PDF has no pages.";
                    return false;
                }

                var d = new PdfDocument { _doc = handle, _bytes = buf, PageCount = count };
                d.MeasurePages();
                doc = d;
                return true;
            }
            catch (Exception e)
            {
                if (handle != IntPtr.Zero) FPDF_CloseDocument(handle);
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                error = "That PDF could not be opened: " + e.Message;
                return false;
            }
        }

        private void MeasurePages()
        {
            _sizes = new Vector2[PageCount + 1];
            for (int i = 1; i <= PageCount; i++)
            {
                IntPtr page = FPDF_LoadPage(_doc, i - 1);
                if (page == IntPtr.Zero) { _sizes[i] = new Vector2(612f, 792f); continue; }   // US Letter
                _sizes[i] = new Vector2(FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page));
                FPDF_ClosePage(page);
            }
        }

        /// <summary>Page size in points (1/72 inch), 1-based.</summary>
        public Vector2 PageSizePoints(int page)
            => _sizes != null && page >= 1 && page <= PageCount ? _sizes[page] : new Vector2(612f, 792f);

        /// <summary>Page size in inches, 1-based: what the reader would measure with a ruler.</summary>
        public Vector2 PageSizeInches(int page) => PageSizePoints(page) / 72f;

        /// <summary>
        /// ONE dpi for the whole document, derived from its LARGEST page.
        ///
        /// This is not a performance knob, it is the mechanism behind calibrating once for a whole plan
        /// set. metersPerPixel is meters per RENDERED pixel, so it is comparable between two pages only
        /// if both were rendered at the same dpi, and then it is comparable regardless of their paper
        /// sizes, because a page drawn at 1/4" = 1' is that scale whether it is printed on A3 or on E.
        /// Sizing each page independently to the pixel cap would silently give the site plan a
        /// different dpi from the floor plan and make one calibration wrong for the other.
        /// </summary>
        public float DocumentDpi()
        {
            float longestPoints = 0f;
            for (int i = 1; i <= PageCount; i++)
            {
                Vector2 s = PageSizePoints(i);
                longestPoints = Mathf.Max(longestPoints, Mathf.Max(s.x, s.y));
            }
            if (longestPoints <= 0f) return TargetDpi;

            float capDpi = MaxRasterSide * 72f / longestPoints;
            return Mathf.Max(24f, Mathf.Min(TargetDpi, capDpi));
        }

        /// <summary>Pixel size page `page` would render at, at `dpi`.</summary>
        public Vector2Int PixelSize(int page, float dpi)
        {
            Vector2 pts = PageSizePoints(page);
            return new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt(pts.x * dpi / 72f)),
                Mathf.Max(1, Mathf.RoundToInt(pts.y * dpi / 72f)));
        }

        /// <summary>
        /// Renders one page. Returns null on any failure. The caller owns the texture and must destroy
        /// it: these are page-sized and leaking one is tens of megabytes.
        /// </summary>
        public Texture2D Render(int page, float dpi)
        {
            if (_doc == IntPtr.Zero || page < 1 || page > PageCount) return null;

            Vector2Int size = PixelSize(page, dpi);
            int w = size.x, h = size.y;
            int stride = w * 4;

            IntPtr native = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr pageHandle = IntPtr.Zero;
            try
            {
                pageHandle = FPDF_LoadPage(_doc, page - 1);
                if (pageHandle == IntPtr.Zero) return null;

                native = Marshal.AllocHGlobal(stride * h);
                bitmap = FPDFBitmap_CreateEx(w, h, FPDFBitmap_BGRA, native, stride);
                if (bitmap == IntPtr.Zero) return null;

                // A PDF page is transparent where nothing is drawn, and a floor plan is mostly nothing.
                // Left unfilled it would trace as a black sheet with white lines over the ground pad,
                // so the page is painted opaque white first: the paper it was printed on.
                FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, pageHandle, 0, 0, w, h, 0, FPDF_ANNOT);

                var raw = new byte[stride * h];
                Marshal.Copy(native, raw, 0, raw.Length);

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(FlipAndSwizzle(raw, w, h));
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.Apply(false, false);
                return tex;
            }
            catch (Exception e)
            {
                Debug.LogError("[PdfRaster] Page " + page + " failed to render: " + e);
                return null;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap);
                if (native != IntPtr.Zero) Marshal.FreeHGlobal(native);
                if (pageHandle != IntPtr.Zero) FPDF_ClosePage(pageHandle);
            }
        }

        /// <summary>Renders one page straight to PNG bytes. Null on failure.</summary>
        public byte[] RenderPng(int page, float dpi)
        {
            Texture2D tex = Render(page, dpi);
            if (tex == null) return null;
            try { return tex.EncodeToPNG(); }
            finally { UnityEngine.Object.Destroy(tex); }
        }

        // Two corrections in one pass, because both are per-pixel and the buffers are large enough that
        // walking them twice is worth avoiding:
        //
        //   * PDFium hands back B,G,R,A and Texture2D.RGBA32 wants R,G,B,A.
        //   * PDFium writes the TOP row first; Unity's raw texture data starts at the BOTTOM row.
        //     Without the flip every imported plan is upside down, which is the kind of wrong that
        //     reads as a property of the source file rather than as a bug in the reader.
        private static byte[] FlipAndSwizzle(byte[] src, int w, int h)
        {
            var dst = new byte[src.Length];
            int stride = w * 4;
            for (int y = 0; y < h; y++)
            {
                int s = y * stride;
                int d = (h - 1 - y) * stride;
                for (int x = 0; x < w; x++, s += 4, d += 4)
                {
                    dst[d]     = src[s + 2];   // R: third byte of BGRA
                    dst[d + 1] = src[s + 1];   // G
                    dst[d + 2] = src[s];       // B: first byte
                    dst[d + 3] = src[s + 3];   // A
                }
            }
            return dst;
        }

        private static string DescribeError(uint code)
        {
            switch (code)
            {
                case 2: return "That file could not be opened.";
                case 3: return "That file is not a PDF, or it is damaged.";
                case 4: return "That PDF is password protected.";
                case 5: return "That PDF uses a security scheme this cannot open.";
                case 6: return "That PDF's pages could not be read.";
                default: return "That PDF could not be opened.";
            }
        }

        public void Dispose()
        {
            if (_doc != IntPtr.Zero) { FPDF_CloseDocument(_doc); _doc = IntPtr.Zero; }
            // AFTER the document, never before: the document reads out of this buffer until it closes.
            if (_bytes != IntPtr.Zero) { Marshal.FreeHGlobal(_bytes); _bytes = IntPtr.Zero; }
        }
    }
}
