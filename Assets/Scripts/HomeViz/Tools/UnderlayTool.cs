using System.IO;
using SimpleFileBrowser;
using UnityEngine;
using UnityEngine.InputSystem;

// Imports a floor-plan sketch and puts it on the ground plane at TRUE SCALE, to be traced over.
//
// With LLM layout parsing out of scope, this is the only route from a paper plan into the model — and
// the calibration step is the single most important interaction in the application. Click two points
// on the image, type the real distance between them, and every wall traced afterwards is
// dimensionally correct. A photo of a hand-drawn plan on graph paper, calibrated against one known
// dimension, beats a model's estimate of the same sketch, which is why dropping generation is not a
// regression here.
//
// Until it is calibrated, the image has no scale and nothing traced over it means anything — so the
// rail says so plainly rather than letting someone trace a whole plan at the wrong size.
public class UnderlayTool : HomeToolBase
{
    public override string Id => "underlay";
    public override string DisplayName => "Sketch";

    private GameObject _quad;
    private Texture2D _texture;
    private string _loadedFor;      // home id + filename the current texture belongs to

    private int _calibStage;        // 0 idle, 1 awaiting first point, 2 awaiting second
    private Vector2 _calibA, _calibB;
    private string _calibText = "";
    private string _calibError;

    public override void Enter(HomeToolContext ctx)
    {
        base.Enter(ctx);
        EnsureQuad();
    }

    public override void Exit() => _calibStage = 0;

    public override void HandleInput()
    {
        if (Ctx?.Doc == null) return;
        EnsureQuad();

        if (_calibStage == 0 || !Ctx.GroundPoint(out Vector2 p)) return;

        if (LeftClicked())
        {
            if (_calibStage == 1) { _calibA = p; _calibStage = 2; }
            else if (_calibStage == 2) { _calibB = p; _calibStage = 3; }
        }

        if (KeyDown(Key.Escape)) _calibStage = 0;
    }

    // ---------------------------------------------------------------------------------------

    public override void DrawRail()
    {
        if (Ctx?.Doc == null) return;

        var underlay = Ctx.Doc.underlay;

        if (underlay == null || string.IsNullOrEmpty(underlay.imageFileName))
        {
            UITheme.Note("Import a photo or scan of the floor plan, then set its scale by measuring " +
                         "one known dimension on it. Everything you trace afterwards will be at true size.");
            GUILayout.Space(8);
            if (UITheme.PrimaryButton("Import sketch…")) Import();
            return;
        }

        UITheme.Header("Sketch");
        UITheme.Note(underlay.imageFileName);

        GUILayout.Space(8);
        UITheme.Header("Scale");

        if (underlay.metersPerPixel <= 0f)
        {
            UITheme.StatusBadge("Not calibrated", false);
            UITheme.Note("The sketch has no real-world size yet. Nothing traced over it will measure " +
                         "correctly until it is calibrated.");
        }
        else
        {
            UITheme.StatusBadge("Calibrated", true);
            if (_texture != null)
                UITheme.Note($"Image is {Units.Format(_texture.width * underlay.metersPerPixel)} wide.");
        }

        GUILayout.Space(6);
        DrawCalibration(underlay);

        GUILayout.Space(10);
        UITheme.Header("Placement");

        float opacity = UITheme.SliderRow("Opacity", underlay.opacity, 0.05f, 1f, "0.00");
        if (!Mathf.Approximately(opacity, underlay.opacity))
        {
            underlay.opacity = opacity;
            ApplyTransform(underlay);
            Ctx.Controller.MarkDirty();
        }

        float rot = UITheme.SliderRow("Rotate", underlay.rotationDeg, -180f, 180f, "0", "°");
        if (!Mathf.Approximately(rot, underlay.rotationDeg))
        {
            underlay.rotationDeg = rot;
            ApplyTransform(underlay);
            Ctx.Controller.MarkDirty();
        }

        bool locked = GUILayout.Toggle(underlay.locked, "  Lock in place while tracing");
        if (locked != underlay.locked) { underlay.locked = locked; Ctx.Controller.MarkDirty(); }

        GUILayout.Space(8);
        if (UITheme.SecondaryButton("Replace sketch…")) Import();
        if (UITheme.DangerButton("Remove sketch"))
        {
            Ctx.Controller.RecordDocEdit("Remove sketch");
            Ctx.Doc.underlay = null;
            DestroyQuad();
            Ctx.Controller.MarkDirty();
        }
    }

