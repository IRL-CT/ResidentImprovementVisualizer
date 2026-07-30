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
    /// A readout chip pinned near the cursor — the live "3' 6" · 90°" feedback that makes drawing to
    /// a dimension possible instead of approximate.
    /// </summary>
    public static void Readout(Vector2 at, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var content = new GUIContent(text);
        Vector2 size = GUI.skin.box.CalcSize(content);
        var rect = new Rect(at.x + 14f, at.y - 0.5f * size.y - 14f, size.x + 12f, size.y + 6f);

        Color prev = GUI.color;
        GUI.color = new Color(0.10f, 0.11f, 0.13f, 0.88f);
        GUI.DrawTexture(rect, White);
        GUI.color = Color.white;
        GUI.Label(new Rect(rect.x + 6f, rect.y + 3f, rect.width, rect.height), content);
        GUI.color = prev;
    }
}
