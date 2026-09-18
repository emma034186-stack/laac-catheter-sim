using UnityEngine;
using System.Collections.Generic;

namespace SofaUnity
{
    /// <summary>
    /// Runtime-only diagnostic: every few frames, samples the catheter's collision
    /// mesh vertices (world space) against the vessel's mesh vertices (world space)
    /// and shows the closest distance found, top-left of the screen. This is a
    /// nearest-vertex approximation, not SOFA's actual contact-surface distance,
    /// but it's enough to tell whether the catheter is genuinely staying clear of
    /// the wall, grazing it, or already inside it, when nothing shows up in the
    /// Console. Attached dynamically at runtime by CatheterWallDistanceHUDInjector,
    /// not saved into the scene.
    /// </summary>
    public class CatheterWallDistanceHUD : MonoBehaviour
    {
        public string catheterObjectName = "SofaBeamAdapterModel  -  InstrumentCombined@Collision - KV1";
        public string vesselObjectName = "OglModel  -  OglVessel";
        public float recomputeInterval = 0.3f;

        MeshFilter catheterMF;
        MeshFilter vesselMF;
        float nextTime = 0f;
        float lastDistance = -1f;
        Vector3 lastCathPoint, lastWallPoint;
        bool ok = false;
        string status = "initializing...";
        readonly List<Vector3> cathBuffer = new List<Vector3>(256);
        readonly List<Vector3> wallBuffer = new List<Vector3>(2600);

        void FindTargets()
        {
            GameObject cathGO = GameObject.Find(catheterObjectName);
            GameObject vesGO = GameObject.Find(vesselObjectName);
            if (cathGO != null) catheterMF = cathGO.GetComponent<MeshFilter>();
            if (vesGO != null) vesselMF = vesGO.GetComponent<MeshFilter>();
        }

        void Update()
        {
            if (catheterMF == null || vesselMF == null)
                FindTargets();

            if (Time.time < nextTime)
                return;
            nextTime = Time.time + recomputeInterval;

            if (catheterMF == null || vesselMF == null || catheterMF.sharedMesh == null || vesselMF.sharedMesh == null)
            {
                ok = false;
                status = "missing objects/meshes (cath=" + (catheterMF != null) + " vessel=" + (vesselMF != null) + ")";
                return;
            }

            catheterMF.sharedMesh.GetVertices(cathBuffer);
            vesselMF.sharedMesh.GetVertices(wallBuffer);
            int cathN = cathBuffer.Count, wallN = wallBuffer.Count;
            if (cathN == 0 || wallN == 0)
            {
                ok = false;
                status = "empty mesh (cathVerts=" + cathN + " wallVerts=" + wallN + ")";
                return;
            }

            Transform cathT = catheterMF.transform;
            Transform wallT = vesselMF.transform;

            // subsample for perf if meshes are big
            int cathStep = Mathf.Max(1, cathN / 200);
            int wallStep = Mathf.Max(1, wallN / 800);

            float best = float.MaxValue;
            Vector3 bestCath = Vector3.zero, bestWall = Vector3.zero;
            for (int i = 0; i < cathN; i += cathStep)
            {
                Vector3 cw = cathT.TransformPoint(cathBuffer[i]);
                for (int j = 0; j < wallN; j += wallStep)
                {
                    Vector3 ww = wallT.TransformPoint(wallBuffer[j]);
                    float d = Vector3.Distance(cw, ww);
                    if (d < best)
                    {
                        best = d;
                        bestCath = cw;
                        bestWall = ww;
                    }
                }
            }

            lastDistance = best;
            lastCathPoint = bestCath;
            lastWallPoint = bestWall;
            ok = true;
            status = "ok";
        }

        void OnGUI()
        {
            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.fontSize = 16;
            style.alignment = TextAnchor.UpperLeft;

            string text;
            Color c;
            if (!ok)
            {
                text = "Wall dist HUD: " + status;
                c = Color.white;
            }
            else
            {
                text = string.Format("Cath-Wall nearest dist: {0:F2} mm\ncath pt: {1}\nwall pt: {2}",
                    lastDistance, lastCathPoint.ToString("F1"), lastWallPoint.ToString("F1"));
                if (lastDistance >= 3f) c = Color.green;
                else if (lastDistance >= 1f) c = Color.yellow;
                else c = Color.red;
            }

            GUI.color = c;
            GUI.Box(new Rect(10, 10, 340, 80), text, style);
            GUI.color = Color.white;
        }
    }

    public static class CatheterWallDistanceHUDInjector
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Inject()
        {
            GameObject go = new GameObject("CatheterWallDistanceHUD (runtime-only)");
            go.AddComponent<CatheterWallDistanceHUD>();
        }
    }
}
