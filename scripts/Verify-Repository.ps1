[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = Join-Path $root 'MANIFEST_SHA256.csv'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw 'MANIFEST_SHA256.csv is missing.' }
$failed = [Collections.Generic.List[string]]::new()
$rows = @(Import-Csv -LiteralPath $manifest -Encoding UTF8)
foreach ($row in $rows) {
    $path = [IO.Path]::GetFullPath((Join-Path $root $row.Path))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A manifest path is outside the repository.'
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $failed.Add('Missing: ' + $row.Path); continue }
    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($item.Length -ne [long]$row.Bytes -or $hash -ne $row.SHA256) { $failed.Add('Changed: ' + $row.Path) }
}
if ($failed.Count -gt 0) { $failed | Write-Output; throw ('Verification failed for ' + $failed.Count + ' file(s).') }
Write-Output ('PASS: ' + $rows.Count + ' listed files match the release manifest.')
