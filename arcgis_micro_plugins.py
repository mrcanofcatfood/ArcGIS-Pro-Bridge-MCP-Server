from __future__ import annotations

import importlib.util
import sys
from pathlib import Path
from typing import Any, Callable

PluginFunc = Callable[[dict[str, Any]], dict[str, Any]]
PluginRegistry = dict[str, tuple[str, PluginFunc]]

MICRO_PLUGIN_DIR = Path(__file__).resolve().parent / "microplugins"

_registry: PluginRegistry = {}
_discovered = False


def _discover() -> None:
    global _discovered
    if _discovered:
        return
    if not MICRO_PLUGIN_DIR.is_dir():
        _discovered = True
        return
    for fpath in sorted(MICRO_PLUGIN_DIR.glob("*.py")):
        if fpath.name.startswith("_"):
            continue
        mod_name = fpath.stem.replace("-", "_").replace(" ", "_")
        try:
            spec = importlib.util.spec_from_file_location(mod_name, fpath)
            if spec is None or spec.loader is None:
                continue
            mod = importlib.util.module_from_spec(spec)
            sys.modules[mod_name] = mod
            spec.loader.exec_module(mod)
            if hasattr(mod, "register"):
                mod.register(_register)
        except Exception as exc:
            import warnings
            warnings.warn(f"Failed to load micro-plugin '{fpath.name}': {exc}", stacklevel=2)
    _discovered = True


def _register(name: str, fn: PluginFunc, description: str = "") -> None:
    _registry[name] = (description, fn)


def _reset() -> None:
    global _discovered
    _registry.clear()
    _discovered = False


def list_micro_plugins() -> list[dict[str, Any]]:
    _discover()
    plugins = []
    for name, (desc, _fn) in sorted(_registry.items()):
        plugins.append({"name": name, "description": desc})
    return plugins


def run_micro_plugin(plugin: str, args: dict[str, Any]) -> dict[str, Any]:
    _discover()
    if plugin not in _registry:
        return {"status": "error", "message": f"Micro-plugin not found: '{plugin}'"}
    _desc, fn = _registry[plugin]
    try:
        result = fn(args)
        if isinstance(result, dict):
            result.setdefault("status", "ok")
        return result
    except Exception as exc:
        return {"status": "error", "message": f"Micro-plugin '{plugin}' failed: {exc}"}
