param($Action = "package")

$projDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir = Join-Path $projDir "bin\Debug\net8.0-windows8.0"
$tempDir = Join-Path $projDir "obj\esri_package"
$esriAddinX = Join-Path $outDir "APBridgeAddIn.esriAddinX"
$addInId = "{56c9c0f7-2a8e-4b3d-9f1a-7e5d2c8b1a4f}"

# ArcGIS Pro checks BOTH locations; use Documents (OneDrive-safe) as primary
$docsPath = [Environment]::GetFolderPath('MyDocuments')
$addInPaths = @(
    (Join-Path $docsPath "ArcGIS\AddIns\ArcGISPro\$addInId"),
    "$env:LOCALAPPDATA\ArcGIS\AddIns\ArcGISPro\$addInId"
)

function Package {
    Write-Output "Packaging APBridgeAddIn.esriAddinX..."
    if (Test-Path $tempDir) { Remove-Item -Recurse -Force $tempDir }
    $null = New-Item -ItemType Directory -Path (Join-Path $tempDir "Install") -Force
    Copy-Item (Join-Path $projDir "Config.daml") (Join-Path $tempDir "Config.daml")
    Copy-Item (Join-Path $outDir "APBridgeAddIn.dll") (Join-Path $tempDir "Install\APBridgeAddIn.dll")
    # Include NuGet dependency DLLs needed at runtime
    $extraDlls = @("Python.Runtime.dll", "Microsoft.CSharp.dll")
    foreach ($dll in $extraDlls) {
        $src = Join-Path $outDir $dll
        if (Test-Path $src) { Copy-Item $src (Join-Path $tempDir "Install\$dll") }
    }
    # Build and collect plugin DLLs
    $pluginsDir = Join-Path $projDir "..\plugins"
    if (Test-Path $pluginsDir) {
        Get-ChildItem $pluginsDir -Directory | ForEach-Object {
            $pluginProj = Join-Path $_.FullName "*.csproj"
            $csproj = Get-ChildItem $pluginProj | Select-Object -First 1
            if ($csproj) {
                Write-Output "  Building plugin: $($_.Name)..."
                $pluginOut = Join-Path $_.FullName "bin\Debug\net8.0-windows8.0"
                & dotnet build $csproj.FullName --no-restore -v q 2>&1 | Out-Null
                $pluginDll = Join-Path $pluginOut "$($_.Name).dll"
                if (Test-Path $pluginDll) {
                    Copy-Item $pluginDll (Join-Path $tempDir "Install\$($_.Name).dll")
                    Write-Output "    -> $($_.Name).dll"
                }
            }
        }
    }
    if (Test-Path $esriAddinX) { Remove-Item $esriAddinX }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempDir, $esriAddinX)
    Remove-Item -Recurse -Force $tempDir
    $size = (Get-Item $esriAddinX).Length
    Write-Output "Created: $esriAddinX ($size bytes)"
}

function Install {
    Package
    Write-Output "Deploying add-in..."
    foreach ($addInPath in $addInPaths) {
        if (Test-Path $addInPath) { Remove-Item -Recurse -Force $addInPath }
        $null = New-Item -ItemType Directory -Path (Join-Path $addInPath "Install") -Force
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($esriAddinX, $addInPath)
    }
    Write-Output "Add-in installed. Start ArcGIS Pro to load it."
}

function Uninstall {
    foreach ($addInPath in $addInPaths) {
        if (Test-Path $addInPath) {
            Remove-Item -Recurse -Force $addInPath
            Write-Output "Removed: $addInPath"
        }
    }
}

switch ($Action.ToLower()) {
    "package" { Package }
    "install" { Install }
    "uninstall" { Uninstall }
    default { Write-Output "Usage: .\package-addin.ps1 [package|install|uninstall]" }
}
