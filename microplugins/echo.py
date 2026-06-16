"""Micro-plugin: echo back the arguments."""

from __future__ import annotations

from typing import Any


def register(registry: Any) -> None:
    registry("echo", run, "Echo back any arguments as-is.")


def run(args: dict[str, Any]) -> dict[str, Any]:
    return {"echoed": args}