    private void DrawCalibration(UnderlayDef underlay)
    {
        switch (_calibStage)
        {
            case 0:
                if (UITheme.PrimaryButton(underlay.metersPerPixel > 0f ? "Re-calibrate…" : "Set scale…"))
                {
                    _calibStage = 1;
                    _calibError = null;
                    _calibText = "";
                }
                break;

            case 1:
                UITheme.Note("Click the FIRST end of something you know the length of — a wall, a door, " +
                             "a graph-paper gridline.");
                if (UITheme.GhostButton("Cancel")) _calibStage = 0;
                break;

            case 2:
                UITheme.Note("Now click the OTHER end.");
                if (UITheme.GhostButton("Cancel")) _calibStage = 0;
                break;

            case 3:
                UITheme.Note("How long is that in real life?");
                _calibText = GUILayout.TextField(_calibText ?? "");
                UITheme.Note("e.g.  12' 6\"   ·   36\"   ·   3.8m");

                if (!string.IsNullOrEmpty(_calibError)) UITheme.Note("⚠ " + _calibError);

                GUILayout.BeginHorizontal();
                if (UITheme.PrimaryButton("Apply")) ApplyCalibration(underlay);
                if (UITheme.GhostButton("Cancel")) _calibStage = 0;
                GUILayout.EndHorizontal();
                break;
        }
    }

    // Rescales the image so the two clicked points end up the stated distance apart.
    private void ApplyCalibration(UnderlayDef underlay)
    {
        if (!Units.TryParse(_calibText, Units.BareUnit.Feet, out float realMeters) || realMeters <= 0f)
        {
            _calibError = "Could not read that measurement.";
            return;
        }

        float drawnMeters = Vector2.Distance(_calibA, _calibB);
        if (drawnMeters < 1e-4f)
        {
            _calibError = "Those two points are in the same place.";
            return;
        }

        float factor = realMeters / drawnMeters;

        Ctx.Controller.RecordDocEdit("Calibrate sketch");
        underlay.metersPerPixel = Mathf.Max(1e-6f, underlay.metersPerPixel <= 0f
            ? factor * DefaultMetersPerPixel()
            : underlay.metersPerPixel * factor);

        _calibStage = 0;
        _calibError = null;
        ApplyTransform(underlay);
        Ctx.Controller.MarkDirty();
        Ctx.Controller.Status("Scale set: that line is " + Units.Format(realMeters) + ".");
    }

    // Before the first calibration the quad is shown at an arbitrary 1 px = 1 cm, purely so there is
    // something visible to click on.
    private float DefaultMetersPerPixel() => 0.01f;

    private void Import()
    {
        FileBrowser.SetFilters(true, new FileBrowser.Filter("Images", ".png", ".jpg", ".jpeg", ".bmp"));
        FileBrowser.SetDefaultFilter(".png");
        FileBrowser.ShowLoadDialog(
            paths =>
            {
                if (paths == null || paths.Length == 0) return;

                string stored = HomeStore.ImportUnderlay(Ctx.Doc.id, paths[0], out string err);
                if (stored == null) { Ctx.Controller.Status("Import failed: " + err); return; }

                Ctx.Controller.RecordDocEdit("Import sketch");
                Ctx.Doc.underlay = new UnderlayDef
                {
                    imageFileName = stored,
                    originMeters = new[] { 0f, 0f },
                    metersPerPixel = 0f,      // uncalibrated until the user measures something
                    opacity = 0.6f,
                };
                _loadedFor = null;            // force a texture reload
                Ctx.Controller.MarkDirty();
                Ctx.Controller.Status("Sketch imported. Set its scale next.");
            },
            () => { },
            FileBrowser.PickMode.Files, false, null, null, "Select a floor-plan sketch", "Import");
    }

