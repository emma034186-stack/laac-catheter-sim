# LAAC Catheter Sim: Project Rebuild, Performance Fix, and GitHub Setup

## 0. Background

Picking up from yesterday's vessel-display fix, the plan for today was to keep tuning camera/UI/vessel material in the old project (`D:\UnityProjects\import`). Partway through, the scene got reverted to a 9/16 backup (from before the vessel fix) and the vessel GameObject subtree was manually transplanted back in. The transplant worked and collision was confirmed once — but then the endoscope camera turned out not to be seeing the vessel at all, and digging into that led to a more fundamental discovery: the old project's Unity↔SOFA reconnect graph (`DAGNodeManager`) had drifted into an unreliable state. The manually transplanted GameObjects looked fine, but collision detection itself (`CollisionPipeline`'s debug draw) produced literally nothing — the problem was deeper than expected.

Control test: the untouched vendor demo scene `BeamDemo_02_KV1.unity` (same old project) still collided correctly. That ruled out anything at the native-DLL/plugin-install level and narrowed it down to `LAAC_Catheter_Demo.unity` itself — almost certainly the result of today's repeated manual YAML transplants, reverts, and re-transplants leaving the Unity-side object state out of sync with the native side.

Tried the plugin's own "Load SOFA Scene (.scn) file" rebuild feature to fix the reconnect graph. It wiped the vessel mesh data down to 0 vertices instead — worse than before. That path is itself unreliable and can't be used to close this out.

**Decision**: instead of continuing to debug a scene in an unknown state, start a brand-new Unity project. Carry over everything already solved (native DLLs, paths, `.scn` collision tuning, material fixes, the `ThirdPersonCamera.cs` fix) unchanged, but let the plugin **regenerate the vessel and catheter Unity-side objects from the `.scn` itself** instead of hand-transplanting YAML again.

---

## 1. New project setup

- Created `D:\UnityProjects\LAAC_Catheter_Sim` via `Unity.exe -createProject` (same version, 2022.3.46f1)
- Copied over only `Assets/SofaUnity/` (319MB, via robocopy). Deliberately left out:
  - `LightBuzz Hand Tracking` (300MB, unrelated to SOFA, and floods the Console with an unrelated DirectML warning)
  - `YughuesFreeNatureMaterials` (231MB, confirmed nothing in SofaUnity references it)
  - The old project's `Assets/Editor/` debug scripts (hardcoded paths, one-off tools from that debugging session)
- Fixed the absolute paths in `sofa.ini` (`SHARE_DIR`/`EXAMPLES_DIR`/`LICENSE_DIR`/`PYTHON_DIR`) to point at the new project path
- **Hit a snag**: a fresh blank Unity project doesn't come with `com.unity.ugui` and friends by default, so the SofaUnity scripts (which use `UnityEngine.UI`) threw 20 `CS0234` compile errors — the whole plugin was dead in the water. Fix: copied the old project's complete `Packages/manifest.json` over, replacing the new project's default one.
- Health check: opened the untouched `BeamDemo_02_KV1.unity` and confirmed collision worked on Play — confirms the DLL/path/package setup is sound, and the problem really was confined to the scene file.

## 2. Building the LAAC_Catheter_Demo scene from scratch

- Duplicated `BeamDemo_02_KV1.unity` as the starting point for a new scene, used a BFS script to remove the KV1-specific `Organs` GameObject (six kidney/liver/etc. props, 14 objects total), verified no dangling references
- Pointed `SofaContext`'s Scene Filename at `LAAC_Catheter.scn`
- Clicked "Load SOFA Scene (.scn) file" in the Inspector — since this is a brand-new scene with no leftover objects to interfere, it cleanly generated `SofaNode - LAAC_Vessel` and `SofaNode - NavigationSceneNode` (the catheter), Console completely clean
- **Result**: confirmed **collision works** on Play — proof the root cause really was the old scene's manual-YAML-transplant history, not the collision parameters themselves

## 3. Vessel material fix

`<OglModel name="OglVessel" color="0.8 0.2 0.2 0.35"/>` in the `.scn` doesn't specify a material, only a color — so "Load SOFA Scene" auto-generation assigned some unrelated default material (at various points this was the endoscope material, and even the prostate material from a completely different demo, "Urinary_system" — likely Unity doing some random/default fallback assignment for objects that reference a "missing" material during certain refresh/save cycles).

Fix: pointed `OglVessel`'s `MeshRenderer.m_Materials` directly at `LAAC_Vessel_Tissue.mat` (guid `68373d8ec08a41a4a271f6d47d9ba30d` — the double-sided opaque Standard-shader version fixed yesterday).

**This happened twice.** After the first fix, work continued in the same Editor session without reloading the scene, and several saves later Unity had overwritten the disk with its stale in-memory state each time, clobbering the external fix. **Lesson: after any external edit to a scene file, the scene must be reopened (discarding current changes) before doing anything else — otherwise the next save writes the fix right back out.** Hit this more than once today.

## 4. Endoscope camera tracking (the longest debugging thread)

### 4.1 Root cause

The catheter's visible mesh (via `SofaBeamAdapterModel`) advances by **rewriting mesh vertex coordinates directly**, not by moving the GameObject's Transform. The scene's original `EndoCamera` was a **child object** under the same parent, following standard Unity Transform hierarchy — vertices being repainted doesn't trigger parent-child propagation, so the camera never actually followed the catheter tip.

### 4.2 Three stacked problems found along the way

1. **A hidden emissive layer on the monitor material.** `BeamCamera-Endoscope.mat` had both an `_EmissionMap` (whose source texture file was actually missing from the project) and a very bright `_EmissionColor` (1.4, above normal range). This emissive layer had likely been washing out the real camera feed underneath the entire time — which explains why every attempt today (repositioning, reangling) produced zero visible change: the monitor always showed the same blue/white gradient regardless. Fix: cleared the texture, zeroed the color.

2. **The old `EndoCamera` and the newly created `Camera_TEST` were both writing to the same RenderTexture.** Two cameras racing to write the same texture — whichever rendered last won, so the new camera's feed kept getting overwritten by the broken old one. Fix: disable the old `EndoCamera`'s Camera component as soon as the new one is detected.

3. **Reading the texture reference from the material at runtime was unreliable.** First attempt tried to read whatever texture the monitor material currently used and reassign it — this indirect read failed often (the material reference itself was unstable, see previous section). Switched to `AssetDatabase.LoadAssetAtPath` loading `BeamEndoscopeTexture.renderTexture` by its exact file path, sidestepping the whole "guess what the material currently points to" problem.

### 4.3 The tracking algorithm

Wrote `EndoCameraTracker.cs` (attached dynamically at runtime, never saved into the scene): periodically reads the catheter mesh's vertex data, takes the centroid of the vertex ring farthest from the local origin as the tip position, computes a forward direction (tangent) from a ring slightly further back, and moves/rotates the camera there (with exponential smoothing to reduce per-frame jitter).

**A sign error along the way**: the "pull back slightly to avoid flickering against the wall" offset was initially applied in the wrong direction — toward the catheter's **own body** instead of away from it — which put the camera stuck inside the catheter's own geometry. Fixed by pushing forward, past the tip, instead.

**Final approach**: after several rounds, switched to manually creating a brand-new camera in the Editor that was confirmed to render correctly (`Camera_TEST`), and had `EndoCameraTracker.cs` only handle moving **that** camera's Transform to follow the catheter tip — leaving everything else (render settings, material, target texture beyond the one-time assignment) untouched. This decoupled "does the camera render at all" from "does the camera track correctly," and that's what finally worked.

**Wrap-up**: saved `Camera_TEST`'s Target Texture directly into the scene file (not just set at runtime), so the main view/monitor also look correct outside Play mode, without needing to press Play first.

## 5. Performance / physics-sync bug (the hidden factor behind collision feel)

Collision felt noticeably springier/more reliable in yesterday's log than in the new project — more prone to tunneling. Checked every collision/elasticity parameter in the `.scn` against the values finalized in yesterday's log: character-for-character identical. Native SOFA reads the `.scn` directly, independent of the Unity-side object graph, so the physics behavior should in theory be identical.

Traced it to `SofaContext.cs`'s `UpdateImplSync()`:

```csharp
// Original: steps physics at most once per Unity frame
if (Time.time >= nextUpdate) { nextUpdate += m_timeStep; m_impl.step(); ... }
```

The actual physics step rate was capped by the render framerate (`if`, not `while`). If render FPS is lower (plausible in a fresh project with cold caches, or just different scene complexity), the physics step rate slows down with it — but key-driven catheter insertion is paced by wall-clock time (`SofaKeyEvent`'s throttle interval), completely decoupled from the physics step rate. The lower the FPS, the more insertion distance each physics step has to "absorb," which naturally makes collision detection coarser and more prone to missing contacts.

**Fix**: changed it to a `while` loop that catches physics up to wall-clock time, with a safety cap of 10 steps per frame to avoid a death spiral if performance tanks:

```csharp
int stepsThisFrame = 0;
const int maxStepsPerFrame = 10;
while (Time.time >= nextUpdate && stepsThisFrame < maxStepsPerFrame)
{
    nextUpdate += m_timeStep;
    m_impl.step();
    if (m_nodeGraphMgr != null) m_nodeGraphMgr.PropagateSetDirty(true);
    stepsThisFrame++;
}
```

Confirmed collision feel improved after this fix (though not fully back to the best-observed state).

### 5.1 Secondary factor: the diagnostic scripts' own overhead

Even after the FPS-sync fix, collision still felt slightly off. Suspected the custom debug tools (`EndoCameraTracker`, `CatheterWallDistanceHUD`) were the culprit: both call `mesh.vertices` frequently, and that Unity API **allocates a brand-new array on every single call** (the vessel mesh has 2560+ vertices) — frequent calls generate real GC pressure and indirectly slow down frame updates. Experiment: temporarily disabling both scripts brought collision feel noticeably closer to the log's baseline (aside from the known structural limitation at the sharp bend).

**Fix**: rewrote both to use `Mesh.GetVertices(List<Vector3>)` with a reused buffer instead of the array-allocating `.vertices` property, and relaxed their update intervals (0.02s→0.08s, 0.15s→0.3s).

## 6. Parameter iteration (speed vs. tunneling resistance, in tension)

The direction this round was "make the controls faster, while making the vessel harder to tunnel through" — two goals that pull against each other by nature (faster movement gives the physics system less time to keep up). Iteration:

| Parameter | Start | Final | Notes |
|---|---|---|---|
| `SofaKeyEvent.m_keyRepeatInterval` (advance/retract throttle) | 0.04 | 0.025 | Tried 0.35/0.25/0.15/0.1/0.07/0.05/0.045 along the way, settled on 0.025 |
| `SofaKeyEvent.m_rotationRepeatInterval` (rotation throttle) | 0.12 | 0.5 | Moved in the slower/safer direction; tried 0.2/0.35/0.7 along the way |
| `InterventionalRadiologyController.step` (distance per trigger) | 0.08 | 0.4 | Tried 0.15 (a value already validated in yesterday's log) and 1.5 (an explicit 10x-speed request, which came with clear, severe stutter/tunneling side effects), then backed off to 0.4 as a compromise |
| `LocalMinDistance.alarmDistance` | 5 | 12 | |
| `LocalMinDistance.contactDistance` | 0.5 | 1.4 | |
| Catheter `LineCollisionModel`/`PointCollisionModel.proximity` | 1.8 | 3.0 | |
| Catheter collision model `contactStiffness` (previously unset, used engine default) | — | 3000 | Added to both vessel and catheter collision models |

**Note**: `step="1.5"` caused visible stuttering — the per-step insertion distance jumped enormously, so the LCP solver's per-step contact complexity spiked too, right as it collided with the "catch up to 10 steps per frame" mechanism from §5: when the solver slows down, the same frame ends up forcing through 10 expensive solves back to back, causing a hard stutter. **Speed, stutter, and tunneling were all symptoms of the same one cause at that point** — there was no way to fix the stutter alone while keeping that speed. `step=0.4` is the compromise landed on for now; it hasn't had a final acceptance pass yet.

## 7. Backups

**Project** (`D:\UnityProjects\LAAC_Catheter_Sim_backups\`):

5. `05_before_github_push_20260918_172230.bak`

---

## 8. Current status

**Done**:
- Collision, vessel rendering, material, camera, and endoscope tracking all confirmed working in the new project
- Physics stepping no longer bottlenecked by render FPS (`while` catch-up fix)
- Diagnostic tools optimized, no longer noticeably affecting collision feel

**Open**:
- `step=0.4` (along with the current collision-margin parameters) is this round's compromise point — not yet given a final acceptance pass, may still get tuned further
- Tunneling near the sharp bend (around the transseptal puncture point) remains a known structural limitation, consistent with yesterday's log's conclusion — out of scope for this round
- For future tuning sessions: confirm the Unity Editor has focus / the scene has been reloaded before assuming a change took effect
