"""Deterministic preflight screening, distinct from trajectory/CFD simulation."""
import math
import re

from .contracts import walk_requirements
from .physics import STANDARD_GRAVITY


def vec(value):
    return (float(value.get("x", 0)), float(value.get("y", 0)), float(value.get("z", 0)))


def add(a, b): return tuple(x + y for x, y in zip(a, b))
def sub(a, b): return tuple(x - y for x, y in zip(a, b))
def mul(a, scale): return tuple(x * scale for x in a)
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def length(a): return math.sqrt(dot(a, a))


def rotate(q, value):
    xyz = (q.get("x", 0), q.get("y", 0), q.get("z", 0))
    return add(value, add(mul(cross(xyz, value), 2*q.get("w", 1)), mul(cross(xyz, cross(xyz, value)), 2)))


def curve(keys, x, default=1.0):
    if not keys: return default
    keys = sorted(keys, key=lambda key: key["x"])
    if x <= keys[0]["x"]: return keys[0]["y"]
    if x >= keys[-1]["x"]: return keys[-1]["y"]
    for left, right in zip(keys, keys[1:]):
        if left["x"] <= x <= right["x"]:
            span = right["x"] - left["x"]
            if span <= 0: return right["y"]
            t = (x-left["x"])/span
            return ((2*t**3-3*t**2+1)*left["y"] + (t**3-2*t**2+t)*span*left.get("outTangent", 0) +
                    (-2*t**3+3*t**2)*right["y"] + (t**3-t**2)*span*right.get("inTangent", 0))
    return default


def engine_output(engine, environment, speed=0):
    pressure = environment["pressureKpa"] / 101.325
    density = environment["densityKgPerCubicMetre"]
    sound = environment.get("speedOfSound", 0)
    mach = speed/sound if sound > 0 else 0
    isp = curve(engine.get("ispCurve"), pressure, engine.get("vacuumIspSeconds", 0))
    density_ratio = density/1.225
    if engine.get("useAtmosphereIspCurve"): isp *= curve(engine.get("atmosphereIspCurve"), density_ratio)
    if engine.get("useVelocityIspCurve"): isp *= curve(engine.get("velocityIspCurve"), mach)
    flow_multiplier = density_ratio if engine.get("atmosphereChangesFlow") else 1
    if engine.get("useAtmosphereFlowCurve"): flow_multiplier = curve(engine.get("atmosphereFlowCurve"), flow_multiplier)
    if engine.get("useVelocityFlowCurve"): flow_multiplier *= curve(engine.get("velocityFlowCurve"), mach)
    cap = engine.get("flowCap", 1e30)
    if cap > 0 and flow_multiplier > cap:
        extra = flow_multiplier-cap
        flow_multiplier = cap + extra/(engine.get("flowCapSharpness", 2) + extra/cap)
    mdot = engine.get("massFlowTonnesPerSecond", 0) * max(0, flow_multiplier) * engine.get("thrustLimiter", 1)
    return max(0, mdot*isp*STANDARD_GRAVITY), max(0, mdot), max(0, isp)


def vehicle_type(prompt, facility, explicit="auto", detail=None):
    if explicit in ("rocket", "aircraft"): return explicit
    if explicit != "auto": raise ValueError("vehicle must be auto, rocket or aircraft.")
    if re.search(r"飞机|航空|喷气|客机|滑翔|螺旋桨|aircraft|airplane|aeroplane|spaceplane|\bplane\b|\bSSTO\b", prompt, re.I): return "aircraft"
    if re.search(r"火箭|rocket", prompt, re.I): return "rocket"
    if detail and "SurveyContract" in detail.get("contract", {}).get("type", ""): return "aircraft"
    return "aircraft" if facility == "SPH" else "rocket"


