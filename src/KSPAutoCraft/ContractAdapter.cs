using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Contracts;

namespace KSPAutoCraft
{
    [Serializable] internal sealed class ContractFact { public string name; public string value; }
    [Serializable] internal sealed class RequirementInfo
    {
        public string id, type, title, notes, state, kind, logic, targetBody, requiredPart, resourceName, crewMode, extractionIssue;
        public bool optional, requireNewVessel;
        public int minimumCrewCapacity = -1;
        public double minimumResource = -1;
        public string[] requiredModules = new string[0], candidateParts = new string[0], candidateModules = new string[0];
        public ContractFact[] facts = new ContractFact[0];
        public RequirementInfo[] children = new RequirementInfo[0];
    }
    [Serializable] internal sealed class ContractSummary
    {
        public string id, title, type, state, prestige;
        public double deadlineUT, expiryUT;
    }
    [Serializable] internal sealed class ContractDetail
    {
        public int schemaVersion = 1;
        public ContractSummary contract;
        public string synopsis, description, notes, saveName, gameMode;
        public double universalTime;
        public RequirementInfo[] requirements;
        public string[] warnings;
    }
    [Serializable] internal sealed class ContractPage
    {
        public bool available;
        public string gameMode;
        public int total, offset;
        public ContractSummary[] contracts;
    }
    [Serializable] internal sealed class BodyInfo
    {
        public string name, displayName, parent;
        public int index;
        public bool isHomeWorld, atmosphere, hasSolidSurface;
        public double radiusMetres, gravitationalParameter, surfaceGravity, atmosphereDepthMetres, atmospherePressureKpa, sphereOfInfluenceMetres;
    }
    [Serializable] internal sealed class WorldInfo
    {
        public string gameMode, saveName, homeBody;
        public bool hasFunds;
        public double funds, universalTime;
        public BodyInfo[] bodies;
        public double liftMultiplier, liftDragMultiplier;
        public string aerodynamicModel;
    }

