# Plugin Handler System — Design

Allow third-party developers to register custom `pro.*` handlers without modifying `ProBridgeService.cs`.

## Problem

`ProBridgeService.cs` is 4,100+ lines with 127 handlers in a single `Dictionary<string, Func<...>>`. Adding a new handler requires:
1. Writing the handler method
2. Adding a dictionary entry
3. Rebuilding the entire Add-In

This creates merge conflicts for teams and prevents community contributions.

## Solution: Attribute-Based Registration

### 1. Handler Interface

```csharp
// IProBridgeHandler.cs
namespace APBridgeAddIn;

public interface IProBridgeHandler
{
    string Op { get; }
    Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken);
}
```

### 2. Registration Attribute

```csharp
// ProBridgeHandlerAttribute.cs
namespace APBridgeAddIn;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class ProBridgeHandlerAttribute : Attribute
{
    public string Op { get; }
    public ProBridgeHandlerAttribute(string op) => Op = op;
}
```

### 3. Example Third-Party Handler

```csharp
using APBridgeAddIn;

[ProBridgeHandler("pro.customTool")]
public class MyCustomHandler : IProBridgeHandler
{
    public string Op => "pro.customTool";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken ct)
    {
        var result = new { message = "Hello from a plugin handler!", args = request.Args };
        return Task.FromResult(new IpcResponse(true, null, result));
    }
}
```

### 4. Discovery at Startup

In `ProBridgeService.Start()`:

```csharp
private void DiscoverPlugins()
{
    var handlerType = typeof(IProBridgeHandler);
    var assemblies = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !a.GlobalAssemblyCache);

    foreach (var assembly in assemblies)
    {
        foreach (var type in assembly.GetExportedTypes()
            .Where(t => handlerType.IsAssignableFrom(t) && !t.IsAbstract))
        {
            var attr = type.GetCustomAttribute<ProBridgeHandlerAttribute>();
            if (attr == null) continue;

            var instance = (IProBridgeHandler)Activator.CreateInstance(type);
            _handlers[attr.Op] = async (req, ct) => await instance.Handle(req, ct);
        }
    }
}
```

## Design Decisions

### Why `AppDomain.GetAssemblies()`?
Searches all loaded assemblies, including ones dropped into the Add-In's `Install` folder. Developers just deploy their `.dll` alongside the Add-In.

### Why Separate Interface + Attribute?
The attribute provides metadata without forcing inheritance. The interface provides a clean contract. A handler could implement multiple ops by declaring multiple attributes.

### Op Key Validation
Reject handlers whose `Op` doesn't start with `pro.` to prevent namespace collisions:
```csharp
if (!attr.Op.StartsWith("pro."))
    throw new InvalidOperationException($"Handler op '{attr.Op}' must start with 'pro.'");
```

### Priority / Conflict Resolution
If a built-in handler and a plugin handler register the same op, the plugin wins (overrides). This lets plugins replace built-in behavior:
```csharp
_handlers[attr.Op] = handlerImpl; // overwrites built-in if present
```

Or log a warning:
```csharp
if (_handlers.ContainsKey(attr.Op))
    Debug.WriteLine($"[Plugin] Overriding built-in handler '{attr.Op}'");
```

## Directory Structure

```
addin/
  APBridgeAddIn/              (main Add-In project)
  plugins/                    (plugin projects, each builds to a .dll)
    ExamplePlugin/            *** example plugin — ready to copy/paste ***
      ExamplePlugin.csproj
      HelloWorldHandler.cs    (simple, no Pro SDK needed)
      MapInfoHandler.cs       (uses MapView.Active via Pro SDK)
```

## Example: ExamplePlugin

Two handlers are included in `addin/plugins/ExamplePlugin/`:

| Handler | Op | What it does |
|---------|----|--------------|
| `HelloWorldHandler.cs` | `pro.plugin.hello` | Returns greeting + incoming args |
| `MapInfoHandler.cs` | `pro.plugin.mapInfo` | Returns active map name, type, layers, extent via Pro SDK |

### `HelloWorldHandler.cs` — No Pro SDK required

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.hello")]
public class HelloWorldHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.hello";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        var data = new
        {
            message = "Hello from the ExamplePlugin!",
            args = request.Args ?? new(),
            plugin = "ExamplePlugin"
        };
        return Task.FromResult(new IpcResponse(true, null, data));
    }
}
```

### `MapInfoHandler.cs` — Uses Pro SDK (QueuedTask + MapView)

```csharp
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.mapInfo")]
public class MapInfoHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.mapInfo";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(() =>
        {
            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            var map = mapView.Map;
            var layers = map.GetLayersAsFlattenedList()
                .Select(l => new { name = l.Name, type = l.GetType().Name, visible = l.IsVisible })
                .ToList();

            var data = new
            {
                mapName = map.Name,
                mapType = map.MapType.ToString(),
                layerCount = layers.Count,
                layers,
                extent = new
                {
                    xmin = mapView.Extent.XMin,
                    ymin = mapView.Extent.YMin,
                    xmax = mapView.Extent.XMax,
                    ymax = mapView.Extent.YMax
                }
            };

            return new IpcResponse(true, null, data);
        });
    }
}
```

### `ExamplePlugin.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows8.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <NoWarn>CA1416,CS8632</NoWarn>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\APBridgeAddIn\APBridgeAddIn.csproj">
      <Private>False</Private>
    </ProjectReference>
  </ItemGroup>
  <!-- Add Pro SDK references if your handlers use ArcGIS.Desktop.* types -->
  <ItemGroup>
    <Reference Include="ArcGIS.Desktop.Framework">
      <HintPath>C:\Program Files\ArcGIS\Pro\bin\ArcGIS.Desktop.Framework.dll</HintPath>
      <CopyLocal>False</CopyLocal>
      <Private>False</Private>
    </Reference>
    <Reference Include="ArcGIS.Core">
      <HintPath>C:\Program Files\ArcGIS\Pro\bin\ArcGIS.Core.dll</HintPath>
      <CopyLocal>False</CopyLocal>
      <Private>False</Private>
    </Reference>
    <Reference Include="ArcGIS.Desktop.Mapping">
      <HintPath>C:\Program Files\ArcGIS\Pro\bin\Extensions\Mapping\ArcGIS.Desktop.Mapping.dll</HintPath>
      <CopyLocal>False</CopyLocal>
      <Private>False</Private>
    </Reference>
  </ItemGroup>