def targets_for(vehicle, prompt, world, detail=None, *, target_altitude=None, cruise_speed=None, required_delta_v=None,
                min_twr=None, max_stall_speed=None, min_endurance=None):
    body = next((b for b in world.get("bodies", []) if b["name"] == world.get("homeBody")), None)
    if not body: raise ValueError("World data does not identify the launch body.")
    for value in (target_altitude, cruise_speed, required_delta_v, min_twr, max_stall_speed, min_endurance):
        if value is not None and (isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0):
            raise ValueError("Performance targets must be finite and nonnegative.")
    if cruise_speed == 0 or max_stall_speed == 0: raise ValueError("Cruise/stall speeds must be positive.")
    requirements = list(walk_requirements(detail.get("requirements", []))) if detail else []
    situations, foreign_bodies, altitudes = set(), set(), []
    reserved_test_parts = set()
    for requirement in requirements:
        facts = {f["name"]: f["value"] for f in requirement.get("facts", [])}
        situation = facts.get("Situation", facts.get("situation", ""))
        if situation: situations.add(situation)
        destination = requirement.get("targetBody") or facts.get("Destination") or facts.get("body")
        if destination and destination != body["name"]: foreign_bodies.add(destination)
        try:
            if "minAltitude" in facts:
                low, high = float(facts["minAltitude"]), float(facts.get("maxAltitude", facts["minAltitude"]))
                if math.isfinite(low) and math.isfinite(high): altitudes.append(max(low, (low+min(high, low+1000000))/2))
        except ValueError: pass
        if requirement.get("kind") == "part_test" and situation in ("ORBITING", "ESCAPING", "SUB_ORBITAL"):
            reserved_test_parts.add(requirement.get("requiredPart"))
    common = {"vehicle": vehicle, "body": body["name"], "fuelReserveFraction": 0.05, "reservedTestParts": sorted(reserved_test_parts),
              "bodyRadius": body["radiusMetres"], "bodyMu": body["gravitationalParameter"]}
    if vehicle == "aircraft":
        if not body.get("atmosphere"): raise ValueError("The launch body has no atmosphere for a conventional aircraft.")
        if world.get("aerodynamicModel", "Stock") != "Stock": raise ValueError("FAR aerodynamics requires a dedicated solver; stock lift screening is not used as a substitute.")
        if foreign_bodies: raise ValueError("Conventional aircraft screening does not cover interplanetary flight. Specify an appropriate rocket mission.")
        altitude = target_altitude if target_altitude is not None else (min(altitudes) if altitudes else 5000)
        if altitude >= body.get("atmosphereDepthMetres", 0) or "ORBITING" in situations or "ESCAPING" in situations:
            raise ValueError("This aircraft solver screens atmospheric fixed-wing flight; an orbital/SSTO mission requires a separate multi-phase propulsion solver.")
        return {**common, "targetAltitude": altitude, "cruiseSpeed": cruise_speed or 150,
                "maxStallSpeed": max_stall_speed or 70, "minEnduranceSeconds": min_endurance if min_endurance is not None else 900,
                "minStaticMargin": 0.03, "maxStaticMargin": 0.6, "maxTrimAoADegrees": 12, "maxLiftAoADegrees": 20,
                "bodyDragCoefficient": 0.08, "dragMargin": 1.2, "minAircraftTwr": min_twr if min_twr is not None else 0.25}
    if foreign_bodies and required_delta_v is None:
        raise ValueError("The contract targets another body. Supply an explicit mission delta-v budget; the solver will not invent a transfer budget.")
    ground = bool(situations) and situations.issubset({"LANDED", "SPLASHED", "PRELAUNCH"}) and not altitudes
    suborbital = "SUB_ORBITAL" in situations or bool(re.search(r"亚轨道|suborbital|sub-orbital", prompt, re.I))
    altitude = target_altitude if target_altitude is not None else (max(altitudes) if altitudes else max(body.get("atmosphereDepthMetres", 0)+10000, 80000))
    radius, mu = body["radiusMetres"], body["gravitationalParameter"]
    loss = 1100 if body.get("atmosphere") else 300
    if ground: estimate = 0
    elif suborbital: estimate = math.sqrt(2*mu*(1/radius-1/(radius+altitude))) + loss*0.6
    else:
        semi_major = radius+altitude/2
        estimate = math.sqrt(mu*(2/radius-1/semi_major)) + math.sqrt(mu/(radius+altitude)) - math.sqrt(mu*(2/(radius+altitude)-1/semi_major)) + loss
    return {**common, "mission": "ground_test" if ground else "suborbital" if suborbital else "orbit", "targetAltitude": altitude,
            "requiredDeltaV": required_delta_v if required_delta_v is not None else estimate,
            "minLaunchTwr": min_twr if min_twr is not None else 0 if ground else 1.2, "lossAllowance": loss,
            "maxThrustOffsetMetres": 0.25, "budgetSource": "explicit" if required_delta_v is not None else "analytic surface-to-target plus stated loss allowance"}


