# MHX

A small, configurable launcher you own. Run .NET tools through NuGet v2, or turn
the same executable into a named launcher for any executable.

```console
mhx Zakira.Recall -- mcp
mhx Some.Tool@1.2.3 -- --help
mhx --mhx-profile recall mcp
```

Callers only need `mhx` on `PATH`. PowerShell profiles, script paths, and shell
wrappers are unnecessary, including for MCP stdio clients:

```json
"command": ["mhx", "Zakira.Recall", "--", "mcp"]
```

## Install / distribute

### Latest release (recommended)

Download binaries and the bootstrap from the [latest GitHub Release](https://github.com/MoaidHathot/MHX/releases/latest).

Keep `scripts/Install-Mhx.ps1` in your dotfiles and run it during machine setup:

```powershell
./scripts/Install-Mhx.ps1
```

The installer downloads the **latest stable Native AOT release**, verifies its
SHA-256 and size, and installs it into the per-user MHX binary directory. There
is no launcher version pin. On Windows it registers user PATH and `MHX_HOME`;
restart existing terminals/background harnesses afterward. On Unix it prints
the PATH/profile-home entries to add to your application environment.

The profile directory defaults to `$XDG_CONFIG_HOME/mhx` on **all platforms,
including Windows**. An existing `MHX_HOME` takes precedence, and an explicit
`-ProfileDirectory` overrides both. Without either environment setting, MHX uses
`%LOCALAPPDATA%/MHX` on Windows or `~/.config/mhx` on Unix. Empty or relative
`XDG_CONFIG_HOME` values are ignored. With your XDG directory pointing at the
dotfiles `config` folder, no profile-directory argument is needed.

The repository is currently private. Downloads use `GH_TOKEN` / `GITHUB_TOKEN`,
or an existing `gh auth login` session. Tokens are never written into profiles,
installation metadata, or update requests. Public releases also work anonymously.

```powershell
mhx update --check
mhx update
```

Updates are explicit. Ordinary `mhx Package -- arguments` invocations make no
update checks. The native executable is replaced transactionally, preserving
profiles and allowing already-running instances to finish on their old image.
Copied executable aliases can be updated individually with `--mhx-update`.

For the .NET-tool installation channel, the same bootstrap can install the
release's verified NuGet package using the SDK:

```powershell
./scripts/Install-Mhx.ps1 -Method dotnet-tool
```

`mhx update` also supports installed global/custom-path .NET tools. Windows
cannot remove the currently loaded package, so MHX hands the update to a staged
helper after exiting. Exit 0 at handoff means **scheduled**, and the helper
prints the final outcome and writes the displayed `status.json`. Close other
instances of that .NET-tool installation if the SDK reports files in use.
Local-manifest and ephemeral `dnx` installations should use their SDK update
commands rather than changing another installation.

Native AOT removes the runtime dependency for MHX itself. The `dotnet-tool`
execution backend still requires SDK 11+. The NuGet distribution requires a
.NET 11 runtime. See [release and update design](docs/releases.md).

### Build a dotfiles executable yourself

Build with the .NET 11 SDK and PowerShell 7:

```powershell
./scripts/Publish.ps1
```

The default publish produces `artifacts/publish/<rid>/mhx.exe` on Windows
(`mhx` elsewhere), a **framework-dependent single-file executable**. It uses
an installed .NET 11 runtime. Copy it into your dotfiles' `bin` directory.
It is a real executable that direct process launchers can start.

For a self-contained Native AOT executable, use `./scripts/Publish.ps1 -NativeAot`.
This needs the platform's .NET Native AOT compiler prerequisites (including the
Visual C++ toolchain on Windows). Build on the target operating system.

Register your dotfiles location once on Windows:

```powershell
./scripts/Register-Mhx.ps1 -BinDirectory 'P:/github/MyDotfiles/bin'
```

This adds the binary directory to **user PATH** and sets **MHX_HOME** at user
scope. Restart existing terminals/background harnesses after registration.
Profiles can then be updated through your normal dotfiles workflow. The release
installer and `mhx update` manage executable updates without tracking binaries
in Git. Stop running launchers before manually overwriting Windows executables.
On Linux/macOS, add the directory to the environment of the applications that
launch MHX and set `MHX_HOME` there; ensure `mhx` has execute permission.

