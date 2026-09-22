"""Conservative contract prerequisite assessment. Never reports completion from a design."""
import math


def walk_requirements(requirements):
    for requirement in requirements:
        yield requirement
        yield from walk_requirements(requirement.get("children", []))


def assess_contract(plan: dict, catalog: list[dict], detail: dict | None) -> dict:
    definitions = {part["name"]: part for part in catalog}
    parts = [definitions[part["partName"]] for part in plan["parts"]]
    names = {part["name"] for part in parts}
    modules = {name for part in parts for name in part.get("moduleNames", [])}
    known_modules = {name for part in catalog for name in part.get("moduleNames", [])}
    capacity = sum(part.get("crewCapacity", 0) for part in parts)
    resources, capacities = {}, {}
    for item, definition in zip(plan["parts"], parts):
        amounts = {r["name"]: r["amount"] for r in item.get("resources", [])}
        for resource in definition.get("resources", []):
            resources[resource["name"]] = resources.get(resource["name"], 0) + amounts.get(resource["name"], resource["amount"])
            capacities[resource["name"]] = capacities.get(resource["name"], 0) + resource.get("maxAmount", resource["amount"])

    def combine(children, logic):
        states = [c["status"] for c in children if not c.get("optional")]
        if not states:
            return "needs_review"
        if logic == "any":
            if any(s in ("complete", "prerequisites_met") for s in states):
                return "prerequisites_met"
            if any(s == "flight_required" for s in states):
                return "flight_required"
            return "failed" if all(s == "failed" for s in states) else "needs_review"
        if logic == "exactlyOne":
            return "failed" if all(s == "failed" for s in states) else "needs_review"
        if "failed" in states:
            return "failed"
        if "needs_review" in states:
            return "needs_review"
        if "flight_required" in states:
            return "flight_required"
        return "prerequisites_met"

    def assess(requirement):
        kind = requirement.get("kind", "unknown")
        status, reason = "needs_review", "This condition has no complete deterministic design-time checker."
        if requirement.get("state") == "Complete":
            status, reason = "complete", "The game currently reports this parameter complete."
        elif requirement.get("extractionIssue"):
            reason = "Parameter extraction was incomplete; inspect the game contract."
        elif kind == "crew_capacity":
            minimum = requirement.get("minimumCrewCapacity", -1)
            if type(minimum) is int and minimum >= 0:
                status = "flight_required" if capacity >= minimum else "failed"
                reason = f"Design has {capacity} seats; needs at least {minimum}. Crew assignment and target-vessel conditions remain in-flight checks."
        elif kind == "part_test":
            name = requirement.get("requiredPart")
            if name:
                status = "flight_required" if name in names else "failed"
                reason = f"Required test part: {name}. " + ("Present; execute the test/haul action within all flight envelopes." if name in names else "Missing from the design.")
        elif kind == "vessel_systems":
            required = requirement.get("requiredModules", [])
            unknown = sorted(set(required) - known_modules)
            missing = sorted((set(required) & known_modules) - modules)
            status = "failed" if missing else "flight_required"
            reason = "Missing required modules: " + ", ".join(missing) if missing else "Required module names are present; verify deployment, operation, new-vessel and crew-state requirements in flight."
            if unknown and not missing:
                status, reason = "needs_review", "Unrecognized module/interface requirements: " + ", ".join(unknown)
            if requirement.get("crewMode") == "MANNED" and capacity < 1:
                status, reason = "failed", "Manned vessel requirement has no crew seat."
        elif kind == "resource_delivery":
            resource, minimum = requirement.get("resourceName"), requirement.get("minimumResource", -1)
            if resource and isinstance(minimum, (int, float)) and not isinstance(minimum, bool) and math.isfinite(minimum) and minimum >= 0:
                amount = resources.get(resource, 0)
                resource_capacity = capacities.get(resource, 0)
                status = "flight_required" if resource_capacity >= minimum else "failed"
                reason = f"Initial {resource}: {amount:g}; capacity: {resource_capacity:g}; required: {minimum:g}. Resource may need production/refueling; consumption, target and transfers remain in-flight checks."
        elif kind == "flight":
            status, reason = "flight_required", "Requires gameplay execution: use the target body, orbit/envelope, named kerbal/vessel and action from the contract."
        children = [assess(child) for child in requirement.get("children", [])]
        if kind == "group" and not requirement.get("extractionIssue") and status != "complete":
            status, reason = combine(children, requirement.get("logic", "all")), "Contract group logic: " + requirement.get("logic", "all")
        elif children and status != "complete":
            status = combine([{"status": status}] + children, "all")
        return {"id": requirement.get("id"), "title": requirement.get("title"), "kind": kind,
                "optional": bool(requirement.get("optional")), "status": status, "reason": reason, "children": children}

    checks = [assess(r) for r in detail.get("requirements", [])] if detail else []
    status = combine(checks, "all") if detail else "no_contract"
    return {"contractId": detail["contract"]["id"] if detail else None, "status": status,
            "hasBlockingFailure": status == "failed", "completionVerified": False, "checks": checks,
            "crewCapacity": sum(part.get("crewCapacity", 0) for part in parts), "initialResources": resources, "resourceCapacities": capacities,
            "notes": ["This checks design prerequisites, not flight performance or contract completion.",
                      "OR/XOR branches, modded predicates and existing-vessel requirements must follow the actual contract tree."]}
