using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SofaUnity
{
    /// <summary>
    /// Editor-side driver: runs each mode listed in Benchmark/queue.txt in its own
    /// Play session (fresh SOFA state per mode). Also available from the menu.
    /// Any mode name other than "legacy" uses the current catch-up stepping.
    ///
    /// A queue line may carry .scn edits for one-factor ablations:
    ///   name|find=>replace;;find=>replace
    /// The original .scn is saved to Benchmark/scn_backup.scn before the edit and
    /// restored as soon as the editor is back in Edit mode.
    /// </summary>
    [InitializeOnLoad]
    public static class LAACBenchmarkDriver
    {
        const string ScnPath = "Assets/SofaUnity/Scenes/Demos/Endoscopy/BeamAdapter/SofaScenes/LAAC_Catheter.scn";
        static string Queue => Path.Combine(LAACBenchmarkRunner.BenchDir, "queue.txt");
        static string Active => Path.Combine(LAACBenchmarkRunner.BenchDir, "active.txt");
        static string ScnBackup => Path.Combine(LAACBenchmarkRunner.BenchDir, "scn_backup.scn");
        static double nextPoll;

        static LAACBenchmarkDriver()
        {
            EditorApplication.playModeStateChanged += s =>
            {
                if (s == PlayModeStateChange.EnteredEditMode)
                {
                    RestoreScn();
                    EditorApplication.delayCall += TryStartNext;
                }
            };
            EditorApplication.update += Poll;
            EditorApplication.delayCall += () => { if (!EditorApplication.isPlayingOrWillChangePlaymode) RestoreScn(); TryStartNext(); };
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1.0;
            TryStartNext();
        }

        [MenuItem("LAAC/Run Benchmark (current + legacy)")]
        static void RunFromMenu()
        {
            Directory.CreateDirectory(LAACBenchmarkRunner.BenchDir);
            File.WriteAllText(Queue, "current\nlegacy\n");
            TryStartNext();
        }

        static void RestoreScn()
        {
            if (!File.Exists(ScnBackup)) return;
            File.Copy(ScnBackup, ScnPath, true);
            File.Delete(ScnBackup);
            Debug.Log("[LAACBenchmark] restored original .scn");
        }

        static void TryStartNext()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (File.Exists(Active)) return;           // a run is (or was) in progress
            if (!File.Exists(Queue)) return;
            var lines = File.ReadAllLines(Queue).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) { File.Delete(Queue); return; }
            string line = lines[0];
            lines.RemoveAt(0);
            if (lines.Count == 0) File.Delete(Queue); else File.WriteAllLines(Queue, lines);

            string[] parts = line.Split('|');
            string mode = parts[0];
            if (parts.Length > 1)
            {
                string scn = File.ReadAllText(ScnPath);
                if (!File.Exists(ScnBackup)) File.WriteAllText(ScnBackup, scn);
                foreach (var edit in parts[1].Split(new[] { ";;" }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var fr = edit.Split(new[] { "=>" }, System.StringSplitOptions.None);
                    if (!scn.Contains(fr[0])) { Debug.LogError("[LAACBenchmark] pattern not found: " + fr[0]); RestoreScn(); return; }
                    scn = scn.Replace(fr[0], fr[1]);
                }
                File.WriteAllText(ScnPath, scn);
            }
            File.WriteAllText(Active, mode);
            Debug.Log("[LAACBenchmark] entering Play mode for: " + mode);
            EditorApplication.isPlaying = true;
        }
    }
}
