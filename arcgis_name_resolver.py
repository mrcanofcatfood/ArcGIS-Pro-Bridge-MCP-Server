from __future__ import annotations

import difflib
from typing import Any

from arcgis_mcp_named_pipe import AddInNotAvailableError, AddInOperationError, call_addin


def resolve_layer_name(
    layer_hint: str,
    known_layers: list[str],
    cutoff: float = 0.6,
) -> str | None:
    """Fuzzy-match a layer name hint against known layer names.

    Returns the best match if found above cutoff, otherwise None.
    Attempts exact match first (case-insensitive), then fuzzy.
    """
    exact = _find_exact(layer_hint, known_layers)
    if exact is not None:
        return exact

    matches = _fuzzy_match(layer_hint, known_layers, n=1, cutoff=cutoff)
    return matches[0] if matches else None


def resolve_field_name(
    field_hint: str,
    known_fields: list[str],
    cutoff: float = 0.6,
) -> str | None:
    """Fuzzy-match a field name hint against known field names.

    Returns the best match if found above cutoff, otherwise None.
    """
    exact = _find_exact(field_hint, known_fields)
    if exact is not None:
        return exact

    matches = _fuzzy_match(field_hint, known_fields, n=1, cutoff=cutoff)
    return matches[0] if matches else None


def resolve_layer_with_details(
    layer_hint: str,
    cutoff: float = 0.6,
) -> dict[str, Any]:
    """Resolve an approximate layer name to the exact layer name in the active map.

    Calls pro_list_layers() internally, fuzzy-matches the hint,
    and returns resolved_name, confidence, and candidates.
    """
    try:
        result = call_addin("pro.listLayers")
    except (AddInNotAvailableError, AddInOperationError) as exc:
        return {
            "resolved_name": None,
            "error": str(exc),
            "confidence": 0.0,
            "candidates": [],
        }

    if not isinstance(result, list):
        return {
            "resolved_name": None,
            "error": "Unexpected response from pro.listLayers",
            "confidence": 0.0,
            "candidates": [],
        }

    known_layers = [layer.get("Name") or layer.get("name", "") for layer in result]
    known_layers = [n for n in known_layers if n]

    if not known_layers:
        return {
            "resolved_name": None,
            "error": "No layers found in active map",
            "confidence": 0.0,
            "candidates": [],
        }

    exact = _find_exact(layer_hint, known_layers)
    if exact is not None:
        return {
            "resolved_name": exact,
            "confidence": 1.0,
            "candidates": [exact],
        }

    matches = _fuzzy_match(layer_hint, known_layers, n=3, cutoff=cutoff)

    if not matches:
        return {
            "resolved_name": None,
            "error": f"No close match for '{layer_hint}'. Known layers: {', '.join(known_layers[:10])}",
            "confidence": 0.0,
            "candidates": known_layers[:10],
        }

    best = matches[0]
    score = difflib.SequenceMatcher(None, layer_hint.lower(), best.lower()).ratio()
    return {
        "resolved_name": best,
        "confidence": round(score, 4),
        "candidates": matches,
    }


def _fuzzy_match(
    hint: str, names: list[str], n: int = 1, cutoff: float = 0.6
) -> list[str]:
    """Case-insensitive fuzzy matching using difflib."""
    hint_lower = hint.lower()
    lower_map = {name.lower(): name for name in names}
    lower_names = list(lower_map.keys())
    matches = difflib.get_close_matches(hint_lower, lower_names, n=n, cutoff=cutoff)
    return [lower_map[m] for m in matches]


def _find_exact(hint: str, names: list[str]) -> str | None:
    hint_lower = hint.lower()
    for name in names:
        if name.lower() == hint_lower:
            return name
    return None
