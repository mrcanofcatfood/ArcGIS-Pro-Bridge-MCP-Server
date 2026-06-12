using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace APBridgeAddIn.Plugins;

[ProBridgeHandler("pro.plugin.fieldCalculator")]
public class FieldCalculatorHandler : IProBridgeHandler
{
    public string Op => "pro.plugin.fieldCalculator";

    public Task<IpcResponse> Handle(IpcRequest request, CancellationToken cancellationToken)
    {
        return QueuedTask.Run<IpcResponse>(async () =>
        {
            var args = request.Args;
            if (args == null || !args.TryGetValue("layer", out var layerName) || !args.TryGetValue("field", out var field) || !args.TryGetValue("expression", out var expression))
                return new IpcResponse(false, "args 'layer', 'field', & 'expression' required", null);

            var mapView = MapView.Active;
            if (mapView == null)
                return new IpcResponse(false, "No active map view", null);

            var layer = mapView.Map.GetLayersAsFlattenedList()
                .OfType<FeatureLayer>()
                .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

            if (layer == null)
                return new IpcResponse(false, $"Layer '{layerName}' not found", null);

            string fcPath;
            try
            {
                fcPath = layer.GetFeatureClass().GetPath().ToString();
            }
            catch (Exception ex)
            {
                return new IpcResponse(false, $"Cannot get feature class path: {SanitizeException(ex)}", null);
            }

            var escapedFcPath = fcPath.Replace("\\", "\\\\").Replace("'", "\\'");
            var escapedField = field.Replace("'", "\\'");
            var escapedExpr = expression.Replace("\\", "\\\\").Replace("'", "\\'");

            var pythonCode = $@"import arcpy, sys
try:
    arcpy.env.overwriteOutput = True
    fc = '{escapedFcPath}'
    field = '{escapedField}'
    expr = '{escapedExpr}'
    result = arcpy.management.CalculateField(fc, field, expr, 'PYTHON3')
    print(f""OK row_count={{result[1]}}"")
except Exception as e:
    print(f""ERROR {{e}}"")
    sys.exit(1)
";

            var response = await RunPythonSubprocess(pythonCode, 120);
            if (!response.Ok)
                return response;

            dynamic data = response.Data;
            string stdout = data.stdout;
            string stderr = data.stderr;
            int exitCode = data.exitCode;

            if (exitCode != 0)
                return new IpcResponse(false, $"CalculateField failed: {stderr ?? stdout}", new { field, expression, exitCode, stderr, stdout });

            string warning = null;
            int rowCount = 0;
            if (stdout != null && stdout.Contains("OK row_count="))
            {
                var parts = stdout.Split(new[] { "OK row_count=" }, StringSplitOptions.None);
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var rc))
                    rowCount = rc;
            }
            else
            {
                warning = "Unexpected output from CalculateField";
            }

            return new IpcResponse(true, null, new { field, expression, rowCount, warning });
        });
    }

    private static async Task<IpcResponse> RunPythonSubprocess(string code, int timeoutSec)
    {
        string pythonExe = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ESRI\ArcGISPro");
            if (key?.GetValue("InstallDir") is string installDir)
            {
                var exe = Path.Combine(installDir, "bin", "Python", "envs", "arcgispro-py3", "python.exe");
                if (File.Exists(exe)) pythonExe = exe;
            }
        }
        catch { }

        if (pythonExe == null)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            pythonExe = Path.Combine(pf, @"ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe");
        }

        if (!File.Exists(pythonExe))
            return new IpcResponse(false, "ArcGIS Pro Python not found", null);

        var tempFile = Path.GetTempFileName() + ".py";
        await File.WriteAllTextAsync(tempFile, code);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{tempFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                }
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            process.WaitForExit(timeoutSec * 1000);
            return new IpcResponse(true, null, new { stdout, stderr, exitCode = process.ExitCode });
        }
        finally
        {
            try { File.Delete(tempFile); } catch { }
        }
    }

    private static string SanitizeException(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 500) msg = msg[..500];
        return msg.Replace("\r\n", " ").Replace("\n", " ");
    }
}
