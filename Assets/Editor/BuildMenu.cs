using UnityEditor;
using UnityEngine;

// One-click builds under a "Build" menu.
//
// HomeViz is the top-level item because this repo's product is CXRHomeViz; the Brownfield and VR
// builds still work but live under "Build → Legacy — Brownfield", out of the way of the app people
// actually ship from here. They are demoted, not deleted: the Brownfield stack is still in this
// repo and removing its menu items would leave it unbuildable. Its Python backend, however, now
// lives OUTSIDE this repo at ../CXRLayoutGen/ — see docs/BROWNFIELD.md.
//
// The VR builds ship only the VRViewer scene; XR is started at runtime by XRBootstrap (XR Plug-in
// Management has "Initialize XR on Startup" OFF), so no other build touches VR even though the XR
// packages are installed.
//
// Each item bakes in its own scene list + platform, so the builds stay cleanly separate regardless of
// the shared Build Settings scene list.
public static class BuildMenu
{
    private const string DesktopScene = "Assets/Scenes/BasicModel.unity";
    private const string VrScene      = "Assets/Scenes/VRViewer.unity";
    private const string HomeVizScene = "Assets/Scenes/HomeViz.unity";

    // CXRHomeViz — the interior/home visioning app. Ships ONLY the HomeViz scene, so the Brownfield
    // stack and the Python server are absent from this build: it is fully stand-alone, storing homes
    // as local files under Application.persistentDataPath. Ctrl+Shift+H.
    [MenuItem("Build/HomeViz (PC, Windows) %#h", priority = 0)]
    public static void BuildHomeViz()
    {
        Build(new[] { HomeVizScene }, BuildTarget.StandaloneWindows64,
              "Builds/HomeViz/CXRHomeViz.exe");
    }

    // The original Brownfield PC app, unchanged (no VR). Still needs the server running:
    // `cd ../CXRLayoutGen && python server/server.py`. Ctrl+Shift+D.
    [MenuItem("Build/Legacy — Brownfield/Desktop (PC, Windows) %#d", priority = 100)]
    public static void BuildDesktop()
    {
        Build(new[] { DesktopScene }, BuildTarget.StandaloneWindows64,
              "Builds/Desktop/CXR-Desktop.exe");
    }

    // Quest / standalone-Android headset. OpenXR is already enabled for the Android target.
    [MenuItem("Build/Legacy — Brownfield/VR — Quest (Android) %#q", priority = 101)]
    public static void BuildVRQuest()
    {
        Build(new[] { VrScene }, BuildTarget.Android,
              "Builds/VR-Quest/CXR-VR.apk");
    }

    // Tethered PCVR (Windows). NOTE: requires OpenXR enabled for the *Standalone* target in
    // Project Settings → XR Plug-in Management (the Android target already has it). Without that this
    // produces a flat Windows app.
    [MenuItem("Build/Legacy — Brownfield/VR — PCVR (Windows)", priority = 102)]
    public static void BuildVRPC()
    {
        Build(new[] { VrScene }, BuildTarget.StandaloneWindows64,
              "Builds/VR-PCVR/CXR-PCVR.exe");
    }

    private static void Build(string[] scenes, BuildTarget target, string outputPath)
    {
        var opts = new BuildPlayerOptions
        {
            scenes           = scenes,
            target           = target,
            targetGroup      = BuildPipeline.GetBuildTargetGroup(target),
            locationPathName = outputPath,
            options          = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[BuildMenu] {target} build → {outputPath} : {report.summary.result} " +
                  $"({report.summary.totalErrors} errors)");
    }
}
