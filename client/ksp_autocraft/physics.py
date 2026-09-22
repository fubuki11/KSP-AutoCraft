"""Deterministic idealized calculations, not a flight or mission assessment."""

import math

STANDARD_GRAVITY = 9.80665  # Isp convention, not the gravity of a particular body.


def _number(value: float, label: str, *, positive: bool = False) -> float:
    requirement = "positive" if positive else "non-negative"
    message = f"{label} must be a finite {requirement} number."
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ValueError(message)
    try:
        number = float(value)
    except OverflowError:
        raise ValueError(message) from None
    if not math.isfinite(number) or number < 0 or (positive and number == 0):
        raise ValueError(message)
    return number


def delta_v(isp_seconds: float, wet_mass_tonnes: float, dry_mass_tonnes: float) -> float:
    """Ideal rocket-equation delta-v in m/s using Isp in s and masses in t.

    Assumes constant Isp and a single mass ratio, without gravity/drag losses.
    Equal wet/dry masses or zero Isp give zero delta-v.
    """
    isp = _number(isp_seconds, "Isp")
    wet = _number(wet_mass_tonnes, "Wet mass", positive=True)
    dry = _number(dry_mass_tonnes, "Dry mass", positive=True)
    if wet < dry:
        raise ValueError("Wet mass must be greater than or equal to dry mass.")
    # log1p preserves accuracy for nearly equal masses. The fallback avoids
    # overflowing an otherwise valid extreme mass ratio.
    relative_mass = (wet - dry) / dry
    log_ratio = math.log1p(relative_mass) if math.isfinite(relative_mass) else math.log(wet) - math.log(dry)
    result = isp * (STANDARD_GRAVITY * log_ratio)
    if not math.isfinite(result):
        raise ValueError("Delta-v exceeds the finite numeric range.")
    return result


def thrust_to_weight(
    thrust_kilonewtons: float,
    mass_tonnes: float,
    gravity: float = STANDARD_GRAVITY,
) -> float:
    """Dimensionless TWR at the supplied local gravity in m/s^2.

    The kN-to-N and tonne-to-kg factors cancel. The default gravity is only
    a reference value; supply the local gravity appropriate to the install.
    """
    thrust = _number(thrust_kilonewtons, "Thrust")
    mass = _number(mass_tonnes, "Mass", positive=True)
    local_gravity = _number(gravity, "Gravity", positive=True)
    result = (thrust / mass) / local_gravity
    if not math.isfinite(result):
        raise ValueError("Thrust-to-weight ratio exceeds the finite numeric range.")
    return result
