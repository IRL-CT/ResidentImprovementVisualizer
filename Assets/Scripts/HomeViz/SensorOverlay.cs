using System.Collections.Generic;
using UnityEngine;

// What the sensing layer can see, drawn over the plan.
//
// A plain class the controller owns and draws, like SelectionOverlay and TimelineBar, and for the
// identical reason: HomeEditController gates IHomeTool.DrawOverlay on !PointerOverUI, so an overlay
// drawn from a tool blanks the instant the cursor reaches the rail, which is exactly where the cursor
// goes to read the coverage figures this is illustrating.
//
// Everything is re-derived per frame rather than cached. Undo restores the whole HomeDoc without
// notifying anyone, so a held SensorDef would be outlining a device that no longer exists.
//
// THE ARGUMENT THIS OVERLAY MAKES is the one a table of percentages cannot: a corridor covered from
// one end and dark at the other is a picture, and "76%" is not. It is what turned a single motion
// sensor per room into two for a long one: the shape was obvious the moment it was drawn.
public class SensorOverlay
{
    /// <summary>Whether the overlay is showing. Off by default: a plan full of discs is unreadable
    /// while you are drawing walls, and this answers a question you go looking for.</summary>
    public bool Enabled;

    /// <summary>Draws every installed device's reach, and rings the ways out that nothing watches.</summary>
    public void Draw(Camera cam, LevelDef level, VariantDef variant, string selectedId)
    {
        if (!Enabled || cam == null || level?.sensors == null) return;

        float y = level.elevation;

        foreach (var sensor in level.sensors)
        {
            if (sensor == null || !sensor.included) continue;

            float radius = SensorDevices.RadiusOf(sensor);
            if (radius <= 0f) continue;

            var pose = SensorPose.Resolve(sensor, level, variant);
            if (!pose.resolved) continue;

            bool picked = sensor.id == selectedId;
            var color = Tint(sensor, picked);

            DrawCone(cam, pose.xz, y, radius, SensorDevices.AngleOf(sensor), pose.yaw, color);
        }

        // The gaps, in Warn rather than Danger: an unwatched back door is not an error, it is a
        // decision someone has not made yet. Same token, same reasoning, as the mode band's amber.
        foreach (var exit in SensorCoverage.UnmonitoredExits(level))
        {
            var wall = SensorPose.Find(level.walls, w => w.id, exit.wallId);
            if (wall == null) continue;

            Vector2 at = HomeMetrics.PointOnWall(wall, exit.offset);
            if (!OverlayDraw.ToScreen(cam, at, y, out Vector2 g)) continue;

            OverlayDraw.Circle(g, 16f, UITheme.Warn, 24, 2.5f);
            OverlayDraw.Readout(g, "Nothing watches this way out");
        }
    }

    // A device that is not selected draws faint, so forty of them do not paint the plan solid; the
    // selected one draws at full strength, which is how you check where one sensor is aimed.
    private static Color Tint(SensorDef sensor, bool selected)
    {
        var device = SensorDevices.Get(sensor.deviceType);
        var baseColor = device.privacy == SensorPrivacy.Video
            ? new Color(0.62f, 0.45f, 0.75f)      // the one camera reads differently on purpose
            : new Color(0.30f, 0.62f, 0.90f);

        return new Color(baseColor.r, baseColor.g, baseColor.b, selected ? 0.95f : 0.35f);
    }

    /// <summary>
    /// A detection envelope on the floor plane: an arc at the radius, plus the two edges of the cone
    /// back to the device. Shared with SensorTool's placement ghost, so the preview and the installed
    /// device draw exactly the same shape.
    /// </summary>
    /// <remarks>
    /// Drawn as an outline rather than filled. IMGUI has no polygon fill, and a filled disc built from
    /// forty overlapping quads over a plan is both slow and muddier than the line: the outline is
    /// what answers "does this reach the far end of the corridor", which is the whole question.
    /// </remarks>
    public static void DrawCone(Camera cam, Vector2 origin, float y, float radius, float angleDeg,
                                float yawDeg, Color color)
    {
        if (cam == null || radius <= 0f) return;

        bool full = angleDeg >= 359.5f;
        float half = 0.5f * Mathf.Clamp(angleDeg, 1f, 360f);

        const int segments = 32;
        var arc = new List<Vector2>(segments + 1);

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float a = (yawDeg - half + 2f * half * t) * Mathf.Deg2Rad;
            Vector2 p = origin + new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * radius;
            if (OverlayDraw.ToScreen(cam, p, y, out Vector2 g)) arc.Add(g);
        }

        if (arc.Count >= 2) OverlayDraw.Polyline(arc, color, 2f, closed: full);

        // The two straight edges back to the device. A full circle has none: its edges would be a
        // radius drawn twice down the middle of the disc.
        if (full || !OverlayDraw.ToScreen(cam, origin, y, out Vector2 center)) return;

        if (arc.Count >= 1) OverlayDraw.Line(center, arc[0], color, 2f);
        if (arc.Count >= 2) OverlayDraw.Line(center, arc[arc.Count - 1], color, 2f);
    }
}
