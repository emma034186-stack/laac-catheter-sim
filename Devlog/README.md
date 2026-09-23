# Dev Log

Chronological debugging/tuning notes for this project, written as they happened.

| Date | Entry | Summary |
|---|---|---|
| 2026-09-16/17 | [../README.md](../README.md) | Initial infrastructure fixes (native DLL, CJK path, relative-path resolution), vessel mesh reconstruction, first round of collision tuning |
| 2026-09-18 | [2026-09-18-new-project-rebuild.md](2026-09-18-new-project-rebuild.md) | Rebuilt the project from scratch in a clean Unity project after the old scene's Unity↔SOFA reconnect state became unreliable; endoscope camera tracking; a physics-step/framerate coupling bug that was silently weakening collision response; parameter tuning for speed vs. tunneling |
| 2026-09-23 | [2026-09-23-performance-benchmark.md](2026-09-23-performance-benchmark.md) | Step timing + scripted benchmark runner; ~31 ms per SOFA step vs. a 5 ms budget; catch-up loop trades FPS for physics rate; one-factor ablation shows collision detection (`alarmDistance`) dominates cost |
