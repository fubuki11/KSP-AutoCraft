import io
import json
import math
import os
import tempfile
import threading
import unittest
from contextlib import redirect_stderr, redirect_stdout
from http.client import IncompleteRead
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from urllib.error import URLError
from urllib.parse import parse_qs, urlsplit
from urllib.request import ProxyHandler

from ksp_autocraft import APIError, KSPClient, ProtocolError, TransportError
from ksp_autocraft.__main__ import main

EXAMPLE = Path(__file__).resolve().parents[2] / "examples" / "starter-stack.json"
TOKEN = "unit-test-token"


class _Handler(BaseHTTPRequestHandler):
    def _respond(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        self.server.requests.append((self.command, self.path, self.headers, body))
        status, response, headers = self.server.reply
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(response)))
        for name, value in headers.items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(response)

    do_GET = _respond
    do_POST = _respond

    def log_message(self, format, *args):
        pass


class ClientTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), _Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base_url = f"http://127.0.0.1:{cls.server.server_port}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=5)

    def setUp(self):
        self.server.requests = []
        self.server.reply = (200, b'{"ok": true}', {})
        self.client = KSPClient(TOKEN, base_url=self.base_url, timeout=2)
        self.plan = json.loads(EXAMPLE.read_text(encoding="utf-8"))

    def test_health_url_auth_and_accept_headers(self):
        self.assertEqual(self.client.health(), {"ok": True})
        method, path, headers, body = self.server.requests[0]
        self.assertEqual((method, path, body), ("GET", "/v1/health", b""))
        self.assertEqual(headers["Authorization"], f"Bearer {TOKEN}")
        self.assertEqual(headers["Accept"], "application/json")
        self.assertNotIn(TOKEN, path)

    def test_ship_endpoint(self):
        self.client.ship()
        self.assertEqual(self.server.requests[0][0:2], ("GET", "/v1/ship"))

    def test_contract_and_world_endpoints(self):
        self.server.reply = (200, b'{"bodies":[],"contracts":[],"contract":{},"requirements":[]}', {})
        self.client.world()
        self.assertEqual(self.server.requests[-1][1], "/v1/world")
        self.client.contracts("offered", 2, 10)
        self.assertEqual(parse_qs(urlsplit(self.server.requests[-1][1]).query), {"state": ["offered"], "offset": ["2"], "limit": ["10"]})
        self.client.contract("11111111-1111-1111-1111-111111111111")
        self.assertEqual(self.server.requests[-1][1], "/v1/contracts/11111111-1111-1111-1111-111111111111")

    def test_missing_nested_dto_data_is_a_protocol_error(self):
        for action in (self.client.world, self.client.contracts, lambda: self.client.contract("11111111-1111-1111-1111-111111111111")):
            with self.assertRaises(ProtocolError):
                action()

    def test_invalid_contract_selector_sends_nothing(self):
        for identifier in (None, "../../file", "", "not-a-guid"):
            with self.assertRaises(ValueError):
                self.client.contract(identifier)
        for kwargs in ({"state": "completed"}, {"offset": -1}, {"limit": 101}, {"limit": True}):
            with self.assertRaises(ValueError):
                self.client.contracts(**kwargs)
        self.assertEqual(self.server.requests, [])

    def test_oversized_catalog_uses_a_smaller_page(self):
        page = {"total": 100, "offset": 10, "parts": [{"name": "part"}]}
        with patch.object(self.client, "_request", side_effect=[APIError(500, "invalid_response", "too large"), page]) as request:
            self.assertEqual(self.client.catalog(offset=10, limit=100), page)
        self.assertEqual(request.call_args_list[0].args, ("GET", "/v1/catalog?offset=10&limit=100"))
        self.assertEqual(request.call_args_list[1].args, ("GET", "/v1/catalog?offset=10&limit=50"))

    def test_catalog_defaults_and_response_shape(self):
        expected = {"total": 250, "offset": 0, "parts": [{"name": "fuelTank"}]}
        self.server.reply = (200, json.dumps(expected).encode(), {})
        self.assertEqual(self.client.catalog(), expected)
        self.assertEqual(self.server.requests[0][1], "/v1/catalog?offset=0&limit=100")

    def test_catalog_search_is_quoted(self):
        search = 'tank & engine/+?=# "\u706b"'
        self.client.catalog(offset=10, limit=200, search=search)
        path = self.server.requests[0][1]
        self.assertNotIn(" ", path)
        self.assertIn("%26", path)
        self.assertIn("%2B", path)
        self.assertIn("%23", path)
        self.assertEqual(urlsplit(path).path, "/v1/catalog")
        self.assertEqual(parse_qs(urlsplit(path).query), {"offset": ["10"], "limit": ["200"], "search": [search]})

    def test_invalid_catalog_parameters_do_not_send(self):
        for kwargs in (
            {"offset": -1}, {"offset": True}, {"offset": 0.5}, {"offset": "0"},
            {"limit": 0}, {"limit": 201}, {"limit": True}, {"limit": 1.5},
            {"search": 5},
        ):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                self.client.catalog(**kwargs)
        self.assertEqual(self.server.requests, [])

    def test_validate_and_build_send_identical_unwrapped_plan(self):
        self.plan["parts"][1]["resources"] = [{"name": "LiquidFuel", "amount": 45.5}]
        self.plan["parts"][2]["stage"] = 3
        self.plan["parts"][2]["rollDegrees"] = 90
        validation = {"valid": True, "partCount": 3, "wetMassTonnes": 3.4, "estimatedCost": 2300, "warnings": ["Example only"]}
        self.server.reply = (200, json.dumps(validation).encode(), {})
        self.assertEqual(self.client.validate(self.plan), validation)
        built = dict(validation, craftFile="AutoCraft Starter.craft")
        self.server.reply = (200, json.dumps(built).encode(), {})
        self.assertEqual(self.client.build(self.plan), built)
        for request, endpoint in zip(self.server.requests, ("/v1/validate", "/v1/build")):
            method, path, headers, body = request
            self.assertEqual((method, path), ("POST", endpoint))
            self.assertEqual(headers["Authorization"], f"Bearer {TOKEN}")
            self.assertEqual(headers["Content-Type"], "application/json")
            self.assertEqual(json.loads(body), self.plan)
        self.assertEqual(self.server.requests[0][3], self.server.requests[1][3])

    def test_plan_unicode_survives_utf8(self):
        self.plan["name"] = "Stack \u706b"
        self.client.validate(self.plan)
        self.assertEqual(json.loads(self.server.requests[0][3]), self.plan)

    def test_invalid_plan_is_rejected_before_request(self):
        cyclic = {}
        cyclic["self"] = cyclic
        for plan in (None, [], "plan", {"mass": math.nan}, {"mass": math.inf}, {"value": object()}, cyclic):
            for method in (self.client.validate, self.client.build):
                with self.subTest(plan_type=type(plan), method=method.__name__), self.assertRaises(ValueError):
                    method(plan)
        self.assertEqual(self.server.requests, [])

    def test_api_error_preserves_status_code_and_message(self):
        payload = {"error": {"code": "FILE_GENERATION_DISABLED", "message": "Enable Allow file generation in game."}}
        self.server.reply = (403, json.dumps(payload).encode(), {})
        with self.assertRaises(APIError) as caught:
            self.client.build(self.plan)
        self.assertEqual(caught.exception.status, 403)
        self.assertEqual(caught.exception.code, "FILE_GENERATION_DISABLED")
        self.assertEqual(caught.exception.message, payload["error"]["message"])
        self.assertEqual(len(self.server.requests), 1)

    def test_error_response_does_not_leak_token(self):
        payload = {"error": {"code": TOKEN, "message": f"Rejected Bearer {TOKEN}"}}
        self.server.reply = (401, json.dumps(payload).encode(), {})
        with self.assertRaises(APIError) as caught:
            self.client.health()
        self.assertNotIn(TOKEN, str(caught.exception))
        self.assertNotIn(TOKEN, caught.exception.code)
        self.assertNotIn(TOKEN, caught.exception.message)

    def test_unstructured_http_error_is_safe(self):
        for body in (TOKEN.encode(), b"[]", b'{"error": "wrong shape"}', b'{"error": {"message": 7}}'):
            self.server.reply = (500, body, {})
            with self.subTest(body=body), self.assertRaises(APIError) as caught:
                self.client.health()
            self.assertEqual(caught.exception.status, 500)
            self.assertEqual(caught.exception.code, "HTTP_ERROR")
            self.assertNotIn(TOKEN, str(caught.exception))

    def test_post_errors_are_never_retried(self):
        for endpoint in (self.client.validate, self.client.build):
            for status in (429, 500, 503):
                self.server.requests.clear()
                self.server.reply = (status, b'{"error":{"code":"BUSY","message":"Try later"}}', {"Retry-After": "0"})
                with self.subTest(endpoint=endpoint.__name__, status=status), self.assertRaises(APIError):
                    endpoint(self.plan)
                self.assertEqual(len(self.server.requests), 1)

    def test_redirects_are_not_followed_for_get_or_post(self):
        for method in (self.client.health, lambda: self.client.validate(self.plan), lambda: self.client.build(self.plan)):
            for status in (301, 302, 303, 307, 308):
                self.server.requests.clear()
                self.server.reply = (status, b"{}", {"Location": self.base_url + "/redirected"})
                with self.subTest(status=status), self.assertRaises(APIError) as caught:
                    method()
                self.assertEqual(caught.exception.status, status)
                self.assertEqual(len(self.server.requests), 1)

    def test_no_proxy_handler_and_environment_proxies_are_ignored(self):
        proxy_env = {
            "HTTP_PROXY": "http://127.0.0.1:1", "http_proxy": "http://127.0.0.1:1",
            "HTTPS_PROXY": "http://127.0.0.1:1", "https_proxy": "http://127.0.0.1:1",
            "ALL_PROXY": "http://127.0.0.1:1", "all_proxy": "http://127.0.0.1:1",
            "NO_PROXY": "", "no_proxy": "",
        }
        with patch.dict(os.environ, proxy_env), patch("ksp_autocraft.client.ProxyHandler", wraps=ProxyHandler) as handler:
            client = KSPClient(TOKEN, base_url=self.base_url, timeout=2)
            self.assertEqual(client.health(), {"ok": True})
            handler.assert_called_once_with({})

    def test_timeout_is_passed_to_urllib(self):
        with patch.object(self.client._opener, "open", wraps=self.client._opener.open) as opened:
            self.client.health()
        self.assertEqual(opened.call_args.kwargs["timeout"], 2)

    def test_transport_errors_are_safe_and_not_retried(self):
        for error in (URLError(TOKEN), TimeoutError(TOKEN), ConnectionResetError(TOKEN), IncompleteRead(b"", 10)):
            with patch.object(self.client._opener, "open", side_effect=error) as opened:
                with self.subTest(error=type(error)), self.assertRaises(TransportError) as caught:
                    self.client.build(self.plan)
                self.assertEqual(opened.call_count, 1)
                self.assertNotIn(TOKEN, str(caught.exception))
                self.assertIn("outcome is unknown", str(caught.exception))

    def test_invalid_success_response_is_protocol_error(self):
        for body in (TOKEN.encode(), b"\xff", b"[]", b"null", b"1", b""):
            self.server.reply = (200, body, {})
            with self.subTest(body=body), self.assertRaises(ProtocolError) as caught:
                self.client.health()
            self.assertNotIn(TOKEN, str(caught.exception))

    def test_rejects_remote_or_unsafe_urls(self):
        for url in (
            "http://example.com:18080", "http://192.168.1.1:18080", "http://localhost:18080",
            "http://127.0.0.2:18080", "http://[::1]:18080", "https://127.0.0.1:18080",
            "file:///tmp/settings", "http://127.0.0.1.evil:18080", "http://127.0.0.1@evil:18080",
            "http://user:secret@127.0.0.1:18080", "http://127.0.0.1:18080/path",
            "http://127.0.0.1:18080?x=1", "http://127.0.0.1:18080#x", "http://127.0.0.1:18080?",
            "http://127.0.0.1:0", "http://127.0.0.1:65536", "http://127.0.0.1:bad",
            "http://127.0.0.1:\n18080", " http://127.0.0.1:18080", "http://[", "", None,
        ):
            with self.subTest(url=url), self.assertRaises(ValueError):
                KSPClient(TOKEN, base_url=url)

    def test_default_port_and_trailing_slash(self):
        self.assertEqual(KSPClient(TOKEN).base_url, "http://127.0.0.1:18080")
        self.assertEqual(KSPClient(TOKEN, base_url="http://127.0.0.1/").base_url, "http://127.0.0.1:18080")

    def test_invalid_tokens_and_timeouts(self):
        for token in (None, "", " \n", "two words", "injected\r\nHeader:value", "\u706b", 123):
            with self.subTest(token=token), self.assertRaises(ValueError):
                KSPClient(token)
        for timeout in (0, -1, 60.1, math.nan, math.inf, -math.inf, True, "10", None, 10**1000):
            with self.subTest(timeout=timeout), self.assertRaises(ValueError):
                KSPClient(TOKEN, timeout=timeout)
        self.assertEqual(KSPClient(TOKEN, timeout=60).timeout, 60)

    def test_starter_is_only_an_explicit_structural_stack(self):
        self.assertEqual(self.plan["facility"], "VAB")
        self.assertEqual(self.plan["maxWetMassTonnes"], 0)
        self.assertEqual(self.plan["maxCost"], 0)
        self.assertEqual([part["partName"] for part in self.plan["parts"]], ["mk1pod.v2", "fuelTank", "liquidEngine"])
        self.assertEqual([part["parentId"] for part in self.plan["parts"]], ["", "pod", "tank"])
        self.assertEqual([part["parentNodeId"] for part in self.plan["parts"]], ["", "bottom", "bottom"])
        self.assertEqual([part["childNodeId"] for part in self.plan["parts"]], ["", "top", "top"])
        self.assertTrue(all(part["stage"] == 0 and part["resources"] == [] for part in self.plan["parts"]))