def _parts(plan, catalog, validation):
    definitions = {p["name"]: p for p in catalog}
    poses = {p["id"]: p for p in validation.get("placements", [])}
    if len(poses) != len(plan["parts"]): raise ValueError("Plugin did not return every resolved placement.")
    result = {}
    for item in plan["parts"]:
        definition = definitions[item["partName"]]
        overrides = {r["name"]: r["amount"] for r in item.get("resources", [])}
        resources = {r["name"]: {**r, "amount": overrides.get(r["name"], r["amount"])} for r in definition.get("resources", [])}
        result[item["id"]] = {"plan": item, "definition": definition, "pose": poses[item["id"]], "resources": resources}
    return result


def _mass(part):
    return part["definition"]["dryMassTonnes"] + sum(r["amount"]*r["densityTonnesPerUnit"] for r in part["resources"].values())


def _point(part, name):
    return add(vec(part["pose"]["position"]), rotate(part["pose"]["rotation"], vec(part["definition"].get(name, {}))))


def _centre(parts, retained):
    total = sum(_mass(parts[i]) for i in retained)
    centre = (0, 0, 0)
    for i in retained: centre = add(centre, mul(_point(parts[i], "massOffset"), _mass(parts[i])))
    return mul(centre, 1/total) if total > 0 else centre


def aircraft_lifting_part(definition):
    modules = definition.get("moduleNames", [])
    return any(not a.get("airbrake", False) for a in definition.get("aero", [])) and not any(name in modules for name in ("ModuleAblator", "ModuleAeroSurface"))


def landing_gear_part(definition):
    if not definition.get("wheel"): return False
    if definition.get("wheelType"): return definition["wheelType"] == "FREE"
    # Compatibility with early flight catalogs that had only the broad wheel flag.
    name = (definition.get("name", "") + " " + definition.get("title", "")).lower()
    return ("gear" in name or "起落架" in name) and "landingleg" not in name


def _fuel_sources(parts, retained, engine_id, propellant, severed):
    mode = propellant.get("flowMode", "UNKNOWN")
    if mode == "NO_FLOW": return [engine_id] if propellant["name"] in parts[engine_id]["resources"] else []
    if mode in ("ALL_VESSEL", "ALL_VESSEL_BALANCE"):
        return [i for i in retained if propellant["name"] in parts[i]["resources"]]
    if mode not in ("STACK_PRIORITY_SEARCH", "STAGE_PRIORITY_FLOW", "STAGE_PRIORITY_FLOW_BALANCE", "STAGE_STACK_FLOW", "STAGE_STACK_FLOW_BALANCE"):
        raise ValueError("Unsupported fuel-flow mode: " + mode)
    queue, seen = [engine_id], {engine_id}
    while queue:
        current = queue.pop()
        definition = parts[current]["definition"]
        if current != engine_id and not definition.get("fuelCrossFeed", True): continue
        for other in retained:
            if other in seen or frozenset((current, other)) in severed: continue
            if parts[other]["plan"].get("parentId") == current:
                a, b = parts[other]["plan"].get("parentNodeId"), parts[other]["plan"].get("childNodeId")
            elif parts[current]["plan"].get("parentId") == other:
                a, b = parts[current]["plan"].get("childNodeId"), parts[current]["plan"].get("parentNodeId")
            else: continue
            if (a and a in definition.get("noCrossFeedNodeKey", "").split(',')) or (b and b in parts[other]["definition"].get("noCrossFeedNodeKey", "").split(',')): continue
            seen.add(other); queue.append(other)
    return [i for i in seen if propellant["name"] in parts[i]["resources"]]


