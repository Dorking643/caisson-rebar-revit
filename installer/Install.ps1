param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$id = '2CBE1D75-D85A-4FB4-B353-813503650A47'
$version = 'R36.Share1'
$dll = 'P7.CaissonWallUi.R36.Share1.dll'
$installRoot = Join-Path $env:LOCALAPPDATA "CaissonRebar\Addins\$version"
$addinRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$manifest = Join-Path $addinRoot 'P7.CaissonWallUi.addin'
$assembly = Join-Path $installRoot $dll
$receiptPath = Join-Path $installRoot 'install-receipt.json'
$expected = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'payload-sha256.json') -Raw | ConvertFrom-Json
if ($expected.version -ne $version -or @($expected.files).Count -eq 0) { throw 'Payload manifest must contain this release version and a nonempty file list.' }
$names = @($expected.files | ForEach-Object { [string]$_.name })
if (@($names | Sort-Object -Unique).Count -ne $names.Count -or $names -notcontains $dll) { throw 'Payload manifest has duplicate names or is missing the main DLL.' }
foreach ($file in $expected.files) {
    if ([string]::IsNullOrWhiteSpace($file.name) -or [IO.Path]::GetFileName($file.name) -ne $file.name -or $file.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid payload name or SHA256.' }
    $source = Join-Path (Join-Path $PSScriptRoot 'payload') $file.name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing payload: $($file.name)" }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.sha256) { throw "Payload checksum mismatch: $($file.name)" }
}
$running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0
$priorText = $null
if (Test-Path -LiteralPath $manifest) {
    $priorText = [IO.File]::ReadAllText($manifest)
    [xml]$prior = $priorText
    $entries = @($prior.SelectNodes('/RevitAddIns/AddIn'))
    if ($entries.Count -ne 1 -or [string]$entries[0].AddInId -ine $id) { throw 'The existing manifest belongs to another add-in. No changes made.' }
}
if ($DryRun) {
    [ordered]@{ mode='DryRun'; payloadChecksums='PASS'; version=$version; revitRunning=$running; readyToInstall=(-not $running); installDirectory=$installRoot; manifest=$manifest; replacesSameAddin=($null -ne $priorText); changesMade=$false } | ConvertTo-Json
    return
}
if ($running) { throw 'Close all Revit windows before installing. No changes made.' }
if (Test-Path -LiteralPath $receiptPath) {
    $existing = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($existing.version -ne $version -or $existing.installDirectory -ne $installRoot) { throw 'Unexpected installation receipt. No changes made.' }
    $same = $true
    foreach ($file in $expected.files) {
        $target = Join-Path $installRoot $file.name
        if (-not (Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $target).Hash -ne $file.sha256) { $same = $false }
    }
    if ($same -and $null -ne $priorText -and [string]$entries[0].Assembly -eq $assembly) { Write-Host 'This release is already installed.'; return }
    throw 'A partial or changed installation exists. Run Uninstall.ps1 before reinstalling.'
}
foreach ($file in $expected.files) {
    $target = Join-Path $installRoot $file.name
    if (Test-Path -LiteralPath $target) { throw "Unexpected existing file: $target. No changes made." }
}
$backup = $null
New-Item -ItemType Directory -Force -Path $installRoot,$addinRoot | Out-Null
if ($null -ne $priorText) {
    $backupRoot = Join-Path $env:LOCALAPPDATA 'CaissonRebar\ManifestBackups'
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $backup = Join-Path $backupRoot ('P7.CaissonWallUi.'+[DateTime]::UtcNow.ToString('yyyyMMdd_HHmmssfff')+'.addin.bak')
    [IO.File]::WriteAllText($backup,$priorText,[Text.UTF8Encoding]::new($false))
}
$escaped = [Security.SecurityElement]::Escape($assembly)
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Caisson Reinforcement R36.Share1</Name>
    <Assembly>$escaped</Assembly>
    <AddInId>$id</AddInId>
    <FullClassName>CaissonWallUi.App</FullClassName>
    <VendorId>P7WU</VendorId>
    <VendorDescription>Wall and slab reinforcement modelling.</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
try {
    foreach ($file in $expected.files) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $PSScriptRoot 'payload') $file.name) -Destination (Join-Path $installRoot $file.name)
    }
    $receipt = [ordered]@{version=$version; installDirectory=$installRoot; manifest=$manifest; assembly=$assembly; addinId=$id; previousManifestBackup=$backup; previousManifestSha256=$(if ($backup) {(Get-FileHash -LiteralPath $backup).Hash} else {$null}); files=$expected.files; installedUtc=[DateTime]::UtcNow.ToString('O')}
    [IO.File]::WriteAllText($receiptPath,($receipt | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($manifest,$xml,[Text.UTF8Encoding]::new($false))
} catch {
    if ($null -ne $priorText) { [IO.File]::WriteAllText($manifest,$priorText,[Text.UTF8Encoding]::new($false)) }
    elseif (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
    foreach ($file in $expected.files) { $target=Join-Path $installRoot $file.name; if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target } }
    if (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }
    throw
}
Write-Host 'Installation complete. Start Revit 2025, open a model, and choose Caisson Reinforcement > Model Creation > Wall or Slab.'
