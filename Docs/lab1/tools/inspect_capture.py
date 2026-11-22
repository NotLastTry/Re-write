import sys
import pandas as pd

path = sys.argv[1]
df = pd.read_csv(path, engine="python")
print(df.shape)
steady = df[df["stage"] == "steady"]
print(steady["dt_ms"].describe())
cols = [c for c in ["time_s", "frame", "dt_ms", "cpu_main_ms", "cpu_render_ms", "gpu_ms",
                    "mono_mb", "total_mb", "Voxels.Column", "Voxels.ChunkMesh",
                    "Voxels.NeighbourRemesh", "Voxels.ColliderBake", "GC Allocated In Frame",
                    "Standard Draw Calls Count", "Triangles Count"] if c in df.columns]
print(df.nlargest(15, "dt_ms")[cols].to_string())
