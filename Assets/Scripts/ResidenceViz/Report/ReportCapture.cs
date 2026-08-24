using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Photographing the same residence twice, from the same place, once as it is and once as proposed.
//
// The four constraints below are the whole file. Each one has already caught this codebase out
// somewhere, and a rewrite that drops any of them will look like it works and produce a broken
// document.
//
// 1. A HIDDEN CAMERA, NOT ScreenCapture. The entire UI here is IMGUI: the rails, the timeline, the
//    selection halos, the readout chips, so a backbuffer grab photographs the app rather than the
//    residence. A separate camera never sees any of it.
//
// 2. NEVER FROM OnGUI. Rendering a camera swaps the active render target, and doing that during
//    IMGUI's repaint blanks the whole UI. That is the warning ThumbnailCache carries at its head and
//    the reason its jobs are queued. The rail's button sets a flag; ResidenceEditController.Update drains
//    it and starts this coroutine.
//
// 3. TWO REBUILDS, NOT TWO PER SHOT. Each variant is rendered once and every framing is taken from
//    it, rather than swapping variants per shot. Beyond being ~n times faster on a furnished plan, it
//    is what guarantees the two halves of a pair share a camera pose exactly: the pose is computed
//    once and used twice.
//
// 4. FRAMED OVER THE UNION OF BOTH VARIANTS. A shot framed on the proposal alone crops whatever the
//    baseline had and the proposal removed, so the "before" image loses precisely the thing the
//    reader is looking for. Bounds are unioned before either shot is taken.
//
// And it puts everything back: the active variant, the ceiling and occupant visibility the view mode
// wants, and the selection. A report is a read, and a read must not leave the editor somewhere else.
namespace ResidenceViz.Report
{
    public class ReportCapture : MonoBehaviour
    {
        public const int Width = 1600;
        public const int Height = 1000;

        /// <summary>JPEG, not PNG. See <see cref="Encode"/>.</summary>
        public const int JpegQuality = 88;

        /// <summary>Breathing room around a framed room or plan, as a fraction of its size.</summary>
        private const float Margin = 0.14f;

        private static ReportCapture _running;

        /// <summary>
        /// Starts a capture. Refuses to start a second one: a report is a modal thing that moves the
        /// document under the editor, and two at once would interleave their variant switches.
        /// </summary>
        public static void Run(ResidenceEditController controller, ResidenceRenderer renderer, ResidenceDoc doc,
                               string fromVariantId, string toVariantId,
                               System.Action<string> status)
        {
            if (_running != null) { status?.Invoke("A report is already being generated."); return; }

            var go = new GameObject("~ReportCapture") { hideFlags = HideFlags.HideAndDontSave };
            _running = go.AddComponent<ReportCapture>();
            _running.StartCoroutine(_running.Capture(controller, renderer, doc, fromVariantId,
                                                     toVariantId, status));
        }

        // ---------------------------------------------------------------------------------------

        private Camera _cam;

        private IEnumerator Capture(ResidenceEditController controller, ResidenceRenderer renderer, ResidenceDoc doc,
                                    string fromId, string toId, System.Action<string> status)
        {
            status?.Invoke("Generating report…");
            yield return null;   // let the toast paint before the scene starts churning

            var from = ResidenceStore.FindVariant(doc, fromId);
            var to = ResidenceStore.FindVariant(doc, toId);
            var changes = VariantDiff.Compare(from, to);

            // Everything to restore, read before anything is touched.
            string restoreVariant = doc.activeVariantId;
            bool restoreCeilings = renderer.CeilingsVisible;
            bool restoreOccupants = renderer.OccupantsVisible;
            bool restoreGhost = renderer.GhostOn;
            var restoreKind = controller.SelectedKind;
            string restoreId = controller.SelectedId;

            var report = ReportBuilder.Build(doc, from, to, changes);
            var shots = Framings(from, to, changes);

            EnsureCamera();

            // The ghost would appear in both halves of every pair and describe the very difference the
            // pair exists to show. Off for the duration.
            renderer.SetGhostVariant(null, false);
            renderer.SetOccupantsVisible(false);
            renderer.SetCeilingsVisible(false);

            // Devices stay VISIBLE (they are part of what the proposal installs) but frozen at idle.
            // A sensor lit red in the "after" shot and not in the "before" one is the clock having
            // reached 03:20, not anything the proposal did, and a reader will try to read it as part
            // of the proposal. Exactly the reason the occupants are hidden two lines up.
            bool restoreSensorStates = renderer.SetSensorStatesLive(false);

            var before = new List<byte[]>();
            var after = new List<byte[]>();

            // Rebuilt once per (variant, story) rather than once per variant, because the renderer
            // draws one level at a time. Still NOT once per shot: every framing of a given story is
            // taken from the one rebuild, which is what guarantees a before/after pair shares a camera
            // pose exactly, and what keeps a six-room report to a handful of rebuilds rather than
            // sixteen. On the single-story residence this is two rebuilds, exactly as it always was.
            for (int i = 0; i < shots.Count; i++) { before.Add(null); after.Add(null); }

            foreach (int levelIndex in StoriesIn(shots))
            {
                renderer.RenderResidence(doc, fromId, levelIndex);
                yield return null;                   // let the rebuild's meshes reach the GPU
                for (int i = 0; i < shots.Count; i++)
                    if (shots[i].levelIndex == levelIndex) before[i] = Shoot(shots[i]);
            }

            foreach (int levelIndex in StoriesIn(shots))
            {
                renderer.RenderResidence(doc, toId, levelIndex);
                yield return null;
                for (int i = 0; i < shots.Count; i++)
                    if (shots[i].levelIndex == levelIndex) after[i] = Shoot(shots[i]);
            }

            for (int i = 0; i < report.sections.Count && i < shots.Count; i++)
            {
                report.sections[i].beforeImage = before[i];
                report.sections[i].afterImage = after[i];
            }

            // Put the editor back exactly where it was, in the reverse order it was taken apart.
            renderer.RenderResidence(doc, restoreVariant);
            doc.activeVariantId = restoreVariant;
            renderer.SetCeilingsVisible(restoreCeilings);
            renderer.SetOccupantsVisible(restoreOccupants);
            renderer.SetSensorStatesLive(restoreSensorStates);
            if (restoreGhost) renderer.SetGhostVariant(fromId, true);
            controller.SelectedKind = restoreKind;
            controller.SelectedId = restoreId;

            string path = HtmlReportWriter.Write(report, doc, to, out string error);
            status?.Invoke(path != null
                ? "Report saved to " + System.IO.Path.GetFileName(path)
                : "Report failed: " + error);

            if (path != null) Application.OpenURL("file:///" + path.Replace('\\', '/'));

            Cleanup();
        }

