using UnityEngine;

// A dark stroke around a scene label, so the text reads on whatever it happens to be floating over.
//
// Every label in HomeViz hangs in the world over a background the app itself picks and the user cannot
// change: the light grey of a default wall, an oak or white-vinyl floor finish, the dark ground pad, a
// muted catalog swatch. One flat colour cannot carry against all of those: the labels were near-black
// ink, which is invisible against exactly the background nearly every furniture label sits on, the wall
// grey behind it.
//
// The fix is the subtitle convention: light text with a dark stroke, which reads on any backdrop
// because it brings both ends of the contrast range with it rather than trusting what is behind.
//
// The stroke is four copies of the same TextMesh offset by about a pixel and pushed slightly further
// from the camera, so the transparent queue (which sorts back to front by distance) draws them
// underneath. Same font, same shared material, same ZTest as the text itself, which is what guarantees
// the halo cannot separate from the glyphs it outlines: anything that makes the label visible or
// hidden does the same to its stroke, in the same pass.
//
// Widths are expressed as a FRACTION of characterSize, not in meters, because the labels over a grab
// bar and over a bedroom are the same size in pixels and different sizes in world units. A fixed
// offset would be a hairline on one and a blur on the other.
[ExecuteAlways]
[RequireComponent(typeof(TextMesh))]
public class LabelOutline : MonoBehaviour
{
    [Tooltip("Stroke color. Slightly transparent so it reads as an edge.")]
    public Color color = new Color(0.06f, 0.07f, 0.09f, 0.92f);

    [Tooltip("Stroke width as a fraction of characterSize. 0.15 is roughly 1.5 px at any label scale.")]
    public float widthFraction = 0.15f;

    // Cardinal only. Adding the diagonals doubles the mesh count for a stroke nobody can tell apart at
    // the size these labels are drawn.
    private static readonly Vector2[] Offsets =
    {
        new Vector2(1f, 0f), new Vector2(-1f, 0f), new Vector2(0f, 1f), new Vector2(0f, -1f),
    };

    private TextMesh   _source;
    private TextMesh[] _copies;
    private string     _lastText;
    private Color      _lastColor;

    private void LateUpdate()
    {
        if (_source == null) _source = GetComponent<TextMesh>();
        if (_source == null) return;

        if (_copies == null) Build();
        Sync();
    }

    private void Build()
    {
        var srcRenderer = GetComponent<MeshRenderer>();
        Material mat = srcRenderer != null ? srcRenderer.sharedMaterial : null;

        float step = Mathf.Max(1e-4f, _source.characterSize * Mathf.Max(0f, widthFraction));
        // Away from the camera: LabelBillboard aims local +Z along the view direction, so this is
        // always "behind" whichever way the label is facing. Big enough that the
        // distance sort is decisive, small enough to be invisible at any viewing distance.
        float back = Mathf.Max(0.01f, step * 4f);

        _copies = new TextMesh[Offsets.Length];
        for (int i = 0; i < Offsets.Length; i++)
        {
            var go = new GameObject("Stroke" + i);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(Offsets[i].x * step, Offsets[i].y * step, back);

            var tm = go.AddComponent<TextMesh>();
            tm.font          = _source.font;
            tm.fontSize      = _source.fontSize;
            tm.fontStyle     = _source.fontStyle;
            tm.characterSize = _source.characterSize;
            tm.lineSpacing   = _source.lineSpacing;
            tm.anchor        = _source.anchor;
            tm.alignment     = _source.alignment;
            tm.tabSize       = _source.tabSize;
            tm.richText      = _source.richText;
            tm.text          = _source.text;
            tm.color         = color;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                // The stroke must never land ON TOP of the glyphs it outlines: that reads as a bold,
                // muddy label rather than a clean one. sortingOrder decides it outright, ahead of the
                // distance sort the localPosition offset above would otherwise be leaning on.
                //
                // Deliberately NOT a material copy with a lower renderQueue, which is the other way to
                // force the order: the built-in font is DYNAMIC, so its atlas texture is replaced
                // whenever a new glyph is requested, and a copied material would keep pointing at the
                // old atlas and start drawing the wrong letters.
                mr.sortingOrder = -1;
                if (mat != null) mr.sharedMaterial = mat;
            }

            _copies[i] = tm;
        }

        _lastText  = _source.text;
        _lastColor = color;
    }

    // Occupant labels rewrite themselves whenever the clock moves them to a new activity, so the stroke
    // has to follow. Guarded on change: assigning TextMesh.text regenerates its mesh, and this runs on
    // every label every frame.
    private void Sync()
    {
        bool textChanged  = !string.Equals(_lastText, _source.text);
        bool colorChanged = _lastColor != color;
        if (!textChanged && !colorChanged) return;

        _lastText  = _source.text;
        _lastColor = color;

        for (int i = 0; i < _copies.Length; i++)
        {
            if (_copies[i] == null) continue;
            if (textChanged)  _copies[i].text  = _lastText;
            if (colorChanged) _copies[i].color = color;
        }
    }
}
