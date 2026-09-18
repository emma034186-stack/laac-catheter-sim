using UnityEngine;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SofaUnity
{
    /// <summary>
    /// The catheter's visible mesh (SofaBeamAdapterModel) advances by rewriting
    /// mesh vertices directly, not by moving its GameObject's Transform - so a
    /// camera parented to it under Unity's normal Transform hierarchy never
    /// actually follows the tip. This reconstructs the tip position/tangent
    /// each frame straight from the mesh's own vertex data and moves the
    /// target camera (by name, set in the Inspector or left at the default
    /// "Camera_TEST") there directly in world space. It only ever touches that
    /// camera's Transform - nothing else about the camera, texture, or
    /// material is touched. Attached dynamically at runtime, not saved into
    /// the scene.
    /// </summary>
    public class EndoCameraTracker : MonoBehaviour
    {
        public string catheterMeshObjectName = "SofaBeamAdapterModel  -  InstrumentCombined@Collision - KV1";
        public string targetCameraName = "Camera_TEST";
        public string cameraScreenObjectName = "CameraScreen";
        public float updateInterval = 0.08f;
        public float ringThickness = 8f;
        public float backOffset = 2f;
        public float smoothing = 6f;

        Transform camTransform;
        MeshFilter catheterMF;
        float nextUpdate = 0f;
        bool loggedOnce = false;
        float logTimer = 0f;
        readonly List<Vector3> vertBuffer = new List<Vector3>(512);

        void LateUpdate()
        {
            if (camTransform == null)
            {
                GameObject go = GameObject.Find(targetCameraName);
                if (go == null)
                {
                    if (!loggedOnce) { Debug.Log("[EndoCameraTracker] target camera NOT FOUND by name: " + targetCameraName); loggedOnce = true; }
                    return;
                }
                camTransform = go.transform;
                Debug.Log("[EndoCameraTracker] found target camera '" + targetCameraName + "' at " + camTransform.position);

                GameObject oldEndoCam = GameObject.Find("EndoCamera");
                if (oldEndoCam != null)
                {
                    Camera oldCam = oldEndoCam.GetComponent<Camera>();
                    if (oldCam != null)
                    {
                        oldCam.enabled = false;
                        Debug.Log("[EndoCameraTracker] disabled old 'EndoCamera' so it stops overwriting the shared RenderTexture");
                    }
                }

                Camera cam = go.GetComponent<Camera>();
                if (cam != null)
                {
                    RenderTexture rt = null;
#if UNITY_EDITOR
                    rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(
                        "Assets/SofaUnity/Scenes/Demos/Endoscopy/BeamAdapter/Material/Textures/BeamEndoscopeTexture.renderTexture");
#endif
                    if (rt == null)
                    {
                        // fallback: read whatever texture CameraScreen's material currently uses
                        GameObject screenGO = GameObject.Find(cameraScreenObjectName);
                        if (screenGO != null)
                        {
                            Renderer rend = screenGO.GetComponentInChildren<Renderer>();
                            if (rend != null && rend.sharedMaterial != null)
                                rt = rend.sharedMaterial.mainTexture as RenderTexture;
                        }
                    }
                    if (rt != null)
                    {
                        cam.targetTexture = rt;
                        Debug.Log("[EndoCameraTracker] wired '" + targetCameraName + "' to render into " + rt.name);
                    }
                    else
                    {
                        Debug.Log("[EndoCameraTracker] FAILED to load/find the BeamEndoscopeTexture RenderTexture asset");
                    }
                }
            }

            if (catheterMF == null)
            {
                GameObject go = GameObject.Find(catheterMeshObjectName);
                if (go == null)
                {
                    if (!loggedOnce) { Debug.Log("[EndoCameraTracker] Catheter mesh GameObject NOT FOUND by name: " + catheterMeshObjectName); loggedOnce = true; }
                    return;
                }
                catheterMF = go.GetComponent<MeshFilter>();
                if (catheterMF == null)
                {
                    if (!loggedOnce) { Debug.Log("[EndoCameraTracker] Catheter GameObject found but has NO MeshFilter: " + go.name); loggedOnce = true; }
                    return;
                }
                Debug.Log("[EndoCameraTracker] found catheter MeshFilter on " + go.name);
            }
            if (Time.time < nextUpdate) return;
            nextUpdate = Time.time + updateInterval;

            Mesh mesh = catheterMF.sharedMesh;
            if (mesh == null) return;
            mesh.GetVertices(vertBuffer); // reuses the buffer, unlike mesh.vertices (which allocates a new array every call)
            int vertCount = vertBuffer.Count;
            if (vertCount < 4) return;

            float maxDist = 0f;
            for (int i = 0; i < vertCount; i++)
            {
                float d = vertBuffer[i].magnitude;
                if (d > maxDist) maxDist = d;
            }

            float backTargetDist = maxDist - ringThickness * 3f;
            Vector3 tipSum = Vector3.zero; int tipCount = 0;
            Vector3 backSum = Vector3.zero; int backCount = 0;

            for (int i = 0; i < vertCount; i++)
            {
                float d = vertBuffer[i].magnitude;
                if (d >= maxDist - ringThickness)
                {
                    tipSum += vertBuffer[i]; tipCount++;
                }
                else if (Mathf.Abs(d - backTargetDist) <= ringThickness)
                {
                    backSum += vertBuffer[i]; backCount++;
                }
            }
            if (tipCount == 0) return;
            Vector3 tipLocal = tipSum / tipCount;

            Vector3 tangentLocal;
            if (backCount > 0)
                tangentLocal = tipLocal - (backSum / backCount);
            else
                tangentLocal = tipLocal;

            if (tangentLocal.sqrMagnitude < 1e-8f)
                return;
            tangentLocal.Normalize();

            Transform meshT = catheterMF.transform;
            Vector3 tipWorld = meshT.TransformPoint(tipLocal);
            Vector3 tangentWorld = meshT.TransformDirection(tangentLocal);

            Vector3 targetPos = tipWorld + tangentWorld * backOffset;
            Quaternion targetRot = Quaternion.LookRotation(tangentWorld,
                Vector3.Cross(tangentWorld, Vector3.right).sqrMagnitude > 1e-4f ? Vector3.up : Vector3.forward);

            float t = 1f - Mathf.Exp(-smoothing * Time.deltaTime);
            camTransform.position = Vector3.Lerp(camTransform.position, targetPos, t);
            camTransform.rotation = Quaternion.Slerp(camTransform.rotation, targetRot, t);

            logTimer += updateInterval;
            if (logTimer >= 1f)
            {
                logTimer = 0f;
                Debug.Log(string.Format("[EndoCameraTracker] maxDist={0:F1} tipWorld={1} tangentWorld={2} camPos={3}",
                    maxDist, tipWorld, tangentWorld, camTransform.position));
            }
        }
    }

    public static class EndoCameraTrackerInjector
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Inject()
        {
            GameObject go = new GameObject("EndoCameraTracker (runtime-only)");
            go.AddComponent<EndoCameraTracker>();
        }
    }
}
