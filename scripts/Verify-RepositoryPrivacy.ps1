#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$violations = @()
$emailPattern = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
$personalPathPattern = '(?i)[a-z]:[\\/]+Users[\\/]+(?!runneradmin\b|runner\b|user\b|username\b|example\b)[^\\/\s"'']+|/(?:Users|home)/(?!runner\b|user\b|username\b|example\b)[^/\s"'']+'
$internalDomainPattern = '(?i)\b(?:eng\.ms|corp\.microsoft\.com|microsoftint\.com|packagefeedproxy\.microsoft\.io|msazure\.visualstudio\.com)\b'
foreach ($relative in @(git -C $root ls-files --cached --others --exclude-standard)) {
    $file = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
    $text = [IO.File]::ReadAllText($file)
    if ($text.Contains([char]0)) { continue }
    if ($text -match $personalPathPattern) { $violations += "$relative : personal home path" }
    if ($text -match $internalDomainPattern) { $violations += "$relative : internal domain" }
    foreach ($match in [regex]::Matches($text, $emailPattern)) {
        $email = $match.Value
        if ($email -match '@users\.noreply\.github\.com$|@example\.(com|test|invalid)$' -or $email -eq 'noreply@github.com') { continue }
        # This is a deliberate invalid-URL test input, not a contact address.
        if ($relative -in @('tests/MHX.Tests/DistributionTests.cs', 'scripts/Verify-RepositoryPrivacy.ps1') -and $email -eq 'secret@github.com') { continue }
        $violations += "$relative : non-placeholder email"
    }
}
if ($violations.Count) { $violations | Sort-Object -Unique; throw 'Repository privacy checks failed. Matched values are intentionally not printed.' }
Write-Host 'PASS tracked-file privacy patterns (private contact addresses, home paths, internal domains)'
