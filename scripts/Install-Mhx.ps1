#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('native', 'dotnet-tool')] [string] $Method = 'native',
    [string] $InstallDirectory,
    [string] $ProfileDirectory,
    [switch] $NoRegister,
    [switch] $LibraryOnly
)
$ErrorActionPreference = 'Stop'

function Get-MhxProfileDirectory([string] $ExplicitDirectory) {
    if (-not [string]::IsNullOrEmpty($ExplicitDirectory)) {
        return [IO.Path]::GetFullPath($ExplicitDirectory, (Get-Location).ProviderPath)
    }
    if (-not [string]::IsNullOrEmpty($env:MHX_HOME)) {
        if (-not [IO.Path]::IsPathFullyQualified($env:MHX_HOME)) { throw 'MHX_HOME must be an absolute path.' }
        return [IO.Path]::GetFullPath($env:MHX_HOME)
    }
    if ($env:XDG_CONFIG_HOME -and [IO.Path]::IsPathFullyQualified($env:XDG_CONFIG_HOME)) {
        return [IO.Path]::GetFullPath([IO.Path]::Combine($env:XDG_CONFIG_HOME, 'mhx'))
    }
    $fallback = if ($IsWindows) {
        [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'MHX')
    } else {
        [IO.Path]::Combine([Environment]::GetFolderPath('UserProfile'), '.config', 'mhx')
    }
    if (-not [IO.Path]::IsPathFullyQualified($fallback)) { throw 'Cannot determine an absolute profile directory.' }
    return $fallback
}

function Get-MhxToken {
    foreach ($name in @('GH_TOKEN', 'GITHUB_TOKEN')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
    }
    $gh = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($gh) {
        $value = & $gh.Source auth token --hostname github.com 2>$null
        if ($LASTEXITCODE -eq 0) { return ($value -join '').Trim() }
    }
    return $null
}

function Assert-MhxUrl([uri] $Uri) {
    if ($Uri.Scheme -ne 'https' -or -not $Uri.IsDefaultPort -or $Uri.UserInfo -or $Uri.Fragment -or
        $Uri.DnsSafeHost -notin @('api.github.com', 'github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')) {
        throw 'Release request left the allowed HTTPS hosts.'
    }
}

function Get-MhxResponse($Client, [uri] $Uri, [string] $Accept, [string] $Token, $CancellationToken) {
    for ($i = 0; $i -lt 6; $i++) {
        Assert-MhxUrl $Uri
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $Uri)
        try {
            $request.Headers.UserAgent.ParseAdd('MHX-Bootstrap/1')
            $request.Headers.Accept.ParseAdd($Accept)
            $request.Headers.Add('X-GitHub-Api-Version', '2022-11-28')
            if ($Token -and $Uri.DnsSafeHost -eq 'api.github.com') {
                $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Token)
            }
            $response = $Client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $CancellationToken).GetAwaiter().GetResult()
        } finally { $request.Dispose() }
        if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
            $location = $response.Headers.Location
            $response.Dispose()
            if (-not $location) { throw 'Invalid release redirect.' }
            $Uri = [uri]::new($Uri, $location)
            continue
        }
        if (-not $response.IsSuccessStatusCode) {
            $status = [int]$response.StatusCode
            $response.Dispose()
            throw "Release request failed (HTTP $status). Private releases require GH_TOKEN/GITHUB_TOKEN or gh auth login; check release availability and rate limits."
        }
        return $response
    }
    throw 'Too many release redirects.'
}

function ConvertFrom-MhxJson([byte[]] $Bytes) {
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 16
    $document = [Text.Json.JsonDocument]::Parse($text, $options)
    function Test-JsonNode($Node) {
        if ($Node.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($property in $Node.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw 'Duplicate release metadata property.' }
                Test-JsonNode $property.Value
            }
        } elseif ($Node.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
            foreach ($item in $Node.EnumerateArray()) { Test-JsonNode $item }
        }
    }
    try { Test-JsonNode $document.RootElement } finally { $document.Dispose() }
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 16
}

function Read-MhxMetadata($Client, [uri] $Uri, [string] $Accept, [string] $Token, $CancellationToken) {
    $response = Get-MhxResponse $Client $Uri $Accept $Token $CancellationToken
    try {
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(8192)
            while (($count = $stream.ReadAsync($buffer, 0, $buffer.Length, $CancellationToken).GetAwaiter().GetResult()) -gt 0) {
                if ($output.Length + $count -gt 1048576) { throw 'Release metadata is too large.' }
                $output.Write($buffer, 0, $count)
            }
            return ,$output.ToArray()
        } finally { $stream.Dispose(); $output.Dispose() }
    } finally { $response.Dispose() }
}

function Get-MhxAsset($Release, [string] $Name) {
    $assets = @($Release.assets | Where-Object { $_.name -ceq $Name -and $_.id -gt 0 })
    if ($assets.Count -ne 1) { throw 'Required release asset is missing or ambiguous.' }
    return $assets[0]
}