### .NET tool

The same publish script builds `artifacts/packages/MHX.<version>.nupkg`.
Install from this local directory (no public feed is needed):

```powershell
dotnet tool install --global MHX --source ./artifacts/packages --version 0.2.2
```

The SDK generates `mhx.exe` in `%USERPROFILE%/.dotnet/tools` (or `mhx` in
`~/.dotnet/tools` on Unix). Ensure this directory is on the application's PATH.
Set `MHX_HOME` to your synced profile directory. Updating profiles takes effect
on the next launch; updating the installed launcher requires `dotnet tool update`.
The package requires a .NET 11 runtime; installing it requires SDK 11 or later.

## Default: .NET tools through v2

Without a profile, MHX invokes the newest installed .NET SDK (11+) using:

```text
dotnet exec <sdk>/dotnet.dll tool exec --verbosity quiet --source https://www.nuget.org/api/v2/ <your arguments>
```

This follows modern `dnx` SDK selection independently of `global.json` while
preserving the caller's working directory. NuGet version selection, caching,
tool manifests, package validation, runtime selection, and execution remain
the SDK's responsibility. The launcher itself makes no HTTP requests.
As with `dnx`, an unversioned package can be selected from a local tool manifest.
Use `Package@Version` to request an explicit version.

MHX supplies `DOTNET_NOLOGO=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1` to its .NET
backend when those variables are not already set. Explicit existing values win.
The SDK and the executed tool still control their own behavior.

SDK logging defaults to `quiet` to keep informational restore messages out of
MCP stdout. An explicit SDK `-v`/`--verbosity` option before `--` overrides this
default. Options after `--` belong to the executed tool. Increasing SDK verbosity
can introduce non-protocol output, so use the quiet default for MCP servers.

## Profiles

Use JSON in a `.mhx` file. Comments and trailing commas are accepted; unknown
fields, duplicate properties, invalid types, and unsupported versions are errors.

`recall.mhx`:

```json
{
  "version": 1,
  "kind": "dotnet-tool",
  "arguments": ["Zakira.Recall", "--"]
}
```

Run it as `mhx --mhx-profile recall mcp`. Alternatively, copy the published
launcher to `recall.exe` and put `recall.mhx` beside it: `recall mcp` now works
from any normal process launcher. Copies of the single-file build are independent
aliases; the framework-dependent executable still needs its compatible runtime.

A generic executable profile:

```json
{
  "version": 1,
  "kind": "command",
  "command": "pwsh",
  "arguments": ["-NoLogo", "-NoProfile", "-File", "${configDir}/scripts/task.ps1"],
  "environment": {
    "MY_SETTING": "${env:MY_SETTING}",
    "UNWANTED_VARIABLE": null
  }
}
```

Configured arguments are prepended to the caller's arguments. Caller arguments
are always literal: MHX never expands or evaluates them. An explicitly configured
interpreter can, of course, apply its own argument/language rules.

See [configuration reference](docs/configuration.md), [examples](examples),
[security and privacy](SECURITY.md), and [design/research notes](docs/design.md).

## Build and verify

```powershell
dotnet build MHX.slnx -c Release
dotnet test MHX.slnx -c Release
./scripts/Publish.ps1
./scripts/Verify-Packages.ps1
./scripts/Verify-Distribution.ps1 -Executable ./artifacts/publish/win-x64/mhx.exe
```

For an explicit network smoke test using a tool you select, append, for example,
`-OnlineTool Zakira.Recall@0.6.1`. This downloads and executes that tool's `--help`
through the built-in v2 backend using isolated caches.

The tests include malformed profiles, discovery rules, literal/randomized argv,
streaming and binary stdio, environment isolation, SDK selection, renamed
executables, full Windows exit codes, and Windows forced-termination cleanup.
Package verification installs the real tool package into an isolated location
and runs a local test-tool package through both distributions, including from
a directory pinned to an older SDK. It does not depend on a public tool package.

CI runs the suite on Windows, Linux, and macOS and exercises both ordinary
single-file and Native AOT publishes. Local Windows results do not imply that
the other platforms or the Native AOT compiler were available on that machine.

MHX has no third-party runtime package dependencies. Test dependencies are
separate. There is no telemetry or background daemon. Network access for MHX's
own releases occurs only during explicit installation/update commands.