        private void Cleanup()
        {
            _running = null;
            if (_cam != null) Destroy(_cam.gameObject);
            Destroy(gameObject);
        }

        // ---------------------------------------------------------------------------------------
        // Framing
        // ---------------------------------------------------------------------------------------

        private struct Shot
        {
            public Vector3 position;
            public Quaternion rotation;
            public bool orthographic;
            public float orthoSize;

            // Which story has to be on screen for this shot to show anything. The renderer draws one
            // level at a time, so a shot of an upstairs bathroom taken while the ground floor is
            // rendered photographs an empty patch of ground.
            public int levelIndex;
        }

        // One shot per PHOTOGRAPHED section, in the same order ReportBuilder emits them: the plan, the
        // overview, then a close-up per changed room. Building both lists from the same source is what
        // keeps section[i] and shot[i] describing the same thing.
        //
        // ReportBuilder appends the Technology section LAST and deliberately without images: a pair of
        // shots differing by a 70 mm grey box says nothing, so this list is shorter than the section
        // list, and the pairing loop stops at whichever runs out first. Any future unphotographed
        // section must also go at the END, or the two lists slip by one and every caption lies.
        private List<Shot> Framings(VariantDef from, VariantDef to, List<VariantDiff.Change> changes)
        {
            var shots = new List<Shot>();

            // The orientation pair is of the ENTRANCE story, framed over every floor's footprint so
            // the two shots share one pose. A building-wide plan view is what the ground floor is; the
            // upper stories get their own close-ups below.
            Bounds residence = Union(AllLevelBounds(from), AllLevelBounds(to));

            shots.Add(Plan(residence, 0));
            shots.Add(Overview(residence, 0));

            foreach (var cr in ReportBuilder.ChangedRooms(from, to, changes))
            {
                // Unioned with the room as it was, so a room the proposal enlarged is not cropped in
                // the "before" shot, and a room it shrank is not cropped in the "after" one.
                Bounds b = RoomBounds(cr.room);
                var was = FindRoom(ReportBuilder.MatchingLevel(from, cr), cr.room.id);
                if (was != null) b = Union(b, RoomBounds(was));
                shots.Add(Plan(b, cr.levelIndex));
            }
            return shots;
        }

