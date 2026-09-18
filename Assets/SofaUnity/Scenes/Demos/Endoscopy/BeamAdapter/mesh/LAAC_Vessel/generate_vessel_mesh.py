# -*- coding: utf-8 -*-
"""
Generates a simplified, idealized tube mesh approximating the LAAC catheter
path: inferior vena cava (IVC) -> right atrium (RA) chamber -> transseptal
puncture point -> left atrium (LA) -> left atrial appendage (LAA) ostium.

This is a PLACEHOLDER anatomy (not derived from a patient CT segmentation).
Once the CT -> Marching Cubes reconstruction pipeline (research method (一))
produces a real patient vessel/chamber mesh, replace this file's output with
that mesh and keep the same coordinate convention (path starts at local
origin, tangent along +X) so the catheter starting pose in the .scn stays
valid.

Units: millimeters, matching the SOFA scene's native unit convention.

Usage:
    python generate_vessel_mesh.py
Output:
    LAAC_vessel_path.obj  (written next to this script)
"""
import numpy as np
import os

# ---- path control points (mm): IVC -> RA -> transseptal -> LA -> LAA ostium
CONTROL_POINTS = np.array([
    [0,   0,   0],    # P0 IVC entry (catheter insertion start, tangent +X)
    [60,  4,   0],    # P1 mid-IVC
    [110, 18,  4],    # P2 IVC/RA junction
    [150, 38,  12],   # P3 inside RA chamber
    [175, 52,  22],   # P4 approaching fossa ovalis
    [185, 58,  30],   # P5 transseptal puncture point (narrowest)
    [205, 62,  42],   # P6 just through septum, entering LA
    [230, 58,  60],   # P7 LA body
    [252, 48,  76],   # P8 approaching LAA ostium
    [268, 40,  90],   # P9 LAA ostium (target)
], dtype=float)

# ---- radius profile (mm) at the same parametric t as control points (0..1)
RADIUS_AT_CP = np.array([
    11.0,  # IVC
    11.0,  # IVC
    13.0,  # IVC/RA junction (widening)
    27.0,  # RA chamber (open space)
    18.0,  # narrowing toward septum
    6.5,   # transseptal puncture point (fossa ovalis) -- TEMP widened from 4.5 for collision-tuning test
    16.0,  # just through septum
    22.0,  # LA body
    18.0,  # approaching ostium
    11.0,  # LAA ostium
])

N_SAMPLES = 160          # points along the path
N_CIRCLE = 16            # vertices per cross-section circle


def catmull_rom(p, n_samples):
    """Catmull-Rom spline through control points p (Nx3) -> (n_samples,3)."""
    pts = np.vstack([p[0], p, p[-1]])  # clamp ends
    n_seg = len(p) - 1
    out = []
    t_all = np.linspace(0, n_seg, n_samples)
    for t in t_all:
        seg = min(int(np.floor(t)), n_seg - 1)
        local_t = t - seg
        p0, p1, p2, p3 = pts[seg], pts[seg + 1], pts[seg + 2], pts[seg + 3]
        tt = local_t
        tt2 = tt * tt
        tt3 = tt2 * tt
        point = 0.5 * (
            (2 * p1)
            + (-p0 + p2) * tt
            + (2 * p0 - 5 * p1 + 4 * p2 - p3) * tt2
            + (-p0 + 3 * p1 - 3 * p2 + p3) * tt3
        )
        out.append(point)
    return np.array(out)


def build_tube(path, radii, n_circle):
    """Extrude circular cross-sections along path using parallel transport
    frames (avoids Frenet-frame flips at inflection/straight points)."""
    n = len(path)
    tangents = np.zeros_like(path)
    tangents[1:-1] = path[2:] - path[:-2]
    tangents[0] = path[1] - path[0]
    tangents[-1] = path[-1] - path[-2]
    tangents /= np.linalg.norm(tangents, axis=1, keepdims=True)

    # seed normal: any vector not parallel to first tangent
    seed = np.array([0.0, 0.0, 1.0])
    if abs(np.dot(seed, tangents[0])) > 0.9:
        seed = np.array([0.0, 1.0, 0.0])
    normal = seed - tangents[0] * np.dot(seed, tangents[0])
    normal /= np.linalg.norm(normal)

    normals = [normal]
    for i in range(1, n):
        t_prev, t_cur = tangents[i - 1], tangents[i]
        axis = np.cross(t_prev, t_cur)
        axis_norm = np.linalg.norm(axis)
        if axis_norm < 1e-8:
            n_new = normals[-1]
        else:
            axis /= axis_norm
            angle = np.arccos(np.clip(np.dot(t_prev, t_cur), -1.0, 1.0))
            n_prev = normals[-1]
            n_new = (
                n_prev * np.cos(angle)
                + np.cross(axis, n_prev) * np.sin(angle)
                + axis * np.dot(axis, n_prev) * (1 - np.cos(angle))
            )
            n_new -= t_cur * np.dot(n_new, t_cur)
            n_new /= np.linalg.norm(n_new)
        normals.append(n_new)
    normals = np.array(normals)
    binormals = np.cross(tangents, normals)

    verts = []
    uvs = []
    # V_REPEAT: how many times the texture tiles along the tube's length,
    # so a real diffuse/normal map doesn't stretch across the whole ~300mm path.
    V_REPEAT = 10.0
    for i in range(n):
        c = path[i]
        r = radii[i]
        nrm, bnm = normals[i], binormals[i]
        v_coord = V_REPEAT * i / (n - 1)
        for k in range(n_circle):
            ang = 2 * np.pi * k / n_circle
            v = c + r * (np.cos(ang) * nrm + np.sin(ang) * bnm)
            verts.append(v)
            uvs.append((k / n_circle, v_coord))
    verts = np.array(verts)
    uvs = np.array(uvs)

    faces = []
    for i in range(n - 1):
        for k in range(n_circle):
            k2 = (k + 1) % n_circle
            a = i * n_circle + k
            b = i * n_circle + k2
            c = (i + 1) * n_circle + k
            d = (i + 1) * n_circle + k2
            faces.append((a, c, b))
            faces.append((b, c, d))
    return verts, np.array(faces), uvs


def write_obj(path_out, verts, faces, uvs):
    with open(path_out, "w") as f:
        f.write("# LAAC idealized vessel path (IVC-RA-transseptal-LA-LAA)\n")
        f.write("# generated by generate_vessel_mesh.py - placeholder geometry\n")
        for v in verts:
            f.write(f"v {v[0]:.4f} {v[1]:.4f} {v[2]:.4f}\n")
        for uv in uvs:
            f.write(f"vt {uv[0]:.5f} {uv[1]:.5f}\n")
        for a, b, c in faces:
            f.write(f"f {a + 1}/{a + 1} {b + 1}/{b + 1} {c + 1}/{c + 1}\n")


if __name__ == "__main__":
    t_cp = np.linspace(0, 1, len(CONTROL_POINTS))
    path = catmull_rom(CONTROL_POINTS, N_SAMPLES)
    t_path = np.linspace(0, 1, N_SAMPLES)
    radii = np.interp(t_path, t_cp, RADIUS_AT_CP)

    verts, faces, uvs = build_tube(path, radii, N_CIRCLE)

    out_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "LAAC_vessel_path.obj")
    write_obj(out_path, verts, faces, uvs)
    print(f"wrote {len(verts)} verts, {len(faces)} faces -> {out_path}")
    print("path start:", path[0], "path end:", path[-1])