function Get-MhxReleasePlan($Client, [string] $Token, $CancellationToken) {
    $api = 'https://api.github.com/repos/MoaidHathot/MHX'
    $release = ConvertFrom-MhxJson (Read-MhxMetadata $Client "$api/releases/latest" 'application/vnd.github+json' $Token $CancellationToken)
    if ($release.draft -or $release.prerelease -or $release.tag_name -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or $release.assets.Count -gt 64) {
        throw 'Expected a published stable MHX release.'
    }
    $asset = Get-MhxAsset $release 'mhx-release.json'
    if ($asset.size -le 0 -or $asset.size -gt 1048576) { throw 'Invalid release manifest size.' }
    $bytes = Read-MhxMetadata $Client "$api/releases/assets/$($asset.id)" 'application/octet-stream' $Token $CancellationToken
    if ($bytes.Length -ne $asset.size) { throw 'Incomplete release manifest.' }
    if ($asset.digest) {
        $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($asset.digest -cne "sha256:$actual") { throw 'Release manifest digest mismatch.' }
    }
    $manifest = ConvertFrom-MhxJson $bytes
    if ($manifest.schemaVersion -ne 1 -or "v$($manifest.version)" -cne $release.tag_name -or $manifest.files.Count -lt 1 -or $manifest.files.Count -gt 32) {
        throw 'Invalid release manifest version or file list.'
    }
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported operating system.' }
    $arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $runtime = "$os-$arch"
    $name = "mhx-$runtime" + $(if ($IsWindows) { '.exe' } else { '' })
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if ($entry.name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$' -or -not $names.Add($entry.name) -or
            $entry.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $entry.size -le 0 -or $entry.size -gt 209715200) { throw 'Invalid release file entry.' }
    }
    $files = @($manifest.files | Where-Object { $_.name -ceq $name -and $_.kind -ceq 'native-aot' -and $_.runtime -ceq $runtime })
    if ($files.Count -ne 1) { throw "No Native AOT release asset is available for $runtime." }
    $file = $files[0]
    $asset = Get-MhxAsset $release $name
    if ($asset.size -ne $file.size) { throw 'Asset size differs from release manifest.' }
    return @{ Uri="$api/releases/assets/$($asset.id)"; File=$file; Version=$manifest.version }
}

function Save-MhxAsset($Client, $Plan, [string] $Destination, [string] $Token, $CancellationToken) {
    $response = Get-MhxResponse $Client $Plan.Uri 'application/octet-stream' $Token $CancellationToken
    try {
        if ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $Plan.File.size) { throw 'Release size mismatch.' }
        $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $buffer = [byte[]]::new(65536)
            [long]$total = 0
            while (($count = $stream.ReadAsync($buffer, 0, $buffer.Length, $CancellationToken).GetAwaiter().GetResult()) -gt 0) {
                $total += $count
                if ($total -gt $Plan.File.size) { throw 'Download exceeded its expected size.' }
                $output.Write($buffer, 0, $count)
            }
            if ($total -ne $Plan.File.size) { throw 'Incomplete release download.' }
            $output.Flush($true)
        } finally { $stream.Dispose(); $output.Dispose() }
    } finally { $response.Dispose() }
}

function Install-Mhx {
    param([string] $Method = 'native', [string] $InstallDirectory, [string] $ProfileDirectory, [switch] $NoRegister)
    if (-not $NoRegister) { $ProfileDirectory = Get-MhxProfileDirectory $ProfileDirectory }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
    $temporary = Join-Path ([IO.Path]::GetTempPath()) "mhx-bootstrap-$([Guid]::NewGuid().ToString('N'))"
    if ($IsWindows) { [IO.Directory]::CreateDirectory($temporary) | Out-Null }
    else { [IO.Directory]::CreateDirectory($temporary, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) | Out-Null }
    try {
        $token = Get-MhxToken
        $plan = Get-MhxReleasePlan $client $token $cancellation.Token
        $binary = Join-Path $temporary $(if ($IsWindows) { 'mhx.exe' } else { 'mhx' })
        Save-MhxAsset $client $plan $binary $token $cancellation.Token
        # Verify independently of transport before executing any downloaded code.
        if ((Get-Item -LiteralPath $binary).Length -ne $plan.File.size -or
            (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ine $plan.File.sha256) { throw 'MHX download failed SHA-256 verification.' }
        if (-not $IsWindows) { [IO.File]::SetUnixFileMode($binary, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
        $start = [Diagnostics.ProcessStartInfo]::new($binary)
        $start.UseShellExecute = $false
        foreach ($argument in @('--mhx-install', '--method', $Method)) { $start.ArgumentList.Add($argument) }
        if ($InstallDirectory) { $start.ArgumentList.Add('--directory'); $start.ArgumentList.Add([IO.Path]::GetFullPath($InstallDirectory)) }
        if ($ProfileDirectory) { $start.ArgumentList.Add('--profile-directory'); $start.ArgumentList.Add([IO.Path]::GetFullPath($ProfileDirectory, (Get-Location).ProviderPath)) }
        if ($NoRegister) { $start.ArgumentList.Add('--no-register') }
        $process = [Diagnostics.Process]::Start($start)
        try { $process.WaitForExit(); if ($process.ExitCode -ne 0) { throw "MHX installation failed (exit $($process.ExitCode))." } }
        finally { $process.Dispose() }
        Write-Host "Installed latest stable MHX release $($plan.Version). Use 'mhx update' for future updates."
    } finally {
        $client.Dispose()
        $cancellation.Dispose()
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}

if (-not $LibraryOnly) {
    Install-Mhx -Method $Method -InstallDirectory $InstallDirectory -ProfileDirectory $ProfileDirectory -NoRegister:$NoRegister
}
