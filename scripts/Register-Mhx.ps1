#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $BinDirectory,
    [string] $ProfileDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'On Unix, configure PATH and MHX_HOME in the environment of your terminal/GUI applications.' }
$bin = (Resolve-Path -LiteralPath $BinDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $bin 'mhx.exe') -PathType Leaf)) { throw 'BinDirectory must contain mhx.exe.' }
# Match the bootstrap and ProfileLocator.Home; preserve an explicit MHX_HOME.
if (-not [string]::IsNullOrEmpty($ProfileDirectory)) {
    $profileHome = [IO.Path]::GetFullPath($ProfileDirectory, (Get-Location).ProviderPath)
} elseif (-not [string]::IsNullOrEmpty($env:MHX_HOME)) {
    if (-not [IO.Path]::IsPathFullyQualified($env:MHX_HOME)) { throw 'MHX_HOME must be an absolute path.' }
    $profileHome = [IO.Path]::GetFullPath($env:MHX_HOME)
} elseif ($env:XDG_CONFIG_HOME -and [IO.Path]::IsPathFullyQualified($env:XDG_CONFIG_HOME)) {
    $profileHome = [IO.Path]::GetFullPath([IO.Path]::Combine($env:XDG_CONFIG_HOME, 'mhx'))
} else {
    $profileHome = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'MHX')
    if (-not [IO.Path]::IsPathFullyQualified($profileHome)) { throw 'Cannot determine an absolute profile directory.' }
}
if ((Test-Path -LiteralPath $profileHome) -and -not (Test-Path -LiteralPath $profileHome -PathType Container)) { throw 'ProfileDirectory must be a directory.' }
$path = [Environment]::GetEnvironmentVariable('PATH', 'User')
$entries = @($bin) + @($path -split ';' | Where-Object { $_ -and $_.TrimEnd('\', '/') -ine $bin.TrimEnd('\', '/') })
if ($PSCmdlet.ShouldProcess('User environment', 'Register MHX PATH and profile home')) {
    New-Item -ItemType Directory -Path $profileHome -Force | Out-Null
    [Environment]::SetEnvironmentVariable('PATH', ($entries -join ';'), 'User')
    [Environment]::SetEnvironmentVariable('MHX_HOME', $profileHome, 'User')
    $env:PATH = (@($bin) + @($env:PATH -split ';' | Where-Object { $_ -and $_.TrimEnd('\', '/') -ine $bin.TrimEnd('\', '/') })) -join ';'
    $env:MHX_HOME = $profileHome
    Write-Host 'MHX registered. Restart already-running terminals and background harnesses to inherit the new environment.'
}
