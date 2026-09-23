import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from ksp_autocraft.client import APIError
from ksp_autocraft.contracts import assess_contract
from ksp_autocraft.designer import ContextChangedError, DesignError, all_parts, design, select_parts, write_design, compact_catalog, compact_part, correction_messages
from ksp_autocraft.llm import LLMError


CATALOG = [
    {"name": "pod", "title": "Pod", "category": "Pods", "unlocked": True, "stackAttach": True,
     "crewCapacity": 2, "dryMassTonnes": 1, "defaultCost": 100, "moduleNames": ["ModuleCommand"],
     "nodes": [{"id": "bottom", "size": 1}], "resources": [{"name": "Fuel", "amount": 10, "maxAmount": 20}]},
    {"name": "testEngine", "category": "Engine", "unlocked": True, "stackAttach": True, "crewCapacity": 0,
     "moduleNames": ["ModuleEngines"], "nodes": [{"id": "top", "size": 1}], "resources": [], "defaultCost": 50},
    {"name": "antenna", "category": "Communication", "unlocked": True, "surfaceAttach": True, "crewCapacity": 0,
     "moduleNames": ["ModuleDataTransmitter"], "nodes": [], "resources": [], "defaultCost": 20},
]
PLAN = {"name": "Test", "facility": "VAB", "parts": [{"id": "root", "partName": "pod", "stage": -1}]}


def requirement(kind, **values):
    return {"id": "0", "kind": kind, "title": "condition", "state": "Incomplete", "children": [], **values}


def contract(*requirements):
    return {"contract": {"id": "11111111-1111-1111-1111-111111111111", "state": "Active", "title": "Mission"},
            "saveName": "test-save", "gameMode": "CAREER", "requirements": list(requirements)}


class FakeGame:
    def __init__(self, detail=None):
        self.detail = copy.deepcopy(detail)
        self.catalog_data = copy.deepcopy(CATALOG)
        self.validated = []
        self.world_reads = 0
        self.changed_save = False
        self.changed_contract = False
        self.reject_first = False

    def health(self):
        return {"facility": "VAB", "capabilities": ["contracts", "world-context", "surface-plan", "nested-json", "surface-geometry", "native-surface-rotation", "flight-catalog", "resolved-placements", "aircraft-geometry"]}

    def world(self):
        self.world_reads += 1
        return {"saveName": "changed" if self.changed_save and self.world_reads > 1 else "test-save",
                "gameMode": "CAREER", "hasFunds": True, "funds": 500, "homeBody": "Gael",
                "bodies": [{"name":"Gael", "radiusMetres":600000, "gravitationalParameter":3530394000000,
                            "atmosphere":True, "atmosphereDepthMetres":70000}]}

    def environment(self, body, altitude=0):
        return {"body":body,"altitude":altitude,"pressureKpa":101.325,"densityKgPerCubicMetre":1.225,"speedOfSound":340,"gravity":9.80665,"oxygen":True}

    def catalog(self, offset=0, limit=200):
        return {"total": len(self.catalog_data), "offset": offset, "parts": self.catalog_data[offset:offset+limit]}

    def contract(self, identifier):
        result = copy.deepcopy(self.detail)
        if self.changed_contract and self.validated:
            result["contract"]["state"] = "Completed"
        return result

    def validate(self, plan):
        self.validated.append(copy.deepcopy(plan))
        if self.reject_first and len(self.validated) == 1:
            raise APIError(422, "invalid_plan", "Attachment node already occupied")
        costs = {p["name"]: p.get("defaultCost", 0) for p in self.catalog_data}
        return {"valid": True, "partCount": len(plan["parts"]), "wetMassTonnes": len(plan["parts"]),
                "estimatedCost": sum(costs[p["partName"]] for p in plan["parts"]), "warnings": []}

    def build(self, plan):
        raise AssertionError("Designer must not automatically build")


