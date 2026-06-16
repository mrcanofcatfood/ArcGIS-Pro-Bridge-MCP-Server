from __future__ import annotations

import json
import os
import re
from pathlib import Path
from typing import Any

from arcgis_mcp_named_pipe import AddInNotAvailableError, AddInOperationError, call_addin

MACRO_DIR = Path(__file__).resolve().parent / "macros"


def list_builtin_macros() -> list[dict[str, Any]]:
    """List all built-in macro definitions from the macros/ directory."""
    macros = []
    if not MACRO_DIR.is_dir():
        return macros
    for fpath in sorted(MACRO_DIR.glob("*.json")):
        try:
            macro = json.loads(fpath.read_text(encoding="utf-8"))
            macros.append({
                "name": macro.get("name", fpath.stem),
                "description": macro.get("description", ""),
                "step_count": len(macro.get("steps", [])),
                "file": fpath.name,
            })
        except Exception:
            pass
    return macros


def _substitute(template: str, variables: dict[str, str]) -> str:
    """Replace {{key}} placeholders with values from variables dict."""
    def _replace(m: re.Match) -> str:
        key = m.group(1).strip()
        return variables.get(key, m.group(0))
    return re.sub(r"\{\{(\w+)\}\}", _replace, template)


def _substitute_step(step: dict[str, Any], variables: dict[str, str]) -> dict[str, Any]:
    """Apply variable substitution to all string values in a step dict."""
    subbed: dict[str, Any] = {}
    for k, v in step.items():
        if isinstance(v, str):
            subbed[k] = _substitute(v, variables)
        elif isinstance(v, dict):
            subbed[k] = _substitute_step(v, variables)
        elif isinstance(v, list):
            subbed[k] = [_substitute_step(i, variables) if isinstance(i, dict) else i for i in v]
        else:
            subbed[k] = v
    return subbed


def load_macro(name_or_path: str) -> dict[str, Any] | None:
    """Load a macro by name (from macros/) or by full file path.

    Tries built-in macros/ directory first, then interprets as a file path.
    """
    # Try built-in first
    if MACRO_DIR.is_dir():
        for fpath in MACRO_DIR.glob("*.json"):
            try:
                macro = json.loads(fpath.read_text(encoding="utf-8"))
                if macro.get("name") == name_or_path or fpath.stem == name_or_path:
                    return macro
            except Exception:
                pass

    # Try as file path
    path = Path(name_or_path)
    if path.is_file():
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            pass

    return None


def execute_macro(
    macro_def: dict[str, Any],
    timeout_per_step: float = 10.0,
    variables: dict[str, str] | None = None,
) -> dict[str, Any]:
    """Execute a workflow macro - a named sequence of pro.* operations.

    Each step is executed sequentially via call_addin().
    {{key}} placeholders in step args are replaced with values from variables dict.
    If a step fails, remaining steps are skipped.
    """
    name = macro_def.get("name", "unnamed")
    steps = macro_def.get("steps", [])
    vars_dict = variables or {}

    if not steps:
        return {
            "status": "error",
            "name": name,
            "message": "Macro has no steps",
            "total_steps": 0,
            "completed": 0,
            "results": [],
        }

    results: list[dict[str, Any]] = []
    all_succeeded = True

    for i, step in enumerate(steps):
        step = _substitute_step(step, vars_dict)
        step_op = step.get("op", "")
        step_args = step.get("args", {})
        step_name = step.get("name", f"step_{i+1}")

        step_result: dict[str, Any] = {
            "step": i + 1,
            "name": step_name,
            "op": step_op,
        }

        try:
            data = call_addin(step_op, step_args, timeout=timeout_per_step)
            step_result["status"] = "ok"
            step_result["data"] = data
        except AddInNotAvailableError as exc:
            step_result["status"] = "unavailable"
            step_result["error"] = str(exc)
            all_succeeded = False
            results.append(step_result)
            break
        except AddInOperationError as exc:
            step_result["status"] = "error"
            step_result["error"] = str(exc)
            all_succeeded = False
            results.append(step_result)
            break
        except Exception as exc:
            step_result["status"] = "error"
            step_result["error"] = f"Unexpected error: {exc}"
            all_succeeded = False
            results.append(step_result)
            break

        results.append(step_result)

    return {
        "status": "ok" if all_succeeded else "error",
        "name": name,
        "total_steps": len(steps),
        "completed": len(results),
        "all_succeeded": all_succeeded,
        "results": results,
    }
