using UnityEngine;
using UnityEngine.InputSystem;

// Camera behaviour for the three ways of looking at a home.
//
//   Plan        orthographic top-down, ceilings off — the tracing and measuring view
//   Dollhouse   perspective orbit, ceilings off — the "show the family the layout" view
//   Walkthrough first person at eye height, ceilings on — the view that actually convinces people
//
// The walkthrough carries a STANDING/SEATED toggle. That single switch is the cheapest meaningful
// accessibility feature in the whole tool: dropping the camera from 1.60 m to 1.19 m shows what a
// wheelchair user actually sees — over a counter or not, out of a window or into its sill, past a
// wall-hung cabinet or into it. It costs one float and answers questions a plan view cannot.
//
// The orbit scheme (right-drag orbit, middle-drag pan, scroll zoom, WASD pivot) is deliberately
// identical to EditController's, so anyone who has used the Brownfield tool already knows it.
public class ViewController : MonoBehaviour
{
    public enum Mode { Plan, Dollhouse, Walkthrough }

    // USER WIRES THIS IN INSPECTOR: falls back to Camera.main.
    [SerializeField] private Camera cam;
    // USER WIRES THIS IN INSPECTOR:
    [SerializeField] private HomeRenderer homeRenderer;

    [Header("Orbit / plan")]
    [SerializeField] private float orbitSpeed = 0.25f;
    [SerializeField] private float panSpeed = 0.01f;
    [SerializeField] private float zoomSpeed = 0.6f;
    [SerializeField] private float keyPanSpeed = 6f;
    [SerializeField] private float minDistance = 2f;
    [SerializeField] private float maxDistance = 80f;

    [Header("Walkthrough")]
    [SerializeField] private float walkSpeed = 1.4f;      // ~5 km/h, an unhurried indoor pace
    [SerializeField] private float lookSpeed = 0.12f;
    [SerializeField] private float bodyRadius = 0.25f;

    public Mode Current { get; private set; } = Mode.Plan;

    /// <summary>Standing (1.60 m) vs seated wheelchair (1.19 m) eye height.</summary>
    public bool Seated { get; private set; }

    public float EyeHeight => Seated ? HomeConventions.EYE_HEIGHT_SEATED
                                     : HomeConventions.EYE_HEIGHT_STANDING;

    private Vector3 _pivot = Vector3.zero;
    private float _yaw = 45f, _pitch = 40f, _distance = 18f;
    private float _orthoSize = 10f;

    private CharacterController _body;
    private float _walkYaw, _walkPitch;
    private float _fallSpeed;

    // ---------------------------------------------------------------------------------------

    private void Awake()
    {
        if (cam == null) cam = Camera.main;
        if (homeRenderer == null) homeRenderer = FindFirstObjectByType<HomeRenderer>();
    }

    private void Start() => SetMode(Mode.Plan);

    private void Update()
    {
        if (cam == null) return;

        switch (Current)
        {
            case Mode.Walkthrough: UpdateWalkthrough(); break;
            case Mode.Plan: UpdatePlan(); break;
            default: UpdateOrbit(); break;
        }
    }

    // ---------------------------------------------------------------------------------------

    public void SetMode(Mode mode)
    {
        Current = mode;
        if (cam == null) return;

        // Ceilings would block every top-down and orbit view, so they only exist in walkthrough.
        homeRenderer?.SetCeilingsVisible(mode == Mode.Walkthrough);

        switch (mode)
        {
            case Mode.Plan:
                DestroyBody();
                cam.orthographic = true;
                cam.orthographicSize = _orthoSize;
                FrameContent();
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.transform.position = _pivot + Vector3.up * 50f;
                break;

            case Mode.Dollhouse:
                DestroyBody();
                cam.orthographic = false;
                ApplyOrbit();
                break;

            case Mode.Walkthrough:
                cam.orthographic = false;
                EnterWalkthrough();
                break;
        }
    }

    public void SetSeated(bool seated)
    {
        Seated = seated;
        if (Current == Mode.Walkthrough && _body != null)
        {
            // Keep the feet planted and move only the eye — the point of the toggle is the change in
            // sightline, so the body must not hop when it is flipped.
            _body.height = Mathf.Max(0.2f, EyeHeight);
            _body.center = new Vector3(0f, 0.5f * _body.height, 0f);
            if (cam != null)
                cam.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
        }
    }

