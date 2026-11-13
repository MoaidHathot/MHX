#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $BinDirectory,
    [Parameter(Mandatory)] [string] $ProfileDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'On Unix, configure PATH and MHX_HOME in the environment of your terminal/GUI applications.' }
$bin = (Resolve-Path -LiteralPath $BinDirectory).Path
$profileHome = (Resolve-Path -LiteralPath $ProfileDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $bin 'mhx.exe') -PathType Leaf)) { throw 'BinDirectory must contain mhx.exe.' }
if (-not (Test-Path -LiteralPath $profileHome -PathType Container)) { throw 'ProfileDirectory must be a directory.' }
$path = [Environment]::GetEnvironmentVariable('PATH', 'User')
$entries = @($path -split ';' | Where-Object { $_ })
if ($entries -notcontains $bin) { $entries += $bin }
if ($PSCmdlet.ShouldProcess('User environment', 'Register MHX PATH and profile home')) {
    [Environment]::SetEnvironmentVariable('PATH', ($entries -join ';'), 'User')
    [Environment]::SetEnvironmentVariable('MHX_HOME', $profileHome, 'User')
    if (($env:PATH -split ';') -notcontains $bin) { $env:PATH += ";$bin" }
    $env:MHX_HOME = $profileHome
    Write-Host 'MHX registered. Restart already-running terminals and background harnesses to inherit the new environment.'
}
