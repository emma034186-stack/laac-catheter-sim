using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SofaUnity
{
    /// <summary>
    /// Counters fed by SofaContext.UpdateImplSync(). Timing is only collected
    /// while a benchmark run is recording, so normal Play sessions are unaffected.
    /// </summary>
    public static class LAACBenchStats
    {
        public static bool LegacySingleStep = false;
        public static bool Recording = false;

        public static readonly List<double> StepMs = new List<double>(20000);
        public static int Steps;
        public static int SyncFrames;
        public static int BacklogFrames;   // frames that ended with physics still behind wall-clock
        public static int CappedFrames;    // frames that hit the per-frame step cap

        static readonly double TickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        public static void OnStep(long ticks)
        {
            if (!Recording) return;
            StepMs.Add(ticks * TickToMs);
            Steps++;
        }

        public static void OnFrame(int stepsThisFrame, bool backlog)
        {
            if (!Recording) return;
            SyncFrames++;
            if (backlog) BacklogFrames++;
            if (stepsThisFrame >= (LegacySingleStep ? 1 : 10) && backlog) CappedFrames++;
        }

        public static void Reset()
        {
            StepMs.Clear(); Steps = 0; SyncFrames = 0; BacklogFrames = 0; CappedFrames = 0;
        }
    }

    /// <summary>
    /// Scripted, repeatable catheter manoeuvre (idle -> advance -> rotate -> retract)
    /// driven through the same SofaKeyPressEvent path and repeat intervals as
    /// SofaKeyEvent, recording frame time, SOFA step time, achieved physics rate
    /// and catheter-vs-wall clearance against the idealized tube geometry.
    /// Only runs when the editor-side driver writes Benchmark/active.txt.
    /// </summary>
    public class LAACBenchmarkRunner : MonoBehaviour
    {
        public static string BenchDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Benchmark"));

        struct Phase { public string name; public float seconds; public int key; public float interval; }
        readonly Phase[] phases =
        {
            new Phase { name = "warmup",  seconds = 3f,  key = -1, interval = 0f },
            new Phase { name = "idle",    seconds = 8f,  key = -1, interval = 0f },
            new Phase { name = "advance", seconds = 18f, key = 19, interval = 0.025f },
            new Phase { name = "rotate",  seconds = 10f, key = 18, interval = 0.5f },
            new Phase { name = "retract", seconds = 10f, key = 21, interval = 0.025f },
        };

        class PhaseResult
        {
            public string name;
            public int frames;
            public double realSeconds, gameSeconds;
            public List<double> frameMs = new List<double>();
            public List<double> stepMs = new List<double>();
            public int steps, backlogFrames, cappedFrames, keyPresses;
            public int clearanceSamples, surfaceContactSamples, centerlineOutsideSamples;
            public double minClearance = double.MaxValue, maxSurfacePenetration = double.MinValue;
        }

        string mode;
        SofaContext ctx;
        int phaseIdx = -1;
        float phaseRealStart, phaseGameStart, nextKey, nextClearance;
        PhaseResult cur;
        readonly List<PhaseResult> results = new List<PhaseResult>();

        MeshFilter cathMF, vesselMF;
        Vector3[] ringCenter; float[] ringRadius;
        readonly List<Vector3> buf = new List<Vector3>(4096);
        const float CatheterRadius = 2.3f;
        string geomNote = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Inject()
        {
            string active = Path.Combine(BenchDir, "active.txt");
            if (!File.Exists(active)) return;
            string m = File.ReadAllText(active).Trim();
            LAACBenchStats.LegacySingleStep = (m == "legacy");
            var go = new GameObject("LAACBenchmarkRunner (runtime-only)");
            go.AddComponent<LAACBenchmarkRunner>().mode = m;
        }

        void Start()
        {
            ctx = FindObjectOfType<SofaContext>();
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            NextPhase();
        }

        void NextPhase()
        {
            if (cur != null)
            {
                cur.realSeconds = Time.realtimeSinceStartupAsDouble - phaseRealStart;
                cur.gameSeconds = Time.timeAsDouble - phaseGameStart;
                cur.steps = LAACBenchStats.Steps;
                cur.stepMs.AddRange(LAACBenchStats.StepMs);
                cur.backlogFrames = LAACBenchStats.BacklogFrames;
                cur.cappedFrames = LAACBenchStats.CappedFrames;
                if (cur.name != "warmup") results.Add(cur);
                if (phases[phaseIdx].key >= 0 && ctx != null) ctx.SofaKeyReleaseEvent(phases[phaseIdx].key);
            }
            phaseIdx++;
            if (phaseIdx >= phases.Length) { Finish(); return; }
            LAACBenchStats.Reset();
            LAACBenchStats.Recording = true;
            cur = new PhaseResult { name = phases[phaseIdx].name };
            phaseRealStart = (float)Time.realtimeSinceStartupAsDouble;
            phaseGameStart = Time.time;
            nextKey = Time.time;
            nextClearance = Time.time;
        }

        void Update()
        {
            if (cur == null) return;
            cur.frames++;
            cur.frameMs.Add(Time.unscaledDeltaTime * 1000.0);

            Phase p = phases[phaseIdx];
            if (p.key >= 0 && ctx != null && Time.time >= nextKey)
            {
                nextKey = Time.time + p.interval;
                ctx.SofaKeyPressEvent(p.key);
                cur.keyPresses++;
            }
            if (Time.time >= nextClearance)
            {
                nextClearance = Time.time + 0.05f;
                SampleClearance();
            }
            if (Time.realtimeSinceStartupAsDouble - phaseRealStart >= p.seconds) NextPhase();
        }

        void FindMeshes()
        {
            foreach (var mf in FindObjectsOfType<MeshFilter>())
            {
                string n = mf.gameObject.name;
                if (n.Contains("OglVessel")) vesselMF = mf;
                else if (n.Contains("InstrumentCombined") && n.Contains("Collision")) cathMF = mf;
                else if (cathMF == null && n.Contains("InstrumentCombined")) cathMF = mf;
            }
            if (vesselMF != null && vesselMF.sharedMesh != null)
            {
                vesselMF.sharedMesh.GetVertices(buf);
                const int ring = 16;
                if (buf.Count % ring == 0 && buf.Count > 0)
                {
                    int n = buf.Count / ring;
                    ringCenter = new Vector3[n]; ringRadius = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 c = Vector3.zero;
                        for (int k = 0; k < ring; k++) c += vesselMF.transform.TransformPoint(buf[i * ring + k]);
                        c /= ring;
                        float r = 0;
                        for (int k = 0; k < ring; k++) r += Vector3.Distance(c, vesselMF.transform.TransformPoint(buf[i * ring + k]));
                        ringCenter[i] = c; ringRadius[i] = r / ring;
                    }
                    float minR = float.MaxValue;
                    foreach (var r in ringRadius) minR = Mathf.Min(minR, r);
                    geomNote = string.Format("vessel rings={0}, min ring radius={1:F2}", n, minR);
                }
                else geomNote = "vessel vertex count " + buf.Count + " not ring-structured; clearance skipped";
            }
        }

        void SampleClearance()
        {
            if (ringCenter == null) { FindMeshes(); if (ringCenter == null || cathMF == null) return; }
            if (cathMF == null || cathMF.sharedMesh == null) return;
            cathMF.sharedMesh.GetVertices(buf);
            if (buf.Count == 0) return;
            double minClear = double.MaxValue, maxPen = double.MinValue;
            bool outside = false;
            foreach (var lv in buf)
            {
                Vector3 p = cathMF.transform.TransformPoint(lv);
                float best = float.MaxValue, bestR = 0;
                for (int i = 0; i < ringCenter.Length - 1; i++)
                {
                    Vector3 a = ringCenter[i], b = ringCenter[i + 1], ab = b - a;
                    float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                    float dist = Vector3.Distance(p, a + t * ab);
                    if (dist < best) { best = dist; bestR = Mathf.Lerp(ringRadius[i], ringRadius[i + 1], t); }
                }
                double clear = bestR - best - CatheterRadius;   // gap between catheter surface and wall
                minClear = System.Math.Min(minClear, clear);
                maxPen = System.Math.Max(maxPen, -clear);
                if (best > bestR) outside = true;
            }
            cur.clearanceSamples++;
            if (minClear < 0) cur.surfaceContactSamples++;
            if (outside) cur.centerlineOutsideSamples++;
            cur.minClearance = System.Math.Min(cur.minClearance, minClear);
            cur.maxSurfacePenetration = System.Math.Max(cur.maxSurfacePenetration, maxPen);
        }

        static double Pct(List<double> v, double q)
        {
            if (v.Count == 0) return double.NaN;
            var s = new List<double>(v); s.Sort();
            int i = (int)System.Math.Ceiling(q * s.Count) - 1;
            return s[System.Math.Max(0, System.Math.Min(s.Count - 1, i))];
        }
        static double Mean(List<double> v) { if (v.Count == 0) return double.NaN; double t = 0; foreach (var x in v) t += x; return t / v.Count; }
        static double Max(List<double> v) { double m = double.NaN; foreach (var x in v) m = double.IsNaN(m) ? x : System.Math.Max(m, x); return m; }

        void Finish()
        {
            LAACBenchStats.Recording = false;
            var sb = new StringBuilder();
            var csv = new StringBuilder("mode,phase,real_s,frames,fps_mean,frame_ms_mean,frame_ms_p95,steps,physics_hz_real,step_ms_mean,step_ms_p50,step_ms_p95,step_ms_max,backlog_frames_pct,capped_frames,key_presses,clearance_samples,surface_contact_samples,centerline_outside_samples,min_clearance_mm,max_surface_penetration_mm\n");
            sb.AppendFormat("LAAC benchmark  mode={0}  unity={1}  cpu={2} ({3} threads)  gpu={4}\n", mode, Application.unityVersion, SystemInfo.processorType, SystemInfo.processorCount, SystemInfo.graphicsDeviceName);
            sb.AppendFormat("dt={0}s target physics rate={1:F0}Hz  {2}\n", ctx != null ? ctx.TimeStep : -1, ctx != null ? 1.0 / ctx.TimeStep : -1, geomNote);
            var allFrame = new List<double>(); var allStep = new List<double>(); double allReal = 0; int allSteps = 0, allFrames = 0;
            foreach (var r in results)
            {
                allFrame.AddRange(r.frameMs); allStep.AddRange(r.stepMs); allReal += r.realSeconds; allSteps += r.steps; allFrames += r.frames;
                string line = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0},{1},{2:F2},{3},{4:F1},{5:F2},{6:F2},{7},{8:F1},{9:F3},{10:F3},{11:F3},{12:F3},{13:F1},{14},{15},{16},{17},{18},{19:F2},{20:F2}",
                    mode, r.name, r.realSeconds, r.frames, r.frames / r.realSeconds, Mean(r.frameMs), Pct(r.frameMs, 0.95), r.steps, r.steps / r.realSeconds,
                    Mean(r.stepMs), Pct(r.stepMs, 0.5), Pct(r.stepMs, 0.95), Max(r.stepMs),
                    100.0 * r.backlogFrames / System.Math.Max(1, r.frames), r.cappedFrames, r.keyPresses,
                    r.clearanceSamples, r.surfaceContactSamples, r.centerlineOutsideSamples,
                    r.minClearance == double.MaxValue ? double.NaN : r.minClearance,
                    r.maxSurfacePenetration == double.MinValue ? double.NaN : r.maxSurfacePenetration);
                csv.AppendLine(line);
            }
            csv.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0},ALL,{1:F2},{2},{3:F1},{4:F2},{5:F2},{6},{7:F1},{8:F3},{9:F3},{10:F3},{11:F3},,,,,,,,",
                mode, allReal, allFrames, allFrames / allReal, Mean(allFrame), Pct(allFrame, 0.95), allSteps, allSteps / allReal,
                Mean(allStep), Pct(allStep, 0.5), Pct(allStep, 0.95), Max(allStep)));
            Directory.CreateDirectory(BenchDir);
            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            File.WriteAllText(Path.Combine(BenchDir, "result_" + mode + "_" + stamp + ".csv"), csv.ToString());
            File.WriteAllText(Path.Combine(BenchDir, "result_" + mode + "_" + stamp + ".txt"), sb.ToString() + csv.ToString());
            try { File.Delete(Path.Combine(BenchDir, "active.txt")); } catch { }
            Debug.Log("[LAACBenchmark] done: " + mode);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
            enabled = false;
        }
    }
}
