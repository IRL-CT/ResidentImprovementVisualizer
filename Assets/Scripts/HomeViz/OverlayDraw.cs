using System.Collections.Generic;
using UnityEngine;

// Screen-space drawing helpers for tool previews, called from OnGUI.
//
// Deliberately IMGUI rather than GL. TransformGizmo carries a hard-won note that Unity 6's URP Render
// Graph never calls OnRenderObject, which is why that gizmo is built from LineRenderers instead of GL
// lines. Tool previews are transient, screen-space, and already inside an OnGUI pass, so drawing them
// with GUI primitives sidesteps the render-pipeline question entirely.
public static class OverlayDraw
{
    private static Texture2D _white;

    private static Texture2D White
    {
        get
        {
            if (_white == null)
            {
                _white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }
            return _white;
        }
    }

    /// <summary>Projects a world XZ point on the level plane to GUI coordinates.</summary>
    public static bool ToScreen(Camera cam, Vector2 worldXZ, float y, out Vector2 gui)
    {
        gui = Vector2.zero;
        if (cam == null) return false;

        Vector3 sp = cam.WorldToScreenPoint(new Vector3(worldXZ.x, y, worldXZ.y));
        if (sp.z < 0f) return false;   // behind the camera

        gui = new Vector2(sp.x, Screen.height - sp.y);   // GUI y runs downward
        return true;
    }