    internal static class ContractAdapter
    {
        private const int MaxParameters = 256;
        private static string Text(string text) { return (text ?? "").Length > 4000 ? text.Substring(0, 4000) : text ?? ""; }
        private static ContractSummary Summary(Contract contract)
        {
            return new ContractSummary { id = contract.ContractGuid.ToString("D"), title = Text(contract.Title),
                type = contract.GetType().FullName, state = contract.ContractState.ToString(), prestige = contract.Prestige.ToString(),
                deadlineUT = contract.DateDeadline, expiryUT = contract.DateExpire };
        }
        internal static ContractPage List(string state, int offset, int limit)
        {
            var system = ContractSystem.Instance;
            var current = system == null ? new Contract[0] : system.Contracts.ToArray();
            var contracts = current.Where(c => c != null && (state == "all" ||
                c.ContractState.ToString().Equals(state, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(c => c.ContractGuid).ToArray();
            return new ContractPage { available = system != null, gameMode = HighLogic.CurrentGame.Mode.ToString(),
                total = contracts.Length, offset = offset, contracts = contracts.Skip(offset).Take(limit).Select(Summary).ToArray() };
        }
        internal static ContractDetail Detail(Guid id)
        {
            var system = ContractSystem.Instance;
            var contract = system == null ? null : system.Contracts.FirstOrDefault(c => c != null && c.ContractGuid == id);
            if (contract == null) return null;
            var warnings = new List<string> {
                "Contract data is read-only. Offered contracts must be accepted in Mission Control before flight.",
                "Static design checks are prerequisites, not proof of contract completion. Unknown/modded parameters require review."
            };
            int remaining = MaxParameters;
            var requirements = new List<RequirementInfo>();
            for (int i = 0; i < contract.ParameterCount && remaining >= 0; i++)
                requirements.Add(Parameter(contract.GetParameter(i), i.ToString(CultureInfo.InvariantCulture), 0, ref remaining));
            return new ContractDetail { contract = Summary(contract), synopsis = Text(contract.Synopsys), description = Text(contract.Description),
                notes = Text(contract.Notes), saveName = HighLogic.SaveFolder, gameMode = HighLogic.CurrentGame.Mode.ToString(),
                universalTime = Planetarium.GetUniversalTime(), requirements = requirements.ToArray(), warnings = warnings.ToArray() };
        }

        private static RequirementInfo Parameter(ContractParameter parameter, string path, int depth, ref int remaining)
        {
            var result = new RequirementInfo { id = path, kind = "unknown", logic = "all" };
            if (parameter == null || depth > 12 || --remaining < 0)
            {
                result.extractionIssue = "Parameter tree exceeds supported bounds or contains a null parameter.";
                return result;
            }
            result.type = parameter.GetType().FullName;
            result.optional = parameter.Optional;
            result.state = parameter.State.ToString();
            try
            {
                result.title = Text(parameter.Title);
                result.notes = Text(parameter.Notes);
                Extract(parameter, result);
            }
            catch (Exception error) { result.extractionIssue = "Extraction unavailable: " + error.GetType().Name; result.kind = "unknown"; }
            var children = new List<RequirementInfo>();
            for (int i = 0; i < parameter.ParameterCount && remaining >= 0; i++)
                children.Add(Parameter(parameter.GetParameter(i), path + "/" + i, depth + 1, ref remaining));
            result.children = children.ToArray();
            return result;
        }

        private static void Extract(ContractParameter parameter, RequirementInfo result)
        {
            string type = result.type;
            if (type == "Contracts.Parameters.OR" || type == "Contracts.Parameters.XOR")
            {
                result.kind = "group"; result.logic = type.EndsWith(".XOR", StringComparison.Ordinal) ? "exactlyOne" : "any";
            }
            else if (type == "FinePrint.Contracts.Parameters.CrewCapacityParameter")
            {
                result.minimumCrewCapacity = Convert.ToInt32(RequiredField(parameter, "targetCapacity"), CultureInfo.InvariantCulture);
                result.kind = "crew_capacity";
            }
            else if (type == "Contracts.Parameters.PartTest")
            {
                var available = RequiredField(parameter, "tgtPartInfo") as AvailablePart;
                if (available == null) throw new InvalidOperationException("Test part unavailable.");
                result.requiredPart = available.name;
                result.kind = "part_test";
            }
            else if (type == "FinePrint.Contracts.Parameters.VesselSystemsParameter")
            {
                result.requiredModules = Strings(RequiredField(parameter, "checkModuleTypes"));
                result.requireNewVessel = (bool)RequiredField(parameter, "requireNew");
                result.crewMode = RequiredField(parameter, "mannedStatus").ToString();
                result.kind = "vessel_systems";
            }
            else if (type == "FinePrint.Contracts.Parameters.PartRequestParameter")
            {
                result.candidateParts = Strings(RequiredField(parameter, "partNames"));
                result.candidateModules = Strings(RequiredField(parameter, "moduleNames"));
                // Lists and existing-vessel conditions are context, not an assumed AND/OR predicate.
                result.kind = "part_request_review";
            }
            else if (type == "FinePrint.Contracts.Parameters.ResourcePossessionParameter")
            {
                result.resourceName = (string)RequiredField(parameter, "resourceName");
                result.minimumResource = Convert.ToDouble(RequiredField(parameter, "goalResource"), CultureInfo.InvariantCulture);
                if (!PlanValidator.Finite(result.minimumResource) || result.minimumResource < 0) throw new InvalidOperationException();
                result.kind = "resource_delivery";
            }
            else if (type.StartsWith("Contracts.Parameters.", StringComparison.Ordinal) ||
                type.StartsWith("FinePrint.Contracts.Parameters.", StringComparison.Ordinal) ||
                type.StartsWith("Expansions.Serenity.Contracts.", StringComparison.Ordinal) ||
                type.StartsWith("SentinelMission.", StringComparison.Ordinal)) result.kind = "flight";

            // A bounded whitelist avoids serializing arbitrary mod objects or calling mutating Save hooks.
            var facts = new List<ContractFact>();
            foreach (var name in new[] { "Destination", "TargetBody", "targetBody", "Situation", "BiomeName", "body", "situation",
                "useAltLimits", "useSpdLimits", "minAltitude", "maxAltitude", "minSpeed", "maxSpeed",
                "inclination", "eccentricity", "sma", "lan", "argumentOfPeriapsis", "meanAnomalyAtEpoch", "epoch", "deviationWindow",
                "targetType", "kerbalName", "SubjectId", "ScienceTitle", "ScienceSubjectId", "TargetLocation",
                "goalHarvested", "resourceName", "VesselName", "VesselPersistentId", "PartName", "vesselName", "hauled", "repeatability" })
            {
                object value = Field(parameter, name);
                if (value == null) continue;
                var body = value as CelestialBody;
                if (body != null) { result.targetBody = body.bodyName; value = body.bodyName; }
                if (value is string || value is bool || value.GetType().IsEnum || value is IConvertible)
                    facts.Add(new ContractFact { name = name, value = Text(Convert.ToString(value, CultureInfo.InvariantCulture)) });
            }
            result.facts = facts.ToArray();
        }
        private static object RequiredField(object value, string name)
        {
            return Field(value, name) ?? throw new InvalidOperationException("Unsupported field layout: " + name);
        }
        private static object Field(object value, string name)
        {
            for (var type = value.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(value);
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0 && property.CanRead) return property.GetValue(value, null);
            }
            return null;
        }
        private static string[] Strings(object value)
        {
            var list = value as IEnumerable;
            if (list == null) throw new InvalidOperationException("Expected a list.");
            return list.Cast<object>().OfType<string>().Take(128).Select(Text).ToArray();
        }
        internal static WorldInfo World()
        {
            var home = FlightGlobals.GetHomeBody();
            return new WorldInfo { gameMode = HighLogic.CurrentGame.Mode.ToString(), saveName = HighLogic.SaveFolder,
                liftMultiplier = PhysicsGlobals.LiftMultiplier, liftDragMultiplier = PhysicsGlobals.LiftDragMultiplier,
                aerodynamicModel = AssemblyLoader.loadedAssemblies.Any(a => a.name.IndexOf("FerramAerospaceResearch", StringComparison.OrdinalIgnoreCase) >= 0) ? "FAR" : "Stock",
                homeBody = home != null ? home.bodyName : "", hasFunds = Funding.Instance != null,
                funds = Funding.Instance != null ? Funding.Instance.Funds : 0, universalTime = Planetarium.GetUniversalTime(),
                bodies = FlightGlobals.Bodies.Select(b => new BodyInfo { name = b.bodyName, displayName = Text(b.displayName),
                    index = b.flightGlobalsIndex, parent = b.referenceBody != null && b.referenceBody != b ? b.referenceBody.bodyName : "",
                    isHomeWorld = b.isHomeWorld, atmosphere = b.atmosphere, hasSolidSurface = b.hasSolidSurface,
                    radiusMetres = b.Radius, gravitationalParameter = b.gravParameter, surfaceGravity = b.GeeASL * 9.80665,
                    atmosphereDepthMetres = b.atmosphereDepth, atmospherePressureKpa = b.atmospherePressureSeaLevel,
                    sphereOfInfluenceMetres = PlanValidator.Finite(b.sphereOfInfluence) ? b.sphereOfInfluence : 0 }).ToArray() };
        }
    }
}
