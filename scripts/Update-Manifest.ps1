[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Join-Path $root 'MANIFEST_SHA256.csv'
$rows = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\','/')
    # The prepared distribution's generated/local-file exclusions. Review this
    # list when changing the repository's .gitignore for a future release.
    if ($relative -eq 'MANIFEST_SHA256.csv') { return }
    if ($relative -match '(^|/)(\.git|bin|obj|build|\.vs|\.idea|logs|drafts|runtime|tmp|temp)(/|$)') { return }
    if ($_.Name -match '(?i)\.(pdb|user|suo|log|tmp|bak|zip|7z)$|\.[^.]{4}\.(rvt|rfa)$') { return }
    if ($_.Name -in @('.DS_Store','Thumbs.db') -or $relative -match '(^|/)[^/]*_backup/') { return }
    if ($_.Name -match '^\.env($|\.)' -and $_.Name -ne '.env.example') { return }
    [pscustomobject]@{Path=$relative;Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
if ($rows.Count -eq 0) { throw 'No distribution files were found.' }
$lines = @($rows | Sort-Object Path | ConvertTo-Csv -NoTypeInformation)
[IO.File]::WriteAllLines($manifest,$lines,[Text.UTF8Encoding]::new($true))
Write-Output ('Updated MANIFEST_SHA256.csv for ' + $rows.Count + ' files. Review the file list before release.')