def _rocket(parts, targets, environment, issues):
    root = next(iter(parts)); retained = set(parts); severed = set(); active = set(); stages = []
    engine_rows = {(i, j): e for i, p in parts.items() for j, e in enumerate(p["definition"].get("engines", [])) if e.get("defaultMode", True)}
    test_parts = set(targets.get("reservedTestParts", []))
    usable = {(i, name): r["amount"]*(1-targets["fuelReserveFraction"]) for i, p in parts.items() for name, r in p["resources"].items()}
    events = sorted({p["plan"]["stage"] for p in parts.values() if p["plan"]["stage"] >= 0}, reverse=True)
    total_dv, launch_twr, first_burn_stage = 0.0, None, None
    vacuum = {**environment, "pressureKpa": 0, "densityKgPerCubicMetre": 0, "speedOfSound": 0}
    for stage in events:
        for i in list(retained):
            p = parts[i]
            if p["plan"]["stage"] != stage: continue
            for separator in p["definition"].get("separators", []):
                parent = p["plan"].get("parentId")
                if parent and (separator.get("omni") or p["plan"].get("childNodeId") == separator.get("nodeId") or p["plan"].get("attachment") == "surface"):
                    severed.add(frozenset((i, parent)))
                for child in retained:
                    if parts[child]["plan"].get("parentId") == i and (separator.get("omni") or parts[child]["plan"].get("parentNodeId") == separator.get("nodeId")):
                        severed.add(frozenset((i, child)))
            if "LaunchClamp" in p["definition"].get("moduleNames", []) and p["plan"].get("parentId"):
                severed.add(frozenset((i, p["plan"]["parentId"])))
        connected, queue = {root}, [root]
        while queue:
            current = queue.pop()
            for other in retained-connected:
                if frozenset((current, other)) not in severed and (parts[current]["plan"].get("parentId") == other or parts[other]["plan"].get("parentId") == current):
                    connected.add(other); queue.append(other)
        dropped = sorted(retained-connected); retained = connected
        active = {key for key in active if key[0] in retained}
        for key, engine in engine_rows.items():
            i = key[0]
            if i in retained and parts[i]["plan"]["stage"] == stage and parts[i]["plan"]["partName"] not in test_parts:
                if not engine.get("supported") or engine.get("family") == "airbreathing":
                    issues.append("无法可靠评估推进模块：" + parts[i]["plan"]["id"]); continue
                active.add(key)
        start_mass = sum(_mass(parts[i]) for i in retained); dv, burn_time = 0.0, 0.0
        for _ in range(2048):
            demands, flows, thrusts = {}, {}, {}
            for key in active:
                i = key[0]; e = engine_rows[key]
                thrust, mdot, _ = engine_output(e, vacuum)
                density_sum = sum(p["ratio"]*p["density"] for p in e.get("mixture", []) if not p.get("ignoreForIsp"))
                if density_sum <= 0 or mdot <= 0: continue
                local = {}; runnable = True
                for propellant in e.get("mixture", []):
                    sources = _fuel_sources(parts, retained, i, propellant, severed)
                    available = sum(usable.get((source, propellant["name"]), 0) for source in sources)
                    if available <= 1e-8: runnable = False; break
                    rate = mdot/density_sum*propellant["ratio"]
                    for source in sources:
                        resource_key = (source, propellant["name"])
                        local[resource_key] = local.get(resource_key, 0) + rate*usable.get(resource_key, 0)/available
                if runnable:
                    flows[key] = mdot; thrusts[key] = thrust
                    for resource, rate in local.items(): demands[resource] = demands.get(resource, 0)+rate
            if not flows or not demands: break
            dt = min(usable[key]/rate for key, rate in demands.items() if rate > 0)
            if dt <= 1e-9: break
            mass_before = sum(_mass(parts[i]) for i in retained)
            force = (0, 0, 0)
            moment = (0, 0, 0); centre = _centre(parts, retained)
            for key, thrust in thrusts.items():
                p = parts[key[0]]
                thrust_vector = mul(rotate(p["pose"]["rotation"], vec(engine_rows[key]["thrustDirection"])), thrust)
                force = add(force, thrust_vector)
                application = add(vec(p["pose"]["position"]), rotate(p["pose"]["rotation"], vec(engine_rows[key].get("thrustPosition", {}))))
                moment = add(moment, cross(sub(application, centre), thrust_vector))
            if length(force) > 0 and length(moment)/length(force) > targets.get("maxThrustOffsetMetres", .25):
                message = f"第 {stage} 级合推力相对质心偏置过大，应调整径向对称或发动机位置。"
                if message not in issues: issues.append(message)
            if first_burn_stage is None:
                first_burn_stage = stage
                surface_force = (0, 0, 0)
                for key in thrusts:
                    thrust = engine_output(engine_rows[key], environment)[0]
                    surface_force = add(surface_force, mul(rotate(parts[key[0]]["pose"]["rotation"], vec(engine_rows[key]["thrustDirection"])), thrust))
                launch_twr = surface_force[1]/(mass_before*environment["gravity"])
                if length(surface_force) > 0 and surface_force[1]/length(surface_force) < 0.97: issues.append("发射级合推力方向偏离火箭向上轴线。")
            for key, rate in demands.items():
                amount = min(usable[key], rate*dt); usable[key] = max(0, usable[key]-amount)
                parts[key[0]]["resources"][key[1]]["amount"] -= amount
            mass_after = sum(_mass(parts[i]) for i in retained)
            if mass_after > 0 and mass_before > mass_after:
                dv += max(0, force[1])/sum(flows.values())*math.log(mass_before/mass_after)
            burn_time += dt
        else: issues.append("分级燃料耗尽事件超过求解限制。")
        if burn_time > 0:
            stages.append({"stage": stage, "startMassTonnes": start_mass, "endMassTonnes": sum(_mass(parts[i]) for i in retained),
                           "deltaVVacuum": dv, "burnSeconds": burn_time, "droppedParts": dropped})
            total_dv += dv
    if targets["mission"] != "ground_test":
        if launch_twr is None: issues.append("没有可点火且有可达推进剂的发射级。")
        elif launch_twr < targets["minLaunchTwr"]: issues.append(f"海平面发射 TWR {launch_twr:.2f} 小于要求 {targets['minLaunchTwr']:.2f}。")
        if total_dv < targets["requiredDeltaV"]: issues.append(f"保留燃料裕量后的分级真空 Δv {total_dv:.0f} m/s 小于预算 {targets['requiredDeltaV']:.0f} m/s。")
    if first_burn_stage is not None:
        for p in parts.values():
            if "ModuleParachute" in p["definition"].get("moduleNames", []) and p["plan"]["stage"] >= first_burn_stage:
                issues.append("降落伞被安排在发射发动机之前或同一级。")
            if p["plan"]["partName"] in test_parts and p["plan"]["stage"] >= first_burn_stage:
                issues.append("轨道/高空测试发动机不应在发射级提前首次激活。")
    return {"launchTwr": launch_twr, "deltaVVacuumWithReserve": total_dv, "requiredDeltaV": targets["requiredDeltaV"], "stages": stages}