class FakeModel:
    model = "test-model"
    max_catalog_parts = 120

    def __init__(self, *plans):
        self.plans = list(plans) or [PLAN]
        self.calls = []

    def generate(self, messages):
        self.calls.append(copy.deepcopy(messages))
        value = self.plans[min(len(self.calls)-1, len(self.plans)-1)]
        return {"plan": copy.deepcopy(value), "missionSteps": ["Execute the flight condition"],
                 "assumptions": ["Flight is not simulated"], "rationale": "Catalog-grounded fixture"}


class FaultyModel(FakeModel):
    def __init__(self, *faults):
        super().__init__()
        self.faults, self.recovery_flags = list(faults), []

    def generate(self, messages, *, recovery=False, recovery_reasons=None):
        self.recovery_flags.append(recovery)
        fault = self.faults.pop(0) if self.faults else None
        if fault:
            self.calls.append(copy.deepcopy(messages))
            raise LLMError("fixture failure", code=fault, details={"outputTokens": 6000, "recoveryAllowed": fault == "model_repetition" or fault in ("output_truncated", "invalid_json_output", "empty_model_output")})
        return super().generate(messages)


class ContractAssessmentTests(unittest.TestCase):
    def assess(self, *requirements, plan=None):
        return assess_contract(plan or PLAN, CATALOG, contract(*requirements))

    def test_crew_seats_are_a_prerequisite_not_completion(self):
        report = self.assess(requirement("crew_capacity", minimumCrewCapacity=2))
        self.assertEqual(report["status"], "flight_required")
        self.assertFalse(report["completionVerified"])
        self.assertEqual(report["crewCapacity"], 2)

    def test_insufficient_crew_capacity_blocks(self):
        self.assertTrue(self.assess(requirement("crew_capacity", minimumCrewCapacity=3))["hasBlockingFailure"])

    def test_missing_test_part_blocks(self):
        self.assertTrue(self.assess(requirement("part_test", requiredPart="testEngine"))["hasBlockingFailure"])

    def test_present_test_part_still_needs_flight(self):
        plan = copy.deepcopy(PLAN)
        plan["parts"].append({"id": "engine", "partName": "testEngine", "stage": 1})
        self.assertEqual(self.assess(requirement("part_test", requiredPart="testEngine"), plan=plan)["status"], "flight_required")

    def test_module_missing_vs_unknown_interface(self):
        self.assertEqual(self.assess(requirement("vessel_systems", requiredModules=["ModuleDataTransmitter"]))["status"], "failed")
        self.assertEqual(self.assess(requirement("vessel_systems", requiredModules=["ModdedInterface"]))["status"], "needs_review")

    def test_resource_capacity_allows_refueling_but_not_missing_tanks(self):
        self.assertEqual(self.assess(requirement("resource_delivery", resourceName="Fuel", minimumResource=15))["status"], "flight_required")
        self.assertEqual(self.assess(requirement("resource_delivery", resourceName="Fuel", minimumResource=25))["status"], "failed")

    def test_resource_override_does_not_change_capacity(self):
        plan = copy.deepcopy(PLAN)
        plan["parts"][0]["resources"] = [{"name": "Fuel", "amount": 0}]
        result = self.assess(requirement("resource_delivery", resourceName="Fuel", minimumResource=15), plan=plan)
        self.assertEqual(result["initialResources"]["Fuel"], 0)
        self.assertEqual(result["resourceCapacities"]["Fuel"], 20)

    def test_or_does_not_require_every_branch(self):
        group = requirement("group", logic="any", children=[requirement("part_test", requiredPart="absent"), requirement("flight")])
        self.assertEqual(self.assess(group)["status"], "flight_required")

    def test_or_all_failed_blocks(self):
        group = requirement("group", logic="any", children=[requirement("part_test", requiredPart="absent"), requirement("crew_capacity", minimumCrewCapacity=8)])
        self.assertEqual(self.assess(group)["status"], "failed")

    def test_xor_and_modded_group_remain_reviewable(self):
        for group in (requirement("group", logic="exactlyOne", children=[requirement("flight")]),
                      requirement("unknown", children=[requirement("flight")])):
            self.assertEqual(self.assess(group)["status"], "needs_review")

    def test_optional_failure_does_not_block_required_flight(self):
        self.assertFalse(self.assess(requirement("part_test", requiredPart="absent", optional=True), requirement("flight"))["hasBlockingFailure"])

    def test_complete_group_is_not_reopened_by_unknown_children(self):
        result = self.assess(requirement("group", logic="any", state="Complete", children=[requirement("unknown")]))
        self.assertEqual(result["checks"][0]["status"], "complete")
        self.assertFalse(result["completionVerified"])

    def test_extraction_failure_and_empty_tree_are_not_success(self):
        self.assertEqual(self.assess()["status"], "needs_review")
        self.assertEqual(self.assess(requirement("crew_capacity", minimumCrewCapacity=0, extractionIssue="missing field"))["status"], "needs_review")


