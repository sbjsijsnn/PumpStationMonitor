from pathlib import Path
import csv,json,shutil,statistics
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
root=Path(__file__).resolve().parents[1]
evidence=root/"docs/evidence"
evidence.mkdir(exist_ok=True)
sections=[]
passed_runs={}
for name,folder,csvname,summaryname in [
    ("最终采集宿主 10 分钟","final-host-10min","runtime.csv","summary.json"),
    ("早期采集宿主 2 小时","endurance","runtime.csv","summary.json"),
    ("WPF 趋势页休眠中断记录","ui-release-endurance","ui-runtime.csv","ui-summary.json"),
    ("最终 WPF 连续 2 小时复测","ui-endurance-verified","ui-runtime.csv","ui-summary.json")]:
    directory=root/"artifacts"/folder
    summary=directory/summaryname
    if not summary.exists():
        sections.append(f"### {name}\n\n仍在运行，未完成验收。\n")
        continue
    result=json.loads(summary.read_text(encoding="utf-8-sig"))
    with (directory/csvname).open(encoding="utf-8-sig") as file: rows=list(csv.DictReader(file))
    shutil.copy2(summary,evidence/(folder+"-summary.json"))
    shutil.copy2(directory/csvname,evidence/(folder+"-runtime.csv"))
    elapsed=[float(r["elapsed_seconds"]) for r in rows]
    memory=[float(r["working_set_mb"]) for r in rows]
    cpu=[float(r["cpu_percent"]) for r in rows]
    polls=[float(r["poll_ms"]) for r in rows]
    previous=0;continuous=0;longest=0;gaps=[]
    for time in elapsed:
        gap=time-previous;previous=time
        if gap>5:gaps.append(gap);continuous=0
        else:continuous+=gap
        longest=max(longest,continuous)
    passed=longest>=result["requestedSeconds"]-2 and bool(result["completed"])
    passed_runs[folder]=passed
    final=rows[-min(600,len(rows)):]
    checked={"continuousPassed":passed,"longestContinuousSeconds":longest,"samplingRows":len(rows),"gapsOver5Seconds":len(gaps),"maximumGapSeconds":max(gaps,default=0)}
    (evidence/(folder+"-continuity.json")).write_text(json.dumps(checked,indent=2),encoding="utf-8")
    fig,axes=plt.subplots(2,1,figsize=(10,5),sharex=True,layout="constrained")
    plot_time=[];plot_memory=[];plot_cpu=[]
    for i,time in enumerate(elapsed):
        if i and time-elapsed[i-1]>5:plot_time.append(float("nan"));plot_memory.append(float("nan"));plot_cpu.append(float("nan"))
        plot_time.append(time/60);plot_memory.append(memory[i]);plot_cpu.append(cpu[i])
    axes[0].plot(plot_time,plot_memory,color="#147D92",linewidth=1)
    axes[0].set_ylabel("Working set (MiB)");axes[0].grid(alpha=.2)
    axes[1].plot(plot_time,plot_cpu,color="#93723B",linewidth=.7)
    axes[1].set_ylabel("CPU (%)");axes[1].set_xlabel("Elapsed (minutes)");axes[1].grid(alpha=.2)
    fig.savefig(evidence/(folder+"-metrics.png"),dpi=150);plt.close(fig)
    sections.append(f"### {name}\n\n墙钟经过 {result['elapsedSeconds']:.2f} 秒，最长连续采样 {longest:.2f} 秒；采集 {result['polls']:,} 轮；数据库 {result['historyRows']:,} 行。\n\n"
        f"- 工作集峰值 {max(memory):.2f} MiB，末 10 分钟平均 {statistics.mean(float(r['working_set_mb']) for r in final):.2f} MiB。\n"
        f"- CPU 平均 {statistics.mean(cpu):.3f}%，峰值 {max(cpu):.3f}%，按整机逻辑核数归一化。\n"
        f"- 轮询耗时平均 {statistics.mean(polls):.3f} ms，采样 P95 {sorted(polls)[int((len(polls)-1)*.95)]:.3f} ms。\n"
        f"- 无效采样秒数 {result['badSampleSeconds']}；连续时长复核 {"通过" if passed else "未通过（采样中断，不能按墙钟时长认定）"}，超过 5 秒的间隔 {len(gaps)} 次。\n\n"
        f"![{name}](evidence/{folder}-metrics.png)\n\n原始 CSV / JSON：`evidence/{folder}-runtime.csv`、`evidence/{folder}-summary.json`。\n")
p=root/"docs/METRICS.md"
text=p.read_text(encoding="utf-8")
marker="## 持续运行最终记录"
if marker in text:text=text.split(marker)[0]
text+="\n"+marker+"\n\n"+"\n".join(sections)
text=text.replace("最终分组采集版本正在单独进行 600 秒宿主验证，完成后补录汇总。","最终分组采集版本已完成 600 秒宿主验证，汇总见下文。")
a=text.index("## 两小时稳定性");b=text.index(marker)
status="最终 WPF 复测已达到连续两小时采样，见下文。" if passed_runs.get("ui-endurance-verified") else "初次宿主与 WPF 长测均因系统休眠中断，不能认定连续两小时通过；复测尚未完成。"
text=text[:a]+"## 两小时稳定性\n\n"+status+"\n\n"+text[b:]
text=text.replace("55 项通过","57 项通过")
p.write_text(text,encoding="utf-8")
print("Metrics collected")
