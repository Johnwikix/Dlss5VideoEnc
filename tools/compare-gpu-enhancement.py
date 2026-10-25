"""Compare complete decoded A/B benchmark outputs with CPU SSIM and PSNR.

Usage: python tools/compare-gpu-enhancement.py .qa/gpu-enhancement-benchmark-20261025
The native byte-equality tests isolate transport; this measures the whole video,
including the GPU NVDEC color conversion versus the compatibility swscale path.
"""
import json
from pathlib import Path
import re
import subprocess
import sys

repo = Path(__file__).resolve().parents[1]
root = Path(sys.argv[1]).resolve()
rows = json.loads((root / "measurements.json").read_text(encoding="utf-8"))
results = []
for name in dict.fromkeys(row["Scenario"] for row in rows):
    host = next(row for row in rows if row["Scenario"] == name and row["Trial"] == 1 and not row["Gpu"])
    gpu = next(row for row in rows if row["Scenario"] == name and row["Trial"] == 1 and row["Gpu"])
    graph = f"[0:v]split[a][c];[1:v]split[b][d];[a][b]ssim=stats_file={name}-ssim.log[s];[c][d]psnr=stats_file={name}-psnr.log[p]"
    run = subprocess.run([str(repo / "runtime/ffmpeg.exe"), "-hide_banner", "-threads", "2", "-i", str(Path(host["Output"]).resolve()),
        "-threads", "2", "-i", str(Path(gpu["Output"]).resolve()), "-filter_complex_threads", "2", "-filter_complex", graph,
        "-map", "[s]", "-map", "[p]", "-f", "null", "-"], cwd=root, capture_output=True, text=True, check=True)
    (root / f"{name}-quality.log").write_text(run.stderr, encoding="utf-8")
    values = [float(v) for v in re.findall(r"All:([\d.]+)", (root / f"{name}-ssim.log").read_text())]
    assert values
    row = dict(scenario=name, decodedFrames=len(values), meanSsim=sum(values)/len(values), minSsim=min(values),
        psnr=float(re.findall(r"average:(\S+)", run.stderr)[-1]))
    results.append(row)
    (root / "quality.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(row), flush=True)
