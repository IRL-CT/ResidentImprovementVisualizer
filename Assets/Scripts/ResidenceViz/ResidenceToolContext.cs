using UnityEngine;
using UnityEngine.InputSystem;

// Everything a tool is allowed to touch, handed to it explicitly.
//
// EditController (5,424 lines) grew the way it did partly because every tool reaches for whatever it
// needs through FindFirstObjectByType and serialized fields, so nothing is separable. ResidenceViz passes
// a context instead: a tool receives exactly this and nothing else, which is what keeps each tool a
// small file that can be read, changed, or deleted on its own.
public class ResidenceToolContext
{
    public ResidenceEditController Controller;
    public ResidenceRenderer Renderer;
    public ViewController View;
    public EditHistory History;
    public Camera Cam;

    public ResidenceDoc Doc => Controller != null ? Controller.Doc : null;
    public VariantDef Variant => Controller != null ? Controller.Variant : null;
    public LevelDef Level => Controller != null ? Controller.Level : null;

    /// <summary>
    /// True when the active variant refuses edits. Only ever the baseline: it is the residence as it
    /// stands, every proposal is measured against it, and ModeBand's "Modify base environment" is
    /// the one switch that opens it. A proposal is always editable.
    /// </summary>
    public bool IsLocked => Variant == null || Variant.locked;

    // ---- undo helpers ----------------------------------------------------------------------

    /// <summary>Snapshot before a discrete edit (one click, one key). Call immediately BEFORE mutating.</summary>
    public void RecordEdit(string label) => History?.RecordBefore(EditHistory.Scope.Environment, label);

    /// <summary>Snapshot once at the start of a drag, so the whole gesture collapses to one undo step.</summary>
    public void BeginGesture(string label) => History?.BeginGesture(EditHistory.Scope.Environment, label);

    /// <summary>Marks the document dirty and re-renders. Every mutation ends here.</summary>
    public void Changed(bool rebuildAll = true)
    {
        Controller?.MarkDirty();
        if (rebuildAll) Renderer?.Rebuild();
    }

    // ---- pointer helpers -------------------------------------------------------------------

    public Vector2 MousePosition => Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;

    /// <summary>
    /// Where the cursor meets this level's floor plane, in world XZ. Everything drawn in plan is
    /// drawn on this plane, so this is the workhorse of every drawing tool.
    /// </summary>
    public bool GroundPoint(out Vector2 xz)
    {
        xz = Vector2.zero;
        if (Cam == null) return false;

        float y = Level?.elevation ?? 0f;
        Ray ray = Cam.ScreenPointToRay(MousePosition);

        var plane = new Plane(Vector3.up, new Vector3(0f, y, 0f));
        if (!plane.Raycast(ray, out float dist)) return false;

        Vector3 hit = ray.GetPoint(dist);
        xz = new Vector2(hit.x, hit.z);
        return true;
    }

    /// <summary>
    /// Where the pick ray meets a rendered wall FACE, in world XZ. Falls back to nothing rather than
    /// to the floor, so the caller decides. The floor projection of a click on a wall lands on the
    /// far side of that wall under the angled overview camera: the parallax SelectTool's drag
    /// remarks walk through, so anything deciding which face a click meant must prefer this.
    /// </summary>
    public bool WallPoint(out Vector2 xz)
    {
        xz = Vector2.zero;
        if (Cam == null) return false;

        Ray ray = Cam.ScreenPointToRay(MousePosition);
        var hits = Physics.RaycastAll(ray, 500f);
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            var m = h.collider.GetComponentInParent<ResidenceElementMarker>();
            if (m == null || m.kind != ResidenceElementMarker.Kind.Wall) continue;
            xz = new Vector2(h.point.x, h.point.z);
            return true;
        }
        return false;
    }

    /// <summary>Raycast into the rendered residence and return the element marker that was hit.</summary>
    public bool PickElement(out ResidenceElementMarker marker, out RaycastHit hit)
    {
        marker = null;
        hit = default;
        if (Cam == null) return false;

        Ray ray = Cam.ScreenPointToRay(MousePosition);
        var hits = Physics.RaycastAll(ray, 500f);
        if (hits == null || hits.Length == 0) return false;

        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        foreach (var h in hits)
        {
            var m = h.collider.GetComponentInParent<ResidenceElementMarker>();
            if (m == null) continue;
            marker = m;
            hit = h;
            return true;
        }
        return false;
    }

    public bool ShiftHeld => Keyboard.current != null &&
                             (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

    public bool CtrlHeld => Keyboard.current != null &&
                            (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);

    /// <summary>True when the cursor is over an IMGUI rail, so world clicks must be ignored.</summary>
    public bool OverUI => Controller != null && Controller.PointerOverUI;

    /// <summary>
    /// Set when the controller has already claimed this frame's click. Currently only to select an
    /// item and carry the UI to the Select tab. Cleared at the top of every Update.
    ///
    /// One flag read by one line in ResidenceToolBase.LeftClicked, which is how all nine tools respect it
    /// without any of them knowing it exists.
    /// </summary>
    public bool ClickConsumed;
}

