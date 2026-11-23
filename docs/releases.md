# Releases, installation, and updates

## Distribution contract

GitHub Releases are the source for the standalone installer and `mhx update`.
The default is the latest **published stable** release, with no pinned version
in the user's dotfiles. Drafts and prereleases are not selected. Installed
versions/hashes are recorded for integrity and downgrade prevention, not as a
version-selection preference.

Current verified platforms are Windows x64, Linux x64, and macOS arm64. A release
contains `mhx-win-x64.exe`, `mhx-linux-x64`, `mhx-osx-arm64` (Native AOT), one
canonical `MHX.<version>.nupkg`, `Install-Mhx.ps1`, `mhx-release.json`, and
`SHA256SUMS`. Unsupported native platforms fail explicitly instead of downloading
an executable for the wrong architecture. The portable NuGet package remains a
separate distribution channel.

The GitHub repository is private. Installer/update requests use environment
credentials (`GH_TOKEN`, then `GITHUB_TOKEN`) or the locally authenticated
GitHub CLI. Neither is required for public repositories. API credentials are
sent only to `api.github.com`; asset-CDN redirects receive no Authorization
header. The repository's visibility is not changed by these workflows.

## Install

PowerShell 7.4+ bootstrap:

```powershell
./scripts/Install-Mhx.ps1
./scripts/Install-Mhx.ps1 -ProfileDirectory '/path/to/dotfiles/config/mhx'
./scripts/Install-Mhx.ps1 -Method dotnet-tool
```

`-InstallDirectory` chooses an installation location. `-NoRegister` installs
without changing PATH/profile-home settings. Windows native defaults to
`%LOCALAPPDATA%/MHX/bin`; Unix native defaults to `~/.local/share/mhx/bin`.
The .NET-tool method defaults to `~/.dotnet/tools`. Registration is per-user;
Unix shells/GUI applications must receive the printed PATH and `MHX_HOME`
settings through their existing environment setup.

`-ProfileDirectory` is optional for both installation methods and the registration
script. The common default order is an explicit directory, nonempty `MHX_HOME`,
absolute `$XDG_CONFIG_HOME/mhx`, then `%LOCALAPPDATA%/MHX` on Windows or
`~/.config/mhx` on Unix. XDG works on Windows too. Empty/relative XDG values
are ignored rather than interpreted relative to the working directory.
Registration creates a missing profile directory (with private permissions on
Unix); `-NoRegister` and registration `-WhatIf` do not create one.

Windows registration puts the selected installation first in **user** PATH,
without deleting other installations. A system-wide PATH entry can still take
precedence; remove an unwanted system-wide installation or adjust that environment
explicitly when switching channels.

The script downloads the AOT installer executable, verifies bytes against the
release manifest, then invokes its `--mhx-install` entry point. Native file
replacement is implemented once in MHX. The .NET-tool method downloads the
verified `.nupkg` and delegates installation to SDK 11+ using a local-only feed.
It works with GitHub releases even before NuGet.org publishing is configured.

Do not fetch and execute arbitrary remote script text with `Invoke-Expression`.
Keep the bootstrap script in your reviewed dotfiles or download it from a release.

## Update

```console
mhx update --check
mhx update
recall-mcp --mhx-update
```

`update` is reserved only on the canonical `mhx` launcher. Renamed aliases still
forward an ordinary `update` argument to their configured command. The explicit
`--mhx-update` form is available on aliases. A NuGet package named `update` can
be selected with `update@Version` or a profile.

Ordinary launches do not check for updates or contact GitHub. Checks and updates
use bounded HTTPS requests, a strict versioned manifest, exact asset identities,
expected sizes, and SHA-256 validation. Hashes authenticate bytes relative to the
trusted GitHub release metadata; they are not an independent publisher signature.

Native updates acquire a per-directory lock, stage verified bytes on the target
filesystem, and replace the selected executable. Unix uses atomic rename-over.
Windows renames the old mapped image aside before installing the new image;
a flushed recovery journal supports rollback/recovery of interrupted swaps.
Running instances retain their loaded image. Locked `.mhx-backup-*` images can
be removed after those instances exit. Internal `.mhx-*` files and
`<executable>.install.json` belong to the installation. Profiles are never replaced.

The .NET-tool updater keeps package-manager ownership: it downloads a verified
package, stages a copy of its small managed helper, exits, and then invokes
`dotnet tool update` against the installation's own tool path. The SDK can remove
the old package only after it is no longer loaded. The handoff message includes
the status-file path. Exit 0 from the first process means the helper was started;
the helper's `status.json` and final console output report completion/failure.
Other running instances may still block the SDK's uninstall; close them and retry.
Helper/status files live in the private per-user MHX `updates` directory and may
be removed after the update has finished. Tokens are removed from the helper/SDK
environment and are not written into the request or status files.

## Publishing a release

1. Change `Directory.Build.props` to the new stable version and commit it.
2. Push a matching tag, for example `v0.2.0`.
3. `release.yml` checks version/tag agreement and calls the full cross-platform
   build/verification workflow.
4. Only after all jobs pass, the publish job assembles and verifies the assets,
   uploads them to a draft, and publishes the complete release as latest.

The release workflow can be rerun on its tag. A published release's assets are
not replaced. A failed upload leaves a draft that can be completed on retry.
Build jobs have read-only repository permissions; only the release publisher
gets `contents: write`. Actions are pinned to commits.

### NuGet.org Trusted Publishing

GitHub release/package downloads need no NuGet publishing credentials. To also
publish to NuGet.org:

1. In the owning NuGet.org account, create a Trusted Publishing policy for
   repository owner `MoaidHathot`, repository `MHX`, workflow file `release.yml`.
2. Set the GitHub repository variable `NUGET_USER` to the NuGet profile name.
3. Release a tag, or rerun the NuGet job after configuring the policy.

The NuGet job uses OIDC and a short-lived key from `NuGet/login`. No long-lived
API key is stored in the repository. With no `NUGET_USER`, that job is skipped;
the `.nupkg` is still available in the GitHub release. Package ownership/policy
setup must be completed by the NuGet account owner.

WinGet can later reference the same versioned Windows asset and SHA-256. Public
WinGet distribution requires a publicly accessible download location; private
GitHub assets alone are not a public WinGet source.