def _aircraft(parts, targets, environment, takeoff, world, issues):
    retained = set(parts); mass = sum(_mass(p) for p in parts.values()); centre = _centre(parts, retained)
    surfaces = [(i, p, aero) for i, p in parts.items() if aircraft_lifting_part(p["definition"]) for aero in p["definition"].get("aero", []) if not aero.get("airbrake", False)]
    engines = [(i, p, e) for i, p in parts.items() for e in p["definition"].get("engines", []) if e.get("defaultMode", True) and p["plan"]["stage"] >= 0]
    def forces(speed, aoa, env):
        velocity = (0, -math.sin(math.radians(aoa)), math.cos(math.radians(aoa)))
        q = 0.5*env["densityKgPerCubicMetre"]*speed*speed
        mach = speed/env["speedOfSound"] if env.get("speedOfSound", 0) > 0 else 0
        lift, drag, weighted, coefficients = 0.0, 0.0, (0, 0, 0), []
        for i, p, aero in surfaces:
            blocked = aero.get("disabledByNode")
            if blocked and (p["plan"].get("childNodeId") == blocked or (blocked == "srfAttach" and p["plan"].get("attachment") == "surface") or
                            any(other["plan"].get("parentId") == i and other["plan"].get("parentNodeId") == blocked for other in parts.values())):
                continue
            normal = rotate(p["pose"]["rotation"], vec(aero["normal"]))
            lift_dot = dot(velocity, normal)
            abs_dot = abs(lift_dot) if aero.get("omnidirectional", True) else max(0, min(1, lift_dot))
            magnitude = curve(aero["liftCurve"], abs_dot, 0)*curve(aero["liftMachCurve"], mach)*aero["liftCoefficient"]*world["liftMultiplier"]*q
            force = mul(normal, -math.copysign(magnitude, lift_dot)) if lift_dot else (0, 0, 0)
            if aero.get("perpendicularOnly", True): force = sub(force, mul(velocity, dot(force, velocity)))
            vertical = max(0, force[1]); lift += vertical
            weighted = add(weighted, mul(_point(p, "liftOffset"), vertical))
            coefficients.append((i, vertical))
            if aero.get("internalDrag", True): drag += max(0, curve(aero["dragCurve"], abs_dot, 0)*curve(aero["dragMachCurve"], mach)*aero["liftCoefficient"]*world["liftDragMultiplier"]*q)
        # Explicit conservative parasite-drag proxy, not the stock drag-cube solver.
        for p in parts.values():
            if aircraft_lifting_part(p["definition"]): continue
            size = vec(p["definition"].get("prefabSize", {})); rotation = p["pose"]["rotation"]
            width = sum(abs(rotate(rotation, axis)[0])*dimension for axis, dimension in zip(((1,0,0),(0,1,0),(0,0,1)), size))
            height = sum(abs(rotate(rotation, axis)[1])*dimension for axis, dimension in zip(((1,0,0),(0,1,0),(0,0,1)), size))
            drag += q*width*height*targets["bodyDragCoefficient"]/1000
        return lift, drag, mul(weighted, 1/lift) if lift > 0 else (0,0,0), coefficients
    weight = mass*environment["gravity"]
    trim = None
    for step in range(1, int(targets["maxTrimAoADegrees"]*2)+1):
        aoa = step/2; lift, drag, ac, distribution = forces(targets["cruiseSpeed"], aoa, environment)
        if lift >= weight: trim = (aoa, lift, drag, ac, distribution); break
    if trim is None: issues.append("巡航条件下，机翼升力不足，或升力面方向错误。")
    stall = None
    for speed in range(5, 301):
        if forces(speed, targets["maxLiftAoADegrees"], takeoff)[0] >= mass*takeoff["gravity"]: stall = speed; break
    if stall is None or stall > targets["maxStallSpeed"]: issues.append(f"估算起飞/失速速度 {stall if stall is not None else '>300'} m/s 高于允许值 {targets['maxStallSpeed']} m/s。")
    forward_force, mass_flow = 0.0, 0.0
    demand, capacities, tank_demand = {}, {}, {}
    for i, p, e in engines:
        if not e.get("supported") or e.get("family") != "airbreathing": issues.append("飞机主推进必须使用可评估的吸气式发动机：" + i); continue
        thrust, flow, _ = engine_output(e, environment, targets["cruiseSpeed"])
        direction = rotate(p["pose"]["rotation"], vec(e["thrustDirection"]))
        if direction[2] < 0.9: issues.append("飞机发动机没有朝向机头推进：" + i)
        forward_force += thrust*direction[2]; mass_flow += flow
        mixture_density = sum(r["ratio"]*r["density"] for r in e.get("mixture", []) if not r.get("ignoreForIsp"))
        for r in e.get("mixture", []):
            if mixture_density > 0: demand[r["name"]] = demand.get(r["name"], 0) + flow/mixture_density*r["ratio"]
            if "Intake" not in r["name"]:
                sources = _fuel_sources(parts, retained, i, r, set())
                total = sum(parts[source]["resources"][r["name"]]["amount"] for source in sources)
                if total <= 0: issues.append("飞机发动机缺少可达燃料：" + i + "/" + r["name"])
                elif mixture_density > 0:
                    for source in sources:
                        key = (source, r["name"])
                        tank_demand[key] = tank_demand.get(key,0) + flow/mixture_density*r["ratio"]*parts[source]["resources"][r["name"]]["amount"]/total
    if forward_force <= 0: issues.append("没有可用的飞机前向推力。")
    aircraft_twr = forward_force/(mass*environment["gravity"]) if mass > 0 else 0
    if aircraft_twr < targets.get("minAircraftTwr", .25): issues.append(f"飞机巡航推重比 {aircraft_twr:.2f} 低于设计门槛 {targets.get('minAircraftTwr', .25):.2f}。")
    required_drag = trim[2] if trim else forces(targets["cruiseSpeed"], targets["maxTrimAoADegrees"], environment)[1]
    if forward_force < required_drag*targets["dragMargin"]: issues.append(f"巡航推力 {forward_force:.1f} kN 不足以覆盖阻力估计 {required_drag:.1f} kN 及裕量。")
    for p in parts.values():
        for intake in p["definition"].get("intakes", []):
            if intake["oxygenRequired"] and not environment["oxygen"]: continue
            direction = rotate(p["pose"]["rotation"], vec(intake["direction"]))
            facing = max(0, direction[2])
            mach = targets["cruiseSpeed"]/environment["speedOfSound"] if environment["speedOfSound"] > 0 else 0
            if intake.get("resourceDensity", 0) <= 0: raise ValueError("Missing intake resource density.")
            supply = intake["area"]*(targets["cruiseSpeed"]*facing + intake["intakeSpeed"])*environment["densityKgPerCubicMetre"]*intake["unitScalar"]*curve(intake.get("machCurve"), mach)/intake["resourceDensity"]
            capacities[intake["resource"]] = capacities.get(intake["resource"], 0) + max(0, supply)
    for resource, rate in demand.items():
        if "Intake" in resource and capacities.get(resource, 0) < rate: issues.append(f"进气资源 {resource} 供给 {capacities.get(resource,0):.2f}/s 小于发动机需求 {rate:.2f}/s。")
    endurance = min((parts[i]["resources"][name]["amount"]*(1-targets["fuelReserveFraction"])/rate for (i,name),rate in tank_demand.items() if rate > 0), default=0)
    if endurance < targets["minEnduranceSeconds"]: issues.append(f"满推力巡航燃料时长估计 {endurance:.0f} s 小于要求 {targets['minEnduranceSeconds']:.0f} s。")
    horizontal = [(i,p,a) for i,p,a in surfaces if abs(rotate(p["pose"]["rotation"], vec(a["normal"]))[1]) > .7]
    if not any(_point(p,"liftOffset")[0] < centre[0]-.2 for _,p,_ in horizontal) or not any(_point(p,"liftOffset")[0] > centre[0]+.2 for _,p,_ in horizontal): issues.append("左右两侧都需要水平升力面；不能用垂直尾翼代替主翼。")
    chord = max((sum(abs(rotate(p["pose"]["rotation"], axis)[2])*dimension for axis, dimension in zip(((1,0,0),(0,1,0),(0,0,1)), vec(p["definition"]["prefabSize"]))) for _,p,_ in horizontal), default=1)
    margin = (centre[2]-trim[3][2])/max(chord, .1) if trim else None
    if margin is not None and not targets["minStaticMargin"] <= margin <= targets["maxStaticMargin"]: issues.append(f"估算静稳定裕度 {margin:.2f} 不在 {targets['minStaticMargin']}–{targets['maxStaticMargin']} 范围，调整主翼/尾翼前后位置。")
    if trim and abs(trim[3][0]-centre[0]) > .2: issues.append("左右升力分布不平衡，升力中心横向偏离质心超过 0.2 m。")
    originals = {(i, name): resource["amount"] for i,p in parts.items() for name,resource in p["resources"].items() if name in demand and "Intake" not in name}
    for (i,name), amount in originals.items(): parts[i]["resources"][name]["amount"] = amount*targets["fuelReserveFraction"]
    dry_centre = _centre(parts, retained)
    for (i,name), amount in originals.items(): parts[i]["resources"][name]["amount"] = amount
    dry_margin = (dry_centre[2]-trim[3][2])/max(chord,.1) if trim else None
    if dry_margin is not None and not targets["minStaticMargin"] <= dry_margin <= targets["maxStaticMargin"]: issues.append("燃料减少后静稳定裕度越界，需要调整油箱与机翼布局。")
    if not any(a.get("controlSurface") and a.get("controlFraction",0)>0 and a.get("controlRange",0)>0 and a.get("pitch") and abs(_point(p,"liftOffset")[2]-centre[2]) > .2 for _,p,a in horizontal): issues.append("缺少有纵向力臂的俯仰控制面。")
    if not any(a.get("controlSurface") and a.get("controlFraction",0)>0 and a.get("controlRange",0)>0 and a.get("roll") and abs(_point(p,"liftOffset")[0]-centre[0]) > .2 for _,p,a in horizontal): issues.append("缺少有横向力臂的滚转控制面。")
    if not any(abs(rotate(p["pose"]["rotation"],vec(a["normal"]))[0]) > .7 and _point(p,"liftOffset")[2] < centre[2]-.2 for _,p,a in surfaces): issues.append("缺少位于质心后方的垂直稳定面。")
    gear = [_point(p,"massOffset") for p in parts.values() if landing_gear_part(p["definition"])]
    if not _inside_support_polygon([(p[0],p[2]) for p in gear], (centre[0],centre[2])):
        issues.append("至少需要三点起落架布局，且其纵横向范围应包住质心。")
    return {"massTonnes": mass, "centreOfMass": centre, "aerodynamicCenter": trim[3] if trim else None, "referenceChordEstimate": chord,
            "liftDistribution": [{"id":i,"liftKn":force} for i,force in trim[4]] if trim else [], "trimAoADegrees": trim[0] if trim else None,
            "liftAtTrimKn": trim[1] if trim else 0, "estimatedDragKn": required_drag, "availableThrustKn": forward_force,
            "staticMargin": margin, "reserveFuelStaticMargin": dry_margin, "reserveFuelCentreOfMass": dry_centre,
            "thrustToWeight": aircraft_twr, "estimatedStallSpeed": stall, "fullThrottleEnduranceSeconds": endurance,
            "intakeSupply": capacities, "propellantDemandPerSecond": demand}