    // ---------------------------------------------------------------------------------------

    private void EnsureQuad()
    {
        var underlay = Ctx?.Doc?.underlay;
        if (underlay == null || string.IsNullOrEmpty(underlay.imageFileName)) { DestroyQuad(); return; }

        string key = Ctx.Doc.id + "|" + underlay.imageFileName;
        if (_quad != null && _loadedFor == key) { ApplyTransform(underlay); return; }

        DestroyQuad();

        string path = HomeStore.UnderlayPath(Ctx.Doc.id, underlay.imageFileName);
        if (path == null || !File.Exists(path)) return;

        _texture = new Texture2D(2, 2);
        if (!_texture.LoadImage(File.ReadAllBytes(path))) { DestroyQuad(); return; }
        _texture.wrapMode = TextureWrapMode.Clamp;

        _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _quad.name = "SketchUnderlay";
        Object.Destroy(_quad.GetComponent<Collider>());   // must never intercept a tracing click

        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        mat.mainTexture = _texture;
        mat.SetFloat("_Surface", 1f);                     // transparent
        mat.renderQueue = 3000;
        _quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

        _loadedFor = key;
        ApplyTransform(underlay);
    }

    private void ApplyTransform(UnderlayDef underlay)
    {
        if (_quad == null || _texture == null) return;

        float mpp = underlay.metersPerPixel > 0f ? underlay.metersPerPixel : DefaultMetersPerPixel();
        float w = _texture.width * mpp;
        float h = _texture.height * mpp;

        float y = (Ctx?.Level?.elevation ?? 0f) - 0.01f;   // just under the floors
        _quad.transform.position = new Vector3(
            (underlay.originMeters != null && underlay.originMeters.Length >= 2 ? underlay.originMeters[0] : 0f) + 0.5f * w,
            y,
            (underlay.originMeters != null && underlay.originMeters.Length >= 2 ? underlay.originMeters[1] : 0f) + 0.5f * h);
        _quad.transform.rotation = Quaternion.Euler(90f, underlay.rotationDeg, 0f);
        _quad.transform.localScale = new Vector3(w, h, 1f);

        var mr = _quad.GetComponent<MeshRenderer>();
        if (mr != null && mr.sharedMaterial != null)
        {
            var c = Color.white;
            c.a = Mathf.Clamp01(underlay.opacity);
            mr.sharedMaterial.color = c;
        }
    }

    private void DestroyQuad()
    {
        if (_quad != null) Object.Destroy(_quad);
        if (_texture != null) Object.Destroy(_texture);
        _quad = null;
        _texture = null;
        _loadedFor = null;
    }

    public override void DrawOverlay()
    {
        if (Ctx?.Cam == null || _calibStage == 0) return;

        float y = Ctx.Level?.elevation ?? 0f;
        var color = new Color(1f, 0.85f, 0.25f);

        if (_calibStage >= 2 && OverlayDraw.ToScreen(Ctx.Cam, _calibA, y, out Vector2 ga))
            OverlayDraw.Dot(ga, 11f, color);

        if (_calibStage == 2 && Ctx.GroundPoint(out Vector2 live) &&
            OverlayDraw.ToScreen(Ctx.Cam, _calibA, y, out Vector2 a2) &&
            OverlayDraw.ToScreen(Ctx.Cam, live, y, out Vector2 l2))
        {
            OverlayDraw.Line(a2, l2, color, 3f);
            OverlayDraw.Readout(l2, "Click the other end");
        }

        if (_calibStage == 3 &&
            OverlayDraw.ToScreen(Ctx.Cam, _calibA, y, out Vector2 a3) &&
            OverlayDraw.ToScreen(Ctx.Cam, _calibB, y, out Vector2 b3))
        {
            OverlayDraw.Line(a3, b3, color, 3f);
            OverlayDraw.Dot(b3, 11f, color);
            OverlayDraw.Readout((a3 + b3) * 0.5f, "How long is this?");
        }
    }
}