    public static void Line(Vector2 a, Vector2 b, Color color, float width = 2f)
    {
        Vector2 d = b - a;
        float len = d.magnitude;
        if (len < 0.01f) return;

        Color prev = GUI.color;
        Matrix4x4 prevMatrix = GUI.matrix;

        GUI.color = color;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - 0.5f * width, len, width), White);

        GUI.matrix = prevMatrix;
        GUI.color = prev;
    }

    public static void DashedLine(Vector2 a, Vector2 b, Color color, float width = 2f, float dash = 8f)
    {
        float len = (b - a).magnitude;
        if (len < 0.01f) return;

        Vector2 dir = (b - a) / len;
        for (float t = 0f; t < len; t += dash * 2f)
            Line(a + dir * t, a + dir * Mathf.Min(t + dash, len), color, width);
    }

    /// <summary>
    /// A chain of lines through <paramref name="pts"/> (already in GUI space), optionally closed.
    /// Silently skips a run of fewer than two points, so a caller can hand over a half-built polygon.
    /// </summary>
    public static void Polyline(IReadOnlyList<Vector2> pts, Color color, float width = 2f,
                                bool closed = false)
    {
        if (pts == null || pts.Count < 2) return;
        for (int i = 1; i < pts.Count; i++) Line(pts[i - 1], pts[i], color, width);
        if (closed && pts.Count > 2) Line(pts[pts.Count - 1], pts[0], color, width);
    }

    /// <summary>
    /// An outline drawn twice: a wide dark halo, then the colour on top. One pass is invisible against
    /// a floor finish of similar tone: a selection highlight has to read on oak, on white vinyl and
    /// against the dark ground pad, so it carries its own contrast rather than trusting the backdrop.
    /// </summary>
    public static void Haloed(IReadOnlyList<Vector2> pts, Color color, float width = 2.5f,
                              bool closed = false)
    {
        if (pts == null || pts.Count < 2) return;
        Polyline(pts, new Color(0f, 0f, 0f, 0.55f), width + 4f, closed);
        Polyline(pts, color, width, closed);
    }

    /// <summary>A small square handle with a dark border: the vertex marker for a selected element.</summary>
    public static void Handle(Vector2 center, float size, Color fill)
    {
        Dot(center, size + 2.5f, new Color(0f, 0f, 0f, 0.45f));
        Dot(center, size, fill);
    }

    public static void Dot(Vector2 center, float size, Color color)
    {
        Color prev = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(center.x - 0.5f * size, center.y - 0.5f * size, size, size), White);
        GUI.color = prev;
    }

    public static void Circle(Vector2 center, float radius, Color color, int segments = 28, float width = 2f)
    {
        Vector2 prev = center + new Vector2(radius, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            Vector2 p = center + new Vector2(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius);
            Line(prev, p, color, width);
            prev = p;
        }
    }

    /// <summary>
    /// The chip's own text style, with an EXPLICIT light text colour.
    ///
    /// This is the whole reason readouts were illegible: GUI.color TINTS the style's colour, it does
    /// not replace it, and the ambient style here is the rail's. UITheme sets `label.normal.textColor`
    /// to Ink (near-black) because every panel is light paper. Multiplying near-black by white leaves
    /// near-black, so the chip drew dark text on its own dark background. A chip floating over the
    /// SCENE cannot inherit the paper palette; it has to carry its own.
    ///
    /// Mono, because almost every readout is a measurement and digits that do not shift width as the
    /// cursor moves are far easier to read mid-drag.
    /// </summary>
    private static GUIStyle _chip;

    private static GUIStyle Chip
    {
        get
        {
            if (_chip == null)
            {
                _chip = new GUIStyle
                {
                    fontSize  = 12,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap  = false,
                    padding   = new RectOffset(9, 9, 5, 5),
                };
                Font mono = UITheme.MonoFont;
                if (mono != null) _chip.font = mono;
                _chip.normal.textColor = new Color(0.97f, 0.97f, 0.95f);
            }
            return _chip;
        }
    }

    /// <summary>
    /// A readout chip pinned near the cursor: the live "3' 6" · 90°" feedback that makes drawing to
    /// a dimension possible instead of approximate.
    ///
    /// Clamped into the window: a chip that runs off the right edge is the one you most want to read,
    /// because that is where you are drawing to.
    /// </summary>
    public static void Readout(Vector2 at, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        GUIStyle style = Chip;
        var content = new GUIContent(text);
        Vector2 size = style.CalcSize(content);   // includes the style's own padding

        var rect = new Rect(at.x + 14f, at.y - 0.5f * size.y - 14f, size.x, size.y);
        PaintChip(rect, content, style);
    }

    /// <summary>
    /// The prose sibling of <see cref="Readout"/>: a hover tooltip, anchored below-right of the cursor
    /// so it does not cover the control it describes.
    ///
    /// Two things differ, and both follow from the content. It wraps at <paramref name="maxWidth"/>,
    /// a readout is "3' 6" · 90°" and a tooltip is a sentence, and CalcSize on a sentence produces a
    /// chip wider than the screen, which the clamp then pins to the left edge with the text running off
    /// the right. And it is set in the sans face rather than mono, because none of this is measurements.
    /// </summary>
    public static void Tip(Vector2 at, string text, float maxWidth = 320f)
    {
        if (string.IsNullOrEmpty(text)) return;

        GUIStyle style = Prose;
        var content = new GUIContent(text);
        float w = Mathf.Min(maxWidth, style.CalcSize(content).x);
        var rect = new Rect(at.x + 16f, at.y + 20f, w, style.CalcHeight(content, w));
        PaintChip(rect, content, style);
    }

    // Shadow, chip, then a top hairline: the same lift the rail's cards have, so the chip reads as
    // sitting above the scene rather than painted onto it. Clamped into the window: a chip that runs
    // off the right edge is the one you most want to read, because that is where you are drawing to.
    private static void PaintChip(Rect rect, GUIContent content, GUIStyle style)
    {
        rect.x = Mathf.Clamp(rect.x, 4f, Mathf.Max(4f, Screen.width  - rect.width  - 4f));
        rect.y = Mathf.Clamp(rect.y, 4f, Mathf.Max(4f, Screen.height - rect.height - 4f));

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.28f);
        GUI.DrawTexture(new Rect(rect.x, rect.y + 2f, rect.width, rect.height), White);
        GUI.color = new Color(0.07f, 0.08f, 0.10f, 0.93f);
        GUI.DrawTexture(rect, White);
        GUI.color = new Color(1f, 1f, 1f, 0.20f);
        GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, 1f), White);
        GUI.color = Color.white;
        GUI.Label(rect, content, style);
        GUI.color = prev;
    }

    // Wrapping sans variant of Chip, carrying the same explicit light textColor for the same reason.
    private static GUIStyle _prose;

    private static GUIStyle Prose
    {
        get
        {
            if (_prose == null)
            {
                _prose = new GUIStyle
                {
                    fontSize  = 12,
                    alignment = TextAnchor.UpperLeft,
                    wordWrap  = true,
                    padding   = new RectOffset(10, 10, 7, 8),
                };
                _prose.normal.textColor = new Color(0.97f, 0.97f, 0.95f);
            }
            return _prose;
        }
    }
}
