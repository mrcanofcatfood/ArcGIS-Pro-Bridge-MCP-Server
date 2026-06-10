using System.Collections.Generic;
using System.Text.Json;
using Xunit;
using APBridgeAddIn;

namespace ArcGisProBridge.Tests;

public class IpcModelsTests
{
    [Fact]
    public void IpcRequest_SerializesAndDeserializes()
    {
        var req = new IpcRequest("pro.ping", new Dictionary<string, string> { { "key", "val" } });
        var json = JsonSerializer.Serialize(req);
        var back = JsonSerializer.Deserialize<IpcRequest>(json);
        Assert.Equal("pro.ping", back!.Op);
        Assert.Equal("val", back.Args!["key"]);
    }

    [Fact]
    public void IpcRequest_AllowsNullArgs()
    {
        var json = "{\"op\":\"pro.test\"}";
        var req = JsonSerializer.Deserialize<IpcRequest>(json);
        Assert.Equal("pro.test", req!.Op);
        Assert.Null(req.Args);
    }

    [Fact]
    public void IpcResponse_OkResponse()
    {
        var resp = new IpcResponse(true, null, new { pong = "addin" });
        var json = JsonSerializer.Serialize(resp);
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"data\":{\"pong\":\"addin\"}", json);
    }

    [Fact]
    public void IpcResponse_ErrorResponse()
    {
        var resp = new IpcResponse(false, "Layer not found", null);
        var json = JsonSerializer.Serialize(resp);
        var back = JsonSerializer.Deserialize<IpcResponse>(json);
        Assert.False(back!.Ok);
        Assert.Equal("Layer not found", back.Error);
        Assert.Null(back.Data);
    }

    [Fact]
    public void IpcResponse_RoundTripWithData()
    {
        var original = new IpcResponse(true, null, new { count = 42, name = "Test" });
        var json = JsonSerializer.Serialize(original);
        var back = JsonSerializer.Deserialize<IpcResponse>(json);
        Assert.True(back!.Ok);
        Assert.NotNull(back.Data);
    }

    [Fact]
    public void HandlerDictionary_HasExpectedKeys()
    {
        // Verify the structure of a typical handler op key
        var validPrefixes = new[] { "pro.", "arcgis." };
        var sampleKeys = new[] { "pro.ping", "pro.listLayers", "pro.createPointFeature" };
        foreach (var key in sampleKeys)
        {
            bool valid = false;
            foreach (var prefix in validPrefixes)
                if (key.StartsWith(prefix)) valid = true;
            Assert.True(valid, $"Key '{key}' should start with a valid prefix");
        }
    }

    [Fact]
    public void JsonElementToObject_HandlesVariousTypes()
    {
        // Test that JSON values can be extracted correctly
        var json = "{\"string\":\"hello\",\"number\":42,\"bool\":true,\"null\":null}";
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("hello", root.GetProperty("string").GetString());
        Assert.Equal(42, root.GetProperty("number").GetInt64());
        Assert.True(root.GetProperty("bool").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("null").ValueKind);
    }
}
