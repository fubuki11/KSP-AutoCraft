using System;
using System.Collections.Generic;
using System.Globalization;
using KSPAutoCraft;

internal static class Program
{
    private const double GeometryTolerance = 1e-9;
    private const int RandomSeed = 4936528;
    private static int passed;
    private static int failed;
    private static string filter;

    private static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        filter = args.Length == 0 ? null : args[0];
        TestTopology();
        TestNumericValidation();
        TestResourcesAndLimits();
        TestGeometry();
        TestSurface();
        TestAircraftFrame();
        TestRotations();
        Console.WriteLine("Core tests: {0} passed, {1} failed. Random seed: {2}.", passed, failed, RandomSeed);
        Console.WriteLine("Pure CraftPlan tests only; JSON schema, Unity and game integration are not applicable.");
        return failed == 0 && passed > 0 ? 0 : 1;
    }

    private static void TestTopology()
    {
        Run("single root and result metadata", () =>
        {
            var f = new Fixture(1);
            var result = f.Validate();
            Check(result.valid && result.partCount == 1 && result.resolved.Length == 1, "Invalid result metadata.");
            Check(ReferenceEquals(result.resolved[0].plan, f.Plan.parts[0]), "Resolved plan reference changed.");
            Check(ReferenceEquals(result.resolved[0].definition, f.Definitions[0]), "Wrong resolved definition.");
            Check(result.warnings != null && result.warnings.Length > 0, "Structural caveats are missing.");
            VectorNear(new Vec(), result.resolved[0].position, "Root origin");
            Near(2.6, result.wetMassTonnes, 1e-12, "Default mass");
            Near(100, result.estimatedCost, 1e-12, "Default cost");
        });
        foreach (string facility in new[] { "VAB", "SPH" })
            Accept("facility " + facility, f => f.Plan.facility = facility);
        Reject("null plan", f => f.Plan = null, "plan object");
        Reject("null part list", f => f.Plan.parts = null, "1-128");
        Reject("empty part list", f => f.Plan.parts = new PlanPart[0], "1-128");
        Reject("129 parts", f => f.Plan = new Fixture(129).Plan, "1-128");
        Run("128-part ordered acyclic chain", () => CheckAttachments(new Fixture(128).Validate()));
        Reject("null part entry", f => f.Plan.parts[1] = null, "identifiers");
        foreach (string name in new[] { null, "", " ", "bad/name", "bad\\name", "bad{name", "bad}name", "bad=name", "bad\nname", new string('n', 81) })
            Reject("invalid craft name " + Display(name), f => f.Plan.name = name, "craft name");
        Accept("80-character craft name", f => f.Plan.name = new string('n', 80));
        foreach (string facility in new[] { null, "", "vab", "VAB ", "UNKNOWN" })
            Reject("invalid facility " + Display(facility), f => f.Plan.facility = facility, "facility");
        foreach (string id in new[] { null, "", "a b", "a:b", "a.b", "a/b", "a\nb", "a\n", "\u00e9", new string('a', 49) })
            Reject("invalid identifier " + Display(id), f => f.Plan.parts[1].id = id, "identifiers");
        Accept("48-character ASCII identifier", f => f.Plan.parts[1].id = new string('a', 45) + "_-9");
        Accept("identifiers are case sensitive", f => { f.Plan.parts[0].id = "ROOT"; f.Plan.parts[1].parentId = "ROOT"; f.Plan.parts[1].id = "root"; });
        Reject("duplicate ID", f => f.Plan.parts[1].id = "p0", "Duplicate part id");
        Reject("root with parent", f => f.Plan.parts[0].parentId = "p1", "sole root");
        Reject("root with parent node", f => f.Plan.parts[0].parentNodeId = "top", "sole root");
        Reject("root with child node", f => f.Plan.parts[0].childNodeId = "bottom", "sole root");
        Accept("empty root attachment fields", f => { f.Plan.parts[0].parentId = ""; f.Plan.parts[0].parentNodeId = ""; f.Plan.parts[0].childNodeId = ""; });
        Reject("second root", f => f.Plan.parts[1].parentId = null, "first part");
        Reject("empty second parent", f => f.Plan.parts[1].parentId = "", "first part");
        Reject("unknown parent", f => f.Plan.parts[1].parentId = "absent", "precede");
        Reject("parent IDs are case sensitive", f => f.Plan.parts[1].parentId = "P0", "precede");
        Reject("self cycle", f => f.Plan.parts[1].parentId = "p1", "precede");
        Reject("forward parent reference", f => { f.Plan.parts[1].parentId = "p2"; f.Plan.parts[2].parentId = "p0"; }, "precede", 3);
        Reject("two-part cycle", f => f.Plan.parts[1].parentId = "p2", "precede", 3);
        Reject("three-part cycle", f => f.Plan.parts[1].parentId = "p3", "precede", 4);
        Reject("parent node occupied by sibling", f => f.Plan.parts[2].parentId = "p0", "occupied", 3);
        Reject("child attachment node reused as parent", f => f.Plan.parts[2].parentNodeId = "bottom", "occupied", 3);
        Accept("distinct parent nodes support siblings", f => { f.Plan.parts[2].parentId = "p0"; f.Plan.parts[2].parentNodeId = "bottom"; }, 3);
        Accept("same node IDs on different parts", f => { }, 4);
        Reject("node size mismatch", f => f.Definitions[1].nodes[1].size = 2, "size mismatch");
        Accept("matching non-default node sizes", f => { f.Definitions[0].nodes[0].size = 2; f.Definitions[1].nodes[1].size = 2; });
        Reject("unknown parent node", f => f.Plan.parts[1].parentNodeId = "missing", "Unknown stack node");
        Reject("unknown child node", f => f.Plan.parts[1].childNodeId = "missing", "Unknown stack node");
        Reject("missing parent node", f => f.Plan.parts[1].parentNodeId = null, "Unknown stack node");
        Reject("missing child node", f => f.Plan.parts[1].childNodeId = null, "Unknown stack node");
        Reject("child cannot stack attach", f => f.Definitions[1].stackAttach = false, "not allowed");
        Reject("parent disallows stack attachment", f => f.Definitions[0].allowStack = false, "not allowed");
        foreach (string partName in new[] { null, "", " " })
            Reject("missing part name " + Display(partName), f => f.Plan.parts[1].partName = partName, "Missing partName");
        Reject("unknown part", f => f.Plan.parts[1].partName = "missing", "Unknown part");
        Reject("part lookup is case sensitive", f => f.Plan.parts[1].partName = "PART1", "Unknown part");
        Reject("locked child part", f => f.Definitions[1].unlocked = false, "not available");
        Reject("locked root part", f => f.Definitions[0].unlocked = false, "not available");
    }

    private static void TestNumericValidation()
    {
        foreach (int stage in new[] { -1, 0, 1, 98, 99 })
            Accept("stage boundary " + stage, f => { f.Plan.parts[0].stage = stage; f.Plan.parts[1].stage = stage; });
        foreach (int stage in new[] { int.MinValue, -2, 100, int.MaxValue })
        {
            Reject("invalid root stage " + stage, f => f.Plan.parts[0].stage = stage, "stage");
            Reject("invalid child stage " + stage, f => f.Plan.parts[1].stage = stage, "stage");
        }
        foreach (double roll in new[] { -360.0, -180, 0, 180, 360 })
            Accept("roll boundary " + roll, f => { f.Plan.parts[0].rollDegrees = roll; f.Plan.parts[1].rollDegrees = roll; });
        foreach (double roll in new[] { -360.0001, 360.0001 })
            Reject("roll outside range " + roll, f => f.Plan.parts[1].rollDegrees = roll, "rollDegrees");
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Run("Finite rejects " + value, () => Check(!PlanValidator.Finite(value), "Nonfinite number accepted."));
            Reject("nonfinite mass limit " + value, f => f.Plan.maxWetMassTonnes = value, "limits");
            Reject("nonfinite cost limit " + value, f => f.Plan.maxCost = value, "limits");
            Reject("nonfinite root roll " + value, f => f.Plan.parts[0].rollDegrees = value, "rollDegrees");
            Reject("nonfinite child roll " + value, f => f.Plan.parts[1].rollDegrees = value, "rollDegrees");
            Reject("nonfinite dry mass " + value, f => f.Definitions[0].dryMassTonnes = value, "mass/cost");
            Reject("nonfinite default cost " + value, f => f.Definitions[0].defaultCost = value, "mass/cost");
            Reject("nonfinite resource override " + value, f => f.Plan.parts[0].resources = new[] { Amount("Fuel", value) }, "capacity");
            Reject("nonfinite resource override capacity " + value, f =>
            {
                f.Definitions[0].resources[0].maxAmount = value;
                f.Plan.parts[0].resources = new[] { Amount("Fuel", 1) };
            }, null);
            Reject("nonfinite default resource amount " + value, f => f.Definitions[0].resources[0].amount = value, "mass/cost");
            Reject("nonfinite resource density " + value, f => f.Definitions[0].resources[0].densityTonnesPerUnit = value, "mass/cost");
            Reject("nonfinite resource unit cost " + value, f => f.Definitions[0].resources[0].unitCost = value, "mass/cost");
            for (int side = 0; side < 2; side++)
            {
                int part = side;
                int node = side == 0 ? 0 : 1;
                for (int component = 0; component < 3; component++)
                {
                    int axis = component;
                    Reject("nonfinite node position " + side + "/" + axis + "/" + value,
                        f => f.Definitions[part].nodes[node].position = Component(axis, value), "geometry");
                    Reject("nonfinite node normal " + side + "/" + axis + "/" + value,
                        f => f.Definitions[part].nodes[node].orientation = Component(axis, value), "geometry");
                }
            }
        }
        foreach (double value in new[] { double.MinValue, -1.0, 0, double.Epsilon, double.MaxValue })
            Run("Finite accepts " + value, () => Check(PlanValidator.Finite(value), "Finite number rejected."));
        Reject("negative mass limit", f => f.Plan.maxWetMassTonnes = -1, "limits");
        Reject("negative cost limit", f => f.Plan.maxCost = -1, "limits");
        Reject("negative dry mass", f => f.Definitions[0].dryMassTonnes = -1, "mass/cost");
        Reject("negative resulting cost", f => f.Definitions[0].defaultCost = -1, "mass/cost");
        Reject("zero parent normal", f => f.Definitions[0].nodes[0].orientation = new Vec(), "geometry");
        Reject("zero child normal", f => f.Definitions[1].nodes[1].orientation = new Vec(), "geometry");
        Reject("normal length at lower boundary", f => f.Definitions[1].nodes[1].orientation = new Vec(1e-6, 0, 0), "geometry");
        Accept("normal length above lower boundary", f => f.Definitions[1].nodes[1].orientation = new Vec(1.001e-6, 0, 0));
        Reject("node position at 1000m", f => f.Definitions[0].nodes[0].position = new Vec(1000, 0, 0), "geometry");
        Reject("node normal at upper boundary", f => f.Definitions[1].nodes[1].orientation = new Vec(0, 1000, 0), "geometry");
        Reject("finite node components overflow length", f => f.Definitions[0].nodes[0].position = new Vec(double.MaxValue, 0, 0), "geometry");
        Accept("craft extent just below 1000m", f => { f.Definitions[0].nodes[0].position = new Vec(998.999, 0, 0); f.Definitions[1].nodes[1].position = new Vec(-1, 0, 0); });
        Reject("craft extent at 1000m", f => { f.Definitions[0].nodes[0].position = new Vec(999, 0, 0); f.Definitions[1].nodes[1].position = new Vec(-1, 0, 0); }, "extent");
        Reject("multi-level cumulative craft extent", f =>
        {
            foreach (var definition in f.Definitions)
            {
                definition.nodes[0].position = new Vec(0, 300, 0);
                definition.nodes[1].position = new Vec(0, -300, 0);
            }
        }, "extent", 3);
    }

    private static void TestResourcesAndLimits()
    {
        Run("defaults already included in prefab cost", () =>
        {
            var f = new Fixture(1);
            f.Definitions[0].wetMassTonnes = 999;
            var result = f.Validate();
            Near(2 + 10 * 0.05 + 5 * 0.02, result.wetMassTonnes, 1e-12, "Dry plus default resource mass");
            Near(100, result.estimatedCost, 1e-12, "Do not double-charge default resources");
        });
        foreach (double amount in new[] { 0.0, 4, 10, 20 })
        {
            Run("partial resource override " + amount, () =>
            {
                var f = new Fixture(1);
                f.Plan.parts[0].resources = new[] { Amount("Fuel", amount) };
                var result = f.Validate();
                Near(2 + amount * 0.05 + 5 * 0.02, result.wetMassTonnes, 1e-12, "Override mass");
                Near(100 + (amount - 10) * 3, result.estimatedCost, 1e-12, "Override cost delta");
                Near(10, f.Definitions[0].resources[0].amount, 0, "Catalog not mutated");
                var again = f.Validate();
                Near(result.estimatedCost, again.estimatedCost, 0, "Repeated validation cost");
                Near(result.wetMassTonnes, again.wetMassTonnes, 0, "Repeated validation mass");
            });
        }
        Run("multiple resource overrides independent of order", () =>
        {
            var f = new Fixture(1);
            f.Plan.parts[0].resources = new[] { Amount("Ox", 15), Amount("Fuel", 0) };
            var result = f.Validate();
            Near(2.3, result.wetMassTonnes, 1e-12, "Mass");
            Near(90, result.estimatedCost, 1e-12, "Cost: 100 - 30 + 20");
        });
        Run("empty overrides preserve defaults", () =>
        {
            var f = new Fixture(1);
            f.Plan.parts[0].resources = new ResourceAmount[0];
            Near(2.6, f.Validate().wetMassTonnes, 1e-12, "Default mass");
            Near(100, f.Validate().estimatedCost, 0, "Default cost");
        });
        Run("resource-free zero-mass zero-cost part", () =>
        {
            var f = new Fixture(1);
            f.Definitions[0].resources = new ResourceInfo[0];
            f.Definitions[0].dryMassTonnes = 0;
            f.Definitions[0].defaultCost = 0;
            Near(0, f.Validate().wetMassTonnes, 0, "Mass");
            Near(0, f.Validate().estimatedCost, 0, "Cost");
        });
        Reject("null resource override", f => f.Plan.parts[0].resources = new ResourceAmount[] { null }, "resource override");
        foreach (string name in new[] { null, "" })
            Reject("invalid resource name " + Display(name), f => f.Plan.parts[0].resources = new[] { Amount(name, 1) }, "resource override");
        Reject("duplicate resource override", f => f.Plan.parts[0].resources = new[] { Amount("Fuel", 1), Amount("Fuel", 2) }, "duplicate resource override");
        Reject("unknown resource", f => f.Plan.parts[0].resources = new[] { Amount("Missing", 1) }, "not present");
        Reject("resource name case sensitive", f => f.Plan.parts[0].resources = new[] { Amount("fuel", 1) }, "not present");
        Reject("negative resource amount", f => f.Plan.parts[0].resources = new[] { Amount("Fuel", -0.01) }, "capacity");
        Reject("resource above capacity", f => f.Plan.parts[0].resources = new[] { Amount("Fuel", 20.01) }, "capacity");
        Reject("resource override makes total cost negative", f => { f.Definitions[0].defaultCost = 1; f.Plan.parts[0].resources = new[] { Amount("Fuel", 0) }; }, "mass/cost");
        Reject("per-part resource mass overflow", f => f.Definitions[0].resources[0].densityTonnesPerUnit = double.MaxValue, "mass/cost");
        Reject("per-part resource cost overflow", f => { f.Definitions[0].resources[0].unitCost = double.MaxValue; f.Plan.parts[0].resources = new[] { Amount("Fuel", 20) }; }, "mass/cost");
        Run("inclusive limits and aggregate accounting", () =>
        {
            var f = new Fixture(3);
            f.Plan.parts[1].resources = new[] { Amount("Fuel", 20) };
            var result = f.Validate();
            Near(8.3, result.wetMassTonnes, 1e-12, "Aggregate mass");
            Near(330, result.estimatedCost, 1e-12, "Aggregate cost");
            f.Plan.maxWetMassTonnes = result.wetMassTonnes;
            f.Plan.maxCost = result.estimatedCost;
            Check(f.Validate().valid, "Exact limits rejected.");
        });
        Reject("wet mass over limit", f => f.Plan.maxWetMassTonnes = 5.19, "Wet mass");
        Reject("cost over limit", f => f.Plan.maxCost = 199.99, "Estimated cost");
        Accept("zero limits mean unlimited", f => { f.Plan.maxCost = 0; f.Plan.maxWetMassTonnes = 0; }, 8);
        Reject("aggregate mass overflow with unlimited budget", f =>
        {
            foreach (var definition in f.Definitions) definition.dryMassTonnes = 1e308;
        }, null);
        Reject("aggregate cost overflow with unlimited budget", f =>
        {
            foreach (var definition in f.Definitions) definition.defaultCost = 1e308;
        }, null);
    }

    private static void TestGeometry()
    {
        Run("root roll rotates off-axis attachment point", () =>
        {
            var f = new Fixture(2);
            f.Plan.parts[0].rollDegrees = 90;
            f.Definitions[0].nodes[0].position = new Vec(2, 1, 0);
            f.Definitions[0].nodes[0].orientation = new Vec(1, 0, 0);
            var result = f.Validate();
            VectorNear(new Vec(0, 0, -1), result.resolved[0].rotation.Apply(new Vec(1, 0, 0)), "Root roll");
            VectorNear(new Vec(0, 1, -3), result.resolved[1].position, "Child position after root roll");
            CheckAttachments(result);
        });
        Run("child roll twists about attachment normal", () =>
        {
            var f = new Fixture(2);
            f.Plan.parts[1].rollDegrees = 90;
            var result = f.Validate();
            VectorNear(new Vec(0, 0, 1), result.resolved[1].rotation.Apply(new Vec(1, 0, 0)), "Child roll sign and axis");
            CheckAttachments(result);
        });
        Run("oblique unnormalized nodes and nonzero offsets", () =>
        {
            var f = new Fixture(2);
            f.Plan.parts[0].rollDegrees = 37;
            f.Plan.parts[1].rollDegrees = -123;
            f.Definitions[0].nodes[0].position = new Vec(1.7, -0.3, 2.1);
            f.Definitions[0].nodes[0].orientation = new Vec(2, -3, 4);
            f.Definitions[1].nodes[1].position = new Vec(-0.8, 1.9, 0.2);
            f.Definitions[1].nodes[1].orientation = new Vec(-5, 2, 3);
            CheckAttachments(f.Validate());
        });
        Run("exact antiparallel attachment with roll", () =>
        {
            var f = new Fixture(2);
            f.Definitions[0].nodes[0].orientation = new Vec(2, -3, 4);
            f.Definitions[1].nodes[1].orientation = new Vec(6, -9, 12);
            f.Plan.parts[1].rollDegrees = 71;
            CheckAttachments(f.Validate());
        });
        Run("near-antiparallel attachment normals remain opposite", () =>
        {
            var f = new Fixture(2);
            f.Definitions[0].nodes[0].orientation = new Vec(1, -1e-5, 0);
            f.Definitions[1].nodes[1].orientation = new Vec(1, 0, 0);
            CheckAttachments(f.Validate());
        });
        Run("multi-level oblique rotations and branching", () =>
        {
            var f = new Fixture(16);
            for (int i = 0; i < f.Plan.parts.Length; i++)
            {
                f.Plan.parts[i].rollDegrees = (i * 73 % 721) - 360;
                f.Definitions[i].nodes[0].position = new Vec(0.1 * i, 1.2, -0.07 * i);
                f.Definitions[i].nodes[0].orientation = new Vec(1 + i * 0.1, 2, -0.3);
                f.Definitions[i].nodes[1].position = new Vec(-0.2, -0.5, 0.4);
                f.Definitions[i].nodes[1].orientation = new Vec(-0.4, -2, 0.2 + i * 0.05);
            }
            f.Plan.parts[15].parentId = "p0";
            f.Plan.parts[15].parentNodeId = "bottom";
            var first = f.Validate();
            var second = f.Validate();
            CheckAttachments(first);
            for (int i = 0; i < first.resolved.Length; i++)
            {
                VectorNear(first.resolved[i].position, second.resolved[i].position, "Repeatable position " + i);
                VectorNear(first.resolved[i].rotation.Apply(new Vec(1, 2, 3)), second.resolved[i].rotation.Apply(new Vec(1, 2, 3)), "Repeatable rotation " + i);
            }
        });
        Run("randomized multi-level geometry", () =>
        {
            var random = new Random(RandomSeed);
            var f = new Fixture(64);
            for (int i = 0; i < f.Plan.parts.Length; i++)
            {
                f.Plan.parts[i].rollDegrees = random.NextDouble() * 720 - 360;
                foreach (var node in f.Definitions[i].nodes)
                {
                    node.position = RandomVector(random);
                    node.orientation = RandomVector(random);
                }
            }
            CheckAttachments(f.Validate());
        });
    }

    private static void TestRotations()
    {
        Run("AxisAngle and multiplication composition", () =>
        {
            var a = Rotation.AxisAngle(new Vec(2, -1, 3), 73);
            var b = Rotation.AxisAngle(new Vec(-3, 5, 2), -121);
            var v = new Vec(0.7, -2, 3.5);
            VectorNear(a.Apply(b.Apply(v)), (a * b).Apply(v), "Composition order");
            VectorNear(v, (Rotation.AxisAngle(new Vec(2, -1, 3), -73) * a).Apply(v), "Inverse");
            VectorNear(v, Rotation.Identity.Apply(v), "Identity");
            Near(v.Length, (a * b).Apply(v).Length, GeometryTolerance, "Length preservation");
        });
        var axes = new[] { new Vec(1, 0, 0), new Vec(-1, 0, 0), new Vec(0, 1, 0), new Vec(0, -1, 0), new Vec(0, 0, 1), new Vec(0, 0, -1) };
        foreach (var from in axes)
            foreach (var to in axes)
                Run("FromTo axes " + Format(from) + " -> " + Format(to), () => CheckFromTo(from, to));
        foreach (var from in new[] { new Vec(0.799999, 0.6, 0), new Vec(0.800001, 0.6, 0), new Vec(2, -3, 4), new Vec(-2, 3, -4) })
            Run("FromTo antiparallel basis " + Format(from), () => CheckFromTo(from, from * -3));
        foreach (double epsilon in new[] { 1e-3, 1e-4, 5e-5, 4e-5, 1e-5, 1e-6, 1e-8, 1e-10 })
        {
            Run("FromTo near antiparallel epsilon=" + epsilon, () => CheckFromTo(new Vec(1, 0, 0), new Vec(-1, epsilon, 0)));
            Run("FromTo near parallel epsilon=" + epsilon, () => CheckFromTo(new Vec(1, 0, 0), new Vec(1, epsilon, 0)));
        }
        foreach (double scale in new[] { 1.001e-6, 0.1, 999.0, 1e-200, 1e200, double.Epsilon, double.MaxValue })
        {
            Run("FromTo finite scale=" + scale, () => CheckFromTo(new Vec(scale, 0, 0), new Vec(0, scale, 0)));
            Run("FromTo unequal magnitudes scale=" + scale, () => CheckFromTo(new Vec(scale, 0, 0), new Vec(0, 2, -3)));
        }
        Run("FromTo deterministic randomized general vectors", () => RandomFromTo(null));
        Run("FromTo deterministic randomized exact antiparallel vectors", () => RandomFromTo(0));
        foreach (double epsilon in new[] { 1e-3, 1e-4, 5e-5, 4e-5, 1e-5, 1e-6, 1e-8, 1e-10 })
            Run("FromTo deterministic randomized antiparallel epsilon=" + epsilon, () => RandomFromTo(epsilon));
    }

    private static Fixture SurfaceFixture()
    {
        var f = new Fixture(2);
        f.Definitions[0].allowSurfaceAttach = true;
        f.Definitions[0].prefabSize = new Vec(2, 4, 2);
        f.Definitions[1].surfaceAttach = true;
        f.Definitions[1].surfaceNode = new NodeInfo { id = "srfAttach", position = new Vec(0.2, 0.3, 0), orientation = new Vec(1, 0, 0) };
        f.Plan.parts[1].attachment = "surface";
        f.Plan.parts[1].parentNodeId = null;
        f.Plan.parts[1].childNodeId = null;
        return f;
    }

    private static void TestSurface()
    {
        foreach (double angle in new[] { -180.0, 0, 45, 90, 360 })
        {
            Run("surface attachment contact and orientation " + angle, () => {
                var f = SurfaceFixture();
                f.Plan.parts[0].rollDegrees = 31;
                f.Definitions[0].prefabCenter = new Vec(0.1, -0.2, 0.3);
                f.Plan.parts[1].radialAngleDegrees = angle;
                f.Plan.parts[1].surfaceHeight = 0.5;
                f.Plan.parts[1].rollDegrees = 72;
                var result = f.Validate();
                var parent = result.resolved[0];
                var child = result.resolved[1];
                double radians = angle * Math.PI / 180;
                var point = parent.position + parent.rotation.Apply(parent.definition.prefabCenter + new Vec(Math.Cos(radians), 1, Math.Sin(radians)));
                VectorNear(point, child.position + child.rotation.Apply(child.definition.surfaceNode.position), "Surface point");
                VectorNear(parent.rotation.Apply(new Vec(-Math.Cos(radians), 0, -Math.Sin(radians))),
                    child.rotation.Apply(child.definition.surfaceNode.orientation.Unit), "Surface normal");
            });
        }
        foreach (string invalid in new[] { "root", "parentRule", "childRule", "missingBounds", "stackIds", "height", "angle", "normal" })
        {
            Run("surface rejects " + invalid, () => {
                var f = SurfaceFixture();
                switch (invalid) {
                    case "root": f.Plan.parts[0].attachment = "surface"; break;
                    case "parentRule": f.Definitions[0].allowSurfaceAttach = false; break;
                    case "childRule": f.Definitions[1].surfaceAttach = false; break;
                    case "missingBounds": f.Definitions[0].prefabSize = new Vec(); break;
                    case "stackIds": f.Plan.parts[1].parentNodeId = "top"; break;
                    case "height": f.Plan.parts[1].surfaceHeight = 1.01; break;
                    case "angle": f.Plan.parts[1].radialAngleDegrees = double.NaN; break;
                    case "normal": f.Definitions[1].surfaceNode.orientation = new Vec(); break;
                }
                bool failed = false;
                try { f.Validate(); } catch (PlanException) { failed = true; }
                Check(failed, "Invalid surface plan was accepted.");
            });
        }
        Run("radial engine negative-Z mount extends outside the parent", () => {
            var f = SurfaceFixture();
            f.Definitions[1].surfaceNode.position = new Vec();
            f.Definitions[1].surfaceNode.orientation = new Vec(0, 0, -1);
            var result = f.Validate();
            var child = result.resolved[1];
            // Real 24-77 mesh extends toward local -Z from its zero-position mount.
            VectorNear(new Vec(1, 0, 0), child.rotation.Apply(new Vec(0, 0, -1)), "Engine body faces outward");
            VectorNear(new Vec(0, 1, 0), child.rotation.Apply(new Vec(0, 1, 0)), "Engine longitudinal axis stays upright");
        });
        Run("LookRotation establishes forward/up basis", () => {
            foreach (var forward in new[] { new Vec(1, 0, 0), new Vec(-1, 0, 0), new Vec(0, 0, -1), new Vec(1, 2, 3) })
            {
                var q = Rotation.LookRotation(forward, new Vec(0, 1, 0));
                VectorNear(forward.Unit, q.Apply(new Vec(0, 0, 1)), "Forward axis");
                Near(1, q.Apply(new Vec(0, 1, 0)).Length, GeometryTolerance, "Up axis normalized");
            }
        });
    }

    private static void TestAircraftFrame()
    {
        Run("SPH root and stack longitudinal axis points forward", () => {
            var f = new Fixture(3); f.Plan.facility = "SPH";
            var r = f.Validate();
            foreach (var part in r.resolved) VectorNear(new Vec(0,0,1), part.rotation.Apply(new Vec(0,1,0)), "SPH forward axis");
            CheckAttachments(r);
        });
        Run("aircraft wings mirror across X while keeping chord direction", () => {
            var f = SurfaceFixture(); f.Plan.facility = "SPH";
            f.Definitions[1].surfaceNode.position = new Vec();
            f.Definitions[1].prefabCenter = new Vec(1,0,0);
            f.Definitions[1].aero = new[] { new AeroInfo { normal = new Vec(0,0,1), liftCoefficient = 1 } };
            f.Plan.parts[1].surfaceOrientation = "wing";
            f.Plan.parts = new[] { f.Plan.parts[0], f.Plan.parts[1], new PlanPart {
                id="mirror", partName=f.Plan.parts[1].partName, parentId=f.Plan.parts[0].id,
                stage=f.Plan.parts[1].stage, attachment="surface", mirrorOf=f.Plan.parts[1].id
            }};
            var r = f.Validate(); var right = r.resolved[1]; var left = r.resolved[2];
            VectorNear(new Vec(-right.position.x,right.position.y,right.position.z),left.position,"Mirrored position");
            VectorNear(new Vec(0,1,0),right.rotation.Apply(new Vec(0,0,1)),"Right wing normal");
            VectorNear(new Vec(0,-1,0),left.rotation.Apply(new Vec(0,0,1)),"Left wing normal");
            VectorNear(right.rotation.Apply(new Vec(0,1,0)),left.rotation.Apply(new Vec(0,1,0)),"Chord remains aligned");
        });
    }

    private static void RandomFromTo(double? epsilon)
    {
        var random = new Random(RandomSeed);
        int failures = 0;
        string first = null;
        // Continue through every sample even if an earlier quaternion failed.
        for (int i = 0; i < 256; i++)
        {
            var from = Unit(RandomVector(random));
            Vec to;
            if (epsilon.HasValue)
            {
                var basis = Math.Abs(from.x) < 0.5 ? new Vec(1, 0, 0) : new Vec(0, 1, 0);
                to = from * -1 + Unit(Vec.Cross(from, basis)) * epsilon.Value;
            }
            else to = RandomVector(random);
            from = from * Math.Pow(10, random.NextDouble() * 4 - 2);
            to = to * Math.Pow(10, random.NextDouble() * 4 - 2);
            try { CheckFromTo(from, to); }
            catch (Exception error)
            {
                failures++;
                if (first == null) first = "sample=" + i + ": " + error.Message;
            }
        }
        Check(failures == 0, failures + "/256 samples failed; seed=" + RandomSeed + "; first " + first);
    }

    private static void CheckFromTo(Vec from, Vec to)
    {
        string context = "from=" + Format(from) + " to=" + Format(to);
        var rotation = Rotation.FromTo(from, to);
        CheckRotation(rotation, context);
        VectorNear(Unit(to), rotation.Apply(Unit(from)), context);
        var probe = new Vec(0.3, -0.7, 1.1);
        Near(probe.Length, rotation.Apply(probe).Length, GeometryTolerance, "Probe length " + context);
    }

    private static void CheckAttachments(PlanResult result)
    {
        Check(result.valid && result.partCount == result.resolved.Length, "Invalid resolved result.");
        var byId = new Dictionary<string, ResolvedPart>(StringComparer.Ordinal);
        foreach (var child in result.resolved)
        {
            CheckRotation(child.rotation, child.plan.id);
            if (byId.Count > 0)
            {
                ResolvedPart parent;
                Check(byId.TryGetValue(child.plan.parentId, out parent), "Resolved parent order changed.");
                var pn = Array.Find(parent.definition.nodes, n => n.id == child.plan.parentNodeId);
                var cn = Array.Find(child.definition.nodes, n => n.id == child.plan.childNodeId);
                var parentPoint = parent.position + parent.rotation.Apply(pn.position);
                var childPoint = child.position + child.rotation.Apply(cn.position);
                VectorNear(parentPoint, childPoint, "Coincident attachment points " + child.plan.id);
                // Do not renormalize transformed normals: that would hide a non-unit quaternion.
                VectorNear(parent.rotation.Apply(Unit(pn.orientation)) * -1,
                    child.rotation.Apply(Unit(cn.orientation)), "Opposite attachment normals " + child.plan.id);
            }
            byId.Add(child.plan.id, child);
        }
    }

    private static void CheckRotation(Rotation rotation, string context)
    {
        double norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
        Near(1, norm, 1e-10, "Unit quaternion " + context);
    }

    private static Vec Unit(Vec vector)
    {
        // Independent scale-safe oracle, not the production Vec.Unit implementation.
        double scale = Math.Max(Math.Abs(vector.x), Math.Max(Math.Abs(vector.y), Math.Abs(vector.z)));
        Check(PlanValidator.Finite(scale) && scale > 0, "Oracle needs a finite nonzero vector.");
        var scaled = new Vec(vector.x / scale, vector.y / scale, vector.z / scale);
        double length = Math.Sqrt(scaled.x * scaled.x + scaled.y * scaled.y + scaled.z * scaled.z);
        return new Vec(scaled.x / length, scaled.y / length, scaled.z / length);
    }

    private static Vec RandomVector(Random random)
    {
        return new Vec(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
    }

    private static Vec Component(int axis, double value)
    {
        return new Vec(axis == 0 ? value : 0, axis == 1 ? value : 0, axis == 2 ? value : 0);
    }

    private static ResourceAmount Amount(string name, double amount)
    {
        return new ResourceAmount { name = name, amount = amount };
    }

    private static void Accept(string name, Action<Fixture> change, int count = 2)
    {
        Run(name, () =>
        {
            var f = new Fixture(count);
            change(f);
            CheckAttachments(f.Validate());
        });
    }

    private static void Reject(string name, Action<Fixture> change, string message, int count = 2)
    {
        Run(name, () =>
        {
            var f = new Fixture(count);
            change(f);
            PlanResult result;
            try { result = f.Validate(); }
            catch (PlanException error)
            {
                Check(message == null || error.Message.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0,
                    "Wrong rejection: " + error.Message + "; expected message containing " + message);
                return;
            }
            throw new Exception("Expected PlanException, got valid=" + result.valid + ", mass=" + result.wetMassTonnes + ", cost=" + result.estimatedCost);
        });
    }

    private static void Run(string name, Action test)
    {
        if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return;
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + ": " + error.Message);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        Check(PlanValidator.Finite(actual) && Math.Abs(expected - actual) <= tolerance,
            message + ": expected=" + expected.ToString("R") + " actual=" + actual.ToString("R") + " tolerance=" + tolerance.ToString("R"));
    }

    private static void VectorNear(Vec expected, Vec actual, string message)
    {
        Near(expected.x, actual.x, GeometryTolerance, message + " x");
        Near(expected.y, actual.y, GeometryTolerance, message + " y");
        Near(expected.z, actual.z, GeometryTolerance, message + " z");
    }

    private static string Format(Vec vector)
    {
        return "(" + vector.x.ToString("R") + "," + vector.y.ToString("R") + "," + vector.z.ToString("R") + ")";
    }

    private static string Display(string value)
    {
        return value == null ? "<null>" : "\"" + value.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
    }

    private sealed class Fixture
    {
        internal CraftPlan Plan;
        internal readonly PartInfo[] Definitions;

        internal Fixture(int count)
        {
            Definitions = new PartInfo[count];
            Plan = new CraftPlan { name = "Core test craft", facility = "VAB", parts = new PlanPart[count] };
            for (int i = 0; i < count; i++)
            {
                Definitions[i] = new PartInfo
                {
                    name = "part" + i, unlocked = true, stackAttach = true, allowStack = true,
                    dryMassTonnes = 2, wetMassTonnes = 2.6, defaultCost = 100,
                    nodes = new[]
                    {
                        new NodeInfo { id = "top", size = 1, position = new Vec(0, 1, 0), orientation = new Vec(0, 1, 0) },
                        new NodeInfo { id = "bottom", size = 1, position = new Vec(0, -1, 0), orientation = new Vec(0, -1, 0) }
                    },
                    resources = new[]
                    {
                        new ResourceInfo { name = "Fuel", amount = 10, maxAmount = 20, densityTonnesPerUnit = 0.05, unitCost = 3 },
                        new ResourceInfo { name = "Ox", amount = 5, maxAmount = 15, densityTonnesPerUnit = 0.02, unitCost = 2 }
                    },
                    engines = new EngineInfo[0]
                };
                Plan.parts[i] = new PlanPart
                {
                    id = "p" + i, partName = Definitions[i].name, stage = -1,
                    parentId = i == 0 ? null : "p" + (i - 1),
                    parentNodeId = i == 0 ? null : "top", childNodeId = i == 0 ? null : "bottom"
                };
            }
        }

        internal PlanResult Validate()
        {
            return PlanValidator.Validate(Plan, name => Array.Find(Definitions, definition => definition.name == name));
        }
    }
}
