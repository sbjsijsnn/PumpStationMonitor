from pathlib import Path
import os, subprocess, sys, time
root=Path(__file__).resolve().parents[1]
mode=sys.argv[1] if len(sys.argv)>1 else "smoke"
out=root/"artifacts"/("ui-release-"+mode)
out.mkdir(parents=True,exist_ok=True)
with (out/"simulator.log").open("w",encoding="utf-8") as log:
    sim=subprocess.Popen([str(root/"artifacts/release/Simulator/PumpStationMonitor.Simulator.exe"),"1502"],stdin=subprocess.PIPE,stdout=log,stderr=log,text=True,encoding="utf-8",creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        time.sleep(1)
        if sim.poll() is not None: raise RuntimeError("Simulator failed to start")
        if mode=="smoke": sim.stdin.write("fault 1\nlevel 9.5\n");sim.stdin.flush()
        env=os.environ.copy();env["PUMP_DATA_DIR"]=str(out)
        app=subprocess.run([str(root/"artifacts/release/App/PumpStationMonitor.App.exe"),"--ui-"+mode],env=env,timeout=7500 if mode=="endurance" else 60)
        (out/"exit-code.txt").write_text(str(app.returncode),encoding="utf-8")
        if app.returncode: raise RuntimeError("UI process failed")
    finally:
        if sim.poll() is None: sim.communicate("quit\n",timeout=10)
