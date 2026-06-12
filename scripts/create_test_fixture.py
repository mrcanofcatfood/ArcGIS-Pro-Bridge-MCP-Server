#!/usr/bin/env python3
"""Generate a test .aprx + .gdb fixture for live Add-In validation.

Run with ArcGIS Pro's Python interpreter:
    "C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe" scripts/create_test_fixture.py

Or use the Add-In's runPythonScript tool (with Pro running):
    from arcgis_mcp_named_pipe import call_addin
    code = open("scripts/create_test_fixture.py").read()
    call_addin("pro.runPythonScript", {"code": code, "timeoutSeconds": "120"})
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent.parent
FIXTURE_DIR = PROJECT_ROOT / "test_project"
FIXTURE_GDB_DIR = FIXTURE_DIR / "TestFixture"


def main() -> int:
    import arcpy
    from arcpy import mp

    FIXTURE_GDB_DIR.mkdir(parents=True, exist_ok=True)

    aprx_path = str(FIXTURE_DIR / "TestFixture.aprx")
    gdb_path = str(FIXTURE_GDB_DIR / "TestFixture.gdb")

    # Create GDB (nested inside TestFixture/ folder)
    if arcpy.Exists(gdb_path):
        arcpy.Delete_management(gdb_path)
    arcpy.CreateFileGDB_management(str(FIXTURE_GDB_DIR), "TestFixture.gdb")
    print(f"Created: {gdb_path}")

    # Create feature classes
    sr = arcpy.SpatialReference(4326)

    for fc_name, geom_type, fields in [
        ("TestPoints", "POINT", [("NAME", "TEXT", 50), ("VALUE", "DOUBLE")]),
        ("TestLines", "POLYLINE", [("NAME", "TEXT", 50)]),
        ("TestPolygons", "POLYGON", [("NAME", "TEXT", 50), ("CATEGORY", "TEXT", 50)]),
    ]:
        fc_path = f"{gdb_path}/{fc_name}"
        arcpy.CreateFeatureclass_management(gdb_path, fc_name, geom_type, spatial_reference=sr)
        for fname, ftype, flen in fields:
            arcpy.AddField_management(fc_path, fname, ftype, field_length=flen)
        print(f"  Created: {fc_name}")

    # Insert test points
    with arcpy.da.InsertCursor(f"{gdb_path}/TestPoints", ["SHAPE@", "NAME", "VALUE"]) as cur:
        for i, (lon, lat, name) in enumerate([
            (0, 0, "Origin"), (10, 10, "PointA"), (-10, -10, "PointB"),
            (20, 5, "PointC"), (-20, -5, "PointD"),
        ]):
            cur.insertRow([arcpy.PointGeometry(arcpy.Point(lon, lat), sr), name, float(i * 10)])
    print("  Inserted 5 points")

    # Insert test lines
    with arcpy.da.InsertCursor(f"{gdb_path}/TestLines", ["SHAPE@", "NAME"]) as cur:
        for pts, name in [
            ([[0, 0], [10, 10]], "LineA"),
            ([[-10, -10], [0, 0]], "LineB"),
            ([[10, 0], [20, 10]], "LineC"),
        ]:
            array = arcpy.Array([arcpy.Point(x, y) for x, y in pts])
            cur.insertRow([arcpy.Polyline(array, sr), name])
    print("  Inserted 3 lines")

    # Insert test polygons
    with arcpy.da.InsertCursor(f"{gdb_path}/TestPolygons", ["SHAPE@", "NAME", "CATEGORY"]) as cur:
        for rings, name, cat in [
            ([[[0, 0], [5, 0], [5, 5], [0, 5], [0, 0]]], "Square", "A"),
            ([[[-10, -10], [-5, -10], [-5, -5], [-10, -5], [-10, -10]]], "SmallSquare", "A"),
            ([[[10, 0], [20, 0], [20, 10], [10, 10], [10, 0]]], "Rectangle", "B"),
        ]:
            array = arcpy.Array([arcpy.Point(x, y) for x, y in rings[0]])
            cur.insertRow([arcpy.Polygon(array, sr), name, cat])
    print("  Inserted 3 polygons")

    # Now create project — this requires running inside ArcGIS Pro
    # Try to use MapView.Active if running inside Pro
    inside_pro = False
    try:
        from arcgis.desktop.mapping import MapView
        if MapView.Active is not None:
            inside_pro = True
    except Exception:
        pass

    if inside_pro:
        # We're running inside ArcGIS Pro, can use .NET APIs
        import clr
        clr.AddReference("ArcGIS.Desktop.Framework")
        from ArcGIS.Desktop.Framework import FrameworkApplication
        from ArcGIS.Desktop.Core import Project

        p = Project.Current
        if p is None:
            print("ERROR: No project open in Pro. Open one first.")
            return 1

        # Add a map
        from ArcGIS.Desktop.Mapping import MapFactory
        test_map = MapFactory.CreateMap("TestMap", "Map")
        try:
            from ArcGIS.Desktop.Mapping import LayerFactory
            for fc_name in ["TestPoints", "TestLines", "TestPolygons"]:
                fc_path = f"{gdb_path}/{fc_name}"
                LayerFactory.Instance.CreateLayer(fc_path, test_map)
        except Exception as ex:
            print(f"  Note: LayerFactory not available: {ex}")

        p.SetDirty()
        p.SaveAsync().GetAwaiter().GetResult()
        print(f"Project saved: {p.Path}")
    else:
        # Running from external Python — can't create .aprx
        # User must open Pro and use Add-In's runPythonScript instead
        print()
        print("=" * 60)
        print("Cannot create .aprx from external Python.")
        print("To complete fixture setup:")
        print(f"  1. Open a blank project in ArcGIS Pro")
        print(f"  2. Add {gdb_path} as a database connection")
        print(f"  3. Add TestPoints, TestLines, TestPolygons to a map called 'TestMap'")
        print(f"  4. Save the project as {aprx_path}")
        print(f"  5. Add a bookmark called 'FullExtent'")
        print("=" * 60)
        print()
        print(f"Or run this script through the Add-In's runPythonScript tool.")
        print(f"  GDB is ready: {gdb_path}")
        print(f"  Note: GDB is nested under test_project\\TestFixture\\ to comply with ArcGIS path conventions.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
