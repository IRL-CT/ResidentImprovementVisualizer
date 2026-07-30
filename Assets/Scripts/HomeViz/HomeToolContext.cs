using UnityEngine;
using UnityEngine.InputSystem;

// Everything a tool is allowed to touch, handed to it explicitly.
//
// EditController (5,424 lines) grew the way it did partly because every tool reaches for whatever it
// needs through FindFirstObjectByType and serialized fields, so nothing is separable. HomeViz passes
// a context instead: a tool receives exactly this and nothing else, which is what keeps each tool a
// small file that can be read, changed, or deleted on its own.
public class HomeToolContext
{
    public HomeEditController Controller;
    public HomeRenderer Renderer;
    public ViewController View;
    public EditHistory History;
    public Camera Cam;

    public HomeDoc Doc => Controller != null ? Controller.Doc : null;
    public VariantDef Variant => Controller != null ? Controller.Variant : null;
    public LevelDef Level => Controller != null ? Controller.Level : null;

    /// <summary>
    /// True when the active variant refuses edits. The baseline is locked by default — it is the
    /// record of how the home actually is, and every proposal is compared against it.
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

    /// <summary>Raycast into the rendered home and return the element marker that was hit.</summary>
    public bool PickElement(out HomeElementMarker marker, out RaycastHit hit)
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
            var m = h.collider.GetComponentInParent<HomeElementMarker>();
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
}

// One editing tool. Adding a tool is a new file plus one line in the controller's registry — the
// property EditController never had, which is why its 13 modes are a flat switch spread over
// thousands of lines.
public interface IHomeTool
{
    string Id { get; }
    string DisplayName { get; }

    void Enter(HomeToolContext ctx);
    void Exit();

    /// <summary>Per-frame input. Not called while the pointer is over a rail.</summary>
    void HandleInput();

    /// <summary>The tool's section of the right-hand inspector rail. Called from OnGUI.</summary>
    void DrawRail();

    /// <summary>Scene overlay: ghosts, handles, readouts. Called from OnGUI (screen space).</summary>
    void DrawOverlay();
}

// Shared plumbing so each tool only writes what makes it different.
public abstract class HomeToolBase : IHomeTool
{
    protected HomeToolContext Ctx;

    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public virtual void Enter(HomeToolContext ctx) { Ctx = ctx; }
    public virtual void Exit() { }
    public virtual void HandleInput() { }
    public virtual void DrawRail() { }
    public virtual void DrawOverlay() { }

    /// <summary>Shows the locked notice and returns true when editing must be refused.</summary>
    protected bool RefuseIfLocked()
    {
        if (Ctx == null || !Ctx.IsLocked) return false;
        UITheme.Note("This variant is locked. Unlock it, or create a proposal from it, to make changes.");
        return true;
    }

    protected static bool LeftClicked()
        => Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;

    protected static bool LeftReleased()
        => Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;

    protected static bool LeftHeld()
        => Mouse.current != null && Mouse.current.leftButton.isPressed;

    protected static bool KeyDown(Key key)
        => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
}
