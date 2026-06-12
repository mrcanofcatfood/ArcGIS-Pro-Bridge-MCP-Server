#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Check if the ArcGIS Pro Add-In Named Pipe is available.
.DESCRIPTION
    Attempts to connect to the Named Pipe and send a ping.
    Exit code 0 = connected, 1 = not available.
#>

$pipeName = "ArcGisProBridgePipe"
$timeout = 2  # seconds

try {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect($timeout * 1000)
    if ($pipe.IsConnected) {
        $writer = New-Object System.IO.StreamWriter($pipe)
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer.WriteLine('{"op":"pro.ping","args":{}}')
        $writer.Flush()
        $response = $reader.ReadLine()
        $pipe.Dispose()
        if ($response -match '"ok"') {
            Write-Output "PIPE_OK"
            exit 0
        }
    }
} catch {
    # Pipe not available
}
Write-Output "PIPE_UNAVAILABLE"
exit 1
