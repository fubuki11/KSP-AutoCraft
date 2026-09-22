using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KSPAutoCraft
{
    [Serializable] internal sealed class EnvironmentInfo
    {
        public string body;
        public double altitude, pressureKpa, densityKgPerCubicMetre, temperatureKelvin, speedOfSound, gravity;
        public bool oxygen;
    }

    internal static class FlightCatalog
    {
        internal static CurveKey[] Curve(FloatCurve curve)
        {
            if (curve == null || curve.Curve == null) return new CurveKey[0];
            return curve.Curve.keys.Select(k => new CurveKey { x = k.time, y = k.value,
                inTangent = PlanValidator.Finite(k.inTangent) ? k.inTangent : 0,
                outTangent = PlanValidator.Finite(k.outTangent) ? k.outTangent : 0 }).ToArray();
        }
        private static bool HasCurve(FloatCurve curve) { return curve != null && curve.Curve != null && curve.Curve.length > 0; }
        private static Vec Direction(Part part, Vector3 world) { return KspAdapter.Vector(Quaternion.Inverse(part.transform.rotation) * world); }
        private static Vec Position(Part part, Vector3 world) { return Direction(part, world - part.transform.position); }
        private static object Member(object value, string name)
        {
            for (var t = value.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(value);
            }
            return null;
        }

        internal static EngineInfo[] Engines(Part part)
        {
            var mode = part.Modules.OfType<MultiModeEngine>().FirstOrDefault();
            string selectedMode = mode == null ? null : !string.IsNullOrEmpty(mode.mode) ? mode.mode : mode.primaryEngineID;
            return part.Modules.OfType<ModuleEngines>().Select(e => {
                var transforms = part.FindModelTransforms(e.thrustVectorTransformName ?? "");
                var direction = new Vec(); var position = new Vec();
                for (int i = 0; i < transforms.Length; i++)
                {
                    double weight = e.thrustTransformMultipliers != null && e.thrustTransformMultipliers.Count == transforms.Length
                        ? e.thrustTransformMultipliers[i] : 1.0 / transforms.Length;
                    direction = direction + Direction(part, -transforms[i].forward) * weight;
                    position = position + Position(part, transforms[i].position) * weight;
                }
                string family = e.propellants.Any(p => p.name.IndexOf("Intake", StringComparison.OrdinalIgnoreCase) >= 0) ? "airbreathing" :
                    e.propellants.Any(p => p.name == "SolidFuel") ? "solid" : "rocket";
                bool supported = (e.GetType() == typeof(ModuleEngines) || e.GetType() == typeof(ModuleEnginesFX)) &&
                    transforms.Length > 0 && !e.useThrustCurve && !e.useThrottleIspCurve && mode == null && !e.propellants.Any(p => p.name == "ElectricCharge");
                return new EngineInfo {
                    engineId = e.engineID, nominalMaxThrustKn = e.maxThrust, vacuumIspSeconds = e.atmosphereCurve.Evaluate(0),
                    seaLevelIspSeconds = e.atmosphereCurve.Evaluate(1), propellants = e.propellants.Select(p => p.name).ToArray(),
                    family = family, supported = supported, defaultMode = mode == null || e.engineID == selectedMode,
                    massFlowTonnesPerSecond = e.maxFuelFlow, thrustLimiter = e.thrustPercentage / 100.0,
                    thrustDirection = direction, thrustPosition = position,
                    mixture = e.propellants.Select(p => { var definition = PartResourceLibrary.Instance.GetDefinition(p.name);
                        string flow = Convert.ToString(Member(p, "resourceFlowMode") ?? Member(p, "flowMode"));
                        if (string.IsNullOrEmpty(flow) || flow == "NULL") flow = definition == null ? "UNKNOWN" : definition.resourceFlowMode.ToString();
                        return new PropellantInfo { name = p.name, ratio = p.ratio, ignoreForIsp = p.ignoreForIsp,
                            density = definition == null ? 0 : definition.density,
                            flowMode = flow };
                    }).ToArray(),
                    ispCurve = Curve(e.atmosphereCurve), atmosphereFlowCurve = Curve(e.atmCurve), velocityFlowCurve = Curve(e.velCurve),
                    atmosphereIspCurve = Curve(e.atmCurveIsp), velocityIspCurve = Curve(e.velCurveIsp),
                    atmosphereChangesFlow = e.atmChangeFlow, useAtmosphereFlowCurve = e.useAtmCurve, useVelocityFlowCurve = e.useVelCurve,
                    useAtmosphereIspCurve = e.useAtmCurveIsp, useVelocityIspCurve = e.useVelCurveIsp,
                    flowCap = e.flowMultCap, flowCapSharpness = e.flowMultCapSharpness,
                    unsupportedReason = supported ? "" : "Custom engine behavior, multimode, thrust/throttle curve or missing thrust transform requires a dedicated solver."
                };
            }).ToArray();
        }

        internal static AeroInfo[] Aero(Part part)
        {
            return part.Modules.OfType<ModuleLiftingSurface>().Select(m => {
                string transformName = Convert.ToString(Member(m, "transformName"));
                var transform = (Member(m, "baseTransform") as Transform) ??
                    (string.IsNullOrEmpty(transformName) ? part.transform : part.FindModelTransform(transformName) ?? part.transform);
                Vector3 axis = m.transformDir.ToString() == "X" ? Vector3.right : m.transformDir.ToString() == "Y" ? Vector3.up : Vector3.forward;
                var defaults = PhysicsGlobals.GetLiftingSurfaceCurve(string.IsNullOrEmpty(m.liftingSurfaceCurve) ? "Default" : m.liftingSurfaceCurve);
                var control = m as ModuleControlSurface;
                return new AeroInfo { liftCoefficient = m.deflectionLiftCoeff, normal = Direction(part, transform.rotation * axis * m.transformSign),
                    controlSurface = control != null, controlFraction = control == null ? 0 : control.ctrlSurfaceArea,
                    controlRange = control == null ? 0 : control.ctrlSurfaceRange,
                    pitch = control != null && !control.ignorePitch, yaw = control != null && !control.ignoreYaw, roll = control != null && !control.ignoreRoll,
                    perpendicularOnly = (Member(m, "perpendicularOnly") as bool?) ?? true, internalDrag = m.useInternalDragModel,
                    omnidirectional = m.omnidirectional, airbrake = m is ModuleAeroSurface,
                    disabledByNode = ((Member(m, "nodeEnabled") as bool?) ?? false) ? Convert.ToString(Member(m, "attachNodeName")) : "",
                    liftCurve = Curve(HasCurve(m.liftCurve) ? m.liftCurve : defaults.liftCurve),
                    liftMachCurve = Curve(HasCurve(m.liftMachCurve) ? m.liftMachCurve : defaults.liftMachCurve),
                    dragCurve = Curve(HasCurve(m.dragCurve) ? m.dragCurve : defaults.dragCurve),
                    dragMachCurve = Curve(HasCurve(m.dragMachCurve) ? m.dragMachCurve : defaults.dragMachCurve) };
            }).ToArray();
        }

        internal static IntakeInfo[] Intakes(Part part)
        {
            return part.Modules.OfType<ModuleResourceIntake>().Select(i => {
                var transform = part.FindModelTransform(i.intakeTransformName ?? "") ?? part.transform;
                return new IntakeInfo { resource = i.resourceName, area = i.area, intakeSpeed = i.intakeSpeed, unitScalar = i.unitScalar,
                    resourceDensity = PartResourceLibrary.Instance.GetDefinition(i.resourceName).density,
                    oxygenRequired = i.checkForOxygen, direction = Direction(part, transform.forward), machCurve = Curve(Member(i, "machCurve") as FloatCurve) };
            }).ToArray();
        }

        internal static SeparationInfo[] Separators(Part part)
        {
            return part.Modules.Cast<PartModule>().Where(m => m is ModuleDecouple || m is ModuleAnchoredDecoupler)
                .Select(m => new SeparationInfo { nodeId = Convert.ToString(Member(m, "explosiveNodeID")), omni = (Member(m, "isOmniDecoupler") as bool?) ?? false }).ToArray();
        }

        internal static string[] Roles(PartInfo part)
        {
            var roles = new List<string>();
            if (part.moduleNames.Contains("ModuleCommand")) roles.Add(part.crewCapacity > 0 ? "cockpit_or_capsule" : "probe_control");
            string description = (part.name + " " + part.title).ToLowerInvariant();
            if (part.moduleNames.Contains("ModuleCommand") && (description.Contains("cockpit") || description.Contains("驾驶舱") || description.Contains("座舱"))) roles.Add("cockpit");
            if (part.aero.Any(a => a.liftCoefficient > 0 && !a.airbrake) && !part.moduleNames.Contains("ModuleAblator")) roles.Add("lifting_surface");
            if (part.aero.Any(a => a.controlSurface && !a.airbrake)) roles.Add("control_surface");
            if (part.aero.Any(a => a.airbrake)) roles.Add("airbrake");
            if (part.wheelType == "FREE") roles.Add("landing_gear");
            else if (part.wheelType == "LEG") roles.Add("landing_leg");
            else if (part.wheel) roles.Add("wheel");
            if (part.intakes.Length > 0) roles.Add("intake");
            if (part.engines.Any(e => e.family == "airbreathing")) roles.Add("airbreathing_engine");
            if (part.engines.Any(e => e.family == "rocket" || e.family == "solid")) roles.Add("rocket_engine");
            if (part.moduleNames.Any(n => n.StartsWith("FSengine", StringComparison.OrdinalIgnoreCase) || n.Contains("Propeller"))) roles.Add("custom_propeller_engine");
            if (part.resources.Any(r => r.name == "LiquidFuel") && !part.resources.Any(r => r.name == "Oxidizer")) roles.Add("liquid_fuel_tank");
            if (part.separators.Length > 0) roles.Add("separator");
            return roles.ToArray();
        }
        internal static string WheelType(Part part)
        {
            var wheel = part.Modules.OfType<ModuleWheelBase>().FirstOrDefault();
            return wheel == null ? "" : Convert.ToString(Member(wheel, "wheelType"));
        }

        internal static EnvironmentInfo Environment(string name, double altitude)
        {
            var body = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName == name);
            if (body == null || !PlanValidator.Finite(altitude) || altitude < 0 || altitude > 1e9) throw new PlanException("Invalid environment body or altitude.");
            double pressure = body.GetPressure(altitude), temperature = body.GetTemperature(altitude);
            double density = body.GetDensity(pressure, temperature);
            return new EnvironmentInfo { body = body.bodyName, altitude = altitude, pressureKpa = pressure, temperatureKelvin = temperature,
                densityKgPerCubicMetre = density, speedOfSound = density > 0 ? body.GetSpeedOfSound(pressure, density) : 0,
                gravity = body.gravParameter / Math.Pow(body.Radius + altitude, 2), oxygen = body.atmosphereContainsOxygen };
        }
    }
}
