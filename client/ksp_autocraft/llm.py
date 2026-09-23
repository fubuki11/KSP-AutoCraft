"""Protocol-neutral errors and JSON helpers. All model access belongs to AI Hub."""
import json
import re
from urllib.request import HTTPRedirectHandler


class LLMError(RuntimeError):
    def __init__(self, message, *, code="model_error", details=None):
        super().__init__(message)
        self.code = code
        self.details = details if isinstance(details, dict) else {}

    @property
    def recoverable(self):
        if self.details.get("recoveryAllowed") is False: return False
        return self.code in ("output_truncated", "invalid_json_output", "empty_model_output") or (
            self.code == "model_repetition" and self.details.get("recoveryAllowed") is True)

    @property
    def recovery_strategy(self):
        if self.code == "output_truncated": return "budget"
        if self.code == "model_repetition": return "repetition"
        return "format"


def diagnostic_metadata(value, token=""):
    if not isinstance(value, dict): return {}
    result = {}
    for key in ("outputLimit", "recoveryLimit", "inputTokens", "outputTokens", "reasoningTokens", "httpStatus"):
        if type(value.get(key)) is int: result[key] = value[key]
    for key in ("requestStreaming", "responseStreaming", "recoveryAllowed", "diagnosticSaved", "providerManagedOutput"):
        if type(value.get(key)) is bool: result[key] = value[key]
    for key in ("finishReason", "protocol", "repetitionRecovery", "thinkingMode", "reasoningEffort", "code"):
        item = value.get(key)
        if isinstance(item, str) and re.fullmatch(r"[A-Za-z0-9_:-]{1,64}", item) and not (token and token in item): result[key] = item
    for key, pattern in (("requestId", r"[a-f0-9]{32}"), ("profile", r"[A-Za-z0-9_.-]{1,80}"), ("model", r"[^\x00-\x1f\x7f]{1,200}")):
        item = value.get(key)
        if isinstance(item, str) and re.fullmatch(pattern, item) and not (token and token in item): result[key] = item
    reasons = value.get("recoveryReasons")
    if isinstance(reasons, list) and len(reasons) <= 3 and all(r in ("output_truncated", "invalid_json_output", "empty_model_output", "model_repetition") for r in reasons):
        result["recoveryReasons"] = list(reasons)
    return result


def strict_json(text: str):
    def object_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON key.")
            result[key] = value
        return result

    def invalid_number(value):
        raise ValueError("Non-finite JSON number.")

    result = json.loads(text, object_pairs_hook=object_pairs, parse_constant=invalid_number)
    json.dumps(result, allow_nan=False)
    return result


class _NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None
