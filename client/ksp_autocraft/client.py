"""Authenticated loopback HTTP, no proxies or POST retries, bounded catalog pages."""

import json
import math
from uuid import UUID
from http.client import HTTPException
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlencode, urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

DEFAULT_PORT = 18080
DEFAULT_TIMEOUT = 10.0
MAX_TIMEOUT = 60.0


class APIError(RuntimeError):
    """An HTTP error returned by the editor, with its status and API code."""

    def __init__(self, status: int, code: str, message: str):
        self.status = status
        self.code = code
        self.message = message
        super().__init__(f"HTTP {status} ({code}): {message}")


class TransportError(RuntimeError):
    """The editor could not be reached or the response was interrupted."""


class ProtocolError(RuntimeError):
    """A successful HTTP response was not a JSON object."""


class _NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Never forward credentials or replay a build through a redirect.
        return None


class KSPClient:
    """Access the VAB/SPH API. The timeout must be in (0, 60] seconds.

    Only literal IPv4 loopback HTTP URLs are accepted. An omitted port uses
    18080. POST requests are never retried. Oversized catalog pages can be
    requested again with a smaller item limit.
    """

    def __init__(
        self,
        token: str,
        *,
        base_url: str = "http://127.0.0.1:18080",
        timeout: float = DEFAULT_TIMEOUT,
    ):
        if not isinstance(token, str) or not token.strip():
            raise ValueError("A non-empty API token is required.")
        token = token.strip()
        if any(not 33 <= ord(char) <= 126 for char in token):
            raise ValueError("The API token must contain only visible ASCII without spaces.")
        try:
            if not isinstance(base_url, str) or any(
                ord(char) <= 32 or ord(char) == 127 for char in base_url
            ):
                raise ValueError
            parsed = urlsplit(base_url)
            port = parsed.port if parsed.port is not None else DEFAULT_PORT
            if (
                parsed.scheme != "http"
                or parsed.hostname != "127.0.0.1"
                or parsed.username is not None
                or parsed.password is not None
                or parsed.path not in ("", "/")
                or "?" in base_url
                or "#" in base_url
                or not 1 <= port <= 65535
            ):
                raise ValueError
        except ValueError:
            raise ValueError("API URL must be http://127.0.0.1:PORT with no credentials or path.") from None
        try:
            valid_timeout = (
                not isinstance(timeout, bool)
                and isinstance(timeout, (int, float))
                and math.isfinite(timeout)
                and 0 < timeout <= MAX_TIMEOUT
            )
        except OverflowError:
            valid_timeout = False
        if not valid_timeout:
            raise ValueError("HTTP timeout must be finite and greater than 0, at most 60 seconds.")
        self.base_url = f"http://127.0.0.1:{port}"
        self.timeout = float(timeout)
        self._token = token
        self._opener = build_opener(ProxyHandler({}), _NoRedirects())

    def health(self) -> dict:
        return self._request("GET", "/v1/health")

    def catalog(self, offset: int = 0, limit: int = 100, search: str | None = None) -> dict:
        """Return one catalog page, preserving the server's total and offset."""
        if isinstance(offset, bool) or not isinstance(offset, int) or offset < 0:
            raise ValueError("Catalog offset must be a non-negative integer.")
        if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 200:
            raise ValueError("Catalog limit must be an integer from 1 to 200.")
        params = {"offset": offset, "limit": limit}
        if search is not None:
            if not isinstance(search, str):
                raise ValueError("Catalog search must be a string.")
            params["search"] = search
        while True:
            try:
                return self._request("GET", "/v1/catalog?" + urlencode(params, quote_via=quote))
            except APIError as error:
                if error.code != "invalid_response" or params["limit"] <= 1:
                    raise
                params["limit"] = max(1, params["limit"] // 2)

    def ship(self) -> dict:
        return self._request("GET", "/v1/ship")

    def world(self) -> dict:
        result = self._request("GET", "/v1/world")
        if not isinstance(result.get("bodies"), list):
            raise ProtocolError("World response is missing body data. Upgrade the plugin to 0.2.1 and restart KSP.")
        return result

    def environment(self, body: str, altitude: float = 0) -> dict:
        if not isinstance(body, str) or not body or isinstance(altitude, bool) or not isinstance(altitude, (int, float)) or not math.isfinite(altitude) or not 0 <= altitude <= 1e9:
            raise ValueError("Environment requires a body name and a finite nonnegative altitude.")
        return self._request("GET", "/v1/environment?" + urlencode({"body": body, "altitude": altitude}, quote_via=quote))

    def contracts(self, state: str = "active", offset: int = 0, limit: int = 50) -> dict:
        if state not in ("active", "offered", "all"):
            raise ValueError("Contract state must be active, offered or all.")
        if type(offset) is not int or offset < 0 or type(limit) is not int or not 1 <= limit <= 100:
            raise ValueError("Invalid contract pagination.")
        result = self._request("GET", "/v1/contracts?" + urlencode({"state": state, "offset": offset, "limit": limit}))
        if not isinstance(result.get("contracts"), list):
            raise ProtocolError("Contract response is missing its list. Upgrade the plugin to 0.2.1 and restart KSP.")
        return result

    def contract(self, identifier: str) -> dict:
        try:
            identifier = str(UUID(identifier))
        except (ValueError, TypeError, AttributeError):
            raise ValueError("Contract identifier must be a GUID from the contracts command.") from None
        result = self._request("GET", "/v1/contracts/" + identifier)
        if not isinstance(result.get("contract"), dict) or not isinstance(result.get("requirements"), list):
            raise ProtocolError("Contract detail is missing its requirement tree. Upgrade the plugin to 0.2.1 and restart KSP.")
        return result

    def validate(self, plan: dict) -> dict:
        """Submit the unwrapped plan unchanged; staging is explicitly supplied."""
        return self._request("POST", "/v1/validate", plan)

    def build(self, plan: dict) -> dict:
        """Save a candidate .craft only; no loading, launching, or deleting.

        Requires the in-game 'Allow file generation' switch. The response's
        craftFile is a basename, not a client-side path to open or execute.
        A transport failure leaves the build outcome unknown: do not retry
        automatically. Inspect the game before deciding whether to resubmit.
        """
        return self._request("POST", "/v1/build", plan)

    def _request(self, method: str, path: str, plan: dict | None = None) -> dict:
        headers = {"Accept": "application/json", "Authorization": f"Bearer {self._token}"}
        data = None
        if method == "POST":
            if not isinstance(plan, dict):
                raise ValueError("Craft plan must be a JSON object, without a wrapper.")
            try:
                data = json.dumps(plan, allow_nan=False, ensure_ascii=False).encode("utf-8")
            except (TypeError, ValueError, RecursionError):
                raise ValueError("Craft plan must contain only finite, JSON-serializable values.") from None
            headers["Content-Type"] = "application/json"
        request = Request(self.base_url + path, data=data, headers=headers, method=method)
        try:
            with self._opener.open(request, timeout=self.timeout) as response:
                body = response.read()
        except HTTPError as error:
            status = error.code
            try:
                with error:
                    error_body = error.read()
                payload = json.loads(error_body)
            except (OSError, HTTPException, ValueError, RecursionError):
                payload = None
            code = "HTTP_ERROR"
            message = "The editor API returned an HTTP error."
            if isinstance(payload, dict) and isinstance(payload.get("error"), dict):
                details = payload["error"]
                if isinstance(details.get("code"), str) and details["code"]:
                    code = details["code"]
                if isinstance(details.get("message"), str) and details["message"]:
                    message = details["message"]
            code = code.replace(self._token, "[REDACTED]")
            message = message.replace(self._token, "[REDACTED]")
            raise APIError(status, code, message) from None
        except (URLError, OSError, HTTPException):
            suffix = " POST outcome is unknown; inspect the game before resubmitting." if method == "POST" else ""
            raise TransportError(
                "Could not communicate with the local editor API. Check the port and that KSP is in VAB/SPH."
                + suffix
            ) from None
        try:
            payload = json.loads(body)
        except (ValueError, RecursionError):
            raise ProtocolError("The editor API returned invalid JSON.") from None
        if not isinstance(payload, dict):
            raise ProtocolError("The editor API response must be a JSON object.")
        return payload
