using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

// Camera behaviour for the two ways of looking at a residence.
//
//   Overview    perspective free-look, ceilings off: the drawing, measuring and "show the family
//               the layout" view, and the one the app opens in
//   Walkthrough first person at eye height, ceilings on: the view that actually convinces people
//
// There used to be a third, Plan: an orthographic top-down camera that could only pan and zoom. It is
// gone. Free-look reaches 89 degrees of pitch, so looking straight down at the plan is a gesture in
// the overview rather than a mode of its own, and being a mode of its own cost a segmented cell in a
// bar that is already tight, a second set of camera rules to keep in step, and a state in which the
// camera could not be turned at all.
//
// The walkthrough carries a STANDING/SEATED toggle. That single switch is the cheapest meaningful
// accessibility feature in the whole tool: dropping the camera from 1.60 m to 1.19 m shows what a
// wheelchair user actually sees. Over a counter or not, out of a window or into its sill, past a
// wall-hung cabinet or into it. It costs one float and answers questions a floor plan cannot.
//
// The overview used to be EditController's turntable (right-drag spun the house around a pivot) 
// on the grounds that anyone who had used the Site tool already knew it. Site looks at a site from
// OUTSIDE, where a turntable is the right gesture; here you are looking INTO a dwelling, and being
// unable to turn your head reads as unnatural next to any game. So right-drag is now a mouse-locked
// FREE-LOOK: the cursor is captured and hidden for the duration (a drag can never die at the window
// edge), the camera turns in place, and the pointer is put back where it was pressed on release.
//
// Q/E still own vertical, which is what keeps W/S horizontal rather than burrowing into the floor
// when you look down. Middle-drag pan captures the cursor too, for the same reason: no drag should be
// cut short by the edge of the screen.
//
// _pivot stays the canonical state and ApplyOrbit is untouched. Free-look is just "rotate about the
// EYE instead of about the pivot, then re-derive the pivot as eye + forward * distance". That is why
// FocusOn, FrameContent and the scroll dolly all keep working unchanged.
public class ViewController : MonoBehaviour
{
    public enum Mode { Overview, Walkthrough }

    // USER WIRES THIS IN INSPECTOR: falls back to Camera.main.
    [SerializeField] private Camera cam;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private ResidenceRenderer residenceRenderer;
    // Optional: found in Awake if unwired. Read for one thing only, whether a drag START landed on a
    // rail. See BeginDrag.
    [SerializeField] private ResidenceEditController editController;

    [Header("Look")]
    // Degrees per pixel of mouse travel, NOT per second: Mouse.delta already accumulates a frame's
    // motion, so multiplying by Time.deltaTime is what would make this framerate-dependent. Matches
    // walkthrough's lookSpeed, because since free-look landed the two are the same gesture.
    [SerializeField] private float lookSensitivity = 0.12f;
    [SerializeField] private float panSpeed = 0.01f;
    [SerializeField] private float zoomSpeed = 0.6f;
    [SerializeField] private float keyPanSpeed = 6f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 80f;

    [Header("Walkthrough")]
    [SerializeField] private float walkSpeed = 1.4f;      // ~5 km/h, an unhurried indoor pace
    [SerializeField] private float lookSpeed = 0.12f;
    // Deliberately narrower than a person. The walkthrough is a camera, not a simulation: a shoulder
    // that catches on a door jamb costs the viewer the room they were trying to see.
    [SerializeField] private float bodyRadius = 0.2f;

    public Mode Current { get; private set; } = Mode.Overview;

    /// <summary>Standing (1.60 m) vs seated wheelchair (1.19 m) eye height.</summary>
    public bool Seated { get; private set; }

    public float EyeHeight => Seated ? ResidenceConventions.EYE_HEIGHT_SEATED
                                     : ResidenceConventions.EYE_HEIGHT_STANDING;

    // The orbit target. Its Y is live (Q/E move it) so the overview can drop to eye level or rise
    // above the roofline.
    private Vector3 _pivot = Vector3.zero;
    private float _yaw = 45f, _pitch = 40f, _distance = 18f;

