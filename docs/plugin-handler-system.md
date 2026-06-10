# Plugin Handler System — Design

Allow third-party developers to register custom `pro.*` handlers without modifying `ProBridgeService.cs`.

## Problem

`ProBridgeService.cs` is 4,100+ lines with 126 handlers in a single `Dictionary<string, Func<...>>`. Adding a new handler requires:
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
  APBridgeAddIn/       (main Add-In project)
  plugins/             (plugin projects, each builds to a .dll)
    MyCustomTool/
      MyCustomTool.csproj
      MyCustomHandler.cs
```

Each plugin `.csproj` references `APBridgeAddIn.csproj` for `IProBridgeHandler`, `IpcResponse`, `IpcRequest`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\APBridgeAddIn\APBridgeAddIn.csproj" />
  </ItemGroup>
</Project>
```

## Build Integration

The `package-addin.ps1` script collects plugin DLLs:

```powershell
# In Package function:
$pluginDirs = Get-ChildItem (Join-Path $projDir "..\plugins") -Directory
foreach ($dir in $pluginDirs) {
    $pluginDll = Join-Path $dir "bin\Debug\net8.0-windows8.0\*.dll"
    Copy-Item $pluginDll (Join-Path $tempDir "Install\")
}
```

## Implementation Order

| Step | Files | Effort |
|------|-------|--------|
| 1 | `IProBridgeHandler.cs`, `ProBridgeHandlerAttribute.cs` | 30 min |
| 2 | `DiscoverPlugins()` in `ProBridgeService.cs` | 1 hr |
| 3 | Example plugin project under `addin/plugins/` | 30 min |
| 4 | Update `package-addin.ps1` for plugin collection | 30 min |
| 5 | Tests: verify plugin handlers are discovered and override built-ins | 1 hr |

## Risks

| Risk | Mitigation |
|------|------------|
| Plugin DLL can't be loaded (version mismatch) | Wrap assembly loading in try/catch, skip failed plugins |
| Plugin handler throws unhandled exception | Handler interface method is called within existing try/catch in RunLoop |
| Plugin op key conflicts with future built-in handler | Document that built-in handlers take precedence — plugin overrides are opt-in |
| Performance: scanning all assemblies on startup | Cache results; only scan on startup or explicit refresh |
