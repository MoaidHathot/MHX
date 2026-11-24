#Requires -Version 7.4
[CmdletBinding()]
param([switch] $LibraryOnly)
$ErrorActionPreference = 'Stop'

function Assert-MhxReleaseContext {
    param(
        [string] $Repository,
        [string] $RepositoryId,
        [string] $OwnerId,
        [string] $EventName,
        [string] $RefType,
        [string] $Ref,
        [string] $Tag,
        [string] $Version
    )
    if ($Repository -cne 'MoaidHathot/MHX' -or $RepositoryId -cne '1442729283' -or $OwnerId -cne '8770486') {
        throw 'Publishing is restricted to the canonical MHX repository identity.'
    }
    if ($EventName -cnotin @('push', 'workflow_dispatch') -or $RefType -cne 'tag') {
        throw 'Only a release-tag push or explicit tag dispatch may publish.'
    }
    if ($Tag -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
        $Ref -cne "refs/tags/$Tag" -or $Tag -cne "v$Version") {
        throw 'The stable release tag must match the project version and triggering ref.'
    }
}

if (-not $LibraryOnly) {
    $root = Split-Path $PSScriptRoot -Parent
    $version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    Assert-MhxReleaseContext -Repository $env:GITHUB_REPOSITORY -RepositoryId $env:GITHUB_REPOSITORY_ID `
        -OwnerId $env:GITHUB_REPOSITORY_OWNER_ID -EventName $env:GITHUB_EVENT_NAME -RefType $env:GITHUB_REF_TYPE `
        -Ref $env:GITHUB_REF -Tag $env:GITHUB_REF_NAME -Version $version
    $head = git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $env:GITHUB_SHA) { throw 'Checkout does not match the triggering commit.' }
    $tagCommit = git -C $root rev-parse --verify "refs/tags/$($env:GITHUB_REF_NAME)^{commit}"
    if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $head) { throw 'Tag does not identify the checked-out commit.' }
    git -C $root merge-base --is-ancestor $head refs/remotes/origin/main
    if ($LASTEXITCODE -ne 0) { throw 'The release commit must already be part of protected main.' }
    Write-Host 'Canonical repository, release event, tag, version, and main ancestry verified.'
}
