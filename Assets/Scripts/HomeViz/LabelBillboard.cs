using UnityEngine;

// Keeps a placeholder's name label facing the camera, and hides it once it is too small to read.
//
// Without the billboard the labels are flat quads pinned to one orientation, which makes them edge-on
// and effectively invisible from directly above, and looking down at the plan is what the overview
// spends most of its time doing. Since the labels are the only thing distinguishing one grey box from
// another until real furniture art exists, a label you cannot read from the main working view is no
// label at all.
//
// The other half of that argument runs the opposite way. Every item, every device and every resident
// carries a name, so a five-bedroom home draws well over a hundred of them at once; pulled back far
// enough to see the whole dwelling, they overlap into a band of grey mush that hides the plan they are
// annotating and cannot be read anyway. So a label draws only while it is big enough on screen to be
// worth drawing.
//
// APPARENT SIZE, NOT DISTANCE: this is the whole reason the test below is not a one-line
// Vector3.Distance. What decides legibility is how big the label lands on screen, which a distance
// cull answers only by accident: pulled back far enough to see a whole dwelling, a hundred labels
// overlap while each of them is still "near". Screen height in pixels is the one question the overview
// and the walkthrough both answer, and it needs no separate threshold per view mode. It is also what
// keeps this honest under an orthographic camera, where distance says nothing at all and the zoom says
// everything, which is what the ortho branch of PixelsPerMeter is for.
//
// The height is read off the generated mesh rather than derived from characterSize and fontSize,
// because TextMesh's mapping between the two is an undocumented internal constant, and because the
// bounds are honest about a label that wrapped onto two lines.
[ExecuteAlways]
public class LabelBillboard : MonoBehaviour
{
    [Tooltip("A label hides once its text would draw shorter than this many pixels on screen.")]
    public float minPixelHeight = 11f;

    // Below this fraction of minPixelHeight a shown label hides again. Without the gap, a label
    // sitting exactly on the threshold flickers on every frame the camera so much as breathes.
    private const float Hysteresis = 0.85f;

    private MeshFilter _mesh;
    private bool       _shown = true;
    private int        _childCount = -1;   // forces one apply on the first frame

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        ApplyVisibility(Legible(cam));
        if (!_shown) return;   // orienting something nobody can see is work for nothing

        // Face the camera. Using the camera's forward rather than a look-at keeps every
        // label in the scene parallel, which reads far more calmly than a field of labels each
        // splaying toward a different point.
        transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
    }

    private bool Legible(Camera cam)
    {
        if (minPixelHeight <= 0f) return true;

        if (_mesh == null) _mesh = GetComponent<MeshFilter>();
        var mesh = _mesh != null ? _mesh.sharedMesh : null;

        // TextMesh builds its mesh lazily, so the first frame of a freshly spawned label has nothing
        // to measure. Show it rather than hiding on a guess: the next frame has the real answer.
        if (mesh == null) return true;

        float worldHeight = mesh.bounds.size.y * Mathf.Abs(transform.lossyScale.y);
        if (worldHeight <= 0f) return true;

        float pixels = worldHeight * PixelsPerMeter(cam);
        return pixels >= minPixelHeight * (_shown ? Hysteresis : 1f);
    }

    private float PixelsPerMeter(Camera cam)
    {
        if (cam.orthographic)
            return cam.orthographicSize > 1e-4f ? cam.pixelHeight / (2f * cam.orthographicSize) : 0f;

        // Depth along the view axis, not straight-line distance: a label at the edge of a wide frame
        // is further from the eye than one in the middle and renders at exactly the same size.
        float depth = Vector3.Dot(transform.position - cam.transform.position, cam.transform.forward);
        if (depth <= cam.nearClipPlane) return float.MaxValue;   // at or behind the eye; the frustum culls it

        return cam.pixelHeight / (2f * depth * Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad));
    }

    // Renderers are toggled rather than the GameObject deactivated, for the reason this component runs
    // at all: a deactivated label gets no LateUpdate, so it could never decide to come back.
    private void ApplyVisibility(bool show)
    {
        // LabelOutline builds the four stroke copies lazily, on ITS first LateUpdate, which may land
        // after this one. Watching the child count is what stops a label hidden before its stroke
        // existed from rendering as a dark smudge with no text in it.
        if (show == _shown && transform.childCount == _childCount) return;

        _shown = show;
        _childCount = transform.childCount;

        var renderers = GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null) renderers[i].enabled = show;
    }
}
