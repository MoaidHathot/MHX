#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $Runtime,
    [switch] $NativeAot
)
$ErrorActionPreference = 'Stop'
if (-not $Runtime) {
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported OS.' }
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $Runtime = "$os-$arch"
}
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/MHX/MHX.csproj'
$suffix = if ($NativeAot) { '-aot' } else { '' }
$output = Join-Path $root "artifacts/publish/$Runtime$suffix"
$packages = Join-Path $root 'artifacts/packages'
$arguments = @('publish', $project, '-c', 'Release', '-r', $Runtime, '-o', $output,
    '-p:PackAsTool=false', '-p:DebugType=None', '-p:DebugSymbols=false', '--nologo')
if ($NativeAot) { $arguments += @('--self-contained', 'true', '-p:PublishAot=true') }
else { $arguments += @('--self-contained', 'false', '-p:PublishSingleFile=true') }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'MHX executable publish failed.' }
$staging = Join-Path $root "artifacts/staging/pack-$([Guid]::NewGuid().ToString('N'))"
try {
    # Tool packing globs the publish directory. An isolated directory prevents
    # stale symbols or unrelated outputs from entering a later package.
    # The SDK generates an apphost when installing a framework-dependent tool;
    # the portable .nupkg must not contain this build machine's apphost.
    & dotnet pack $project -c Release -o $packages "-p:PublishDir=$staging$([IO.Path]::DirectorySeparatorChar)" -p:UseAppHost=false --nologo
    if ($LASTEXITCODE -ne 0) { throw 'MHX tool packaging failed.' }
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
$binary = Join-Path $output $(if ($Runtime.StartsWith('win-')) { 'mhx.exe' } else { 'mhx' })
$files = @((Get-Item -LiteralPath $binary)) + @(Get-ChildItem -LiteralPath $packages -Filter 'MHX.*.nupkg')
$lines = @($files | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
})
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
$files | Select-Object Name, Length, FullName
