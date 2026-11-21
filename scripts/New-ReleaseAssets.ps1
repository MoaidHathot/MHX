#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Runtime)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if ($Runtime -notin @('win-x64', 'linux-x64', 'osx-arm64')) { throw 'Only verified release platforms may be packaged.' }
$extension = if ($Runtime.StartsWith('win-')) { '.exe' } else { '' }
$source = Join-Path $root "artifacts/publish/$Runtime-aot/mhx$extension"
$reportedVersion = & $source --mhx-version
if ($LASTEXITCODE -ne 0 -or $reportedVersion -cne $version) { throw 'Native release executable version does not match the project.' }
$output = Join-Path $root 'artifacts/release'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = "mhx-$Runtime$extension"
Copy-Item -LiteralPath $source -Destination (Join-Path $output $name)
$files = @(@{
    name=$name; kind='native-aot'; runtime=$Runtime
    sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    size=(Get-Item -LiteralPath $source).Length
})
if ($Runtime -eq 'win-x64') {
    $name = "MHX.$version.nupkg"
    $package = Join-Path $root "artifacts/packages/$name"
    Copy-Item -LiteralPath $package -Destination (Join-Path $output $name)
    $files += @{ name=$name; kind='dotnet-tool'; sha256=(Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant(); size=(Get-Item $package).Length }
}
$json = @{ schemaVersion=1; version=$version; files=$files } | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $output "part-$Runtime.json"), $json.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
