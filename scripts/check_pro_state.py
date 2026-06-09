"""Check current ArcGIS Pro project state."""
from arcpy import mp
proj = mp.ArcGISProject("CURRENT")
print("Name:", proj.name)
print("Path:", proj.homeFolder)
print("DefaultGDB:", proj.defaultGeodatabase)
for m in proj.listMaps():
    print("Map:", m.name)
    for l in m.listLayers():
        print("  Layer:", l.name)
print("OK: CURRENT project is accessible")
