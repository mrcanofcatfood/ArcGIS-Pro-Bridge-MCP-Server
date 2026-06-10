using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;
using APBridgeAddIn;

namespace ArcGisProBridge.Tests;

public class HandlerRegistryTests
{
    // All 126 expected handler op keys — kept in sync with ProBridgeService.cs _handlers dictionary
    private static readonly string[] ExpectedHandlers =
    {
        "pro.ping",
        "pro.getActiveMapName",
        "pro.listLayers",
        "pro.countFeatures",
        "pro.getLayerSchema",
        "pro.getSelectionCount",
        "pro.selectByAttribute",
        "pro.clearSelection",
        "pro.zoomToLayer",
        "pro.getCurrentExtent",
        "pro.panToExtent",
        "pro.getCamera",
        "pro.setLayerVisibility",
        "pro.getLayerExtent",
        "pro.selectByRectangle",
        "pro.switchSelection",
        "pro.getFeatureByOid",
        "pro.undoEdit",
        "pro.redoEdit",
        "pro.setActiveTool",
        "pro.is3d",
        "pro.getLayerRenderer",
        "pro.setLayerColor",
        "pro.removeLayer",
        "pro.addLayerFromFile",
        "pro.selectByPolygon",
        "pro.listLayouts",
        "pro.getProjectProperties",
        "pro.getGeometryDistance",
        "pro.setLayerTransparency",
        "pro.getAllMapNames",
        "pro.getMapFrame",
        "pro.selectByLayer",
        "pro.getFeaturesByExtent",
        "pro.deleteFeaturesByOid",
        "pro.updateFeatureAttributes",
        "pro.createPointFeature",
        "pro.listBookmarks",
        "pro.zoomToBookmark",
        "pro.createBookmark",
        "pro.reorderLayer",
        "pro.setLabelsEnabled",
        "pro.openDockpane",
        "pro.exportLayoutToFile",
        "pro.flyToLocation",
        "pro.applyUniqueValueRenderer",
        "pro.applyClassBreaksRenderer",
        "pro.getElevationSources",
        "pro.setGroundOpacity",
        "pro.getActiveTool",
        "pro.listFieldValues",
        "pro.addField",
        "pro.deleteField",
        "pro.createPolygonFeature",
        "pro.createLineFeature",
        "pro.setMapScale",
        "pro.getMapScale",
        "pro.zoomToSelected",
        "pro.getEditState",
        "pro.setSnapping",
        "pro.deleteBookmark",
        "pro.flashSelection",
        "pro.selectAll",
        "pro.setStatusBarMessage",
        "pro.listStandaloneTables",
        "pro.listGpHistory",
        "pro.isTimeEnabled",
        "pro.getTimeExtent",
        "pro.setTimeExtent",
        "pro.listLayoutElements",
        "pro.renameField",
        "pro.getLayerDescription",
        "pro.setLayerDescription",
        "pro.listSceneLayerTypes",
        "pro.countFeaturesByExpression",
        "pro.splitFeatures",
        "pro.mergeFeatures",
        "pro.runGpTool",
        "pro.listGpTools",
        "pro.copyFeatures",
        "pro.renameLayer",
        "pro.getLayerStatistics",
        "pro.projectGeometry",
        "pro.addLayoutText",
        "pro.addLayoutPicture",
        "pro.addLayoutLegend",
        "pro.addLayoutNorthArrow",
        "pro.removeLayoutElement",
        "pro.createLayout",
        "pro.createMap",
        "pro.addBasemap",
        "pro.setAtmosphere",
        "pro.setSunPosition",
        "pro.getSunPosition",
        "pro.explore3D",
        "pro.setLayerElevation",
        "pro.setSceneBackground",
        "pro.createFeatureClass",
        "pro.deleteFeatureClass",
        "pro.saveProject",
        "pro.addAttributeIndex",
        "pro.searchAddress",
        "pro.openAttributeTable",
        "pro.exportToCsv",
        "pro.exportToGeoJSON",
        "pro.importCsv",
        "pro.exportToShapefile",
        "pro.exportToKml",
        "pro.importGeoJSON",
        "pro.showMessage",
        "pro.showProgressDialog",
        "pro.setStatusBarProgress",
        "pro.listDockpanes",
        "pro.activateRibbonTab",
        "pro.listDomains",
        "pro.createDomain",
        "pro.assignDomainToField",
        "pro.listSubtypes",
        "pro.setSubtypeField",
        "pro.enableAttachments",
        "pro.listToolboxes",
        "pro.describeTool",
        "pro.getGeoprocessingHistory",
        "pro.runPythonScript",
        "pro.setEnvironment",
        "pro.getEnvironment",
    };

    [Fact]
    public void AllExpectedHandlers_ArePresent()
    {
        Assert.Equal(126, ExpectedHandlers.Length);
    }

    [Fact]
    public void AllHandlers_StartWithProPrefix()
    {
        foreach (var key in ExpectedHandlers)
            Assert.StartsWith("pro.", key);
    }

    [Fact]
    public void NoDuplicateHandlers()
    {
        Assert.Equal(ExpectedHandlers.Length, ExpectedHandlers.Distinct().Count());
    }

    [Fact]
    public void KnownDockPanes_MapToDamlIds()
    {
        var dockPanes = new Dictionary<string, string>
        {
            ["Contents"] = "esri_core_contentsDockPane",
            ["Catalog"] = "esri_core_projectDockPane",
            ["Attribute Table"] = "esri_mapping_tableWindow",
            ["Table"] = "esri_mapping_tableWindow",
            ["Search"] = "esri_core_searchDockPane",
            ["Geoprocessing"] = "esri_mapping_geoprocessingPane",
            ["Symbology"] = "esri_mapping_symbologyDockPane",
            ["Labeling"] = "esri_mapping_labelClassDockPane",
            ["Bookmarks"] = "esri_mapping_bookmarksManagerDockPane",
            ["Time"] = "esri_mapping_timeDockPane",
        };
        Assert.Equal(10, dockPanes.Count);
        foreach (var (name, damlId) in dockPanes)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(damlId));
            Assert.StartsWith("esri_", damlId);
        }
    }

    [Fact]
    public void KnownRibbonTabs_MapToDamlIds()
    {
        var ribbonTabs = new Dictionary<string, string>
        {
            ["Map"] = "esri_mapping_mapTab",
            ["Edit"] = "esri_mapping_editTab",
            ["Catalog"] = "esri_core_catalogTab",
            ["Insert"] = "esri_mapping_insertTab",
            ["Analysis"] = "esri_mapping_analysisTab",
            ["View"] = "esri_mapping_viewTab",
            ["Appearance"] = "esri_mapping_appearanceTab",
        };
        Assert.Equal(7, ribbonTabs.Count);
        foreach (var (name, damlId) in ribbonTabs)
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.False(string.IsNullOrWhiteSpace(damlId));
            Assert.StartsWith("esri_", damlId);
        }
    }
}
