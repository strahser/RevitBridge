# Copies RevitBridge addin binaries + .addin manifest into
# %AppData%\Autodesk\Revit\Addins\2024\RevitBridge\  (ASCII only, PS 5.1)
param(
  [string]$Configuration = "Release.R24",
  [string]$RevitYear = "2024"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$dll = Get-ChildItem -LiteralPath (Join-Path $root "RevitBridge.Revit\bin") -Filter "RevitBridge.Revit.dll" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -like "*$Configuration*" } | Select-Object -First 1
if ($null -eq $dll) { throw "DLL not found under bin (*$Configuration*). Build the csproj first." }
$dll = $dll.FullName
$manifest = Join-Path $root "RevitBridge.Revit\RevitBridge.addin"
if (-not (Test-Path -LiteralPath $dll)) { throw "DLL not found: $dll. Build the csproj first." }
if (-not (Test-Path -LiteralPath $manifest)) { throw "Manifest not found: $manifest" }

$dest = Join-Path ([Environment]::GetFolderPath("ApplicationData")) "Autodesk\Revit\Addins\$RevitYear\RevitBridge"
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $dest "RevitBridge.Revit.dll") -Force
$deps = @("Newtonsoft.Json.dll", "RevitBridge.Contracts.dll", "RevitBridge.Core.dll")
foreach ($d in $deps) {
  $src = Join-Path (Split-Path -Parent $dll) $d
  if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $dest $d) -Force }
}
$addinText = Get-Content -LiteralPath $manifest -Raw
Set-Content -LiteralPath (Join-Path (Split-Path -Parent $dest) "RevitBridge.addin") -Value $addinText -Encoding UTF8
Write-Output "Installed to $dest"
Get-ChildItem -LiteralPath $dest | Select-Object Name, Length
