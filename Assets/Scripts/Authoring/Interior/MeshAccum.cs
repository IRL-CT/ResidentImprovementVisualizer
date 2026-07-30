using System.Collections.Generic;
using UnityEngine;

// A small multi-submesh mesh accumulator, shared by WallMeshBuilder and RoomMeshBuilder.
//
// PathMesh builds its ribbons straight into raw vertex/triangle lists, which is fine for a
// single-material strip. Interior geometry needs several materials on ONE mesh — a wall's two faces
// are painted differently and its top and door reveals are a third material — so triangles have to be
// bucketed per submesh. That is the only thing this class adds.
//
// WINDING CONVENTION (the part that is easy to get wrong and expensive to debug):
// Unity is left-handed and treats a triangle as front-facing when its vertices appear CLOCKWISE from
// the viewer. Vector3.Cross also follows the left-hand rule, with Cross(right, up) == forward. Put
// together, the rule AddQuad relies on is:
//
//     pick in-plane axes u and v such that Vector3.Cross(u, v) == outward normal,
//     then pass the corners as  p0,  p0+u,  p0+u+v,  p0+v.
//
// Follow that and the face points outward every time, with no trial-and-error flipping.
public class MeshAccum
{
    private readonly List<Vector3> _verts = new List<Vector3>();
    private readonly List<Vector3> _normals = new List<Vector3>();
    private readonly List<Vector2> _uvs = new List<Vector2>();
    private readonly List<int>[] _tris;

    public MeshAccum(int subMeshCount)
    {
        _tris = new List<int>[Mathf.Max(1, subMeshCount)];
        for (int i = 0; i < _tris.Length; i++) _tris[i] = new List<int>();
    }

    public int SubMeshCount => _tris.Length;
    public int VertexCount => _verts.Count;
    public bool IsEmpty
    {
        get
        {
            foreach (var t in _tris) if (t.Count > 0) return false;
            return true;
        }
    }

    /// <summary>
    /// Appends one quad. Corners must be ordered p0, p0+u, p0+u+v, p0+v where Cross(u, v) == normal
    /// (see the winding note at the top of this file). Vertices are duplicated per quad so each face
    /// keeps a hard normal — correct for architectural geometry, where a wall corner is a crease and
    /// smoothing it would look melted.
    /// </summary>
    public void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                        Vector3 normal,
                        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3,
                        int subMesh)
    {
        int b = _verts.Count;
        _verts.Add(p0); _verts.Add(p1); _verts.Add(p2); _verts.Add(p3);
        _normals.Add(normal); _normals.Add(normal); _normals.Add(normal); _normals.Add(normal);
        _uvs.Add(uv0); _uvs.Add(uv1); _uvs.Add(uv2); _uvs.Add(uv3);

        var t = _tris[Mathf.Clamp(subMesh, 0, _tris.Length - 1)];
        t.Add(b); t.Add(b + 1); t.Add(b + 2);
        t.Add(b); t.Add(b + 2); t.Add(b + 3);
    }

    /// <summary>
    /// Appends a triangle with an explicit normal. Corners must already be wound clockwise as seen
    /// from the direction <paramref name="normal"/> points. Used by the polygon triangulator, which
    /// produces indexed triangles rather than quads.
    /// </summary>
    public void AddTriangle(Vector3 p0, Vector3 p1, Vector3 p2,
                            Vector3 normal,
                            Vector2 uv0, Vector2 uv1, Vector2 uv2,
                            int subMesh)
    {
        int b = _verts.Count;
        _verts.Add(p0); _verts.Add(p1); _verts.Add(p2);
        _normals.Add(normal); _normals.Add(normal); _normals.Add(normal);
        _uvs.Add(uv0); _uvs.Add(uv1); _uvs.Add(uv2);

        var t = _tris[Mathf.Clamp(subMesh, 0, _tris.Length - 1)];
        t.Add(b); t.Add(b + 1); t.Add(b + 2);
    }

    /// <summary>
    /// Produces the Mesh. Returns null when nothing was accumulated, so callers can skip creating an
    /// empty renderer. Uses a 32-bit index buffer when needed — a fully traced floor of a large group
    /// home can exceed 65k vertices once every wall face is a hard-normal quad.
    /// </summary>
    public Mesh ToMesh(string name = "Interior")
    {
        if (IsEmpty) return null;

        var mesh = new Mesh { name = name };
        if (_verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        mesh.SetVertices(_verts);
        mesh.SetNormals(_normals);
        mesh.SetUVs(0, _uvs);
        mesh.subMeshCount = _tris.Length;
        for (int i = 0; i < _tris.Length; i++)
            mesh.SetTriangles(_tris[i], i, calculateBounds: false);

        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    public void Clear()
    {
        _verts.Clear();
        _normals.Clear();
        _uvs.Clear();
        foreach (var t in _tris) t.Clear();
    }
}