</Project>
```

## Step-by-Step: Creating a Plugin

### 1. Create the project skeleton

```
addin/plugins/MyPlugin/MyPlugin.csproj
addin/plugins/MyPlugin/MyHandler.cs
```

Use the `ExamplePlugin.csproj` above as a template. Add Pro SDK references only for the DLLs you actually need.

### 2. Implement a handler

```csharp
namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.myTool")]
public class MyHandler : IProBridgeHandler
{
    public string Op => "pro.myTool";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken ct)
    {
        // Your logic here
        return Task.FromResult(new IpcResponse(true, null, new { result = "ok" }));
    }
}
```

### 3. Build

```powershell
dotnet build addin/plugins/MyPlugin/MyPlugin.csproj
```

### 4. Package

```powershell
.\addin\APBridgeAddIn\package-addin.ps1 -Action package
```

The packaging script auto-discovers all projects under `addin/plugins/` and bundles their DLLs into the `.esriAddinX`.

### 5. Install

```powershell
.\addin\APBridgeAddIn\package-addin.ps1 -Action install
```

### 6. Verify

After starting ArcGIS Pro, from the OpenCode MCP session:

```python
# Call your new plugin tool
result = await session.call_tool("arcgis-pro_pro_run_python_script", {
    "code": "import json; print(json.dumps(client.call('pro.myTool', {})))"
})
```

Or use the Python test harness to ping it:

```python
from arcgis_mcp_named_pipe import ArcGisProNamedPipeClient

client = ArcGisProNamedPipeClient()
result = client.call("pro.plugin.hello", {"name": "world"})
print(result)  # {"ok": true, "data": {"message": "Hello from the ExamplePlugin!", ...}}
```

## Build Integration

The `package-addin.ps1` script auto-collects plugin DLLs:

```powershell
# Inside Package() function — added in v0.5.3:
$pluginsDir = Join-Path $projDir "..\plugins"
Get-ChildItem $pluginsDir -Directory | ForEach-Object {
    $pluginDll = Join-Path $_.FullName "bin\Debug\net8.0-windows8.0\$($_.Name).dll"
    if (Test-Path $pluginDll) {
        Copy-Item $pluginDll (Join-Path $tempDir "Install\$($_.Name).dll")
    }
}
```

## Cookbook Plugins

Five example/cookbook plugins are included under `addin/plugins/` as reference implementations:

| Plugin | Tool Op | Description | Pro SDK Required |
|--------|---------|-------------|:----------------:|
| `BatchExportPlugin` | `pro.plugin.batchExport` | Export all layers to CSV/GeoJSON/SHP/KML | Yes |
| `CoordinateCapturePlugin` | `pro.plugin.coordinateCapture` | Capture map center coords with optional reprojection | Yes |
| `FeatureInspectorPlugin` | `pro.plugin.featureInspector` | All attributes + geometry summary by OID | Yes |
| `QueryBuilderPlugin` | `pro.plugin.queryBuilder` | SQL query builder with equals/contains/gt/lt operators | Yes |
| `FieldCalculatorPlugin` | `pro.plugin.fieldCalculator` | Calculate field via arcpy subprocess | Yes |
| `ExamplePlugin` | `pro.plugin.hello`, `pro.plugin.mapInfo` | Starter template with two simple handlers | Partial |

Each plugin has a corresponding Python `@mcp.tool()` wrapper (e.g. `pro_plugin_batch_export`, `pro_plugin_feature_inspector`). See `arcgis_mcp_server.py:1480` for the Python-side wrappers.

## Implementation Status ✅

All 5 steps are implemented and verified:

| Step | Files | Status |
|------|-------|--------|
| 1 | `IProBridgeHandler.cs`, `ProBridgeHandlerAttribute.cs` | ✅ Done |
| 2 | `DiscoverPlugins()` in `ProBridgeService.cs` | ✅ Done (with override logging) |
| 3 | Example plugin project + 5 cookbook plugins at `addin/plugins/` | ✅ Done (6 projects) |
| 4 | `package-addin.ps1` auto-collects plugin DLLs | ✅ Done |
| 5 | Build verification (0 errors, plugin DLL in package) | ✅ Done |

## Risks

| Risk | Mitigation |
|------|------------|
| Plugin DLL can't be loaded (version mismatch) | Wrap assembly loading in try/catch, skip failed plugins |
| Plugin handler throws unhandled exception | Handler interface method is called within existing try/catch in RunLoop |
| Plugin op key conflicts with future built-in handler | Document that built-in handlers take precedence — plugin overrides are opt-in |
| Performance: scanning all assemblies on startup | Cache results; only scan on startup or explicit refresh |
