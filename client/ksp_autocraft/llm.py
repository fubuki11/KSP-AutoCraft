"""Protocol-neutral errors and JSON helpers. All model access belongs to AI Hub."""
import json
from urllib.request import HTTPRedirectHandler


class LLMError(RuntimeError):
    def __init__(self, message, *, code="model_error", details=None):
        super().__init__(message)
        self.code = code
        self.details = details if isinstance(details, dict) else {}

    @property
    def recoverable(self):
        return self.code in ("output_truncated", "invalid_json_output", "empty_model_output")


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
