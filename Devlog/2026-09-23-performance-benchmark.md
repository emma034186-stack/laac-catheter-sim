# LAAC Catheter Sim: Performance Benchmark & Ablation

## 0. Why

Until now every judgement about "collision feels better / worse" was subjective. Before tuning anything else, I wanted three numbers: rendered FPS, time per SOFA physics step, and the physics rate actually achieved. It turned out the measurement changed the story.

## 1. Tooling

- **`SofaContext.cs`**: `m_impl.step()` is wrapped with `Stopwatch` timing, reported to a static `LAACBenchStats`. Nothing is recorded unless a benchmark run is active. A `LegacySingleStep` switch reproduces the original plugin behaviour (at most one step per rendered frame, i.e. the old `if`) so the 2026-09-18 catch-up fix can be compared against it.
- **`Utils/LAACBenchmark.cs`** (`LAACBenchmarkRunner`): injected at runtime only when `Benchmark/active.txt` exists. It replays a fixed manoeuvre through the same `SofaKeyPressEvent` path and repeat intervals as `SofaKeyEvent`:
  warmup 3 s → idle 8 s → advance 18 s (key 19, every 0.025 s) → rotate 10 s (key 18, every 0.5 s) → retract 10 s (key 21, every 0.025 s).
  Per phase it records frame time, per-step physics time, achieved physics rate, frames that ended with physics behind wall-clock, and catheter–wall clearance (catheter collision vertices vs. the vessel tube's ring centres and radii, catheter radius 2.3 mm). Results go to `Benchmark/result_<mode>_<timestamp>.csv|.txt`, then Play mode exits.
- **`Editor/LAACBenchmarkDriver.cs`**: runs every line of `Benchmark/queue.txt` in its own Play session (fresh SOFA state each time). A line can carry `.scn` edits for one-factor ablations (`name|find=>replace`); the original `.scn` is backed up and restored as soon as the editor is back in Edit mode. Menu: **LAAC → Run Benchmark (current + legacy)**.

Machine: Intel Core i5-13500HX (20 threads), RTX 4060 Laptop GPU, Unity 2022.3.46f1 **Editor** (not a standalone build). Vessel geometry: the idealized tube with the transseptal radius still at the `TEMP` 6.5 mm (see 2026-09-16 log). `dt = 0.005 s` → target physics rate 200 Hz.

## 2. Result 1: the catch-up fix trades frame rate for physics rate

| Metric (46 s scripted run) | Original (≤ 1 step/frame) | Catch-up fix (≤ 10 steps/frame) |
|---|---|---|
| Mean rendered FPS | 13.5 | **2.7** |
| Frame time mean / p95 | 74.2 / 94.8 ms | 365.7 / 388.8 ms |
| Physics step mean / p95 | 41.5 / 53.1 ms | 31.8 / 35.3 ms |
| Physics step, idle phase (identical initial state) | 30.9 ms | 30.6 ms |
| Achieved physics rate | 13.5 Hz (6.8 % of target) | 27.3 Hz (13.7 %) |
| Frames ending with physics behind wall-clock | 100 % | 100 % |
| Key presses during the 18 s advance phase | 244 | 50 |

- **One SOFA step costs ~31 ms. Real time at 200 Hz and 60 FPS needs < 5 ms** (≈ 3.3 steps per 16.7 ms frame, minus rendering). Neither variant is real time.
- The catch-up loop doubles the physics rate, but *every* frame hits the 10-step cap, so frame time jumps to ~366 ms: the spiral of death the cap was meant to bound. The "collision feels better" impression from 2026-09-18 was physics rate bought with frame rate.
- Key presses are still limited to one per rendered frame, so at 2.7 FPS the catheter advanced ~20 mm vs. ~98 mm in the original run. **Penetration numbers between these two runs are not comparable.** Removing this input/frame coupling is the next fix.
- Control run with `CollisionPipeline verbose="0" draw="0"`: 31.3 ms per step, so debug output is not the cost.

## 3. Result 2: one-factor ablation, collision detection dominates

Catch-up stepping, same scripted run, one `.scn` parameter changed per run. The idle phase is the cleanest comparison (same state, no input).

| Change (only this one) | Idle step | Whole run step / FPS | Catheter surface penetration (max; share of rotate-phase samples) |
|---|---|---|---|
| Baseline (alarmDistance 12, LCP maxIt 2500, nx 200, nbEdgesCollis 90) | 30.4 ms | 31.3 ms / 2.8 | none |
| LCP `maxIt` 2500 → 500 | 30.5 ms | 31.3 ms / 2.8 | none |
| Beam nodes `nx` 200 → 100 | 30.0 ms | 31.2 ms / 2.8 | none |
| `nbEdgesCollis` 90 → 45 | 18.4 ms (−39 %) | 19.5 ms / 4.5 | 0.04 mm; 0 % |
| `alarmDistance` 12 → 5 | **6.5 ms (−79 %)** | 7.2 ms / 9.3 | **0.42 mm; 25 %** |

- LCP iterations and beam discretization barely matter, so the solver converges well under 500 iterations and the beam mechanics are not the bottleneck.
- Collision detection is. `alarmDistance` controls how many candidate contacts reach narrow phase and the constraint solver; 12 mm (> 5× the catheter radius) generated far more than needed.
- The saving has a price: at 5 mm the catheter surface starts penetrating the wall (up to 0.42 mm, a quarter of rotate-phase samples). The catheter centreline never left the vessel in any run. Caveat again: the faster configs got more key presses, so they pushed deeper.

That is the accuracy–performance trade-off in one table. Even the fastest config (7.2 ms) is still over budget, and going further costs stability. The right operating point has to come from a systematic sweep, not from hand-tuning.

## 4. Reproduce

1. Open `LAAC_Catheter_Demo.unity`.
2. **LAAC → Run Benchmark (current + legacy)**, or write your own `Benchmark/queue.txt`, e.g.
   ```
   abl_baseline
   abl_alarm5|alarmDistance="12"=>alarmDistance="5"
   ```
   then trigger a script reload. The driver polls the queue once per second in Edit mode.
3. Don't touch the editor while it runs (~1 min per line). Results land in `Benchmark/`.

## 5. Open

- Input is still frame-coupled (≤ 1 key event per frame); fix it so every config replays the identical trajectory.
- Re-measure in a standalone build and on the original 4.5 mm transseptal geometry.
- Full sweep over `alarmDistance` × `nbEdgesCollis` × `dt`, plus sweep-and-prune broad phase, to get a proper Pareto front.
