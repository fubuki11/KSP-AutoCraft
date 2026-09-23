import json
import io
import os
from pathlib import Path
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from contextlib import redirect_stdout, redirect_stderr
from unittest.mock import patch

from ksp_autocraft.gateway import GatewayModelClient
from ksp_autocraft.llm import LLMError
from ksp_autocraft.__main__ import main, _parser


class Handler(BaseHTTPRequestHandler):
    def respond(self, value):
        self.send_response(self.server.status); self.end_headers(); self.wfile.write(json.dumps(value).encode())
    def do_GET(self):
        self.server.requests.append((self.command, self.path, dict(self.headers), None))
        if self.path == '/v1/health':
            self.respond({"ok": True, "apiVersion": self.server.api_version, "version": "0.4.0", "capabilities": self.server.capabilities})
        elif self.path.startswith('/v1/ui'):
            self.respond({"ok": True, "routingReady": self.server.routing_ready, "selectedProfile": "first", "model": self.server.model})
        else:
            self.respond({"ok": True, "profiles": [{"id": "first", "enabled": True, "model": "model-a", "authState": self.server.auth_state}]})
    def do_POST(self):
        value = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        self.server.requests.append((self.command, self.path, dict(self.headers), value))
        self.respond(self.server.result)
    def log_message(self, *args): pass


class GatewayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True); cls.thread.start()
    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown(); cls.server.server_close(); cls.thread.join()
    def setUp(self):
        directory = tempfile.TemporaryDirectory(); self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.hub = self.root / 'GameData/KSPAIHub'
        (self.hub / 'Plugins').mkdir(parents=True)
        (self.hub / 'PluginData').mkdir()
        (self.hub / 'Plugins/KSPAIHub.dll').write_bytes(b'installed Hub fixture')
        (self.hub / 'KSPAIHub.version').write_text(json.dumps({'VERSION': {'MAJOR': 0, 'MINOR': 4, 'PATCH': 0}}))
        self.path = self.hub / 'PluginData/connection.json'
        self.token = 'gateway-test-token-at-least-32-characters'
        self.path.write_text(json.dumps({'endpoint': f'http://127.0.0.1:{self.server.server_port}', 'token': self.token}))
        self.server.status = 200; self.server.requests = []; self.server.model = 'model-a'; self.server.auth_state = 'ready'
        self.server.api_version = 1; self.server.routing_ready = True
        self.server.capabilities = ['recovery-reasons', 'generation-diagnostics']
        self.server.result = {'ok': True, 'jsonText': '{"plan":{}}'}
    def test_auto_discovery_ignores_legacy_provider_environment(self):
        with patch.dict(os.environ, {'AUTOCRAFT_LLM_BASE_URL': 'https://unrelated.example/v1', 'AUTOCRAFT_LLM_MODEL': 'independent-model', 'AUTOCRAFT_LLM_API_KEY': 'must-not-be-sent'}):
            client = GatewayModelClient(self.root)
            client.generate([{'role': 'user', 'content': 'design'}])
        self.assertEqual(client.model, 'model-a')
        self.assertNotIn('must-not-be-sent', json.dumps(self.server.requests))
        self.assertEqual(self.server.requests[1][1], '/v1/ui?clientId=KSPAutoCraft')
    def test_generation_pins_selection_and_returns_json(self):
        client = GatewayModelClient(self.root)
        self.server.model = 'model-b'
        self.assertEqual(client.generate([{'role': 'user', 'content': 'design'}]), {'plan': {}})
        _, path, headers, body = self.server.requests[-1]
        self.assertEqual(path, '/v1/generate')
        self.assertEqual(body['model'], 'model-a')
        self.assertEqual(headers['Authorization'], 'Bearer ' + self.token)
        self.assertNotIn('credential', body)
    def test_readiness_has_no_inference(self):
        client = GatewayModelClient(self.root); client.check_ready()
        self.assertTrue(all(request[0] == 'GET' for request in self.server.requests))
        self.server.auth_state = 'login_required'
        with self.assertRaises(LLMError): client.check_ready()
    def test_non_loopback_connection_is_rejected(self):
        self.path.write_text(json.dumps({'endpoint': 'https://untrusted.example', 'token': self.token}))
        with self.assertRaises(LLMError): GatewayModelClient(self.root)
        self.assertFalse(self.server.requests)
    def test_invalid_gateway_json_and_errors_do_not_leak_ipc_token(self):
        client = GatewayModelClient(self.root)
        self.server.result = {'ok': True, 'jsonText': '[]'}
        with self.assertRaises(LLMError): client.generate([{'role': 'user', 'content': 'test'}])
        self.server.status = 503
        self.server.result = {'ok': False, 'code': 'auth_missing', 'message': self.token}
        with self.assertRaises(LLMError) as error: client.generate([{'role': 'user', 'content': 'test'}])
        self.assertNotIn(self.token, str(error.exception))

    def test_hub_dll_missing_is_detected_before_any_network_call(self):
        (self.hub / 'Plugins/KSPAIHub.dll').unlink()
        with self.assertRaisesRegex(LLMError, 'not installed'): GatewayModelClient(self.root)
        self.assertFalse(self.server.requests)

    def test_hub_error_details_are_safe_and_recovery_budget_is_explicit(self):
        client = GatewayModelClient(self.root)
        self.server.status = 502
        self.server.result = {'ok': False, 'code': 'output_truncated', 'message': self.token,
                              'details': {'outputLimit': 6000, 'reasoningTokens': 6000, 'debug': self.token}}
        with self.assertRaises(LLMError) as error: client.generate([{'role': 'user', 'content': 'test'}], recovery=True)
        self.assertTrue(error.exception.recoverable)
        self.assertEqual(error.exception.details, {'outputLimit': 6000, 'reasoningTokens': 6000})
        self.assertNotIn(self.token, str(error.exception))
        self.assertTrue(self.server.requests[-1][3]['recovery'])

    def test_repetition_marker_request_id_and_transport_flags_survive_gateway(self):
        client = GatewayModelClient(self.root)
        request_id = 'a' * 32
        self.server.status = 502
        self.server.result = {'ok': False, 'code': 'model_repetition', 'message': 'repetitive generation', 'requestId': request_id,
                              'details': {'finishReason': 'repetition_truncation', 'recoveryAllowed': True,
                                          'requestStreaming': False, 'responseStreaming': False, 'rawOutput': self.token}}
        with self.assertRaises(LLMError) as error:
            client.generate([{'role': 'user', 'content': 'test'}], recovery_reasons=['model_repetition'])
        self.assertTrue(error.exception.recoverable)
        self.assertEqual(error.exception.details['finishReason'], 'repetition_truncation')
        self.assertEqual(error.exception.details['requestId'], request_id)
        self.assertFalse(error.exception.details['responseStreaming'])
        self.assertNotIn('rawOutput', error.exception.details)
        self.assertEqual(client.events[-1]['requestId'], request_id)
        self.assertEqual(self.server.requests[-1][3]['recoveryReasons'], ['model_repetition'])

    def test_old_running_hub_is_rejected_even_if_new_files_are_installed(self):
        self.server.capabilities = []
        with self.assertRaisesRegex(LLMError, 'Restart/update'): GatewayModelClient(self.root)

    def test_old_or_incomplete_installation_is_rejected(self):
        version = self.hub / 'KSPAIHub.version'
        for text in ('{}', '{"VERSION":{"MAJOR":0,"MINOR":1,"PATCH":2}}', '{"VERSION":{"MAJOR":false,"MINOR":2,"PATCH":0}}'):
            version.write_text(text)
            with self.assertRaisesRegex(LLMError, 'incomplete or older'): GatewayModelClient(self.root)
        self.assertFalse(self.server.requests)

    def test_unstarted_hub_has_actionable_connection_message(self):
        self.path.unlink()
        with self.assertRaisesRegex(LLMError, 'Start service'): GatewayModelClient(self.root)
        self.path.write_text(json.dumps({'endpoint': 'http://127.0.0.1:1', 'token': self.token}))
        with self.assertRaisesRegex(LLMError, 'Start service'): GatewayModelClient(self.root)

    def test_incompatible_service_api_is_rejected_before_routing(self):
        self.server.api_version = 2
        with self.assertRaisesRegex(LLMError, 'incompatible API'): GatewayModelClient(self.root)
        self.assertEqual(len(self.server.requests), 1)

    def test_placeholder_or_unconfigured_route_requires_hub_selection(self):
        for model in ('set-your-model-id', '', None):
            self.server.model = model
            with self.assertRaisesRegex(LLMError, 'Apply global model'): GatewayModelClient(self.root)
        self.server.model = 'model-a'; self.server.routing_ready = False
        with self.assertRaisesRegex(LLMError, 'Apply global model'): GatewayModelClient(self.root)

    def test_connection_from_another_installation_is_not_used(self):
        other = self.root / 'other-game'; other.mkdir()
        with self.assertRaisesRegex(LLMError, 'not installed'): GatewayModelClient(other)
        with self.assertRaises(LLMError): GatewayModelClient({'connectionFile': str(self.path)})
        self.assertFalse(self.server.requests)

    def test_hub_health_cli_does_not_require_editor_token_or_inference(self):
        stream = io.StringIO()
        with redirect_stdout(stream), patch('ksp_autocraft.__main__._client_from_args') as editor:
            code = main(['--ksp-root', str(self.root), 'hub-health'])
        self.assertEqual(code, 0)
        self.assertEqual(json.loads(stream.getvalue())['model'], 'model-a')
        editor.assert_not_called()
        self.assertTrue(all(r[0] == 'GET' for r in self.server.requests))

    def test_legacy_cli_model_configuration_option_is_removed(self):
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            _parser().parse_args(['design', 'test', '--llm-config', 'old.json', '--output', 'plan.json'])


if __name__ == '__main__': unittest.main()
