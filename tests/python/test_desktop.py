import io
import json
from pathlib import Path
import tempfile
import unittest
from contextlib import redirect_stdout
from unittest.mock import MagicMock, patch

from ksp_autocraft.desktop import run_job, write_reply
from ksp_autocraft.llm import LLMError


class DesktopTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.path = self.root / "job.json"
        self.client = MagicMock()
        self.client.health.return_value = {"version": "0.3.0", "allowFileGeneration": True}
        self.job = {"task": "health", "expectedVersion": "0.3.0"}

    def run_job(self):
        self.path.write_text(json.dumps(self.job), encoding="utf-8")
        return run_job(self.client, self.path, self.root)

    def test_automatic_health_never_generates_or_builds(self):
        with patch("ksp_autocraft.desktop.GatewayModelClient") as factory, patch("ksp_autocraft.desktop.design") as design:
            result = self.run_job()
        self.assertTrue(result["ok"] and result["apiReady"] and result["modelReady"])
        factory.return_value.check_ready.assert_called_once()
        factory.assert_called_once_with(self.root)
        factory.return_value.generate.assert_not_called()
        design.assert_not_called()
        self.client.build.assert_not_called()

    def test_model_configuration_failure_does_not_hide_api_health(self):
        with patch("ksp_autocraft.desktop.GatewayModelClient", side_effect=ValueError("Configure model in AI Hub")):
            result = self.run_job()
        self.assertTrue(result["apiReady"])
        self.assertFalse(result["modelReady"])
        self.assertIn("Configure model in AI Hub", result["modelStatus"])

    def test_different_plugin_is_rejected_before_model(self):
        self.client.health.return_value = {"version": "0.2.3"}
        with patch("ksp_autocraft.desktop.GatewayModelClient") as factory:
            with self.assertRaises(ValueError):
                self.run_job()
        factory.assert_not_called()

    def test_design_writes_artifacts_but_worker_does_not_build(self):
        self.job.update(task="design", prompt="为合同设计火箭", contractId="11111111-1111-1111-1111-111111111111",
                        budget=80000, maxMass=30, output=str(self.root / "result.json"))
        plan = {"name": "test", "facility": "VAB", "parts": [{"id": "root", "partName": "pod", "stage": 0}]}
        report = {"assessment": {"status": "flight_required"}, "validation": {"wetMassTonnes": 2, "estimatedCost": 100},
                  "performance":{"status":"passed","metrics":{"launchTwr":1.5,"deltaVVacuumWithReserve":4000,"requiredDeltaV":3500}}, "vehicleType":"rocket",
                  "missionSteps": ["进入轨道"], "assumptions": ["尚未飞行验证"]}
        with patch("ksp_autocraft.desktop.GatewayModelClient"), patch("ksp_autocraft.desktop.design", return_value=(plan, report)) as designer:
            result = self.run_job()
        self.assertTrue(result["ok"])
        self.assertEqual(json.loads((self.root / "result.json").read_text()), plan)
        self.assertEqual(result["missionSteps"], ["进入轨道"])
        self.assertTrue((self.root / "result.report.json").exists())
        self.assertEqual(designer.call_args.kwargs["budget"], 80000)
        self.client.build.assert_not_called()

    def test_existing_output_is_rejected_without_model_request(self):
        output = self.root / "existing.json"
        output.write_text("original")
        self.job.update(task="design", prompt="rocket", output=str(output))
        with patch("ksp_autocraft.desktop.GatewayModelClient") as factory, self.assertRaises(ValueError):
            self.run_job()
        factory.assert_not_called()
        self.assertEqual(output.read_text(), "original")

    def test_bad_job_cannot_inject_commands(self):
        self.job["command"] = "arbitrary shell command"
        with self.assertRaises(ValueError):
            self.run_job()
        self.client.health.assert_not_called()

    def test_job_cannot_override_model_configuration(self):
        self.job['modelConfig'] = 'legacy-direct.json'
        with self.assertRaises(ValueError): self.run_job()
        self.client.health.assert_not_called()

    def test_failed_design_writes_only_safe_diagnostic_metadata(self):
        (self.root / 'GameData/KSPAutoCraft').mkdir(parents=True)
        self.job.update(task='design', prompt='PRIVATE PROMPT', output=str(self.root / 'failed.json'))
        model = MagicMock()
        model._token = 'private-ipc-token'
        model.events = [{'attempt': 1, 'code': 'model_repetition', 'requestId': 'a' * 32,
                         'finishReason': 'repetition_truncation', 'rawOutput': 'PRIVATE OUTPUT'}]
        error = LLMError('PRIVATE ERROR TEXT', code='model_repetition', details={'finishReason': 'repetition_truncation', 'debug': 'PRIVATE SECRET'})
        with patch('ksp_autocraft.desktop.GatewayModelClient', return_value=model), patch('ksp_autocraft.desktop.design', side_effect=error):
            with self.assertRaises(LLMError) as raised: self.run_job()
        path = Path(raised.exception.diagnostic_file)
        text = path.read_text()
        for secret in ('PRIVATE PROMPT', 'PRIVATE OUTPUT', 'PRIVATE ERROR TEXT', 'PRIVATE SECRET', model._token): self.assertNotIn(secret, text)
        record = json.loads(text)
        self.assertEqual(record['modelRequests'][0]['requestId'], 'a' * 32)
        self.assertFalse((self.root / 'failed.json').exists())
        self.client.build.assert_not_called()

    def test_diagnostic_write_failure_preserves_original_model_error(self):
        (self.root / 'GameData/KSPAutoCraft').mkdir(parents=True)
        self.job.update(task='design', prompt='rocket', output=str(self.root / 'failed.json'))
        error = LLMError('original', code='model_repetition')
        with patch('ksp_autocraft.desktop.GatewayModelClient'), patch('ksp_autocraft.desktop.design', side_effect=error), patch('ksp_autocraft.desktop.tempfile.NamedTemporaryFile', side_effect=OSError('disk full')):
            with self.assertRaises(LLMError) as raised: self.run_job()
        self.assertIs(raised.exception, error)
        self.assertEqual(raised.exception.diagnostic_file, '')

    def test_large_job_and_wrong_text_types_are_rejected(self):
        self.path.write_bytes(b"x" * 65537)
        with self.assertRaises(ValueError):
            run_job(self.client, self.path)
        self.job["prompt"] = {"not": "text"}
        with self.assertRaises(ValueError):
            self.run_job()

    def test_reply_is_one_line_unicode_json(self):
        stream = io.StringIO()
        with redirect_stdout(stream):
            write_reply({"ok": True, "missionSteps": ["进入轨道\n再测试"]})
        self.assertEqual(len(stream.getvalue().splitlines()), 1)
        self.assertEqual(json.loads(stream.getvalue())["missionSteps"], ["进入轨道\n再测试"])


if __name__ == "__main__":
    unittest.main()
