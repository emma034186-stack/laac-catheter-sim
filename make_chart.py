import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm
import numpy as np

labels = ["IVC entry","mid-IVC","IVC/RA\njunction","RA\nchamber","approaching\nfossa ovalis","transseptal\npuncture","post-\nseptum","LA\nbody","approaching\nLAA ostium","LAA\nostium"]
t = np.linspace(0,1,10)
radius = np.array([11.0,11.0,13.0,27.0,18.0,4.5,16.0,22.0,18.0,11.0])
catheter_r = 2.3
bottleneck_i = 5

fig, ax = plt.subplots(figsize=(9.5,4.6), dpi=200)

ax.fill_between(t, radius, catheter_r, color="#0E7C86", alpha=0.10, zorder=1)
ax.plot(t, radius, color="#0E7C86", linewidth=2.2, marker="o", markersize=5,
        markerfacecolor="#0E7C86", markeredgecolor="white", markeredgewidth=1, zorder=3, label="Vessel wall radius")
ax.axhline(catheter_r, color="#55636B", linewidth=1.1, linestyle=(0,(4,3)), zorder=2, label="Catheter radius (2.3 mm)")

ax.scatter([t[bottleneck_i]],[radius[bottleneck_i]], s=90, facecolor="#FAEAE8", edgecolor="#C1443B", linewidth=2, zorder=4)
ax.annotate("bottleneck: 4.5 mm\nclearance ≈ 2.2 mm",
            xy=(t[bottleneck_i], radius[bottleneck_i]), xytext=(t[bottleneck_i]+0.05, radius[bottleneck_i]+7.2),
            fontsize=9.5, color="#C1443B", fontweight="bold",
            arrowprops=dict(arrowstyle="-", color="#C1443B", linewidth=1))

ax.set_xticks(t)
ax.set_xticklabels(labels, fontsize=8.2, rotation=0)
ax.set_ylabel("radius (mm)", fontsize=10)
ax.set_ylim(0, 30)
ax.set_xlim(-0.02, 1.02)
ax.grid(axis="y", color="#D8E0E3", linewidth=0.8, zorder=0)
for spine in ["top","right"]:
    ax.spines[spine].set_visible(False)
ax.spines["left"].set_color("#B8C2C7")
ax.spines["bottom"].set_color("#B8C2C7")
ax.tick_params(colors="#55636B")
ax.set_title("Vessel radius along the IVC → RA → transseptal → LA → LAA path", fontsize=11.5, fontweight="bold", color="#1B2530", loc="left", pad=12)
ax.legend(loc="upper right", frameon=False, fontsize=9)

fig.tight_layout()
fig.savefig("C:/Users/f6086/AppData/Local/Temp/laac_repo/assets/radius-profile.png", facecolor="white")
print("done")
