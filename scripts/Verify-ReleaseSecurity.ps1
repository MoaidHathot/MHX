#Requires -Version 7.4
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-ReleaseContext.ps1') -LibraryOnly
$valid = @{
    Repository='MoaidHathot/MHX'; RepositoryId='1442729283'; OwnerId='8770486'
    EventName='push'; RefType='tag'; Ref='refs/tags/v1.2.3'; Tag='v1.2.3'; Version='1.2.3'
}
Assert-MhxReleaseContext @valid
$dispatch = $valid.Clone(); $dispatch.EventName = 'workflow_dispatch'
Assert-MhxReleaseContext @dispatch
$cases = @(
    @{ Repository='fork-owner/MHX' },
    @{ RepositoryId='1' },
    @{ OwnerId='1' },
    @{ EventName='pull_request' },
    @{ EventName='pull_request_target' },
    @{ EventName='workflow_run' },
    @{ EventName='schedule' },
    @{ EventName='workflow_dispatch'; RefType='branch'; Ref='refs/heads/main'; Tag='main' },
    @{ Ref='refs/pull/123/merge' },
    @{ Tag='v1.2.3-preview'; Ref='refs/tags/v1.2.3-preview' },
    @{ Tag='v1.2.3;bad'; Ref='refs/tags/v1.2.3;bad' },
    @{ Version='9.9.9' }
)
foreach ($change in $cases) {
    $context = $valid.Clone()
    foreach ($key in $change.Keys) { $context[$key] = $change[$key] }
    $rejected = $false
    try { Assert-MhxReleaseContext @context } catch { $rejected = $true }
    if (-not $rejected) { throw 'An untrusted publication context was accepted.' }
}
Write-Host "PASS publication gate: two accepted contexts, $($cases.Count) rejected contexts"
