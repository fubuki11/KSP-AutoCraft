using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KSPAutoCraft
{
    internal sealed class GeometryInfo
    {
        internal Vec size, center;
        internal string source;
    }
    internal static class PrefabGeometry
    {
        private static readonly Dictionary<Part, GeometryInfo> Cache = new Dictionary<Part, GeometryInfo>();
        internal static GeometryInfo Get(Part part)
        {
            GeometryInfo value;
            if (Cache.TryGetValue(part, out value)) return value;
            // Part.prefabSize is zero for unloaded prefabs in actual KSP catalogs.
            // Use model meshes, in part-aligned metre coordinates, independent of scene activation.
            var matrix = Matrix4x4.TRS(part.transform.position, part.transform.rotation, Vector3.one).inverse;
            var meshes = part.GetComponentsInChildren<MeshFilter>(true);
            for (int pass = 0; pass < 2; pass++)
            {
                bool found = false;
                Bounds combined = new Bounds();
                foreach (var mesh in meshes)
                {
                    if (mesh.sharedMesh == null || (pass == 0 && !Visible(mesh.transform, part.transform))) continue;
                    var renderer = mesh.GetComponent<Renderer>();
                    if (pass == 0 && renderer != null && !renderer.enabled) continue;
                    var bounds = mesh.sharedMesh.bounds;
                    var transform = matrix * mesh.transform.localToWorldMatrix;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var p = transform.MultiplyPoint3x4(new Vector3(
                            (corner & 1) == 0 ? bounds.min.x : bounds.max.x,
                            (corner & 2) == 0 ? bounds.min.y : bounds.max.y,
                            (corner & 4) == 0 ? bounds.min.z : bounds.max.z));
                        if (!found) { combined = new Bounds(p, Vector3.zero); found = true; }
                        else combined.Encapsulate(p);
                    }
                }
                if (found && Usable(combined.size))
                {
                    value = new GeometryInfo { size = KspAdapter.Vector(combined.size), center = KspAdapter.Vector(combined.center),
                        source = pass == 0 ? "default-mesh-bounds" : "all-mesh-bounds" };
                    Cache[part] = value;
                    return value;
                }
            }
            if (Usable(part.prefabSize))
                value = new GeometryInfo { size = KspAdapter.Vector(part.prefabSize), source = "prefab-size" };
            else
            {
                var nodes = part.attachNodes.Where(n => n.nodeType == AttachNode.NodeType.Stack).ToArray();
                if (nodes.Length >= 2)
                {
                    float low = nodes.Min(n => n.position.y), high = nodes.Max(n => n.position.y);
                    int size = nodes.Max(n => n.size);
                    double diameter = size <= 0 ? 0.625 : 1.25 * size;
                    value = new GeometryInfo { size = new Vec(diameter, high - low, diameter), center = new Vec(0, (high + low) / 2, 0), source = "stack-node-estimate" };
                }
                else value = new GeometryInfo { source = "unavailable" };
            }
            Cache[part] = value;
            return value;
        }
        private static bool Usable(Vector3 size)
        {
            return PlanValidator.Finite(size.x) && PlanValidator.Finite(size.y) && PlanValidator.Finite(size.z) &&
                size.x > 1e-4 && size.y > 1e-4 && size.z > 1e-4 && size.magnitude < 1000;
        }
        private static bool Visible(Transform node, Transform root)
        {
            for (var current = node; current != null && current != root; current = current.parent)
                if (!current.gameObject.activeSelf) return false;
            return true;
        }
    }
}