// One editing tool. Adding a tool is a new file plus one line in the controller's registry: the
// property EditController never had, which is why its 13 modes are a flat switch spread over
// thousands of lines.
public interface IResidenceTool
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>
    /// How to use this tool, in a sentence or two. Shown on hover over the tool's tab in the command
    /// bar (and its chip, where a stage has more than one).
    ///
    /// This is where the instruction paragraph each rail used to print permanently now lives. A tool is
    /// used for minutes at a time and its instructions are read once, so they were paying for their
    /// screen space every second after the first.
    /// </summary>
    string Hint { get; }

    /// <summary>
    /// True while this tool's own click must win over selecting whatever the cursor is on. The wall and
    /// room tools claim clicks only mid-run. Starting a run somewhere is fine, but a click that should
    /// extend one must never jump to the Select tab instead.
    /// </summary>
    bool ClaimsClicks { get; }

    void Enter(ResidenceToolContext ctx);
    void Exit();

    /// <summary>Per-frame input. Not called while the pointer is over a rail.</summary>
    void HandleInput();

    /// <summary>
    /// Per-frame, and UNGATED. Called whether or not the pointer is over a rail.
    ///
    /// This is where a tool applies work its own rail deferred. HandleInput cannot do it: the
    /// controller skips that call while the cursor is over a rail, which is exactly where the cursor
    /// is at the moment a rail button is clicked, so anything queued from DrawRail and drained there
    /// would fire only once the pointer happened to wander over the scene.
    ///
    /// Deferring at all is the OnGUI rule this codebase runs on: an action that changes a panel's
    /// control count between the layout and repaint passes is the Mismatched LayoutGroup that
    /// _pendingStage and friends exist to prevent.
    /// </summary>
    void Tick();

    /// <summary>The tool's section of the right-hand inspector rail. Called from OnGUI.</summary>
    void DrawRail();

    /// <summary>Scene overlay: ghosts, handles, readouts. Called from OnGUI (screen space).</summary>
    void DrawOverlay();
}

// Shared plumbing so each tool only writes what makes it different.
public abstract class ResidenceToolBase : IResidenceTool
{
    protected ResidenceToolContext Ctx;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string Hint => null;
    public virtual bool ClaimsClicks => false;

    public virtual void Enter(ResidenceToolContext ctx) { Ctx = ctx; }
    public virtual void Exit() { }
    public virtual void HandleInput() { }
    public virtual void Tick() { }
    public virtual void DrawRail() { }
    public virtual void DrawOverlay() { }

    /// <summary>
    /// Shows the read-only state and returns true when editing must be refused.
    ///
    /// A badge, and nothing else. This used to carry its own Unlock button, which made two of them,
    /// one here in every tool rail, one in ModeBand: with nothing to say which you were looking at.
    /// The switch has one residence now, in the band directly above this rail, where it is on screen
    /// whichever tool is open. That keeps the refusal actionable without duplicating the action.
    /// </summary>
    protected bool RefuseIfLocked()
    {
        if (Ctx == null || !Ctx.IsLocked) return false;

        UITheme.LockBadge("Read-only",
            "The base environment is locked. Press Modify base environment above to edit it, or work "
            + "in a proposal.");
        return true;
    }

    // The one chokepoint every tool's click goes through, which is what makes the controller able to
    // claim a click for the selection without any tool having to opt in.
    protected bool LeftClicked()
        => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame
           && !(Ctx?.ClickConsumed ?? false);

    protected static bool LeftReleased()
        => Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;

    protected static bool LeftHeld()
        => Mouse.current != null && Mouse.current.leftButton.isPressed;

    // Gated on typing as well as on the pointer: HandleInput only runs with the cursor off the rails,
    // but a field keeps keyboard focus wherever the cursor wanders, so without this a name typed into
    // the rail would also spin the chair and delete the selection.
    protected static bool KeyDown(Key key)
        => Keyboard.current != null && !ResidenceEditController.TypingInUI
           && Keyboard.current[key].wasPressedThisFrame;
}
