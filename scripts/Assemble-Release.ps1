#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $InputDirectory, [Parameter(Mandatory)] [string] $Tag)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($Tag -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or $Tag -cne "v$version") { throw 'Release tag must match the project stable version.' }
$output = Join-Path $root 'artifacts/release-final'
if (Test-Path $output) { throw 'Release assembly needs an empty destination.' }
New-Item -ItemType Directory $output | Out-Null
$entries = @()
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$parts = @(Get-ChildItem -LiteralPath $InputDirectory -Recurse -Filter 'part-*.json')
if ($parts.Count -ne 3) { throw 'Expected one verified part from each release platform.' }
foreach ($part in $parts) {
    $manifest = Get-Content -LiteralPath $part.FullName -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.version -cne $version) { throw 'Release parts disagree on version.' }
    foreach ($entry in $manifest.files) {
        if ($entry.name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$' -or -not $names.Add($entry.name)) { throw 'Invalid or duplicate release asset.' }
        $source = Join-Path $part.DirectoryName $entry.name
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $entry.sha256 -or (Get-Item $source).Length -ne $entry.size) { throw 'Release part checksum/size mismatch.' }
        Copy-Item -LiteralPath $source -Destination (Join-Path $output $entry.name)
        $entries += $entry
    }
}
$expected = @('mhx-win-x64.exe', 'mhx-linux-x64', 'mhx-osx-arm64', "MHX.$version.nupkg")
if ($names.Count -ne 4 -or @($expected | Where-Object { -not $names.Contains($_) }).Count) { throw 'Release platform/package set is incomplete.' }
$installer = Join-Path $output 'Install-Mhx.ps1'
Copy-Item (Join-Path $root 'scripts/Install-Mhx.ps1') $installer
$entries += @{ name='Install-Mhx.ps1'; kind='installer'; sha256=(Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant(); size=(Get-Item $installer).Length }
$utf8 = [Text.UTF8Encoding]::new($false)
$json = @{ schemaVersion=1; version=$version; files=@($entries | Sort-Object name) } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $output 'mhx-release.json'), $json.Replace("`r`n", "`n") + "`n", $utf8)
$sums = @(Get-ChildItem $output -File | Sort-Object Name | ForEach-Object { "$( (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $($_.Name)" })
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS'), ($sums -join "`n") + "`n", $utf8)
$notes = @"
MHX $version - .NET 11 configurable launcher.

Direct downloads are self-contained Native AOT builds. NuGet tool execution still needs SDK 11+.
The installer selects the latest stable release; no version pin is needed. Use ``mhx update`` afterward.
Private downloads support GH_TOKEN/GITHUB_TOKEN or GitHub CLI authentication.

Assets were tested on Windows x64, Linux x64, and macOS arm64 before publication.
``MHX.$version.nupkg`` is the portable .NET tool package. ``SHA256SUMS`` and ``mhx-release.json`` describe the release contents.

NuGet.org publication is a separate job enabled by the repository's NUGET_USER trusted-publishing configuration.
"@
[IO.File]::WriteAllText((Join-Path $root 'artifacts/release-notes.md'), $notes.Replace("`r`n", "`n"), $utf8)
Get-ChildItem $output | Select-Object Name, Length
