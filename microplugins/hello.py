"""Micro-plugin: return a greeting."""

from __future__ import annotations

from typing import Any


def register(registry: Any) -> None:
    registry("hello", run, "Return a friendly greeting.")


def run(args: dict[str, Any]) -> dict[str, Any]:
    name = args.get("name", "world")
    return {"greeting": f"Hello, {name}!"}
