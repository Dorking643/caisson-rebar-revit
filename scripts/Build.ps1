param(
    [string]$RevitApiDir = 'C:\Program Files\Autodesk\Revit 2025',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build')
)
$ErrorActionPreference = 'Stop'
foreach ($file in 'RevitAPI.dll','RevitAPIUI.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $RevitApiDir $file) -PathType Leaf)) { throw "Missing $file in RevitApiDir." }
}
$project = Join-Path $PSScriptRoot '..\src\CaissonWall.UiReview.Revit2025\CaissonWall.UiReview.Revit2025.csproj'
& dotnet build $project -c Release "-p:RevitApiDir=$RevitApiDir" -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Host "Build complete: $OutputDirectory"
