#!/usr/bin/env python3
"""Auto-build the test .aprx for live Add-In validation.

Creates test_project/TestFixture.aprx with a map, layers, and layout.

Note: Bookmarks require the C# Add-In SDK (use pro_create_bookmark).

Run via ArcGIS Pro's Python (Pro must be closed for write).
"""

from __future__ import annotations

import os
import shutil
import sys
from pathlib import Path

FIXTURE_DIR = Path(__file__).resolve().parent.parent / "test_project"
GDB_PATH = FIXTURE_DIR / "TestFixture" / "TestFixture.gdb"
APRX_PATH = FIXTURE_DIR / "TestFixture.aprx"
BLANK_APRX = r"C:\Program Files\ArcGIS\Pro\Resources\ArcToolBox\Services\routingservices\data\Blank.aprx"


def main() -> int:
    import arcpy

    arcpy.env.overwriteOutput = True

    if not arcpy.Exists(str(GDB_PATH)):
        print(f"ERROR: Fixture GDB not found at {GDB_PATH}")
        print("Run scripts/create_test_fixture.py first.")
        return 1

    if not os.path.exists(BLANK_APRX):
        print(f"ERROR: Blank template not found at {BLANK_APRX}")
        return 1

    if APRX_PATH.exists():
        APRX_PATH.unlink()
        print(f"Removed old project: {APRX_PATH}")

    shutil.copy2(BLANK_APRX, str(APRX_PATH))

    aprx = arcpy.mp.ArcGISProject(str(APRX_PATH))
    aprx.homeFolder = str(FIXTURE_DIR)
    aprx.defaultGeodatabase = str(GDB_PATH)

    m = aprx.createMap("TestMap", "MAP")
    print(f"Created map: {m.name}")

    for fc_name in ("TestPoints", "TestLines", "TestPolygons"):
        fc_path = os.path.join(str(GDB_PATH), fc_name)
        m.addDataFromPath(fc_path)
        print(f"  Added layer: {fc_name}")

    layout = aprx.createLayout(297, 210, "MILLIMETER", name="TestLayout")
    print(f"Created layout: {layout.name}")

    mf = layout.createMapFrame(arcpy.Extent(10, 10, 200, 190), m, "Map Frame")
    print(f"  Created map frame: {mf.name}")

    aprx.save()
    print(f"\nSaved: {APRX_PATH}")
    for ma in aprx.listMaps():
        print(f"  Map '{ma.name}': {len(ma.listLayers())} layers")
    for la in aprx.listLayouts():
        print(f"  Layout '{la.name}': {len(la.listElements())} elements")

    return 0


if __name__ == "__main__":
    sys.exit(main())