    public void ToggleSeated() => SetSeated(!Seated);

    // ---------------------------------------------------------------------------------------

    private void UpdatePlan()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
        {
            _orthoSize = Mathf.Clamp(_orthoSize - scroll * 0.01f * zoomSpeed * _orthoSize, 1f, 200f);
            cam.orthographicSize = _orthoSize;
        }

        Vector2 delta = mouse.delta.ReadValue();
        if (mouse.middleButton.isPressed || mouse.rightButton.isPressed)
            _pivot += new Vector3(-delta.x, 0f, -delta.y) * panSpeed * _orthoSize * 0.15f;

        _pivot += KeyPan(Vector3.forward, Vector3.right);
        cam.transform.position = _pivot + Vector3.up * 50f;
        cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    private void UpdateOrbit()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 delta = mouse.delta.ReadValue();

        if (mouse.rightButton.isPressed)
        {
            _yaw += delta.x * orbitSpeed;
            _pitch = Mathf.Clamp(_pitch - delta.y * orbitSpeed, 5f, 89f);
        }

        if (mouse.middleButton.isPressed)
        {
            Vector3 right = cam.transform.right;
            Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
            _pivot += (-right * delta.x - fwd * delta.y) * panSpeed * _distance * 0.1f;
        }

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f)
            _distance = Mathf.Clamp(_distance - scroll * 0.01f * zoomSpeed * _distance, minDistance, maxDistance);

        _pivot += KeyPan(
            Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized,
            cam.transform.right);

        ApplyOrbit();
    }

    private Vector3 KeyPan(Vector3 forward, Vector3 right)
    {
        var kb = Keyboard.current;
        if (kb == null) return Vector3.zero;

        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move += forward;
        if (kb.sKey.isPressed) move -= forward;
        if (kb.dKey.isPressed) move += right;
        if (kb.aKey.isPressed) move -= right;

        return move.sqrMagnitude > 0.001f
            ? move.normalized * keyPanSpeed * Time.deltaTime * Mathf.Max(1f, _distance * 0.1f)
            : Vector3.zero;
    }

    private void ApplyOrbit()
    {
        var rot = Quaternion.Euler(_pitch, _yaw, 0f);
        cam.transform.position = _pivot + rot * new Vector3(0f, 0f, -_distance);
        cam.transform.rotation = rot;
    }

    // ---------------------------------------------------------------------------------------

    private void EnterWalkthrough()
    {
        // Start where the orbit camera was looking, so entering the walkthrough drops you into the
        // part of the home you were just studying rather than at the world origin.
        Vector3 start = _pivot;
        start.y = (homeRenderer?.Level?.elevation ?? 0f) + 0.1f;

        var holder = new GameObject("WalkBody");
        holder.transform.SetParent(transform, false);
        holder.transform.position = start;

        _body = holder.AddComponent<CharacterController>();
        _body.radius = bodyRadius;
        _body.height = Mathf.Max(0.2f, EyeHeight);
        _body.center = new Vector3(0f, 0.5f * _body.height, 0f);
        _body.slopeLimit = 45f;
        _body.stepOffset = 0.2f;

        cam.transform.SetParent(holder.transform, false);
        cam.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
        cam.transform.localRotation = Quaternion.identity;

        _walkYaw = _yaw;
        _walkPitch = 0f;
        _fallSpeed = 0f;
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
        var kb = Keyboard.current;

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

    /// <summary>Centres the camera on the current level's content. Used on load and on demand.</summary>
    public void FrameContent()
    {
        if (!ContentBounds(out Bounds b))
        {
            _pivot = Vector3.zero;
            _orthoSize = 10f;
            _distance = 18f;
            return;
        }

        _pivot = new Vector3(b.center.x, 0f, b.center.z);

        float extent = Mathf.Max(b.extents.x, b.extents.z);
        _orthoSize = Mathf.Max(2f, extent * 1.25f);
        _distance = Mathf.Clamp(extent * 2.6f, minDistance, maxDistance);

        if (cam != null && cam.orthographic) cam.orthographicSize = _orthoSize;
    }

    // Derived from the level data rather than renderer bounds so framing works before anything has
    // been drawn (a freshly created home with one traced wall, for instance).
    private bool ContentBounds(out Bounds bounds)
    {
        bounds = new Bounds();
        var level = homeRenderer?.Level;
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