class DesignerTests(unittest.TestCase):
    def setUp(self):
        # These tests focus on orchestration/contracts. Numerical physics has its own fixtures.
        mock = patch("ksp_autocraft.designer.assess_performance", return_value={"status":"passed","hasBlockingFailure":False,"metrics":{},"issues":[]})
        self.performance = mock.start(); self.addCleanup(mock.stop)

    def test_truncation_recovers_once_and_budget_stays_boosted_for_structure_correction(self):
        model, game = FaultyModel("output_truncated"), FakeGame()
        game.reject_first = True
        _, report = design(game, model, "rocket")
        self.assertEqual(model.recovery_flags, [False, True, True])
        self.assertEqual(report["attempts"], 3)
        self.assertEqual(report["modelRecoveries"][0]["code"], "output_truncated")
        self.assertIn("COMPLETE compact JSON", model.calls[1][-1]["content"])
        self.assertEqual(len(game.validated), 2)

    def test_json_format_recovery_does_not_increase_budget(self):
        model = FaultyModel("invalid_json_output")
        _, report = design(FakeGame(), model, "rocket")
        self.assertEqual(model.recovery_flags, [False, False])
        self.assertEqual(report["attempts"], 2)

    def test_truncation_then_repetition_recovers_within_three_total_calls(self):
        model, game = FaultyModel('output_truncated', 'model_repetition'), FakeGame()
        _, report = design(game, model, 'rocket')
        self.assertEqual(model.recovery_flags, [False, True, True])
        self.assertEqual([r['code'] for r in report['modelRecoveries']], ['output_truncated', 'model_repetition'])
        self.assertEqual(len(game.validated), 1)
        self.assertEqual(len(model.calls), 3)

    def test_repeated_repetition_and_equivalent_format_errors_do_not_loop(self):
        for errors in (('model_repetition', 'model_repetition'), ('invalid_json_output', 'empty_model_output')):
            model, game = FaultyModel(*errors), FakeGame()
            with self.assertRaises(LLMError): design(game, model, 'rocket')
            self.assertEqual(len(model.calls), 2)
            self.assertFalse(game.validated)

    def test_model_recovery_is_bounded_and_incomplete_output_never_reaches_game(self):
        model, game = FaultyModel("output_truncated", "output_truncated"), FakeGame()
        with self.assertRaises(LLMError): design(game, model, "rocket", attempts=3)
        self.assertEqual(len(model.calls), 2)
        self.assertFalse(game.validated)
        model = FaultyModel("output_truncated")
        with self.assertRaises(LLMError): design(game, model, "rocket", attempts=1)
        self.assertEqual(len(model.calls), 1)

    def test_transport_refusal_and_unfinished_stream_are_never_automatically_replayed(self):
        for code in ("provider_timeout", "provider_connection_error", "model_refusal", "provider_incomplete"):
            model, game = FaultyModel(code), FakeGame()
            with self.assertRaises(LLMError): design(game, model, "rocket")
            self.assertEqual(len(model.calls), 1)
            self.assertFalse(game.validated)

    def test_changed_save_stops_before_model_error_recovery(self):
        model, game = FaultyModel("output_truncated"), FakeGame()
        game.changed_save = True
        with self.assertRaises(ContextChangedError): design(game, model, "rocket")
        self.assertEqual(len(model.calls), 1)

    def test_columnar_catalog_reduces_payload_without_mutating_validation_data(self):
        parts = [dict(copy.deepcopy(CATALOG[0]), name="part" + str(i), dryMassTonnes=1.23456789123) for i in range(120)]
        original = copy.deepcopy(parts)
        table = compact_catalog(parts, None, 0)
        decoded = [dict(zip(table["columns"], row)) for row in table["rows"]]
        self.assertEqual([r["name"] for r in decoded], [p["name"] for p in parts])
        self.assertEqual(decoded[0]["nodes"], parts[0]["nodes"])
        self.assertAlmostEqual(decoded[0]["dryMassTonnes"], parts[0]["dryMassTonnes"], places=5)
        self.assertEqual(parts, original)
        old_size = len(json.dumps([compact_part(p) for p in parts], separators=(",", ":")))
        self.assertLess(len(json.dumps(table, separators=(",", ":"))), old_size * .75)

    def test_correction_context_keeps_only_last_bounded_draft_and_feedback(self):
        base = [{'role': 'system', 'content': 'rules'}, {'role': 'user', 'content': 'immutable targets'}]
        messages = correction_messages(base, 'fix geometry', {'plan': PLAN, 'rationale': 'x' * 5000, 'unwanted_catalog': 'x' * 100000})
        self.assertEqual(len(messages), 4)
        self.assertNotIn('unwanted_catalog', messages[-2]['content'])
        self.assertEqual(json.loads(messages[-2]['content'])['plan'], PLAN)
        huge = correction_messages(base, 'regenerate', {'plan': {'data': 'x' * 100000}})
        self.assertEqual(len(huge), 3)
        self.assertEqual(base[1]['content'], 'immutable targets')

    def test_performance_failures_are_sent_back_and_not_accepted_as_valid_structure(self):
        self.performance.side_effect = [{"status":"failed","hasBlockingFailure":True,"issues":["TWR too low"],"metrics":{}},
                                        {"status":"passed","hasBlockingFailure":False,"issues":[],"metrics":{}}]
        model = FakeModel()
        _, report = design(FakeGame(), model, "orbital rocket")
        self.assertEqual(report["attempts"], 2)
        self.assertIn("TWR too low", model.calls[1][-1]["content"])
        self.assertEqual(report["performance"]["status"], "passed")

    def test_aircraft_request_in_vab_stops_before_model_call(self):
        model = FakeModel()
        with self.assertRaises(DesignError): design(FakeGame(), model, "设计喷气飞机")
        self.assertFalse(model.calls)
    def test_grounded_contract_design_uses_actual_world_and_live_validation(self):
        game = FakeGame(contract(requirement("crew_capacity", minimumCrewCapacity=2)))
        model = FakeModel()
        plan, report = design(game, model, "为该合同设计飞船", contract_id=game.detail["contract"]["id"], budget=200)
        self.assertEqual(plan["maxCost"], 200)
        self.assertEqual(len(game.validated), 1)
        self.assertFalse(report["flightPerformanceSimulated"])
        self.assertEqual(report["assessment"]["status"], "flight_required")
        context = json.loads(model.calls[0][1]["content"])
        self.assertEqual(context["world"]["homeBody"], "Gael")
        self.assertNotIn("saveName", context["world"])
        self.assertNotIn("saveName", context["contract"])

    def test_unknown_model_part_is_never_submitted_to_game(self):
        bad = copy.deepcopy(PLAN)
        bad["parts"][0]["partName"] = "invented"
        game, model = FakeGame(), FakeModel(bad)
        with self.assertRaises(DesignError):
            design(game, model, "make a rocket", attempts=2)
        self.assertEqual(len(model.calls), 2)
        self.assertFalse(game.validated)

    def test_structural_error_is_fed_back_once(self):
        game, model = FakeGame(), FakeModel()
        game.reject_first = True
        _, report = design(game, model, "rocket")
        self.assertEqual(report["attempts"], 2)
        self.assertIn("Attachment node already occupied", model.calls[1][-1]["content"])

    def test_static_contract_failure_is_repaired(self):
        game = FakeGame(contract(requirement("part_test", requiredPart="testEngine")))
        fixed = copy.deepcopy(PLAN)
        fixed["parts"].append({"id": "engine", "partName": "testEngine", "stage": 1})
        _, report = design(game, FakeModel(PLAN, fixed), "test engine", contract_id=game.detail["contract"]["id"])
        self.assertEqual(report["attempts"], 2)
        self.assertFalse(report["assessment"]["hasBlockingFailure"])

    def test_zero_budget_is_a_real_bound(self):
        with self.assertRaises(DesignError):
            design(FakeGame(), FakeModel(), "free rocket", budget=0, attempts=1)

    def test_career_funds_cap_model_budget(self):
        plan, _ = design(FakeGame(), FakeModel(), "rocket", budget=10000)
        self.assertEqual(plan["maxCost"], 500)

    def test_changed_context_stops_without_spending_more_model_calls(self):
        for field in ("changed_save", "changed_contract"):
            game, model = FakeGame(contract(requirement("flight"))), FakeModel()
            setattr(game, field, True)
            with self.assertRaises(ContextChangedError):
                design(game, model, "rocket", contract_id=game.detail["contract"]["id"])
            self.assertEqual(len(model.calls), 1)

    def test_unavailable_server_capabilities_fail_before_model_call(self):
        game, model = FakeGame(), FakeModel()
        game.health = lambda: {"capabilities": [], "facility": "VAB"}
        with self.assertRaises(DesignError):
            design(game, model, "rocket")
        self.assertFalse(model.calls)

    def test_locked_parts_are_not_sent_to_model(self):
        locked = {**CATALOG[0], "name": "locked", "unlocked": False}
        selected = select_parts(CATALOG + [locked], None, 20)
        self.assertNotIn("locked", [p["name"] for p in selected])

    def test_large_catalog_response_reduces_page_size_without_losing_rows(self):
        game = FakeGame()
        original = game.catalog
        limits = []
        def catalog(offset=0, limit=200):
            limits.append(limit)
            if limit > 2:
                raise APIError(500, "invalid_response", "Response too large")
            return original(offset, limit)
        game.catalog = catalog
        self.assertEqual([p["name"] for p in all_parts(game)], [p["name"] for p in CATALOG])
        self.assertEqual(limits[0], 50)
        self.assertLessEqual(limits[-1], 2)

    def test_catalog_retains_launch_engines_among_cheap_aircraft_parts(self):
        aircraft = [{"name": f"cheapJet{i}", "category": "Engine", "unlocked": True, "stackAttach": True,
                     "defaultCost": i, "resources": [], "engines": [{"nominalMaxThrustKn": 150, "propellants": ["LiquidFuel", "IntakeAir"]}]} for i in range(150)]
        rocket = {"name": "launchEngine", "category": "Engine", "unlocked": True, "stackAttach": True, "defaultCost": 10000,
                  "resources": [], "engines": [{"nominalMaxThrustKn": 200, "propellants": ["LiquidFuel", "Oxidizer"]}]}
        selected = select_parts(CATALOG + aircraft + [rocket], None, 40)
        self.assertIn("launchEngine", [p["name"] for p in selected])

    def test_output_files_are_separate_and_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "plan.json"
            report_path = write_design(path, PLAN, {"completionVerified": False})
            self.assertEqual(json.loads(path.read_text()), PLAN)
            self.assertFalse(json.loads(report_path.read_text())["completionVerified"])
            with self.assertRaises(ValueError):
                write_design(path, {"changed": True}, {})
            self.assertEqual(json.loads(path.read_text()), PLAN)


if __name__ == "__main__":
    unittest.main()
