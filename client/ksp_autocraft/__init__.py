"""Stdlib client for the KSP 1.12.5 editor-only KSPAutoCraft API.

Build requests save candidate craft files, not loaded or launched vessels.
Loading requires manual confirmation in the game GUI, with automatic backup.
"""

from .client import APIError, KSPClient, ProtocolError, TransportError
from .physics import STANDARD_GRAVITY, delta_v, thrust_to_weight

__author__ = "fubuki11st"

__all__ = [
    "APIError",
    "KSPClient",
    "ProtocolError",
    "TransportError",
    "STANDARD_GRAVITY",
    "delta_v",
    "thrust_to_weight",
]
