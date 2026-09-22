using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KSPAutoCraft
{
    [Serializable]
    public sealed class CraftPlan
    {
        public string name;
        public string facility;
        public PlanPart[] parts;
        public double maxWetMassTonnes;
        public double maxCost;
    }

    [Serializable]
    public sealed class PlanPart
    {
        public string id;
        public string partName;
        public string parentId;
        public string parentNodeId;
        public string childNodeId;
        public int stage;
        public double rollDegrees;
        public string attachment;
        public double radialAngleDegrees;
        public double surfaceHeight;
        public string surfaceOrientation;
        public string mirrorOf;
        public ResourceAmount[] resources;
    }

    [Serializable]
    public sealed class ResourceAmount
    {
        public string name;
        public double amount;
    }

    [Serializable]
    public sealed class ResourceInfo
    {
        public string name;
        public double amount;
        public double maxAmount;
        public double densityTonnesPerUnit;
        public double unitCost;
    }

    [Serializable]
    public sealed class NodeInfo
    {
        public string id;
        public int size;
        public Vec position;
        public Vec orientation;
    }

    [Serializable]
    public sealed class EngineInfo
    {
        public string engineId;
        public double nominalMaxThrustKn;
        public double vacuumIspSeconds;
        public double seaLevelIspSeconds;
        public string[] propellants;
        public string family;
        public bool supported;
        public bool defaultMode;
        public double massFlowTonnesPerSecond;
        public double thrustLimiter;
        public Vec thrustDirection;
        public Vec thrustPosition;
        public PropellantInfo[] mixture;
        public CurveKey[] ispCurve, atmosphereFlowCurve, velocityFlowCurve, atmosphereIspCurve, velocityIspCurve;
        public bool atmosphereChangesFlow, useAtmosphereFlowCurve, useVelocityFlowCurve, useAtmosphereIspCurve, useVelocityIspCurve;
        public double flowCap, flowCapSharpness;
        public string unsupportedReason;
    }

    [Serializable] public sealed class CurveKey { public double x, y, inTangent, outTangent; }
    [Serializable] public sealed class PropellantInfo { public string name, flowMode; public double ratio, density; public bool ignoreForIsp; }
    [Serializable] public sealed class AeroInfo
    {
        public double liftCoefficient, controlFraction, controlRange;
        public bool controlSurface, pitch, yaw, roll, perpendicularOnly, internalDrag, omnidirectional, airbrake;
        public string disabledByNode;
        public Vec normal;
        public CurveKey[] liftCurve, liftMachCurve, dragCurve, dragMachCurve;
    }
    [Serializable] public sealed class IntakeInfo { public string resource; public double area, intakeSpeed, unitScalar, resourceDensity; public bool oxygenRequired; public Vec direction; public CurveKey[] machCurve; }
    [Serializable] public sealed class SeparationInfo { public string nodeId; public bool omni; }

    [Serializable]
    public sealed class PartInfo
    {
        public string name;
        public string title;
        public string category;
        public bool unlocked;
        public bool stackAttach;
        public bool allowStack;
        public bool surfaceAttach;
        public bool allowSurfaceAttach;
        public Vec prefabSize;
        public Vec prefabCenter;
        public string geometrySource;
        public NodeInfo surfaceNode;
        public double dryMassTonnes;
        public double wetMassTonnes;
        public double defaultCost;
        public int crewCapacity;
        public bool experimental;
        public string[] moduleNames;
        public string[] experiments;
        public NodeInfo[] nodes;
        public ResourceInfo[] resources;
        public EngineInfo[] engines;
        public AeroInfo[] aero;
        public IntakeInfo[] intakes;
        public SeparationInfo[] separators;
        public string[] roles;
        public bool wheel, fuelCrossFeed, multimode;
        public string wheelType;
        public string noCrossFeedNodeKey;
        public Vec massOffset, liftOffset;
    }

    // Pure math keeps topology and node alignment testable without loading Unity.
    [Serializable]
    public struct Vec
    {
        public double x, y, z;
        public Vec(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }
        private double Scale { get { return Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))); } }
        public double Length
        {
            get
            {
                double scale = Scale;
                if (scale == 0) return 0;
                var v = new Vec(x / scale, y / scale, z / scale);
                return scale * Math.Sqrt(Dot(v, v));
            }
        }
        public Vec Unit
        {
            get
            {
                double scale = Scale;
                if (scale == 0 || !PlanValidator.Finite(scale)) throw new ArgumentException("A finite nonzero vector is required.");
                var v = new Vec(x / scale, y / scale, z / scale);
                return v * (1 / Math.Sqrt(Dot(v, v)));
            }
        }
        public static Vec operator +(Vec a, Vec b) { return new Vec(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vec operator -(Vec a, Vec b) { return new Vec(a.x - b.x, a.y - b.y, a.z - b.z); }
        public static Vec operator *(Vec a, double f) { return new Vec(a.x * f, a.y * f, a.z * f); }
        public static double Dot(Vec a, Vec b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
        public static Vec Cross(Vec a, Vec b) { return new Vec(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
    }

    public struct Rotation
    {
        public double x, y, z, w;
        public Rotation(double x, double y, double z, double w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Rotation Identity { get { return new Rotation(0, 0, 0, 1); } }
        public Rotation Inverse { get { return new Rotation(-x, -y, -z, w); } }
        public static Rotation operator *(Rotation a, Rotation b)
        {
            return new Rotation(a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y,
                a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x, a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w,
                a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z);
        }
        public Vec Apply(Vec v)
        {
            var q = new Vec(x, y, z);
            return v + Vec.Cross(q, v) * (2 * w) + Vec.Cross(q, Vec.Cross(q, v)) * 2;
        }
        public static Rotation AxisAngle(Vec axis, double degrees)
        {
            double half = degrees * Math.PI / 360;
            var v = axis.Unit * Math.Sin(half);
            return new Rotation(v.x, v.y, v.z, Math.Cos(half));
        }
        public static Rotation FromTo(Vec from, Vec to)
        {
            from = from.Unit; to = to.Unit;
            double dot = Math.Max(-1, Math.Min(1, Vec.Dot(from, to)));
            if (dot < -0.999999)
            {
                var basis = Math.Abs(from.x) < 0.8 ? new Vec(1, 0, 0) : new Vec(0, 1, 0);
                // Split off the half-turn to avoid cancellation in (1 + dot).
                return FromTo(from * -1, to) * AxisAngle(Vec.Cross(from, basis), 180);
            }
            var cross = Vec.Cross(from, to);
            double w = 1 + dot;
            double scale = Math.Sqrt(Vec.Dot(cross, cross) + w * w);
            return new Rotation(cross.x / scale, cross.y / scale, cross.z / scale, w / scale);
        }
        public static Rotation LookRotation(Vec forward, Vec up)
        {
            forward = forward.Unit;
            var right = Vec.Cross(up, forward);
            if (right.Length < 1e-10) return FromTo(new Vec(0, 0, 1), forward);
            right = right.Unit;
            up = Vec.Cross(forward, right);
            double trace = right.x + up.y + forward.z;
            if (trace > 0)
            {
                double s = Math.Sqrt(trace + 1) * 2;
                return new Rotation((up.z - forward.y) / s, (forward.x - right.z) / s, (right.y - up.x) / s, s / 4);
            }
            if (right.x > up.y && right.x > forward.z)
            {
                double s = Math.Sqrt(1 + right.x - up.y - forward.z) * 2;
                return new Rotation(s / 4, (up.x + right.y) / s, (forward.x + right.z) / s, (up.z - forward.y) / s);
            }
            if (up.y > forward.z)
            {
                double s = Math.Sqrt(1 + up.y - right.x - forward.z) * 2;
                return new Rotation((up.x + right.y) / s, s / 4, (forward.y + up.z) / s, (forward.x - right.z) / s);
            }
            else
            {
                double s = Math.Sqrt(1 + forward.z - right.x - up.y) * 2;
                return new Rotation((forward.x + right.z) / s, (forward.y + up.z) / s, s / 4, (right.y - up.x) / s);
            }
        }
    }

    public sealed class ResolvedPart
    {
        public PlanPart plan;
        public PartInfo definition;
        public Vec position;
        public Rotation rotation;
        public Vec surfaceNormal;
    }

    [Serializable] public sealed class PlacementInfo
    {
        public string id;
        public Vec position;
        public Rotation rotation;
    }

    [Serializable]
    public sealed class PlanResult
    {
        public bool valid;
        public int partCount;
        public double wetMassTonnes;
        public double estimatedCost;
        public string craftFile;
        public string[] warnings;
        public PlacementInfo[] placements;
        [NonSerialized] public ResolvedPart[] resolved;
    }

    public sealed class PlanException : Exception
    {
        public PlanException(string message) : base(message) { }
    }

    public static class PlanValidator
    {
        private static readonly Regex Identifier = new Regex("\\A[A-Za-z0-9_-]{1,48}\\z");
        public static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static void Require(bool condition, string message) { if (!condition) throw new PlanException(message); }
        private static bool ValidVector(Vec v) { return Finite(v.x) && Finite(v.y) && Finite(v.z) && v.Length < 1000; }

        public static PlanResult Validate(CraftPlan plan, Func<string, PartInfo> lookup)
        {
            Require(plan != null, "A craft plan object is required.");
            Require(!string.IsNullOrWhiteSpace(plan.name) && plan.name.Length <= 80 &&
                !plan.name.Any(c => char.IsControl(c) || "{}=\\/".IndexOf(c) >= 0), "Invalid craft name (1-80 plain text characters).");
            Require(plan.facility == "VAB" || plan.facility == "SPH", "facility must be VAB or SPH.");
            Require(plan.parts != null && plan.parts.Length >= 1 && plan.parts.Length <= 128, "Plans must contain 1-128 parts.");
            Require(Finite(plan.maxCost) && plan.maxCost >= 0 && Finite(plan.maxWetMassTonnes) && plan.maxWetMassTonnes >= 0,
                "Mass and cost limits must be finite and non-negative (0 means no limit).");
            var result = new PlanResult { valid = true, partCount = plan.parts.Length };
            var resolved = new List<ResolvedPart>();
            var byId = new Dictionary<string, ResolvedPart>(StringComparer.Ordinal);
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in plan.parts)
            {
                Require(item != null && item.id != null && Identifier.IsMatch(item.id), "Part ids must be unique ASCII identifiers (1-48 characters).");
                Require(!byId.ContainsKey(item.id), "Duplicate part id: " + item.id);
                Require(!string.IsNullOrWhiteSpace(item.partName), "Missing partName: " + item.id);
                var definition = lookup(item.partName);
                Require(definition != null, "Unknown part: " + item.partName);
                Require(definition.unlocked, "Part is not available in this save: " + item.partName);
                Require(item.stage >= -1 && item.stage <= 99, "stage must be -1 (unstaged) or 0-99.");
                Require(Finite(item.rollDegrees) && Math.Abs(item.rollDegrees) <= 360, "rollDegrees must be finite and between -360 and 360.");
                bool surface = item.attachment == "surface";
                Require(string.IsNullOrEmpty(item.attachment) || item.attachment == "stack" || surface, "attachment must be stack or surface.");
                Require(Finite(item.radialAngleDegrees) && Math.Abs(item.radialAngleDegrees) <= 360 &&
                    Finite(item.surfaceHeight) && Math.Abs(item.surfaceHeight) <= 1, "Invalid surface attachment angle or height.");
                Require(surface || (item.radialAngleDegrees == 0 && item.surfaceHeight == 0), "Surface coordinates require attachment=surface.");
                Require(string.IsNullOrEmpty(item.surfaceOrientation) || item.surfaceOrientation == "default" || item.surfaceOrientation == "wing" || item.surfaceOrientation == "fin", "Unknown surface orientation mode.");
                var part = new ResolvedPart { plan = item, definition = definition, rotation = Rotation.Identity };
                if (resolved.Count == 0)
                {
                    Require(string.IsNullOrEmpty(item.parentId) && string.IsNullOrEmpty(item.parentNodeId) && string.IsNullOrEmpty(item.childNodeId),
                        "The first part must be the sole root, without attachment fields.");
                    Require(!surface, "The root cannot use surface attachment.");
                    Require(string.IsNullOrEmpty(item.surfaceOrientation) || item.surfaceOrientation == "default", "The root cannot have a surface orientation mode.");
                    var facilityRotation = plan.facility == "SPH" ? Rotation.AxisAngle(new Vec(1, 0, 0), 90) : Rotation.Identity;
                    part.rotation = facilityRotation * Rotation.AxisAngle(new Vec(0, 1, 0), item.rollDegrees);
                    Require(string.IsNullOrEmpty(item.mirrorOf), "The root cannot mirror another part.");
                }
                else
                {
                    ResolvedPart parent;
                    Require(!string.IsNullOrEmpty(item.parentId), "Only the first part can be a root.");
                    Require(byId.TryGetValue(item.parentId, out parent), "Parents must precede children: " + item.id);
                    if (surface)
                    {
                        Require(string.IsNullOrEmpty(item.parentNodeId) && string.IsNullOrEmpty(item.childNodeId), "Surface attachments must omit stack node ids.");
                        Require(definition.surfaceAttach && parent.definition.allowSurfaceAttach && definition.surfaceNode != null,
                            "Surface attachment is not allowed for: " + item.id);
                        var size = parent.definition.prefabSize;
                        Require(ValidVector(size) && size.x > 1e-4 && size.y > 1e-4 && size.z > 1e-4, "Parent has no usable prefab bounds for surface attachment.");
                        var cn = definition.surfaceNode;
                        Require(ValidVector(cn.position) && ValidVector(cn.orientation) && cn.orientation.Length > 1e-6, "Invalid surface node geometry.");
                        double angle = item.radialAngleDegrees * Math.PI / 180;
                        Require(ValidVector(parent.definition.prefabCenter), "Invalid parent geometry center.");
                        var localPoint = parent.definition.prefabCenter + new Vec(Math.Cos(angle) * size.x / 2, item.surfaceHeight * size.y / 2, Math.Sin(angle) * size.z / 2);
                        var localNormal = new Vec(Math.Cos(angle) / size.x, 0, Math.Sin(angle) / size.z).Unit;
                        var normal = parent.rotation.Apply(localNormal);
                        part.surfaceNormal = normal;
                        // Match EditorLogic's native surface convention. It differs
                        // from opposing stack normals, notably for radial engines.
                        part.rotation = Rotation.AxisAngle(normal, item.rollDegrees) *
                            Rotation.LookRotation(normal, parent.rotation.Apply(new Vec(0, 1, 0))) *
                            Rotation.LookRotation(cn.orientation, new Vec(0, 1, 0));
                        if (!string.IsNullOrEmpty(item.surfaceOrientation) && item.surfaceOrientation != "default")
                        {
                            Require(plan.facility == "SPH" && (item.surfaceOrientation == "wing" || item.surfaceOrientation == "fin"), "wing/fin orientation requires SPH.");
                            Require(definition.aero != null && definition.aero.Length > 0, "wing/fin orientation requires aerodynamic surface data.");
                            Require(ValidVector(definition.aero[0].normal) && definition.aero[0].normal.Length > 1e-6, "Invalid aerodynamic normal.");
                            var liftNormal = definition.aero[0].normal.Unit;
                            var span = definition.prefabCenter - cn.position;
                            span = span - liftNormal * Vec.Dot(span, liftNormal);
                            Require(span.Length > 1e-5, "Cannot infer the aerodynamic surface span axis.");
                            var desiredSpan = item.surfaceOrientation == "fin" ? new Vec(0, 1, 0) : new Vec(part.surfaceNormal.x >= 0 ? 1 : -1, 0, 0);
                            var desiredNormal = item.surfaceOrientation == "fin" ? new Vec(-1, 0, 0) : new Vec(0, part.surfaceNormal.x >= 0 ? 1 : -1, 0);
                            part.rotation = Rotation.AxisAngle(desiredSpan, item.rollDegrees) * Rotation.LookRotation(desiredSpan, desiredNormal) * Rotation.LookRotation(span, liftNormal).Inverse;
                        }
                        part.position = parent.position + parent.rotation.Apply(localPoint) - part.rotation.Apply(cn.position);
                        if (!string.IsNullOrEmpty(item.mirrorOf))
                        {
                            ResolvedPart source;
                            Require(plan.facility == "SPH" && byId.TryGetValue(item.mirrorOf, out source), "mirrorOf requires an earlier SPH surface part.");
                            source = byId[item.mirrorOf];
                            Require(source.plan.attachment == "surface" && source.definition.name == definition.name && source.plan.stage == item.stage, "Mirror counterparts must have the same surface part and stage.");
                            Require(string.IsNullOrEmpty(source.plan.mirrorOf) && !resolved.Any(p => p.plan.mirrorOf == source.plan.id), "Only one mirror counterpart is supported per source part.");
                            Require(item.parentId == source.plan.parentId || parent.plan.mirrorOf == source.plan.parentId, "Mirror counterpart parent is not compatible.");
                            if (item.parentId == source.plan.parentId) Require(Math.Abs((parent.position + parent.rotation.Apply(parent.definition.prefabCenter)).x) < .05, "A shared mirror parent must lie on the aircraft centre plane.");
                            Require((item.resources == null || item.resources.Length == 0) && (source.plan.resources == null || source.plan.resources.Length == 0), "Mirror counterparts currently use default resources.");
                            var anchor = source.position + source.rotation.Apply(cn.position);
                            anchor.x = -anchor.x;
                            part.surfaceNormal = source.surfaceNormal; part.surfaceNormal.x = -part.surfaceNormal.x;
                            if (definition.aero != null && definition.aero.Length > 0)
                            {
                                Require(ValidVector(definition.aero[0].normal) && definition.aero[0].normal.Length > 1e-6, "Invalid mirrored aerodynamic normal.");
                                var n = definition.aero[0].normal.Unit;
                                Func<Vec, Vec> reflect = v => { var r = source.rotation.Apply(v - n * (2 * Vec.Dot(v, n))); r.x = -r.x; return r; };
                                part.rotation = Rotation.LookRotation(reflect(new Vec(0, 0, 1)), reflect(new Vec(0, 1, 0)));
                            }
                            else part.rotation = Rotation.LookRotation(part.surfaceNormal, parent.rotation.Apply(new Vec(0, 1, 0))) * Rotation.LookRotation(cn.orientation, new Vec(0, 1, 0));
                            part.position = anchor - part.rotation.Apply(cn.position);
                        }
                    }
                    else
                    {
                        Require(string.IsNullOrEmpty(item.mirrorOf) && (string.IsNullOrEmpty(item.surfaceOrientation) || item.surfaceOrientation == "default"), "Surface orientation/mirroring cannot be applied to stack parts.");
                        Require(definition.stackAttach && parent.definition.allowStack, "Stack attachment is not allowed for: " + item.id);
                        var pn = FindNode(parent.definition, item.parentNodeId);
                        var cn = FindNode(definition, item.childNodeId);
                        Require(pn.size == cn.size, "Node size mismatch: " + item.id + ". Use an adapter part.");
                        Require(occupied.Add(item.parentId + ":" + pn.id) && occupied.Add(item.id + ":" + cn.id), "Attachment node already occupied: " + item.id);
                        var normal = parent.rotation.Apply(pn.orientation) * -1;
                        part.rotation = plan.facility == "SPH"
                            ? parent.rotation * Rotation.AxisAngle(pn.orientation * -1, item.rollDegrees) * Rotation.FromTo(cn.orientation, pn.orientation * -1)
                            : Rotation.AxisAngle(normal, item.rollDegrees) * Rotation.FromTo(cn.orientation, normal);
                        part.position = parent.position + parent.rotation.Apply(pn.position) - part.rotation.Apply(cn.position);
                    }
                    Require(ValidVector(part.position), "Craft extent exceeds the supported 1000 m coordinate range.");
                }
                Require(Finite(definition.dryMassTonnes) && definition.dryMassTonnes >= 0 && Finite(definition.defaultCost),
                    "Part has unsupported mass/cost data: " + item.partName);
                Require(definition.resources != null && definition.resources.All(r => r != null &&
                    !string.IsNullOrEmpty(r.name) && Finite(r.amount) && r.amount >= 0 &&
                    Finite(r.maxAmount) && r.maxAmount >= r.amount &&
                    Finite(r.densityTonnesPerUnit) && r.densityTonnesPerUnit >= 0 &&
                    Finite(r.unitCost) && r.unitCost >= 0), "Unsupported resource mass/cost or capacity data: " + item.partName);
                double mass = definition.dryMassTonnes, cost = definition.defaultCost;
                var amounts = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var resource in item.resources ?? new ResourceAmount[0])
                {
                    Require(resource != null && !string.IsNullOrEmpty(resource.name) && !amounts.ContainsKey(resource.name), "Invalid or duplicate resource override: " + item.id);
                    var source = definition.resources.FirstOrDefault(r => r.name == resource.name);
                    Require(source != null, "Resource is not present on part: " + resource.name);
                    Require(Finite(resource.amount) && resource.amount >= 0 && resource.amount <= source.maxAmount, "Resource amount outside capacity: " + resource.name);
                    amounts.Add(resource.name, resource.amount);
                }
                foreach (var resource in definition.resources)
                {
                    double amount;
                    if (!amounts.TryGetValue(resource.name, out amount)) amount = resource.amount;
                    mass += amount * resource.densityTonnesPerUnit;
                    cost += (amount - resource.amount) * resource.unitCost;
                }
                Require(Finite(mass) && mass >= 0 && Finite(cost) && cost >= 0, "Unsupported resource mass/cost data: " + item.partName);
                result.wetMassTonnes += mass;
                result.estimatedCost += cost;
                Require(Finite(result.wetMassTonnes) && Finite(result.estimatedCost), "Aggregate mass/cost overflow.");
                resolved.Add(part);
                byId.Add(item.id, part);
            }
            Require(plan.maxWetMassTonnes == 0 || result.wetMassTonnes <= plan.maxWetMassTonnes, "Wet mass exceeds the plan limit.");
            Require(plan.maxCost == 0 || result.estimatedCost <= plan.maxCost, "Estimated cost exceeds the plan limit.");
            result.resolved = resolved.ToArray();
            result.placements = resolved.Select(p => new PlacementInfo { id = p.plan.id, position = p.position, rotation = p.rotation }).ToArray();
            result.warnings = new[] {
                "Structural validation only. Collision clearance, flight stability and mission feasibility are NOT verified.",
                "Stages are explicit KSP inverseStage numbers (highest fires first); no automatic staging or fuel-flow simulation.",
                "Surface placement uses an elliptical prefab-bounds approximation, not collision detection. Review positions in the editor.",
                "Default prefab configuration only. Variants and rescaling are not supported; SPH mirrorOf pairs use the default surface geometry.",
                "Mass and cost estimates use prefab module defaults; verify mod-specific behavior after loading."
            };
            return result;
        }

        private static NodeInfo FindNode(PartInfo part, string id)
        {
            var node = part.nodes.FirstOrDefault(n => n.id == id);
            Require(node != null, "Unknown stack node on " + part.name + ": " + id);
            Require(ValidVector(node.position) && ValidVector(node.orientation) && node.orientation.Length > 1e-6,
                "Invalid node geometry on " + part.name + ": " + id);
            return node;
        }
    }
}
