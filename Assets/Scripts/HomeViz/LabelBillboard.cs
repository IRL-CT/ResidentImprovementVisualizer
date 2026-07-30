using UnityEngine;

// Keeps a placeholder's name label facing the camera.
//
// Without this the labels are flat quads pinned to one orientation, which makes them edge-on and
// effectively invisible from directly above — and plan view is the view people spend the most time
// in. Since the labels are the only thing distinguishing one grey box from another until real
// furniture art exists, a label you cannot read from the main working view is no label at all.
[ExecuteAlways]
public class LabelBillboard : MonoBehaviour
{
    [Tooltip("Keep the text upright in world Z rather than rolling with the camera.")]
    public bool lockUpright = true;

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        if (lockUpright && cam.orthographic && Vector3.Dot(cam.transform.forward, Vector3.down) > 0.9f)
        {
            // Looking straight down (plan view): lay the label flat on the ground plane so it reads
            // as a floor-plan annotation rather than a sign standing on edge.
            transform.rotation = Quaternion.Euler(90f, cam.transform.eulerAngles.y, 0f);
            return;
        }

        // Otherwise face the camera. Using the camera's forward rather than a look-at keeps every
        // label in the scene parallel, which reads far more calmly than a field of labels each
        // splaying toward a different point.
        transform.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);
    }
}
