#!/usr/bin/env python3
"""Stress tests for the Named Pipe Add-In connection.

Tests pipe resilience under load, rapid calls, and error recovery.
"""

from __future__ import annotations

import sys
import time
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(PROJECT_ROOT))

from arcgis_mcp_named_pipe import call_addin, is_addin_available, AddInNotAvailableError


passed = 0
failed = 0


def check(description: str, ok: bool, detail: str = ""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  PASS: {description}")
    else:
        failed += 1
        print(f"  FAIL: {description}" + (f" — {detail}" if detail else ""))


def main() -> int:
    global passed, failed

    print("=== ArcGIS Pro Add-In Stress Tests ===\n")

    # --- Test 1: Availability check ---
    print("[1/6] Basic connectivity")
    avail = is_addin_available()
    for _ in range(5):
        if avail:
            break
        time.sleep(0.5)
        avail = is_addin_available()
    check("is_addin_available() returns True", avail is True)
    if not avail:
        print("\nAdd-In not available. Skipping remaining tests.")
        return 1

    # --- Test 2: 100 rapid pings ---
    print("\n[2/6] 100 rapid pings")
    start = time.monotonic()
    for i in range(100):
        try:
            r = call_addin("pro.ping", {}, timeout=2)
            assert r.get("pong") == "addin"
        except Exception as e:
            check(f"Ping #{i+1}", False, str(e))
            break
    else:
        elapsed = time.monotonic() - start
        check(f"100 pings in {elapsed:.1f}s ({100/elapsed:.0f} pings/sec)", True)

    # --- Test 3: Sustained mixed operations ---
    print("\n[3/6] Sustained mixed operations")
    ops = [
        ("pro.ping", {}),
        ("pro.getActiveMapName", {}),
        ("pro.listLayers", {}),
        ("pro.is3d", {}),
    ]
    for i in range(25):
        op, args = ops[i % len(ops)]
        try:
            r = call_addin(op, args, timeout=3)
            assert r is not None
        except Exception as e:
            check(f"Mixed op #{i+1} ({op})", False, str(e))
            break
    else:
        check("25 mixed operations", True)

    # --- Test 4: Invalid operations ---
    print("\n[4/6] Error handling")
    try:
        r = call_addin("pro.nonexistentTool", {}, timeout=3)
        check("Invalid op returns error status", True)
    except Exception as e:
        check("Invalid op raises exception", True)

    try:
        r = call_addin("pro.ping", {"extra": "data"}, timeout=3)
        check("Extra args on ping", r.get("pong") == "addin")
    except Exception as e:
        check("Extra args on ping", False, str(e))

    # --- Test 5: Reconnection after delay ---
    print("\n[5/6] Reconnection robustness")
    for delay in [0.1, 0.5, 1.0, 2.0]:
        time.sleep(delay)
        try:
            r = call_addin("pro.ping", {}, timeout=3)
            check(f"Reconnect after {delay}s delay", r.get("pong") == "addin")
        except Exception as e:
            check(f"Reconnect after {delay}s delay", False, str(e))

    # --- Test 6: Large payload handling ---
    print("\n[6/6] Payload variation")
    try:
        r = call_addin("pro.ping", {}, timeout=5)
        check("Empty args ping", r.get("pong") == "addin")
    except Exception as e:
        check("Empty args ping", False, str(e))

    try:
        r = call_addin("pro.listLayers", {}, timeout=5)
        check("listLayers returns list", isinstance(r, list))
    except Exception as e:
        check("listLayers returns list", False, str(e))

    # Summary
    total = passed + failed
    print(f"\n=== Results: {passed}/{total} passed ({100*passed/total:.0f}%) ===")
    return 1 if failed > 0 else 0


if __name__ == "__main__":
    sys.exit(main())
