#Requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Executable)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$build = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup
$version = [version]$build.Version
$nextVersion = "$($version.Major).$($version.Minor).$($version.Build + 1)"
$extension = if ($IsWindows) { '.exe' } else { '' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) "opencode/mhx-distribution-test-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $temporary -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$oldProcess = $null
$helper = $null
$saved = @{}
foreach ($name in @('DOTNET_CLI_HOME', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO', 'MHX_HOME', 'XDG_CONFIG_HOME')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    $env:DOTNET_CLI_HOME = Join-Path $temporary 'sdk'
    $env:NUGET_PACKAGES = Join-Path $temporary 'packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $temporary 'http'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    . (Join-Path $root 'scripts/Install-Mhx.ps1') -LibraryOnly
    [Environment]::SetEnvironmentVariable('MHX_HOME', $null)
    [Environment]::SetEnvironmentVariable('XDG_CONFIG_HOME', $null)
    $fallbackProfiles = Get-MhxProfileDirectory
    $env:XDG_CONFIG_HOME = Join-Path $temporary 'xdg-config'
    $expectedProfiles = Join-Path $env:XDG_CONFIG_HOME 'mhx'
    if ((Get-MhxProfileDirectory) -ne $expectedProfiles) { throw 'Bootstrap does not use XDG_CONFIG_HOME/mhx.' }
    $env:MHX_HOME = Join-Path $temporary 'custom-profiles'
    if ((Get-MhxProfileDirectory) -ne $env:MHX_HOME) { throw 'Bootstrap did not preserve MHX_HOME.' }
    $explicitProfiles = Join-Path $temporary 'explicit-profiles'
    if ((Get-MhxProfileDirectory $explicitProfiles) -ne $explicitProfiles) { throw 'Explicit profile directory lost precedence.' }
    $env:MHX_HOME = 'invalid-relative'
    if ((Get-MhxProfileDirectory $explicitProfiles) -ne $explicitProfiles) { throw 'Explicit profile directory must not depend on unused defaults.' }
    $invalidRejected = $false
    try { Get-MhxProfileDirectory | Out-Null } catch { $invalidRejected = $true }
    if (-not $invalidRejected) { throw 'Relative MHX_HOME was accepted.' }
    [Environment]::SetEnvironmentVariable('MHX_HOME', $null)
    $env:XDG_CONFIG_HOME = 'relative'
    if ((Get-MhxProfileDirectory) -ne $fallbackProfiles) { throw 'Relative XDG_CONFIG_HOME must be ignored.' }
    $env:XDG_CONFIG_HOME = Join-Path $temporary 'xdg-config'
    Write-Host 'PASS bootstrap profile-directory defaults and override precedence'
    # Network seams only: real hash verification and real installer execution.
    $fixturePlan = @{ Version=$build.Version; File=@{ size=(Get-Item $executablePath).Length; sha256=(Get-FileHash $executablePath -Algorithm SHA256).Hash } }
    function Get-MhxToken { return $null }
    function Get-MhxReleasePlan { return $fixturePlan }
    function Save-MhxAsset($Client, $Plan, [string] $Destination) { Copy-Item -LiteralPath $executablePath -Destination $Destination }
    $nativeDirectory = Join-Path $temporary 'native'
    Install-Mhx -InstallDirectory $nativeDirectory -NoRegister
    Install-Mhx -InstallDirectory $nativeDirectory -NoRegister
    if (Test-Path -LiteralPath $expectedProfiles) { throw 'NoRegister created a profile directory.' }
    if ($IsWindows) {
        & (Join-Path $root 'scripts/Register-Mhx.ps1') -BinDirectory $nativeDirectory -WhatIf
        if (Test-Path -LiteralPath $expectedProfiles) { throw 'Registration WhatIf created a profile directory.' }
    }
    $native = Join-Path $nativeDirectory "mhx$extension"
    if ((& $native --mhx-version) -ne $build.Version) { throw 'Installed native version is wrong.' }
    if (-not $IsWindows) {
        # Unix registration prints environment instructions; it does not edit
        # shell configuration. Exercise managed defaults without registry writes.
        & $native --mhx-install --directory $nativeDirectory
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $expectedProfiles -PathType Container)) { throw 'Native installer did not create its default XDG profile directory.' }
        $expectedMode = [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute
        if ([IO.File]::GetUnixFileMode($expectedProfiles) -ne $expectedMode) { throw 'New profile directory must be private.' }
    }
    $initialHash = (Get-FileHash $native -Algorithm SHA256).Hash
    $fixturePlan.File.sha256 = '0' * 64
    $rejected = $false
    try { Install-Mhx -InstallDirectory $nativeDirectory -NoRegister } catch { $rejected = $true }
    if (-not $rejected -or (Get-FileHash $native -Algorithm SHA256).Hash -ne $initialHash) { throw 'Corrupt download changed the installation.' }
    $fixturePlan.File.sha256 = $initialHash
    Write-Host 'PASS installer idempotency and checksum failure preservation'

    $feed = Join-Path $temporary 'feed'
    $packPublish = Join-Path $temporary 'pack-publish'
    New-Item -ItemType Directory $feed -Force | Out-Null
    Copy-Item (Join-Path $root "artifacts/packages/MHX.$($build.Version).nupkg") $feed
    & dotnet pack (Join-Path $root 'src/MHX/MHX.csproj') -c Release -o $feed "-p:Version=$nextVersion" -p:UseAppHost=false "-p:PublishDir=$packPublish$([IO.Path]::DirectorySeparatorChar)" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Upgrade fixture pack failed.' }
    $nuget = Join-Path $temporary 'nuget.config'
    [IO.File]::WriteAllText($nuget, "<configuration><packageSources><clear/><add key=`"fixture`" value=`"$([Security.SecurityElement]::Escape($feed))`"/></packageSources></configuration>", $utf8)
    $tools = Join-Path $temporary 'tools'
    & dotnet tool install MHX --version $build.Version --tool-path $tools --configfile $nuget
    if ($LASTEXITCODE -ne 0) { throw 'Initial tool installation failed.' }
    $tool = Join-Path $tools "mhx$extension"
    $probe = Join-Path $root "tests/MHX.TestProbe/bin/Release/$($build.TargetFramework)/MHX.TestProbe$extension"
    $profile = Join-Path $temporary 'hold.mhx'
    [IO.File]::WriteAllText($profile, (@{command=$probe} | ConvertTo-Json), $utf8)
    $start = [Diagnostics.ProcessStartInfo]::new($tool)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--mhx-config'); $start.ArgumentList.Add($profile)
    $oldProcess = [Diagnostics.Process]::Start($start)
    $ready = $oldProcess.StandardError.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
    if (-not $ready.StartsWith('{')) { throw "Installed tool did not start the probe: $ready" }
    $staging = Join-Path $temporary 'helper'
    New-Item -ItemType Directory $staging -Force | Out-Null
    $payload = Get-ChildItem -LiteralPath (Join-Path $tools '.store') -Recurse -Filter mhx.dll | Select-Object -First 1
    foreach ($name in @('mhx.dll', 'mhx.runtimeconfig.json', 'mhx.deps.json')) { Copy-Item (Join-Path $payload.DirectoryName $name) $staging }
    $package = Join-Path $staging "MHX.$nextVersion.nupkg"
    Copy-Item (Join-Path $feed "MHX.$nextVersion.nupkg") $package
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $sdkLine = @(& $dotnet --list-sdks)[-1]
    if ($sdkLine -notmatch '^(\S+)\s+\[(.+)\]$') { throw 'Cannot parse SDK location.' }
    $sdk = Join-Path $Matches[2] "$($Matches[1])/dotnet.dll"
    $request = @{ parentId=$oldProcess.Id; parentStartTicks=$oldProcess.StartTime.ToUniversalTime().Ticks; installDirectory=$tools; version=$nextVersion; sha256=(Get-FileHash $package -Algorithm SHA256).Hash; dotnet=$dotnet; sdk=$sdk }
    $requestPath = Join-Path $staging 'request.json'
    [IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json), $utf8)
    [IO.File]::WriteAllText((Join-Path $staging '.mhx-tool-update'), '1', $utf8)
    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($arg in @((Join-Path $staging 'mhx.dll'), '--mhx-complete-tool-update', $requestPath)) { $start.ArgumentList.Add($arg) }
    $helper = [Diagnostics.Process]::Start($start)
    $stdout = $helper.StandardOutput.ReadToEndAsync()
    $stderr = $helper.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path (Join-Path $staging '.waiting-for-parent'))) {
        if ($helper.HasExited) { throw "Tool updater did not reach the parent-exit handoff: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())" }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Tool updater did not reach the parent-exit handoff before the deadline.' }
        Start-Sleep -Milliseconds 20
    }
    if ($helper.HasExited) { throw 'Updater must wait for the running tool to exit.' }
    $oldProcess.StandardInput.Close()
    if (-not $oldProcess.WaitForExit(15000)) { throw 'Old tool did not exit.' }
    $oldProcess.Dispose(); $oldProcess = $null
    if (-not $helper.WaitForExit(120000)) { throw 'Tool updater timed out.' }
    $out = $stdout.GetAwaiter().GetResult()
    $err = $stderr.GetAwaiter().GetResult()
    if ($helper.ExitCode -ne 0) { throw "Tool updater failed: $err $out" }
    if ((& $tool --mhx-version) -ne $nextVersion) { throw 'Installed tool did not advance to the new version.' }
    if ((Get-Content (Join-Path $staging 'status.json') -Raw | ConvertFrom-Json).state -ne 'completed') { throw 'Missing completed update status.' }
    Write-Host 'PASS real .NET-tool update after the old process exits'
} finally {
    foreach ($process in @($oldProcess, $helper)) {
        if ($process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
