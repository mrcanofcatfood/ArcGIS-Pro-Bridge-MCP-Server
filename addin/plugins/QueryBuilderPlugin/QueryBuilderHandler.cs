using ArcGIS.Core.Data;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.queryBuilder")]
public class QueryBuilderHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.queryBuilder";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(() =>
        {
            var args = request.Args;
            if (args == null || !args.TryGetValue("layer", out var layerName) || !args.TryGetValue("field", out var field) || !args.TryGetValue("value", out var value))
                return new IpcResponse(false, "args 'layer', 'field', & 'value' required", null);

            var op = "equals";
            if (args.TryGetValue("operator", out var opArg))
                op = opArg.ToLowerInvariant();

            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            var layer = mapView.Map.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

            if (layer == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);

            try
            {
                var table = layer.GetTable();
                var whereClause = BuildWhereClause(field, value, op, table);

                var queryFilter = new QueryFilter { WhereClause = whereClause };
                var matchingOids = new List<long>();
                var matchedValues = new List<string>();

                using var cursor = table.Search(queryFilter, false);
                while (cursor.MoveNext() && matchingOids.Count < 100)
                {
                    using var row = cursor.Current;
                    var oidVal = row["OBJECTID"];
                    if (oidVal != null && oidVal != DBNull.Value)
                        matchingOids.Add(Convert.ToInt64(oidVal));

                    var fieldVal = row[field];
                    matchedValues.Add(fieldVal?.ToString() ?? "(null)");
                }

                var data = new
                {
                    field,
                    value,
                    operatorName = op,
                    matchCount = matchingOids.Count,
                    matchingOids,
                    matchedValues,
                    whereClause,
                    truncated = matchingOids.Count >= 100
                };

                return new IpcResponse(true, null, data);
            }
            catch (Exception ex)
            {
                return new IpcResponse(false, $"Query error: {SanitizeException(ex)}", null);
            }
        });
    }

    private static string BuildWhereClause(string field, string value, string op, Table table)
    {
        var fieldDef = table.GetDefinition().GetFields().FirstOrDefault(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        var isText = fieldDef?.FieldType == FieldType.String || fieldDef?.FieldType == FieldType.GUID;
        var quotedValue = isText ? $"'{value.Replace("'", "''")}'" : value;

        return op switch
        {
            "contains" => isText ? $"UPPER({field}) LIKE UPPER('%{value.Replace("'", "''")}%')" : $"{field} = {quotedValue}",
            "gt" => $"{field} > {quotedValue}",
            "lt" => $"{field} < {quotedValue}",
            _ => $"{field} = {quotedValue}"
        };
    }

    private static string SanitizeException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 500) msg = msg[..500];
        return msg.Replace("\r\n", " ").Replace("\n", " ");
    }
}
