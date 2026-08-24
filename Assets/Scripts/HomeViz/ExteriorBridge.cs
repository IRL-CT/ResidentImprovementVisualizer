using System.Collections.Generic;

// Adapts a HomeViz variant's optional outdoor layer into the shape the existing WorldRenderer expects.
//
// This tiny file IS the exterior feature. Because VariantDef.exterior is a SiteDef: the Site tool's
// type, reused verbatim rather than re-invented: everything an outdoor additive needs already exists
// and is already tested:
//
//     PathDef                        walkway, entry ramp, driveway apron
//     FenceDef                       railing, privacy screen
//     SurfaceStrokeDef/TerrainZone   patio, deck, lawn, mulch bed
//     ObjectInstance                 outdoor bench, planter, raised bed
//     gradePoints                    the slope a ramp has to overcome
//
// WorldRenderer.RenderEnvironment takes an EnvironmentDef, so all that is missing is this translation.
// Buildings are deliberately left empty: the house itself is HomeRenderer's job, and handing
// WorldRenderer a BuildingInstance would draw a second, tile-based house on top of the real one.
public static class ExteriorBridge
{
    /// <summary>
    /// True when this variant has any outdoor content worth rendering. A SiteDef that exists but is
    /// empty does not count. Otherwise merely opening the exterior panel once would make every
    /// later comparison report "added an exterior".
    /// </summary>
    public static bool HasContent(VariantDef variant)
    {
        if (variant == null) return false;
        if ((variant.exteriorObjects?.Count ?? 0) > 0) return true;

        var s = variant.exterior;
        if (s == null) return false;

        return (s.paths?.Count ?? 0) > 0
            || (s.fences?.Count ?? 0) > 0
            || (s.surfaceStrokes?.Count ?? 0) > 0
            || (s.terrainZones?.Count ?? 0) > 0
            || (s.gradePoints?.Count ?? 0) > 0;
    }

    /// <summary>
    /// Wraps the variant's exterior as an EnvironmentDef for WorldRenderer. Returns null when there is
    /// nothing outdoors, so the caller can leave the whole Terrain subtree switched off.
    /// </summary>
    public static EnvironmentDef ToEnvironmentDef(HomeDoc doc, VariantDef variant)
    {
        if (doc == null || variant == null || !HasContent(variant)) return null;

        SiteDef site = variant.exterior ?? new SiteDef();

        // A home lot, not a 200 m industrial site: default the ground to something house-sized so an
        // enabled-but-unsized exterior does not stretch a 500 m terrain around a bungalow.
        if (site.terrainSize == null || site.terrainSize.Length < 2 ||
            site.terrainSize[0] <= 0f || site.terrainSize[1] <= 0f)
            site.terrainSize = new[] { 60f, 60f };

        return new EnvironmentDef
        {
            // Namespaced so a published exterior can never collide with a real Site environment
            // id in WorldRenderer's per-environment render map.
            id = "homeviz:" + doc.id + ":" + variant.id,
            name = doc.name + " · " + variant.name,
            version = doc.version,
            tags = new List<string> { "homeviz-exterior" },
            locked = false,
            site = site,
            buildingInstances = new List<BuildingInstance>(),   // the house is HomeRenderer's job
            objectInstances = variant.exteriorObjects ?? new List<ObjectInstance>(),
        };
    }

    /// <summary>
    /// Creates the outdoor layer for a variant that has none, sized to a typical residential lot.
    /// Called the first time a user turns the exterior on for a home.
    /// </summary>
    public static SiteDef NewExterior()
        => new SiteDef
        {
            terrainSize = new[] { 60f, 60f },
            terrainZones = new List<TerrainZoneDef>(),
            paths = new List<PathDef>(),
            fences = new List<FenceDef>(),
            surfaceStrokes = new List<SurfaceStrokeDef>(),
            gradePoints = new List<GradePointDef>(),
            scaleNote = "Residential lot around the home.",
            maxGradeHeight = 4f,          // a garden grade, not a site-scale landform
            lotBoundary = null,
            outsideTerrainType = "grass", // not "water": a home's surroundings are not a river
        };
}
