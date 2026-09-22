using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KSPAutoCraft
{
    [Serializable]
    internal sealed class CatalogPage
    {
        public int total;
        public int offset;
        public PartInfo[] parts;
    }

    [Serializable]
    internal sealed class ShipPartInfo
    {
        public uint craftId;
        public string partName;
        public uint parentCraftId;
        public int stage;
    }

    [Serializable]
    internal sealed class ShipState
    {
        public string name;
        public string facility;
        public int partCount;
        public double dryMassTonnes;
        public double resourceMassTonnes;
        public double wetMassTonnes;
        public double cost;
        public Vec estimatedCenterOfMassWorld;
        public string homeBody;
        public double homeSurfaceGravity;
        public ShipPartInfo[] parts;
        public string[] limitations;
    }

    // Every entry point is called exclusively on Unity's main thread.
    internal static class KspAdapter
    {
        internal static Vec Vector(Vector3 v) { return new Vec(v.x, v.y, v.z); }
        private static Vector3 UnityVector(Vec v) { return new Vector3((float)v.x, (float)v.y, (float)v.z); }
        private static Quaternion UnityRotation(Rotation q) { return new Quaternion((float)q.x, (float)q.y, (float)q.z, (float)q.w); }

        internal static bool Unlocked(AvailablePart available)
        {
            if (HighLogic.CurrentGame == null) return false;
            if (HighLogic.CurrentGame.Mode == Game.Modes.SANDBOX) return true;
            if (ResearchAndDevelopment.IsExperimentalPart(available)) return true;
            return ResearchAndDevelopment.PartTechAvailable(available) &&
                (HighLogic.CurrentGame.Mode != Game.Modes.CAREER || ResearchAndDevelopment.PartModelPurchased(available));
        }

        internal static PartInfo Snapshot(AvailablePart available)
        {
            var part = available.partPrefab;
            var geometry = PrefabGeometry.Get(part);
            var resources = part.Resources.Cast<PartResource>().Select(r => new ResourceInfo {
                name = r.resourceName, amount = r.amount, maxAmount = r.maxAmount,
                densityTonnesPerUnit = r.info.density, unitCost = r.info.unitCost
            }).ToArray();
            double dry = part.mass + part.GetModuleMass(part.mass, ModifierStagingSituation.CURRENT);
            var info = new PartInfo {
                name = available.name, title = available.title, category = available.category.ToString(),
                unlocked = Unlocked(available), crewCapacity = part.CrewCapacity,
                experimental = HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.IsExperimentalPart(available),
                moduleNames = part.Modules.Cast<PartModule>().Select(m => m.moduleName).Distinct().OrderBy(n => n).ToArray(),
                experiments = part.Modules.OfType<ModuleScienceExperiment>().Select(m => m.experimentID).Distinct().ToArray(),
                massOffset = Vector(part.CoMOffset), liftOffset = Vector(part.CoLOffset), fuelCrossFeed = part.fuelCrossFeed,
                noCrossFeedNodeKey = part.NoCrossFeedNodeKey, multimode = part.Modules.OfType<MultiModeEngine>().Any(),
                wheel = part.Modules.Cast<PartModule>().Any(m => m.moduleName == "ModuleWheelBase" || m.moduleName == "KSPWheelBase" || m.moduleName.StartsWith("FSwheel")),
                wheelType = FlightCatalog.WheelType(part),
                stackAttach = part.attachRules.stack, allowStack = part.attachRules.allowStack,
                surfaceAttach = part.attachRules.srfAttach, allowSurfaceAttach = part.attachRules.allowSrfAttach,
                prefabSize = geometry.size, prefabCenter = geometry.center, geometrySource = geometry.source,
                surfaceNode = part.srfAttachNode == null ? null : new NodeInfo { id = part.srfAttachNode.id, size = part.srfAttachNode.size,
                    position = Vector(part.srfAttachNode.position), orientation = Vector(part.srfAttachNode.orientation) },
                dryMassTonnes = dry, wetMassTonnes = dry + resources.Sum(r => r.amount * r.densityTonnesPerUnit),
                defaultCost = available.cost + part.GetModuleCosts(available.cost, ModifierStagingSituation.CURRENT),
                resources = resources,
                nodes = part.attachNodes.Where(n => n.nodeType == AttachNode.NodeType.Stack).Select(n => new NodeInfo {
                    id = n.id, size = n.size, position = Vector(n.position), orientation = Vector(n.orientation)
                }).ToArray(),
                engines = FlightCatalog.Engines(part), aero = FlightCatalog.Aero(part), intakes = FlightCatalog.Intakes(part), separators = FlightCatalog.Separators(part)
            };
            info.roles = FlightCatalog.Roles(info);
            return info;
        }

        internal static CatalogPage Catalog(int offset, int limit, string search)
        {
            var available = PartLoader.LoadedPartsList.Where(p => p != null && p.partPrefab != null);
            if (!string.IsNullOrEmpty(search))
                available = available.Where(p => p.name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (p.title ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            var all = available.OrderBy(p => p.name, StringComparer.Ordinal).ToArray();
            return new CatalogPage { total = all.Length, offset = offset, parts = all.Skip(offset).Take(limit).Select(Snapshot).ToArray() };
        }

        internal static PlanResult Validate(CraftPlan plan)
        {
            var cache = new Dictionary<string, PartInfo>(StringComparer.Ordinal);
            return PlanValidator.Validate(plan, name => {
                PartInfo info;
                if (cache.TryGetValue(name, out info)) return info;
                var available = PartLoader.LoadedPartsList.FirstOrDefault(p => p.name == name);
                if (available == null || available.partPrefab == null) return null;
                info = Snapshot(available);
                cache.Add(name, info);
                return info;
            });
        }

        internal static ShipState State()
        {
            var ship = EditorLogic.fetch.ship;
            float dry = 0, fuel = 0, dryCost = 0, fuelCost = 0;
            ship.GetShipMass(out dry, out fuel);
            double cost = ship.GetShipCosts(out dryCost, out fuelCost);
            var center = new Vec();
            double centerMass = 0;
            foreach (var part in ship.parts)
            {
                double mass = part.mass + part.GetResourceMass();
                center = center + Vector(part.transform.TransformPoint(part.CoMOffset)) * mass;
                centerMass += mass;
            }
            var home = FlightGlobals.GetHomeBody();
            return new ShipState {
                name = ship.shipName, facility = EditorDriver.editorFacility.ToString(), partCount = ship.parts.Count,
                dryMassTonnes = dry, resourceMassTonnes = fuel, wetMassTonnes = dry + fuel, cost = cost,
                estimatedCenterOfMassWorld = centerMass > 0 ? center * (1 / centerMass) : center,
                homeBody = home != null ? home.bodyName : "unknown", homeSurfaceGravity = home != null ? home.GeeASL * 9.80665 : 0,
                parts = ship.parts.Select(p => new ShipPartInfo {
                    craftId = p.craftID, partName = p.partInfo.name, parentCraftId = p.parent != null ? p.parent.craftID : 0, stage = p.inverseStage
                }).ToArray(),
                limitations = new[] {
                    "Mass and cost are KSP editor totals; center of mass is an approximation in Unity world coordinates.",
                    "Stage delta-v, fuel availability, active-mode TWR, CoT and aerodynamic stability are not computed in this release.",
                    "A CoM/CoL comparison alone is not a rocket stability assessment. Use in-game analysis and flight tests."
                }
            };
        }

        internal static ConfigNode CraftConfig(CraftPlan plan, PlanResult result)
        {
            var craft = new ConfigNode();
            craft.AddValue("ship", plan.name);
            craft.AddValue("version", "1.12.5");
            craft.AddValue("description", "KSPAutoCraft structural prototype. Review staging and flight safety before use.");
            craft.AddValue("type", plan.facility);
            craft.AddValue("size", Vector3.zero);
            craft.AddValue("rot", Quaternion.identity);
            craft.AddValue("persistentId", NewId());
            craft.AddValue("steamPublishedFileId", "0");
            craft.AddValue("missionFlag", "Squad/Flags/default");
            craft.AddValue("vesselType", "Ship");
            var parts = result.resolved.ToDictionary(p => p.plan.id, StringComparer.Ordinal);
            var ids = new Dictionary<string, uint>(StringComparer.Ordinal);
            var usedIds = new HashSet<uint>();
            foreach (var part in result.resolved)
            {
                uint id;
                do { id = NewId(); } while (!usedIds.Add(id));
                ids.Add(part.plan.id, id);
            }
            Func<string, string> reference = id => parts[id].definition.name + "_" + ids[id].ToString(CultureInfo.InvariantCulture);
            var connected = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in result.resolved.Skip(1))
            {
                if (part.plan.attachment == "surface") continue;
                connected.Add(part.plan.id + ":" + part.plan.childNodeId, part.plan.parentId);
                connected.Add(part.plan.parentId + ":" + part.plan.parentNodeId, part.plan.id);
            }
            var origin = EditorLogic.fetch.initialPodPosition;
            foreach (var part in result.resolved)
            {
                var prefab = PartLoader.getPartInfoByName(part.definition.name).partPrefab;
                var node = craft.AddNode("PART");
                var position = UnityVector(part.position) + origin;
                var rotation = UnityRotation(part.rotation);
                var relativePosition = position;
                var relativeRotation = rotation;
                if (!string.IsNullOrEmpty(part.plan.parentId))
                {
                    var parent = parts[part.plan.parentId];
                    var inverse = Quaternion.Inverse(UnityRotation(parent.rotation));
                    relativePosition = inverse * UnityVector(part.position - parent.position);
                    relativeRotation = inverse * rotation;
                }
                node.AddValue("part", reference(part.plan.id));
                node.AddValue("partName", "Part");
                node.AddValue("persistentId", NewId());
                node.AddValue("pos", position);
                node.AddValue("attPos", Vector3.zero);
                node.AddValue("attPos0", relativePosition);
                node.AddValue("rot", rotation);
                node.AddValue("attRot", Quaternion.identity);
                node.AddValue("attRot0", relativeRotation);
                node.AddValue("mir", Vector3.one);
                node.AddValue("symMethod", plan.facility == "SPH" ? "Mirror" : "Radial");
                if (!string.IsNullOrEmpty(part.plan.mirrorOf)) node.AddValue("sym", reference(part.plan.mirrorOf));
                foreach (var mirror in result.resolved.Where(p => p.plan.mirrorOf == part.plan.id)) node.AddValue("sym", reference(mirror.plan.id));
                node.AddValue("autostrutMode", "Off");
                node.AddValue("rigidAttachment", false);
                node.AddValue("istg", part.plan.stage);
                node.AddValue("dstg", part.plan.stage);
                node.AddValue("resPri", 0);
                node.AddValue("sidx", -1);
                node.AddValue("sqor", -1);
                node.AddValue("sepI", -1);
                node.AddValue("attm", part.plan.attachment == "surface" ? 1 : 0);
                if (part.plan.attachment == "surface")
                {
                    var attach = prefab.srfAttachNode;
                    node.AddValue("srfN", attach.id + "," + reference(part.plan.parentId) + ",," +
                        KSPUtil.WriteVector(attach.position, "|") + "," + KSPUtil.WriteVector(attach.orientation, "|") + "," +
                        KSPUtil.WriteVector(attach.position, "|"));
                }
                node.AddValue("modCost", prefab.GetModuleCosts(prefab.partInfo.cost, ModifierStagingSituation.CURRENT));
                node.AddValue("modMass", prefab.GetModuleMass(prefab.mass, ModifierStagingSituation.CURRENT));
                node.AddValue("modSize", Vector3.zero);
                foreach (var child in result.resolved.Where(p => p.plan.parentId == part.plan.id))
                    node.AddValue("link", reference(child.plan.id));
                foreach (var attach in prefab.attachNodes)
                {
                    string target;
                    string partner = connected.TryGetValue(part.plan.id + ":" + attach.id, out target) ? reference(target) : "Null_0";
                    node.AddValue("attN", attach.id + "," + partner + "_" +
                        KSPUtil.WriteVector(attach.originalPosition, "|") + "_" + KSPUtil.WriteVector(attach.originalOrientation, "|") + "_" +
                        KSPUtil.WriteVector(attach.position, "|") + "_" + KSPUtil.WriteVector(attach.orientation, "|"));
                }
                node.AddNode("EVENTS");
                node.AddNode("ACTIONS");
                node.AddNode("PARTDATA");
                // Native module/resource persistence preserves defaults added by installed mods.
                foreach (PartModule module in prefab.Modules) module.Save(node.AddNode("MODULE"));
                foreach (PartResource resource in prefab.Resources)
                {
                    var resourceNode = node.AddNode("RESOURCE");
                    resource.Save(resourceNode);
                    var replacement = (part.plan.resources ?? new ResourceAmount[0]).FirstOrDefault(r => r.name == resource.resourceName);
                    if (replacement != null) resourceNode.SetValue("amount", replacement.amount.ToString("R", CultureInfo.InvariantCulture), true);
                }
            }
            return craft;
        }

        private static uint NewId()
        {
            uint value = BitConverter.ToUInt32(Guid.NewGuid().ToByteArray(), 0);
            return value == 0 ? 1 : value;
        }

        internal static string SaveNew(ConfigNode config, string folder, string prefix)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".craft");
            // CreateNew avoids replacing any existing craft or backup, even across sessions.
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try
            {
                // ToString() adds an outer anonymous node; .craft requires Save's root format.
                if (!config.Save(path)) throw new IOException("KSP could not save the craft file.");
                return path;
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }
    }
}