        /// <summary>Every story's footprint, so a framing over the whole building crops none of it.</summary>
        private Bounds AllLevelBounds(VariantDef v)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var l in v?.levels ?? new List<LevelDef>())
            {
                Bounds lb = LevelBounds(l);
                if (lb.size == Vector3.zero) continue;
                b = any ? Union(b, lb) : lb;
                any = true;
            }
            return b;
        }

        private Shot Plan(Bounds b, int levelIndex)
        {
            float half = Mathf.Max(b.extents.x, b.extents.z * Aspect()) * (1f + Margin);
            return new Shot
            {
                position = new Vector3(b.center.x, b.center.y + 50f, b.center.z),
                rotation = Quaternion.Euler(90f, 0f, 0f),
                orthographic = true,
                orthoSize = Mathf.Max(1f, half / Aspect()),
                levelIndex = levelIndex,
            };
        }

        private Shot Overview(Bounds b, int levelIndex)
        {
            // The angle the Overview view mode opens at, so the report looks like the app.
            const float yaw = 45f, pitch = 40f;
            float extent = Mathf.Max(b.extents.x, b.extents.z, 2f);
            float distance = extent * 3.1f;
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            return new Shot
            {
                position = b.center + rot * new Vector3(0f, 0f, -distance),
                rotation = rot,
                orthographic = false,
                orthoSize = 0f,
                levelIndex = levelIndex,
            };
        }

        /// <summary>The distinct stories a shot list touches, in ascending order.</summary>
        private static List<int> StoriesIn(List<Shot> shots)
        {
            var seen = new List<int>();
            foreach (var s in shots)
                if (!seen.Contains(s.levelIndex)) seen.Add(s.levelIndex);
            seen.Sort();
            return seen;
        }

        private static float Aspect() => (float)Width / Height;

        private static Bounds LevelBounds(LevelDef level)
        {
            var acc = new Bounds();
            bool any = false;
            void Include(float x, float z)
            {
                var p = new Vector3(x, 0f, z);
                if (!any) { acc = new Bounds(p, Vector3.zero); any = true; }
                else acc.Encapsulate(p);
            }

            if (level?.walls != null)
                foreach (var w in level.walls)
                {
                    if (w?.a == null || w.b == null || w.a.Length < 2 || w.b.Length < 2) continue;
                    Include(w.a[0], w.a[1]);
                    Include(w.b[0], w.b[1]);
                }
            if (level?.rooms != null)
                foreach (var r in level.rooms)
                {
                    if (r?.polygon == null) continue;
                    foreach (var p in r.polygon) if (p != null && p.Length >= 2) Include(p[0], p[1]);
                }
            return any ? acc : new Bounds(Vector3.zero, new Vector3(10f, 0f, 10f));
        }

        private static Bounds RoomBounds(RoomDef room)
        {
            var acc = new Bounds();
            bool any = false;
            foreach (var p in room?.polygon ?? new float[0][])
            {
                if (p == null || p.Length < 2) continue;
                var v = new Vector3(p[0], 0f, p[1]);
                if (!any) { acc = new Bounds(v, Vector3.zero); any = true; }
                else acc.Encapsulate(v);
            }
            return any ? acc : new Bounds(Vector3.zero, new Vector3(4f, 0f, 4f));
        }

        private static Bounds Union(Bounds a, Bounds b)
        {
            var acc = a;
            acc.Encapsulate(b);
            return acc;
        }

        private static RoomDef FindRoom(LevelDef level, string id)
        {
            if (level?.rooms == null || string.IsNullOrEmpty(id)) return null;
            foreach (var r in level.rooms) if (r != null && r.id == id) return r;
            return null;
        }

        // ---------------------------------------------------------------------------------------
        // The camera
        // ---------------------------------------------------------------------------------------

        private void EnsureCamera()
        {
            if (_cam != null) return;

            var go = new GameObject("cam") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(transform, false);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;                  // driven manually; never part of the normal render
            _cam.nearClipPlane = 0.05f;
            _cam.farClipPlane = 5000f;
            _cam.fieldOfView = 55f;

            // Match what the user is looking at, so the report is recognisably the same scene.
            var main = Camera.main;
            if (main != null)
            {
                _cam.clearFlags = main.clearFlags;
                _cam.backgroundColor = main.backgroundColor;
                _cam.cullingMask = main.cullingMask;
            }
        }

        private byte[] Shoot(Shot shot)
        {
            _cam.transform.SetPositionAndRotation(shot.position, shot.rotation);
            _cam.orthographic = shot.orthographic;
            if (shot.orthographic) _cam.orthographicSize = shot.orthoSize;

            var rt = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;

            // Cross-pipeline synchronous render, with the built-in path as a fallback: the same pair
            // ThumbnailCache uses, and for the same reason: URP's Render Graph does not honour a bare
            // Camera.Render on every version.
            var req = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(_cam, req))
                RenderPipeline.SubmitRenderRequest(_cam, req);
            else { _cam.targetTexture = rt; _cam.Render(); _cam.targetTexture = null; }

            RenderTexture.active = rt;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            tex.Apply();

            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);

            byte[] bytes = Encode(tex);
            Destroy(tex);
            return bytes;
        }

        /// <summary>
        /// JPEG rather than PNG, and the choice is load-bearing rather than cosmetic. A 1600×1000 PNG
        /// of shaded 3-D geometry runs 300-800 KB, and a six-room report holds sixteen images, so a
        /// PNG report is a ~15 MB HTML file that no one can email. It is also the format a PDF writer
        /// wants: PDF embeds JPEG bytes verbatim through DCTDecode, so the follow-up format gets its
        /// images for nothing.
        ///
        /// RGB24 above rather than RGBA32 for the same reason ThumbnailCache forces alpha to 1: URP
        /// writes alpha 0 for opaque geometry, and a JPEG has no alpha to be confused by.
        /// </summary>
        private static byte[] Encode(Texture2D tex) => tex.EncodeToJPG(JpegQuality);
    }
}
