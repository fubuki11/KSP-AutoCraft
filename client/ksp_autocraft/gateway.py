"""Consumer-side AI Hub adapter; no provider credentials or provider logic."""
import json
import math
from http.client import HTTPException
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import ProxyHandler, Request, build_opener

from .llm import LLMError, _NoRedirects, strict_json


class GatewayModelClient:
    def __init__(self, ksp_root, *, timeout=630, max_catalog_parts=120):
        if not isinstance(ksp_root, (str, Path)) or not str(ksp_root):
            raise LLMError("Supply --ksp-root so AutoCraft can locate the installed KSP AI Hub mod.")
        root = Path(ksp_root)
        if not root.is_absolute():
            raise LLMError("The KSP installation path must be absolute.")
        hub = root / "GameData/KSPAIHub"
        if not (hub / "Plugins/KSPAIHub.dll").is_file():
            raise LLMError("KSP AI Hub is not installed in this game. Install KSPAIHub 0.3.0+ through CKAN, then restart KSP.")
        try:
            version = strict_json((hub / "KSPAIHub.version").read_text(encoding="utf-8-sig"))["VERSION"]
            parts = tuple(version[name] for name in ("MAJOR", "MINOR", "PATCH"))
            if any(type(p) is not int or p < 0 for p in parts) or parts < (0, 3, 0): raise ValueError
        except (OSError, ValueError, TypeError, KeyError):
            raise LLMError("KSP AI Hub installation is incomplete or older than 0.3.0. Repair/upgrade it through CKAN.") from None
        self.client_id = "KSPAutoCraft"
        self.max_catalog_parts = max_catalog_parts
        self.timeout = timeout
        if type(self.max_catalog_parts) is not int or not 20 <= self.max_catalog_parts <= 300:
            raise ValueError("maxCatalogParts must be 20-300.")
        if isinstance(self.timeout, bool) or not isinstance(self.timeout, (int, float)) or not math.isfinite(self.timeout) or not 1 <= self.timeout <= 630:
            raise ValueError("Gateway timeout must be 1-630 seconds.")
        try:
            connection = strict_json((hub / "PluginData/connection.json").read_text(encoding="utf-8-sig"))
            endpoint = connection["endpoint"]
            if not isinstance(endpoint, str) or any(ord(c) <= 32 for c in endpoint): raise ValueError
            url = urlsplit(endpoint)
            if url.scheme != "http" or url.hostname != "127.0.0.1" or not url.port or url.path not in ("", "/") or url.query or url.fragment or url.username:
                raise ValueError
            self.endpoint = endpoint.rstrip("/")
            self._token = connection["token"]
            if not isinstance(self._token, str) or len(self._token) < 32 or any(not 33 <= ord(c) <= 126 for c in self._token): raise ValueError
        except (OSError, ValueError, KeyError, TypeError):
            raise LLMError("KSP AI Hub is installed but its connection file is unavailable. Open AI Hub and Start service.") from None
        self._opener = build_opener(ProxyHandler({}), _NoRedirects())
        health = self._call("GET", "/v1/health")
        if type(health.get("apiVersion")) is not int or health["apiVersion"] != 1:
            raise LLMError("The running AI Hub service uses an incompatible API. Restart/update AI Hub.")
        self.hub_version = health.get("version", "unknown")
        # Pin this design session's selection. Later UI switches affect future sessions.
        selection = self._call("GET", "/v1/ui?" + urlencode({"clientId": self.client_id}))
        self.profile, self.model = selection.get("selectedProfile"), selection.get("model")
        if (selection.get("routingReady") is not True or not isinstance(self.profile, str) or not self.profile.strip() or
                not isinstance(self.model, str) or not self.model.strip() or self.model == "set-your-model-id"):
            raise LLMError("AI Hub is running but no usable model is selected. Open AI Hub, configure a provider and Apply global model (or the KSPAutoCraft override).")

    def _call(self, method, path, body=None):
        data = None if body is None else json.dumps(body, ensure_ascii=False, allow_nan=False).encode("utf-8")
        request = Request(self.endpoint + path, data=data, method=method, headers={
            "Authorization": "Bearer " + self._token, "Content-Type": "application/json", "Accept": "application/json"})
        try:
            with self._opener.open(request, timeout=min(5, self.timeout) if method == "GET" else self.timeout) as response:
                raw = response.read(4500001)
            if len(raw) > 4500000: raise ValueError
            result = strict_json(raw)
            if not isinstance(result, dict) or result.get("ok") is not True: raise ValueError
            return result
        except HTTPError as error:
            try: value = strict_json(error.read(16000))
            except (ValueError, OSError, HTTPException): value = {}
            finally: error.close()
            code = value.get("code", "gateway_error") if isinstance(value, dict) else "gateway_error"
            message = value.get("message", "AI Hub rejected the request.") if isinstance(value, dict) else "AI Hub rejected the request."
            details = value.get("details", {}) if isinstance(value, dict) else {}
            details = {k: v for k, v in details.items() if k in ("outputLimit", "recoveryLimit", "inputTokens", "outputTokens", "reasoningTokens", "httpStatus") and type(v) is int} if isinstance(details, dict) else {}
            raise LLMError((str(code) + ": " + str(message)).replace(self._token, "[REDACTED]"), code=str(code), details=details) from None
        except (URLError, OSError, HTTPException, ValueError, TypeError):
            raise LLMError("Cannot communicate with the installed AI Hub service. Open AI Hub, Start service, then check again.") from None

    def check_ready(self):
        rows = self._call("GET", "/v1/profiles").get("profiles", [])
        selected = next((p for p in rows if p.get("id") == self.profile and p.get("enabled")), None)
        if not selected or selected.get("authState") not in ("ready", "refresh_needed"):
            raise LLMError("The selected AI Hub profile needs configuration or sign-in. Open the AI Hub panel.")

    def generate(self, messages, *, recovery=False):
        value = self._call("POST", "/v1/generate", {"clientId": self.client_id, "profile": self.profile,
            "model": self.model, "format": "json", "messages": messages, "recovery": recovery})
        try:
            result = strict_json(value["jsonText"])
            if not isinstance(result, dict) or self._token in json.dumps(result, ensure_ascii=False): raise ValueError
            return result
        except (ValueError, KeyError, TypeError):
            raise LLMError("AI Hub did not return a valid JSON design object.") from None
