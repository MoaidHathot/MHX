#Requires -Version 7.0
[CmdletBinding()]
param([string] $Executable, [string] $OnlineTool)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$buildProperties = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup
$version = $buildProperties.Version
$targetFramework = $buildProperties.TargetFramework
$extension = if ($IsWindows) { '.exe' } else { '' }
if (-not $Executable) {
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { 'osx' }
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $Executable = Join-Path $root "artifacts/publish/$os-$arch/mhx$extension"
}
$Executable = (Resolve-Path -LiteralPath $Executable).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) "opencode/mhx-package-check-$([Guid]::NewGuid().ToString('N'))"
$feed = Join-Path $root 'artifacts/test-feed'
$packages = Join-Path $root 'artifacts/packages'
$utf8 = [Text.UTF8Encoding]::new($false)
$package = Join-Path $packages "MHX.$version.nupkg"
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -match '\.(exe|pdb)$') { throw 'The portable tool package must not contain apphosts or debug symbols.' }
    }
    $runtimeEntry = $archive.GetEntry("tools/$targetFramework/any/mhx.runtimeconfig.json")
    if (-not $runtimeEntry) { throw "The tool package does not contain $targetFramework runtime metadata." }
    $reader = [IO.StreamReader]::new($runtimeEntry.Open())
    try { $runtimeConfiguration = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($runtimeConfiguration.runtimeOptions.tfm -ne $targetFramework) { throw 'The tool package targets the wrong framework.' }
    $expectedMajor = [int]($targetFramework -replace '^net(\d+)\..*$', '$1')
    if (([version]$runtimeConfiguration.runtimeOptions.framework.version).Major -ne $expectedMajor) {
        throw 'The tool package requires the wrong runtime major version.'
    }
} finally { $archive.Dispose() }

function Invoke-Captured {
    param([string] $File, [string[]] $Arguments, [string] $WorkingDirectory, [hashtable] $Environment, [string] $InputText = '')
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $start.WorkingDirectory = $WorkingDirectory
    $start.Environment.Remove('MHX_CONFIG') | Out-Null
    $start.Environment.Remove('MHX_LAUNCH_DEPTH') | Out-Null
    foreach ($item in $Arguments) { $start.ArgumentList.Add($item) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText)
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(90000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Verification process timed out.'
        }
        return [pscustomobject]@{ Exit = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

New-Item -ItemType Directory -Path $temporary -Force | Out-Null
try {
    & dotnet pack (Join-Path $root 'tests/MHX.TestProbe/MHX.TestProbe.csproj') -c Release -o $feed --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test tool packaging failed.' }
    $profiles = Join-Path $temporary 'profiles'
    $cwd = Join-Path $temporary 'pinned-project'
    New-Item -ItemType Directory -Path $profiles, $cwd -Force | Out-Null
    $cwdMarker = "cwd-$([Guid]::NewGuid().ToString('N'))"
    $cwdMarkerValue = [Guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText((Join-Path $cwd $cwdMarker), $cwdMarkerValue, $utf8)
    [IO.File]::WriteAllText((Join-Path $profiles 'mhx.mhx'), (@{ version=1; kind='dotnet-tool'; sources=@($feed) } | ConvertTo-Json), $utf8)
    [IO.File]::WriteAllText((Join-Path $cwd 'global.json'), '{"sdk":{"version":"8.0.100","rollForward":"disable"}}', $utf8)
    $nugetConfig = Join-Path $temporary 'install.config'
    $escaped = [Security.SecurityElement]::Escape($packages)
    [IO.File]::WriteAllText($nugetConfig, "<configuration><packageSources><clear/><add key=`"local`" value=`"$escaped`"/></packageSources></configuration>", $utf8)
    $environment = @{
        MHX_HOME = $profiles
        DOTNET_CLI_HOME = (Join-Path $temporary 'sdk-home')
        NUGET_PACKAGES = (Join-Path $temporary 'packages')
        NUGET_HTTP_CACHE_PATH = (Join-Path $temporary 'http-cache')
        NUGET_PLUGINS_CACHE_PATH = (Join-Path $temporary 'plugin-cache')
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        PROBE_EXIT = '23'
    }
    $tools = Join-Path $temporary 'tools'
    $dotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $install = Invoke-Captured -File $dotnet -Arguments @(
        'tool', 'install', 'MHX', '--version', $version, '--tool-path', $tools, '--configfile', $nugetConfig
    ) -WorkingDirectory $root -Environment $environment
    if ($install.Exit -ne 0) { throw "Local MHX installation failed: $($install.Error) $($install.Output)" }
    $payload = "{`"jsonrpc`":`"2.0`",`"id`":1,`"result`":`"héllo 世界`"}`n"
    $caller = @('', 'two words', 'a"b', '--%', '%PATH%', 'a&b|c', '雪')
    foreach ($entry in @($Executable, (Join-Path $tools "mhx$extension"))) {
        foreach ($cache in @('cold', 'warm')) {
            # Each distribution gets a separate cold tool cache.
            $result = Invoke-Captured -File $entry -Arguments (@("MHX.TestProbe@$version", '--') + $caller) -WorkingDirectory $cwd -Environment $environment -InputText $payload
            if ($result.Exit -ne 23) { throw "Tool execution failed ($($result.Exit)): $($result.Error) $($result.Output)" }
            if ($result.Output -cne $payload) {
                throw "Tool stdout was changed or polluted. Expected: $($payload | ConvertTo-Json -Compress) Actual: $($result.Output | ConvertTo-Json -Compress)"
            }
            $report = $result.Error | ConvertFrom-Json
            if (($report.args | ConvertTo-Json -Compress) -cne ($caller | ConvertTo-Json -Compress)) { throw 'Tool arguments changed.' }
            # macOS resolves /var through /private/var in getcwd(). Compare the
            # selected directory's identity rather than its lexical spelling.
            if ([IO.File]::ReadAllText((Join-Path $report.cwd $cwdMarker)) -cne $cwdMarkerValue) {
                throw 'Tool working directory changed.'
            }
            Write-Host "PASS $cache cache: $entry"
        }
        $environment.DOTNET_CLI_HOME = Join-Path $temporary 'second-sdk-home'
        $environment.NUGET_PACKAGES = Join-Path $temporary 'second-packages'
    }
    if ($OnlineTool) {
        # Explicit opt-in integration smoke test: execute the selected tool's help
        # through the built-in v2 backend, with fresh SDK/package/HTTP caches.
        $environment.MHX_HOME = Join-Path $temporary 'online-profiles'
        $environment.DOTNET_CLI_HOME = Join-Path $temporary 'online-sdk-home'
        $environment.NUGET_PACKAGES = Join-Path $temporary 'online-packages'
        $environment.NUGET_HTTP_CACHE_PATH = Join-Path $temporary 'online-http'
        $result = Invoke-Captured -File $Executable -Arguments @($OnlineTool, '--', '--help') -WorkingDirectory $cwd -Environment $environment
        if ($result.Exit -ne 0) { throw "Online tool help failed ($($result.Exit)): $($result.Error) $($result.Output)" }
        if ([string]::IsNullOrWhiteSpace($result.Output)) { throw 'Online tool help produced no output.' }
        Write-Host "PASS NuGet v2 cold-cache execution: $OnlineTool"
    }
} finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