    // How close FocusOn pulls in. Chosen so one person plus the furniture they are using is in shot,
    // any closer and the answer to "where is Alice" loses the room that makes it mean anything.
    private const float FOCUS_DISTANCE = 5f;

    private const float FLOOR_CLEARANCE = 0.05f;   // the camera stops just above the floor, not in it
    private const float LIFT_ABOVE = 25f;          // well clear of any single story

    private CharacterController _body;
    private float _walkYaw, _walkPitch;
    private float _fallSpeed;

    // Latched drags, the EditController convention: a drag only BEGINS in the 3D view, and once begun
    // it keeps going wherever the cursor travels. Polling isPressed the way this file used to means a
    // press that lands on a rail also drives the camera.
    private bool _lookDrag, _panDrag;
    private Vector2 _cursorBeforeCapture;
    // Locking warps the OS cursor to the center of the screen, and that warp arrives as one large
    // delta. Applying it snaps the view a quarter turn on the first frame of every look.
    private bool _swallowDelta;

    // Free-look reaches level and above, which the turntable never could: half of why the old gesture
    // felt wrong. The downward limit keeps 89 so the near-top-down reach is not lost.
    private const float MIN_PITCH = -85f;
    private const float MAX_PITCH = 89f;

    // ---------------------------------------------------------------------------------------

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
        if (residenceRenderer == null) residenceRenderer = FindFirstObjectByType<ResidenceRenderer>();
        if (editController == null) editController = FindFirstObjectByType<ResidenceEditController>();
    }

    private void Start() => SetMode(Mode.Overview);

    // A captured cursor that outlives the thing that captured it is invisible with no way back, so
    // every exit releases it: leaving the component, losing the window, and switching view mode.
    private void OnDisable() => EndDrags();

    private void OnApplicationFocus(bool focused)
    {
        if (!focused) EndDrags();
    }

    private void Update()
    {
        if (cam == null) return;

        switch (Current)
        {
            case Mode.Walkthrough: UpdateWalkthrough(); break;
            default: UpdateOrbit(); break;
        }
    }

    // ---------------------------------------------------------------------------------------

    public void SetMode(Mode mode)
    {
        // Switching out from under a live drag would strand the capture. Walkthrough never takes one.
        EndDrags();

        Current = mode;
        if (cam == null) return;

        // Ceilings would block every view from above, so they only exist in walkthrough.
        residenceRenderer?.SetCeilingsVisible(mode == Mode.Walkthrough);

        // Both modes are perspective, and the scene's camera is serialized that way, but a stray
        // orthographic camera would silently ignore every pose below, so it is settled once here.
        cam.orthographic = false;

        // Re-entering a mode without this leaves the previous WalkBody in the scene, colliding with
        // the new one.
        DestroyBody();

        if (mode == Mode.Walkthrough) EnterWalkthrough();
        else ApplyOrbit();
    }

    public void SetSeated(bool seated)
    {
        Seated = seated;
        if (Current == Mode.Walkthrough && _body != null)
        {
            // Keep the feet planted and move only the eye: the point of the toggle is the change in
            // sightline, so the body must not hop when it is flipped.
            _body.height = Mathf.Max(0.2f, EyeHeight);
            _body.center = new Vector3(0f, 0.5f * _body.height, 0f);
            if (cam != null)
                cam.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
        }
    }

    public void ToggleSeated() => SetSeated(!Seated);

    // ---------------------------------------------------------------------------------------
    // Cursor capture. The rule a game gets right and a visible cursor cannot: while you are looking
    // around, the pointer is not a pointer, so it is hidden, locked to the center, and restored to the
    // exact pixel it was pressed at when you let go.

    /// <summary>True while the pointer is over a rail: a drag may not BEGIN there. Once begun it
    /// keeps going regardless, which is what ResidenceEditController's gizmo note depends on.</summary>
    private bool OverUI => editController != null && editController.PointerOverUI;

    /// <summary>Someone is in a text field, so W/A/S/D/Q/E are letters rather than camera keys.
    /// The controller latches this inside OnGUI, where the focus state is authoritative; the raw
    /// GUIUtility read is only the fallback for a scene with no controller wired.</summary>
    private bool TypingInUI
        => editController != null ? ResidenceEditController.TypingInUI : GUIUtility.keyboardControl != 0;

    /// <summary>
    /// Latches a drag on the frame <paramref name="button"/> goes down in the 3D view, and captures the
    /// cursor with it. Returns the drag's new state.
    /// </summary>
    private bool BeginDrag(ButtonControl button, bool live)
    {
        if (live || !button.wasPressedThisFrame || OverUI) return live;

        // Guarded because the other gesture may already hold the capture, and overwriting the recorded
        // position would release the cursor somewhere it was never pressed.
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            var mouse = Mouse.current;
            if (mouse != null) _cursorBeforeCapture = mouse.position.ReadValue();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _swallowDelta = true;
        }
        return true;
    }

    /// <summary>
    /// Whether a latched drag survives this frame. It is the release path that has to be paranoid
    /// rather than the press path: two things end a drag that no button event reports: a release
    /// while the window is unfocused (no wasReleasedThisFrame ever arrives, hence isPressed rather
    /// than the edge), and Esc in the Editor, which drops Unity's lock behind our back and would
    /// otherwise leave Cursor.visible false forever.
    /// </summary>
    private static bool StillDragging(bool held, bool live)
        => live && held && Cursor.lockState == CursorLockMode.Locked;

    /// <summary>
    /// Releases the capture once no gesture is holding it. Right and middle can be down at once, so
    /// this is the one place that decides: an end-of-drag that released on its own would drop the
    /// cursor into the middle of the other one.
    /// </summary>
    private void SyncCapture()
    {
        if (!_lookDrag && !_panDrag) ReleaseCursor();
    }

    private void EndDrags()
    {
        _lookDrag = _panDrag = false;
        ReleaseCursor();
    }

    private void ReleaseCursor()
    {
        if (Cursor.lockState == CursorLockMode.None && Cursor.visible) return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // Unity re-centers the pointer when a lock is released. Putting it back is what makes letting
        // go feel like nothing happened, rather than like the cursor teleported.
        Mouse.current?.WarpCursorPosition(_cursorBeforeCapture);
    }

    /// <summary>This frame's mouse travel, or zero on the frame a capture began. See _swallowDelta.</summary>
    private Vector2 DragDelta(Mouse mouse)
    {
        if (!_swallowDelta) return mouse.delta.ReadValue();

        _swallowDelta = false;
        return Vector2.zero;
    }

    // ---------------------------------------------------------------------------------------

    private void UpdateOrbit()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        _lookDrag = StillDragging(mouse.rightButton.isPressed, _lookDrag);
        _panDrag = StillDragging(mouse.middleButton.isPressed, _panDrag);
        _lookDrag = BeginDrag(mouse.rightButton, _lookDrag);
        _panDrag = BeginDrag(mouse.middleButton, _panDrag);
        SyncCapture();

        Vector2 delta = DragDelta(mouse);

        if (_lookDrag) FreeLook(delta);

        if (_panDrag)
        {
            Vector3 right = cam.transform.right;
            Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            _pivot += (-right * delta.x - fwd * delta.y) * panSpeed * _distance * 0.1f;
        }

        // Not while a rail's scroll view is under the cursor: scrolling a list must scroll the list.
        float scroll = OverUI ? 0f : mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            _distance = Mathf.Clamp(_distance - scroll * 0.01f * zoomSpeed * _distance, minDistance, maxDistance);

        _pivot += KeyPan(
            Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized,
            cam.transform.right);

        KeyLift();

        // Unconditional, not just after a lift: the bound depends on pitch and distance, so looking
        // around or zooming while parked at the floor has to re-settle the pivot too.
        ClampLift();
        ApplyOrbit();
    }

    /// <summary>
    /// Turn the camera in place, the way every game does it: yaw and pitch rotate about the EYE, not
    /// about the pivot. Expressed as a round trip through the eye so that _pivot stays the canonical
    /// state: everything downstream (FocusOn, the scroll dolly, KeyPan, ClampLift, plan mode) reads
    /// _pivot and keeps meaning what it meant. Afterwards the pivot is simply eye + forward * distance,
    /// which is what makes scroll go on dollying along the view axis.
    /// </summary>
    private void FreeLook(Vector2 delta)
    {
        Vector3 eye = _pivot + Quaternion.Euler(_pitch, _yaw, 0f) * new Vector3(0f, 0f, -_distance);

        _yaw += delta.x * lookSensitivity;
        _pitch = Mathf.Clamp(_pitch - delta.y * lookSensitivity, MIN_PITCH, MAX_PITCH);

        _pivot = eye + Quaternion.Euler(_pitch, _yaw, 0f) * new Vector3(0f, 0f, _distance);
    }

    private Vector3 KeyPan(Vector3 forward, Vector3 right)
    {
        var kb = Keyboard.current;
        if (kb == null || TypingInUI) return Vector3.zero;

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move += forward;
        if (kb.sKey.isPressed) move -= forward;
        if (kb.dKey.isPressed) move += right;
        if (kb.aKey.isPressed) move -= right;

        return move.sqrMagnitude > 0.001f
            ? move.normalized * keyPanSpeed * Time.deltaTime * Mathf.Max(1f, _distance * 0.1f)
            : Vector3.zero;
    }

    /// <summary>
    /// Q down, E up. Same speed law as KeyPan (including the distance scaling) so lifting and
    /// panning feel like one gesture rather than two unrelated speeds. The range is ClampLift's job.
    /// </summary>
    private void KeyLift()
    {
        var kb = Keyboard.current;
        if (kb == null || TypingInUI) return;

        float dir = 0f;
        if (kb.eKey.isPressed) dir += 1f;
        if (kb.qKey.isPressed) dir -= 1f;
        if (Mathf.Abs(dir) < 0.001f) return;

        _pivot.y += dir * keyPanSpeed * Time.deltaTime * Mathf.Max(1f, _distance * 0.1f);
    }

    /// <summary>
    /// Q descends until the CAMERA reaches the floor: not the pivot. The two are not the same thing
    /// and the difference is the whole feature: the camera sits sin(pitch) * distance above its
    /// target, so a pivot stopped at floor level leaves the camera metres over the roof, and holding
    /// Q appears to stop while you are still looking down at the building from outside.
    ///
    /// So the pivot's floor is derived from where it puts the camera, which means the pivot itself is
    /// free to go below the floor: that is what descending to standing height around a target across
    /// the room requires. Re-run every frame, because looking around or zooming changes the
    /// offset and a bound computed once would be wrong the moment either did.
    ///
    /// The UPPER bound switches on which of the two is higher, and that is free-look's doing. Capping
    /// the pivot is right while the camera is above it. Looking down, this is bit-identical to what
    /// it always did, so zooming out at a long distance still lifts the camera freely. But looking UP
    /// puts the pivot above the eye by construction, and capping it there would drag the camera down
    /// out of a gesture that must not translate it at all. So above level the cap moves onto the eye,
    /// which is what stops E from flying to space while you look at the ceiling.
    /// </summary>
    private void ClampLift()
    {
        float elev = Elevation;
        float camAbovePivot = Mathf.Sin(_pitch * Mathf.Deg2Rad) * _distance;

        float min = elev + FLOOR_CLEARANCE - camAbovePivot;
        float max = elev + LIFT_ABOVE - Mathf.Min(0f, camAbovePivot);

        _pivot.y = Mathf.Clamp(_pivot.y, min, Mathf.Max(min, max));
    }

    private float Elevation => residenceRenderer?.Level?.elevation ?? 0f;

    private void ApplyOrbit()
    {
        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        cam.transform.position = _pivot + rot * new Vector3(0f, 0f, -_distance);
        cam.transform.rotation = rot;
    }

    // ---------------------------------------------------------------------------------------

    private void EnterWalkthrough()
    {
        var holder = new GameObject("WalkBody");
        holder.transform.SetParent(transform, false);
        holder.transform.position = StandableStart();

        _body = holder.AddComponent<CharacterController>();
        _body.radius = bodyRadius;
        _body.height = Mathf.Max(0.2f, EyeHeight);
        _body.center = new Vector3(0f, 0.5f * _body.height, 0f);
        _body.slopeLimit = 50f;
        _body.stepOffset = 0.25f;               // over a threshold without a hop

        // Unity's defaults are the two things that make a CharacterController feel like it is dragging
        // along every surface: a skin fatter than a fifth of this body's radius, and a floor under
        // which a frame's movement is silently thrown away.
        _body.skinWidth = Mathf.Max(0.005f, bodyRadius * 0.1f);
        _body.minMoveDistance = 0f;

        cam.transform.SetParent(holder.transform, false);
        cam.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
        cam.transform.localRotation = Quaternion.identity;

        _walkYaw = _yaw;
        _walkPitch = 0f;
        _fallSpeed = 0f;
    }

    /// <summary>
    /// Somewhere you can actually stand. The orbit pivot is the framing center of the residence, which is
    /// as likely to be a wall as a room, and a CharacterController that starts inside a wall never
    /// gets out. So: the room under the pivot, else the largest room, and the center of its largest
    /// inscribed circle, which is by construction the point furthest from any wall. That metric
    /// ignores furniture (ResidenceMetrics.cs:195), which is exactly right here. Furniture no longer
    /// blocks the walkthrough.
    /// </summary>
    private Vector3 StandableStart()
    {
        float elev = Elevation;
        var fallback = new Vector3(_pivot.x, elev + 0.1f, _pivot.z);

        var level = residenceRenderer?.Level;
        if (level?.rooms == null || level.rooms.Count == 0) return fallback;

        var room = ResidenceMetrics.RoomAt(new Vector2(_pivot.x, _pivot.z), level);
        if (room == null)
        {
            float bestArea = 0f;
            foreach (var r in level.rooms)
            {
                if (r?.polygon == null || r.polygon.Length < 3) continue;
                float area = ResidenceMetrics.RoomArea(r);
                if (area <= bestArea) continue;
                bestArea = area; room = r;
            }
        }
        if (room == null) return fallback;

        var circle = ResidenceMetrics.LargestInscribedCircle(room);
        if (!circle.valid) return fallback;

        return new Vector3(circle.center.x, elev + 0.1f, circle.center.y);
    }

    private void DestroyBody()
    {
        if (_body == null) return;

        if (cam != null) cam.transform.SetParent(transform, true);
        if (Application.isPlaying) Destroy(_body.gameObject); else DestroyImmediate(_body.gameObject);
        _body = null;
    }

    private void UpdateWalkthrough()
    {
        if (_body == null) { EnterWalkthrough(); return; }

        var mouse = Mouse.current;
        var kb = TypingInUI ? null : Keyboard.current;   // R and WASD are letters while a field has focus

        // The escape hatch. Only walls and floors are solid now, so being wedged should not happen,
        // but "should not" is not "cannot", and a viewer stuck in a wall mid-meeting has no other way
        // out except leaving the walkthrough entirely.
        if (kb != null && kb.rKey.wasPressedThisFrame)
        {
            _body.enabled = false;                                  // Move() would fight the teleport
            _body.transform.position = StandableStart();
            _body.enabled = true;
            _fallSpeed = 0f;
        }

        // Look only while a button is held. Free mouselook would fight the IMGUI rails, which stay
        // on screen in the walkthrough so the change list is still readable.
        if (mouse != null && (mouse.rightButton.isPressed || mouse.leftButton.isPressed))
        {
            Vector2 delta = mouse.delta.ReadValue();
            _walkYaw += delta.x * lookSpeed;
            _walkPitch = Mathf.Clamp(_walkPitch - delta.y * lookSpeed, -85f, 85f);
        }

        _body.transform.rotation = Quaternion.Euler(0f, _walkYaw, 0f);
        cam.transform.localRotation = Quaternion.Euler(_walkPitch, 0f, 0f);

        Vector3 move = Vector3.zero;
        if (kb != null)
        {
            if (kb.wKey.isPressed) move += _body.transform.forward;
            if (kb.sKey.isPressed) move -= _body.transform.forward;
            if (kb.dKey.isPressed) move += _body.transform.right;
            if (kb.aKey.isPressed) move -= _body.transform.right;
            if (move.sqrMagnitude > 0.001f) move = move.normalized;
            if (kb.leftShiftKey.isPressed) move *= 2f;
        }

        // Enough gravity to settle onto the floor collider and drop down a threshold, without the
        // launch-off-a-doorsill feel a full 9.81 gives at walking pace.
        _fallSpeed = _body.isGrounded ? -0.5f : _fallSpeed - 9.81f * Time.deltaTime;

        Vector3 velocity = move * walkSpeed + Vector3.up * _fallSpeed;
        _body.Move(velocity * Time.deltaTime);
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Centers the camera on the current level's content. Used on load and on demand.</summary>
    public void FrameContent()
    {
        if (!ContentBounds(out Bounds b))
        {
            _pivot = new Vector3(0f, Elevation, 0f);
            _distance = 18f;
            return;
        }

        _pivot = new Vector3(b.center.x, Elevation, b.center.z);

        float extent = Mathf.Max(b.extents.x, b.extents.z);
        _distance = Mathf.Clamp(extent * 2.6f, minDistance, maxDistance);
    }

    /// <summary>
    /// Re-centers the overview camera on a point. With <paramref name="closeUp"/> it also
    /// pulls in: the answer to "where is Alice" has to be visible, and at the 18 m the samples open
    /// at, moving the center alone shifts a person by a few pixels and reads as nothing happening.
    ///
    /// The zoom only ever tightens (Mathf.Min), never loosens: someone already looking closely at a
    /// bathroom must not be yanked backwards by asking where a resident is. The point's Y is honoured,
    /// so the subject is centered on screen rather than sitting at the bottom of it.
    ///
    /// No-op in walkthrough, where the camera is a body standing somewhere and must not teleport.
    /// </summary>
    public void FocusOn(Vector3 worldPoint, bool closeUp = false)
    {
        if (Current == Mode.Walkthrough) return;

        _pivot = worldPoint;
        ClampLift();
        if (!closeUp) return;

        _distance = Mathf.Clamp(Mathf.Min(_distance, FOCUS_DISTANCE), minDistance, maxDistance);
    }

    // Derived from the level data rather than renderer bounds so framing works before anything has
    // been drawn (a freshly created residence with one traced wall, for instance).
    private bool ContentBounds(out Bounds bounds)
    {
        bounds = new Bounds();
        var level = residenceRenderer?.Level;
        if (level == null) return false;

        // Accumulate into a local: C# forbids capturing an `out` parameter inside a local function.
        var acc = new Bounds();
        bool any = false;
        void Include(float x, float z)
        {
            var p = new Vector3(x, 0f, z);
            if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
            else acc.Encapsulate(p);
        }

        if (level.walls != null)
            foreach (var w in level.walls)
            {
                if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) continue;
                Include(w.a[0], w.a[1]);
                Include(w.b[0], w.b[1]);
            }

        if (level.rooms != null)
            foreach (var r in level.rooms)
            {
                if (r?.polygon == null) continue;
                foreach (var p in r.polygon)
                    if (p != null && p.Length >= 2) Include(p[0], p[1]);
            }

        if (level.furniture != null)
            foreach (var f in level.furniture)
                if (f?.position != null && f.position.Length >= 3) Include(f.position[0], f.position[2]);

        bounds = acc;
        return any;
    }
}
