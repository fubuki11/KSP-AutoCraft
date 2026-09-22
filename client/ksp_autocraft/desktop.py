"""One-shot machine interface for the in-game panel, with no model call at startup."""
from pathlib import Path
import json

from .designer import design, write_design
from .llm import strict_json
from .gateway import GatewayModelClient


def _short_strings(values, limit):
    return [str(value)[:1000] for value in values[:limit]]


def run_job(client, job_path: Path, ksp_root=None) -> dict:
    if job_path.stat().st_size > 65536:
        raise ValueError("Desktop job exceeds the size limit.")
    job = strict_json(job_path.read_text(encoding="utf-8"))
    allowed = {"task", "expectedVersion", "prompt", "contractId", "budget", "maxMass", "output",
               "vehicle", "targetAltitude", "cruiseSpeed", "requiredDeltaV", "minTwr", "maxStallSpeed", "minEndurance"}
    if not isinstance(job, dict) or set(job) - allowed or job.get("task") not in ("health", "design"):
        raise ValueError("Invalid desktop job.")
    if not isinstance(job.get("expectedVersion"), str) or not job["expectedVersion"]:
        raise ValueError("Desktop job needs an expected plugin version.")
    for name in ("prompt", "contractId", "output"):
        if job.get(name) is not None and not isinstance(job[name], str):
            raise ValueError("Desktop job paths and text must be strings.")
    health = client.health()
    if health.get("version") != job.get("expectedVersion"):
        raise ValueError("The Python client reached a different plugin version. Restart KSP with the matching release.")
    reply = {"ok": True, "task": job["task"], "pluginVersion": health["version"], "apiReady": True}
    if job["task"] == "health":
        try:
            model = GatewayModelClient(ksp_root)
            model.check_ready()  # Configuration/status check only, never inference or shared-token rotation.
            reply.update(modelReady=True, modelStatus=f"AI Hub {model.hub_version}: {model.profile} / {model.model}. Inference starts only when you request a design.")
        except (RuntimeError, ValueError, OSError, TypeError) as error:
            reply.update(modelReady=False, modelStatus=str(error)[:1000])
        return reply
    output = Path(job.get("output", ""))
    if not output.is_absolute() or output.suffix.lower() != ".json" or not output.parent.is_dir():
        raise ValueError("Desktop output must be an absolute .json path in an existing directory.")
    if output.exists() or output.with_suffix(".report.json").exists():
        raise ValueError("Desktop output already exists.")
    model = GatewayModelClient(ksp_root)
    model.check_ready()
    plan, report = design(client, model, job.get("prompt"), contract_id=job.get("contractId"),
                          budget=job.get("budget"), max_mass=job.get("maxMass"), vehicle=job.get("vehicle") or "auto",
                          target_altitude=job.get("targetAltitude"), cruise_speed=job.get("cruiseSpeed"), required_delta_v=job.get("requiredDeltaV"),
                          min_twr=job.get("minTwr"), max_stall_speed=job.get("maxStallSpeed"), min_endurance=job.get("minEndurance"))
    report_path = write_design(output, plan, report)
    reply.update(planFile=str(output), reportFile=str(report_path), assessment=report["assessment"]["status"],
                 partCount=len(plan["parts"]), wetMassTonnes=report["validation"]["wetMassTonnes"],
                 estimatedCost=report["validation"]["estimatedCost"],
                 missionSteps=_short_strings(report["missionSteps"], 20), assumptions=_short_strings(report["assumptions"], 12))
    performance = report["performance"]
    metrics = performance["metrics"]
    reply["performanceStatus"] = performance["status"]
    if report["vehicleType"] == "rocket":
        reply["performanceSummary"] = [f"发射 TWR: {metrics.get('launchTwr')}",
            f"预留燃料后的分级真空 Δv: {metrics.get('deltaVVacuumWithReserve',0):.0f} m/s / 目标 {metrics.get('requiredDeltaV',0):.0f} m/s"]
    else:
        reply["performanceSummary"] = [f"估算失速速度: {metrics.get('estimatedStallSpeed')} m/s", f"巡航配平迎角: {metrics.get('trimAoADegrees')}°",
            f"可用推力 / 阻力估计: {metrics.get('availableThrustKn',0):.1f} / {metrics.get('estimatedDragKn',0):.1f} kN",
            f"静稳定裕度: {metrics.get('staticMargin')}", f"满推力燃料时长: {metrics.get('fullThrottleEnduranceSeconds',0):.0f} s"]
    reply["performanceSummary"].append("通过性能筛选不等于完成实际飞行验证。")
    return reply


def write_reply(reply):
    # Compact UTF-8 output is consumed only by the owning in-game process runner.
    print(json.dumps(reply, ensure_ascii=False, allow_nan=False, separators=(",", ":")))
