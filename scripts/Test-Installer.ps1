param([string]$WorkDirectory = (Join-Path $PSScriptRoot '..\build\installer-tests'))
$ErrorActionPreference = 'Stop'
$installer = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer'))
$sandbox = [IO.Path]::GetFullPath((Join-Path $WorkDirectory ([Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$originalAppData = $env:APPDATA
$originalLocalAppData = $env:LOCALAPPDATA
$results = [Collections.Generic.List[object]]::new()
function Assert-Check([string]$Name,[bool]$Pass) { $results.Add([ordered]@{name=$Name;pass=$Pass}); if (-not $Pass) { throw "Check failed: $Name" } }
# All installation paths are redirected to this newly created sandbox. The
# process probe is mocked only here, so a live Revit session is never touched.
$global:CaissonRebarTestRevitRunning = $false
function Get-Process { [CmdletBinding()]param([string]$Name) if ($global:CaissonRebarTestRevitRunning) { return [pscustomobject]@{ProcessName='Revit'} }; return @() }
try {
    $env:APPDATA = Join-Path $sandbox 'Roaming'
    $env:LOCALAPPDATA = Join-Path $sandbox 'Local'
    $dry = & (Join-Path $installer 'Install.ps1') -DryRun | ConvertFrom-Json
    Assert-Check 'dry_run_writes_no_install_files' (-not (Test-Path -LiteralPath $env:APPDATA) -and -not (Test-Path -LiteralPath $env:LOCALAPPDATA) -and -not $dry.changesMade)
    $global:CaissonRebarTestRevitRunning=$true
    $blocked=$false; try { & (Join-Path $installer 'Install.ps1') } catch { $blocked=$true }
    Assert-Check 'running_revit_blocks_install_without_writes' ($blocked -and -not (Test-Path -LiteralPath $env:APPDATA) -and -not (Test-Path -LiteralPath $env:LOCALAPPDATA))
    $global:CaissonRebarTestRevitRunning=$false
    $badInstaller = Join-Path $sandbox 'bad-installer'
    New-Item -ItemType Directory -Path $badInstaller | Out-Null
    Copy-Item -LiteralPath (Join-Path $installer 'Install.ps1') -Destination $badInstaller
    $mainFile = @(Get-Content -LiteralPath (Join-Path $installer 'payload-sha256.json') -Raw | ConvertFrom-Json)[0].files | Where-Object name -like '*.dll'
    foreach ($case in @(
        @{name='empty_payload_rejected';value=@{version='R36.Share1';files=@()}},
        @{name='wrong_payload_version_rejected';value=@{version='R99';files=@($mainFile)}},
        @{name='missing_main_dll_rejected';value=@{version='R36.Share1';files=@(@{name='other.dll';sha256=('0'*64)})}},
        @{name='duplicate_payload_names_rejected';value=@{version='R36.Share1';files=@($mainFile,$mainFile)}}
    )) {
        [IO.File]::WriteAllText((Join-Path $badInstaller 'payload-sha256.json'),($case.value|ConvertTo-Json -Depth 6))
        $blocked=$false; try { & (Join-Path $badInstaller 'Install.ps1') -DryRun } catch { $blocked=$true }
        Assert-Check $case.name $blocked
    }
    $manifest = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\P7.CaissonWallUi.addin'
    $root = Join-Path $env:LOCALAPPDATA 'CaissonRebar\Addins\R36.Share1'
    & (Join-Path $installer 'Install.ps1')
    Assert-Check 'new_install_creates_manifest_and_receipt' ((Test-Path -LiteralPath $manifest) -and (Test-Path -LiteralPath (Join-Path $root 'install-receipt.json')))
    [xml]$m = Get-Content -LiteralPath $manifest -Raw
    Assert-Check 'manifest_points_to_installed_dll' (Test-Path -LiteralPath ([string]$m.RevitAddIns.AddIn.Assembly))
    $hash = (Get-FileHash -LiteralPath $manifest).Hash
    & (Join-Path $installer 'Install.ps1')
    Assert-Check 'repeat_install_is_idempotent' ($hash -eq (Get-FileHash -LiteralPath $manifest).Hash)
    $uninstallDry = & (Join-Path $installer 'Uninstall.ps1') -DryRun | ConvertFrom-Json
    Assert-Check 'uninstall_dry_run_preserves_manifest' ((Test-Path -LiteralPath $manifest) -and -not $uninstallDry.changesMade)
    & (Join-Path $installer 'Uninstall.ps1')
    Assert-Check 'new_install_uninstalls_cleanly' (-not (Test-Path -LiteralPath $manifest) -and -not (Test-Path -LiteralPath $root))
    $old = '<RevitAddIns><AddIn Type="Application"><Name>Previous Release</Name><Assembly>C:\Example\previous.dll</Assembly><AddInId>2CBE1D75-D85A-4FB4-B353-813503650A47</AddInId></AddIn></RevitAddIns>'
    [IO.File]::WriteAllText($manifest,$old,[Text.UTF8Encoding]::new($false))
    & (Join-Path $installer 'Install.ps1')
    $lockedReceipt = [IO.File]::Open((Join-Path $root 'install-receipt.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try { $blocked=$false; try { & (Join-Path $installer 'Uninstall.ps1') } catch { $blocked=$true }; Assert-Check 'locked_receipt_leaves_retryable_state' ($blocked -and [IO.File]::ReadAllText($manifest) -ceq $old) } finally { $lockedReceipt.Dispose() }
    & (Join-Path $installer 'Uninstall.ps1')
    Assert-Check 'uninstall_retry_completes_after_restoration' (-not (Test-Path -LiteralPath $root))
    Assert-Check 'previous_manifest_restored_verbatim' ([IO.File]::ReadAllText($manifest) -ceq $old)
    [IO.File]::WriteAllText($manifest,$old.Replace('2CBE1D75-D85A-4FB4-B353-813503650A47','00000000-0000-0000-0000-000000000000'))
    $blocked = $false
    try { & (Join-Path $installer 'Install.ps1') } catch { $blocked = $true }
    Assert-Check 'foreign_manifest_rejected_without_install' ($blocked -and -not (Test-Path -LiteralPath $root))
    $result = [ordered]@{status='PASS';passed=$results.Count;failed=0;checks=$results}
    $json = $result | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $sandbox 'result.json'),$json,[Text.UTF8Encoding]::new($false))
    Write-Output $json
} finally { $env:APPDATA=$originalAppData; $env:LOCALAPPDATA=$originalLocalAppData; Remove-Variable -Name CaissonRebarTestRevitRunning -Scope Global -ErrorAction SilentlyContinue }