def _inside_support_polygon(points, point):
    points = sorted(set(points))
    if len(points) < 3: return False
    def turn(a,b,c): return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])
    def half(values):
        hull=[]
        for p in values:
            while len(hull)>=2 and turn(hull[-2],hull[-1],p)<=0: hull.pop()
            hull.append(p)
        return hull
    hull=half(points)[:-1]+half(reversed(points))[:-1]
    return len(hull)>=3 and all(turn(a,b,point)>1e-6 for a,b in zip(hull,hull[1:]+hull[:1]))


def assess_performance(plan, catalog, validation, targets, world, environment, takeoff=None):
    issues = []
    try:
        parts = _parts(plan, catalog, validation)
        if not any("ModuleCommand" in p["definition"].get("moduleNames", []) for p in parts.values()): issues.append("缺少控制指令模块。")
        metrics = _aircraft(parts, targets, environment, takeoff or environment, world, issues) if targets["vehicle"] == "aircraft" else _rocket(parts, targets, environment, issues)
        status = "failed" if issues else "passed"
    except (ValueError, KeyError, TypeError, ZeroDivisionError, OverflowError) as error:
        metrics, status = {}, "unverified"
        issues.append("性能数据不足或求解器不支持该构型：" + str(error))
    return {"status": status, "hasBlockingFailure": status != "passed", "targets": targets, "metrics": metrics, "issues": issues,
            "method": "staged-resource-event screening" if targets["vehicle"] == "rocket" else "stock-lift-curve quasi-steady screening",
            "trajectorySimulated": False, "limitations": ["分级估计不等于飞行轨迹仿真；损失预算是明确的估计值。", "飞机阻力/静稳定裕度和轮组支撑使用近似；不包含 FAR、地效、真实接地姿态或 CFD。", "多模式、自定义推力曲线和不支持的发动机不会被当作已验证性能。"]}