class CLITests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.settings = self.root / "GameData" / "KSPAutoCraft" / "PluginData" / "settings.json"
        self.settings.parent.mkdir(parents=True)
        self.settings.write_text(json.dumps({"token": TOKEN, "port": 18123}), encoding="utf-8")
        self.environment = patch.dict(os.environ, {}, clear=True)
        self.environment.start()
        self.addCleanup(self.environment.stop)

    def invoke(self, *args):
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            status = main(list(args))
        self.assertNotIn("Traceback", stderr.getvalue())
        self.assertNotIn(TOKEN, stdout.getvalue() + stderr.getvalue())
        return status, stdout.getvalue(), stderr.getvalue()

    def test_loads_generated_settings_from_game_root(self):
        with patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
            client.return_value.health.return_value = {"ok": True}
            status, output, error = self.invoke("--ksp-root", str(self.root), "health")
            client.assert_called_once_with(TOKEN, base_url="http://127.0.0.1:18123", timeout=10)
        self.assertEqual((status, error), (0, ""))
        self.assertEqual(json.loads(output), {"ok": True})

    def test_environment_token_and_explicit_port_override_settings(self):
        with patch.dict(os.environ, {"KSP_AUTOCRAFT_TOKEN": "env-token"}), patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
            client.return_value.ship.return_value = {"partCount": 0}
            status, output, error = self.invoke("--ksp-root", str(self.root), "--port", "18456", "--timeout", "1.5", "ship")
            client.assert_called_once_with("env-token", base_url="http://127.0.0.1:18456", timeout=1.5)
        self.assertEqual((status, error), (0, ""))
        self.assertEqual(json.loads(output), {"partCount": 0})

    def test_environment_without_root_uses_default_port(self):
        with patch.dict(os.environ, {"KSP_AUTOCRAFT_TOKEN": TOKEN}), patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
            client.return_value.health.return_value = {"ok": True}
            status, _, error = self.invoke("health")
            client.assert_called_once_with(TOKEN, base_url="http://127.0.0.1:18080", timeout=10)
        self.assertEqual((status, error), (0, ""))

    def test_token_file_precedes_environment_and_settings(self):
        token_file = self.root / "token.txt"
        token_file.write_text(TOKEN, encoding="utf-8")
        with patch.dict(os.environ, {"KSP_AUTOCRAFT_TOKEN": "env-token"}), patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
            client.return_value.health.return_value = {}
            status, _, error = self.invoke("--ksp-root", str(self.root), "--token-file", str(token_file), "health")
            client.assert_called_once_with(TOKEN, base_url="http://127.0.0.1:18123", timeout=10)
        self.assertEqual((status, error), (0, ""))

    def test_missing_token_is_clear_and_does_not_connect(self):
        with patch("ksp_autocraft.__main__.KSPClient") as client:
            status, output, error = self.invoke("health")
            client.assert_not_called()
        self.assertEqual((status, output), (1, ""))
        self.assertIn("No API token", error)
        self.assertIn("--ksp-root", error)

    def test_missing_settings_explains_token_configuration(self):
        self.settings.unlink()
        status, output, error = self.invoke("--ksp-root", str(self.root), "health")
        self.assertEqual((status, output), (1, ""))
        self.assertIn("settings.json", error)

    def test_malformed_settings_are_not_printed(self):
        for value in (TOKEN, "[]", "null", "{broken json"):
            self.settings.write_text(value, encoding="utf-8")
            with self.subTest(value=value):
                status, output, error = self.invoke("--ksp-root", str(self.root), "health")
                self.assertEqual((status, output), (1, ""))
                self.assertIn("Plugin settings", error)

    def test_bad_port_timeout_and_token_fail_without_traceback(self):
        for options in (("--port", "0"), ("--port", "65536"), ("--timeout", "nan"), ("--timeout", "61")):
            with self.subTest(options=options):
                status, output, error = self.invoke("--ksp-root", str(self.root), *options, "health")
                self.assertEqual((status, output), (1, ""))
                self.assertIn("error:", error)
        with patch.dict(os.environ, {"KSP_AUTOCRAFT_TOKEN": ""}):
            status, _, error = self.invoke("--ksp-root", str(self.root), "health")
            self.assertEqual(status, 1)
            self.assertIn("non-empty", error)

    def test_catalog_can_write_output(self):
        destination = self.root / "catalog.json"
        expected = {"total": 1, "offset": 2, "parts": []}
        with patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
            client.return_value.catalog.return_value = expected
            status, output, error = self.invoke(
                "--ksp-root", str(self.root), "catalog", "--offset", "2", "--limit", "200",
                "--search", "tank & pod", "--output", str(destination),
            )
            client.return_value.catalog.assert_called_once_with(offset=2, limit=200, search="tank & pod")
        self.assertEqual((status, output, error), (0, "", ""))
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), expected)

    def test_unwritable_catalog_output_has_clear_error(self):
        with patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
            client.return_value.catalog.return_value = {}
            status, output, error = self.invoke("--ksp-root", str(self.root), "catalog", "--output", str(self.root))
        self.assertEqual((status, output), (1, ""))
        self.assertIn("Cannot write catalog", error)

    def test_validate_and_build_read_unwrapped_plan(self):
        plan = json.loads(EXAMPLE.read_text(encoding="utf-8"))
        for command in ("validate", "build"):
            with self.subTest(command=command), patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
                method = getattr(client.return_value, command)
                method.return_value = {"valid": True}
                status, output, error = self.invoke("--ksp-root", str(self.root), command, str(EXAMPLE))
                method.assert_called_once_with(plan)
                self.assertEqual((status, error), (0, ""))
                self.assertEqual(json.loads(output), {"valid": True})

    def test_bad_plan_file_never_posts(self):
        plan = self.root / "bad.json"
        for content in (None, "[]", TOKEN, "\xff"):
            if content is not None:
                plan.write_bytes(content.encode("latin-1"))
            with self.subTest(content=content), patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
                status, output, error = self.invoke("--ksp-root", str(self.root), "build", str(plan))
                self.assertEqual((status, output), (1, ""))
                self.assertIn("Craft plan", error)
                client.return_value.build.assert_not_called()

    def test_api_and_transport_errors_are_reported_without_traceback(self):
        for failure in (APIError(403, "DISABLED", "Enable file generation."), TransportError("Editor is unavailable."), ProtocolError("Invalid JSON.")):
            with self.subTest(failure=type(failure)), patch("ksp_autocraft.__main__.KSPClient", autospec=True) as client:
                client.return_value.build.side_effect = failure
                status, output, error = self.invoke("--ksp-root", str(self.root), "build", str(EXAMPLE))
                self.assertEqual((status, output), (1, ""))
                self.assertIn(str(failure), error)
                client.return_value.build.assert_called_once()

    def test_physics_is_offline_and_gravity_only_affects_twr(self):
        self.settings.write_text(TOKEN, encoding="utf-8")
        outputs = []
        with patch("ksp_autocraft.__main__.KSPClient") as client:
            for gravity in ("3", "6"):
                status, output, error = self.invoke(
                    "--ksp-root", str(self.root), "physics", "--isp", "300", "--wet", "10",
                    "--dry", "2", "--thrust", "120", "--gravity", gravity,
                )
                self.assertEqual((status, error), (0, ""))
                outputs.append(json.loads(output))
            client.assert_not_called()
        self.assertEqual(outputs[0]["thrustToWeightRatio"], 4)
        self.assertEqual(outputs[1]["thrustToWeightRatio"], 2)
        self.assertEqual(outputs[0]["deltaVMetresPerSecond"], outputs[1]["deltaVMetresPerSecond"])
        self.assertAlmostEqual(outputs[0]["deltaVMetresPerSecond"], 300 * 9.80665 * math.log(5))

    def test_invalid_physics_is_a_cli_error(self):
        status, output, error = self.invoke("physics", "--isp", "nan", "--wet", "10", "--dry", "2", "--thrust", "100")
        self.assertEqual((status, output), (1, ""))
        self.assertIn("Isp", error)


if __name__ == "__main__":
    unittest.main()
