param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'CaissonRebar\Addins\R36.Share1'
$receiptPath = Join-Path $installRoot 'install-receipt.json'
$manifest = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\P7.CaissonWallUi.addin'
if (-not (Test-Path -LiteralPath $receiptPath)) { Write-Host 'R36.Share1 is not installed for this user.'; return }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.version -ne 'R36.Share1' -or $receipt.installDirectory -ne $installRoot -or $receipt.manifest -ne $manifest -or $receipt.addinId -ne '2CBE1D75-D85A-4FB4-B353-813503650A47') { throw 'Unexpected receipt. No changes made.' }
if (Test-Path -LiteralPath $manifest) {
    [xml]$current = Get-Content -LiteralPath $manifest -Raw
    $entries = @($current.SelectNodes('/RevitAddIns/AddIn'))
    $ours = $entries.Count -eq 1 -and [string]$entries[0].AddInId -eq $receipt.addinId -and [string]$entries[0].Assembly -eq $receipt.assembly
    # A previous attempt may already have restored the manifest before a locked
    # DLL or receipt prevented cleanup. Accept that exact backed-up manifest so
    # the remaining removal can be retried safely.
    $alreadyRestored = $receipt.previousManifestBackup -and $receipt.previousManifestSha256 -and (Get-FileHash -LiteralPath $manifest).Hash -eq $receipt.previousManifestSha256
    if (-not $ours -and -not $alreadyRestored) { throw 'The manifest has been replaced by another installation. No changes made.' }
}
foreach ($file in $receipt.files) {
    if ([IO.Path]::GetFileName($file.name) -ne $file.name) { throw 'Invalid receipt payload name.' }
    $target = Join-Path $installRoot $file.name
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne $file.sha256) { throw "Installed file changed: $($file.name). No changes made." }
}
$backup = $receipt.previousManifestBackup
if ($backup) {
    $backupRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CaissonRebar\ManifestBackups')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($backup).StartsWith($backupRoot,[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $backup) -or (Get-FileHash -LiteralPath $backup).Hash -ne $receipt.previousManifestSha256) { throw 'Previous-manifest backup is missing or changed. No changes made.' }
}
$running = @(Get-Process -Name Revit -ErrorAction SilentlyContinue).Count -gt 0
if ($DryRun) { [ordered]@{mode='DryRun'; readyToUninstall=(-not $running); restoresPreviousManifest=[bool]$backup; changesMade=$false} | ConvertTo-Json; return }
if ($running) { throw 'Close all Revit windows before uninstalling. No changes made.' }
if ($backup) { Copy-Item -LiteralPath $backup -Destination $manifest -Force }
elseif (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
foreach ($file in $receipt.files) { $target=Join-Path $installRoot $file.name; if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target } }
Remove-Item -LiteralPath $receiptPath
if (@(Get-ChildItem -LiteralPath $installRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $installRoot }
Write-Host 'R36.Share1 uninstalled. Drafts, logs, models, and manifest backups were retained.'
