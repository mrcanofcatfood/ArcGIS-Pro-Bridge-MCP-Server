using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Python.Runtime;

namespace APBridgeAddIn
{
    internal partial class ProBridgeService : IDisposable
    {
        // This file is intentionally empty.
        // Handlers are now in domain-specific partial class files:
        //   ProBridgeService.Map.cs
        //   ProBridgeService.Selection.cs
        //   ProBridgeService.Editing.cs
        //   ProBridgeService.Layers.cs
        //   ProBridgeService.Scene3D.cs
        //   ProBridgeService.Layouts.cs
        //   ProBridgeService.Schema.cs
        //   ProBridgeService.Geoprocessing.cs
        //   ProBridgeService.DataExchange.cs
        //   ProBridgeService.Gui.cs
        //   ProBridgeService.TimeQuery.cs
    }
}
