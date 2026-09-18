from pathlib import Path
import csv, json, subprocess, time

root = Path(__file__).resolve().parents[1]
run = root / "artifacts/ui-endurance-verified"
# 这是本次长测的有限收尾步骤，不是常驻定时任务。
deadline = time.monotonic() + 7800
while not (run / "exit-code.txt").exists():
    if time.monotonic() > deadline:
        raise TimeoutError("Validation process did not complete")
    time.sleep(30)
if (run / "exit-code.txt").read_text().strip() != "0":
    raise RuntimeError("UI validation failed")
result = json.loads((run / "ui-summary.json").read_text(encoding="utf-8-sig"))
if not result.get("completed") or result.get("continuousSeconds", 0) < 7200 or result.get("badSampleSeconds", 1) != 0:
    raise RuntimeError("Continuous validation did not meet acceptance criteria")
subprocess.run(["python", "scripts/Collect-Metrics.py"], cwd=root, check=True)
subprocess.run(["python", "scripts/Package.py"], cwd=root, check=True)
subprocess.run(["git", "add", "docs/METRICS.md", "docs/evidence"], cwd=root, check=True)
subprocess.run(["git", "commit", "-m", "记录 WPF 连续两小时验收与最终性能曲线"], cwd=root, check=True)
(run / "delivery-complete.txt").write_text(
    "Continuous validation, metrics, package and evidence commit completed.\n", encoding="utf-8"
)
