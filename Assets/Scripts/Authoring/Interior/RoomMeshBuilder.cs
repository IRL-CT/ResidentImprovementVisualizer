using System.Collections.Generic;
using UnityEngine;

// Floor and ceiling meshes for a RoomDef polygon.
//
// Floor and ceiling are built as SEPARATE meshes rather than two submeshes of one. That is what makes
// the ceiling toggle cheap: plan view and dollhouse view hide ceilings so you can see into the rooms,
// walkthrough shows them, and flipping between those is then just SetActive on one renderer instead
// of rebuilding geometry.
//
// WINDING (the reason PolygonTriangulator hands back "unrendered" triangles). Unity front-faces a
// triangle when its vertices read clockwise from the viewer. Looking down at the XZ plane from +Y,
// screen-right is +X and screen-up is +Z, so a polygon wound counter-clockwise in (x, z) also reads
// counter-clockwise on screen — the wrong way round for an up-facing floor. Floors therefore reverse
// the triangulator's order; ceilings, which face down, keep it.
//
// UVs are the raw world (x, z) in meters, so a 600 mm tile texture set to tile once per unit lands at
// real size and stays continuous across adjacent rooms instead of restarting per polygon.
public static class RoomMeshBuilder
{
    /// <summary>Up-facing floor slab at the level's elevation. Null for a degenerate polygon.</summary>
    public static Mesh BuildFloor(RoomDef room, LevelDef level)
    {
        var poly = PolygonTriangulator.ToVector2(room?.polygon);
        if (poly.Count < 3) return null;

        float y = level?.elevation ?? 0f;
        return BuildSurface(poly, y, Vector3.up, reverseWinding: true, "RoomFloor");
    }

    /// <summary>Down-facing ceiling plane at elevation + the room's effective ceiling height.</summary>
    public static Mesh BuildCeiling(RoomDef room, LevelDef level)
    {
        var poly = PolygonTriangulator.ToVector2(room?.polygon);
        if (poly.Count < 3) return null;

        float y = (level?.elevation ?? 0f) + EffectiveCeilingHeight(room, level);
        return BuildSurface(poly, y, Vector3.down, reverseWinding: false, "RoomCeiling");
    }

    public static float EffectiveCeilingHeight(RoomDef room, LevelDef level)
    {
        if (room != null && room.ceilingHeight > HomeConventions.EPS) return room.ceilingHeight;
        if (level != null && level.ceilingHeight > HomeConventions.EPS) return level.ceilingHeight;
        return HomeConventions.DEFAULT_CEILING_HEIGHT;
    }

    /// <summary>Floor area in square meters — the number shown in the room inspector.</summary>
    public static float FloorArea(RoomDef room)
        => PolygonTriangulator.Area(PolygonTriangulator.ToVector2(room?.polygon));

    // ---------------------------------------------------------------------------------------

    private static Mesh BuildSurface(List<Vector2> poly, float y, Vector3 normal,
                                     bool reverseWinding, string name)
    {
        var tris = PolygonTriangulator.Triangulate(poly);
        if (tris.Count < 3) return null;

        var acc = new MeshAccum(1);
        for (int i = 0; i + 2 < tris.Count; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            if (reverseWinding) (i0, i2) = (i2, i0);

            Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
            acc.AddTriangle(
                new Vector3(a.x, y, a.y),
                new Vector3(b.x, y, b.y),
                new Vector3(c.x, y, c.y),
                normal,
                a, b, c,          // world-meter UVs, continuous across rooms
                0);
        }

        return acc.ToMesh(name);
    }
}
