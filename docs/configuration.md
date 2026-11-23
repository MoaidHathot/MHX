# Configuration reference (version 1)

## Selection

1. `mhx --mhx-config <file> [args...]` selects an explicit file; relative paths
   are resolved from the caller's directory.
2. `mhx --mhx-profile <name> [args...]` selects `<name>.mhx` or `<name>.shim`
   inside the profile home.
3. Otherwise, an absolute `MHX_CONFIG` environment variable selects a file.
4. Otherwise, use `<executable-name>.mhx` or `.shim` beside the actual launcher.
5. Otherwise, look for the same name inside the profile home.
6. Only a launcher named `mhx` falls back to the built-in .NET-tool profile.
   A renamed launcher without a profile fails rather than guessing a command.

The profile home is nonempty absolute `MHX_HOME`, then `$XDG_CONFIG_HOME/mhx`
on every platform (including Windows), then `%LOCALAPPDATA%/MHX` on Windows
or `~/.config/mhx` on Unix. Empty or relative XDG values are ignored; a nonempty
relative `MHX_HOME` is an error. Installer `-ProfileDirectory` / `--profile-directory`
options override these defaults. Working directories and
ancestor repositories are never searched automatically. If both extensions
exist for a selected name, selection fails. Access errors and malformed files
fail rather than falling back to defaults. A symlink does not necessarily keep
its alias name on every OS; copy the single-file executable for named launchers.

`--mhx-help` and `--mhx-version` are reserved standalone management invocations.
`mhx update [--check]` and `--mhx-update [--check]` manage MHX's own release.
An ordinary `update` on a renamed alias is still forwarded to that alias's target.
`--mhx-config` and `--mhx-profile` are recognized only as the first argument.
Everything following the selected profile/name is passed to the target.
`mhx` and `mhx --help` show help when no configuration has been selected.

## `.mhx` JSON

| Field | Default | Meaning |
|---|---|---|
| `$schema` | absent | Optional editor schema reference; MHX does not fetch it. |
| `version` | `1` | Configuration format version. |
| `kind` | `command` | `command` or `dotnet-tool`. |
| `command` | required for `command` | Executable name or path. |
| `arguments` | `[]` | Array of fixed arguments, before the caller's argv. |
| `workingDirectory` | caller's directory | Explicit target directory. |
| `inheritEnvironment` | `true` | Set false for an explicit environment allowlist. |
| `environment` | `{}` | Child-only overrides. `null` removes a variable. |
| `dotnet` | `dotnet` | Executable to use for the `dotnet-tool` backend. |
| `sources` | NuGet.org v2 | Array of NuGet sources, forwarded with `--source`. |
| `nugetConfig` | absent | NuGet configuration file forwarded with `--configfile`. |

`dotnet`, `sources`, and `nugetConfig` are valid only for `dotnet-tool`.
`command` is valid only for `command`. When `nugetConfig` is supplied and
`sources` is absent, sources come from that file; the v2 default is not added.
Explicit source arguments from the caller also reach the SDK and follow its rules.

The .NET backend defaults SDK verbosity to `quiet` for clean MCP stdout. Explicit
SDK verbosity options before `--` take precedence; verbosity arguments after
`--` only affect the child tool. Logging verbosity does not change NuGet signature
verification or trust settings.

Paths in configuration are relative to the **configuration directory**, not
the caller's directory. Bare executables are searched only in absolute entries
of the effective child PATH. Windows searches `.exe` and `.com`, not PATHEXT
script associations. Scripts require an explicitly configured interpreter.
Use `./tool.exe` to select an executable beside the profile rather than on PATH.
Local NuGet feed paths are also relative to the configuration directory.

### Substitutions

Substitution applies only to configuration values, in one pass:

- `${configDir}`: directory containing the selected configuration.
- `${launcherDir}`: directory containing the launcher entry point.
- `${cwd}`: original caller working directory.
- `${home}`: current user's home directory.
- `${env:NAME}`: value from the environment MHX received.
- `$$`: a literal dollar sign (`$${env:NAME}` stays literal).

Unset variables and unknown placeholders fail. Expanded values are never
expanded again. Environment overrides do not depend on their declaration order
and cannot reference each other's new values. Use inherited environment values
for secrets; MHX does not print resolved argv or environment values.

Configuration is UTF-8 (optional BOM), at most 1 MiB, with maximum JSON nesting
depth 16 and at most 4096 configured arguments. NUL characters are rejected.
Environment names cannot contain `=` and must be unique using the OS's casing
rules. Caller argv is additionally subject to the OS command-line size limit.
Nested MHX invocations are limited to 16 using `MHX_LAUNCH_DEPTH`.

## `.shim` compatibility subset

```ini
path = pwsh
args = -NoLogo -NoProfile -File "${configDir}/scripts/task.ps1"
cwd = ${configDir}
env:MY_SETTING = ${env:MY_SETTING}
```

Supported fields: `path`, repeated `args`, `cwd`, and `env:NAME` (child overrides).
Keys are case-insensitive; duplicate scalar keys fail. Blank lines and lines
starting with `#`, `;`, or `//` are ignored. `args` uses Windows CRT argument
quoting on every OS; shell metacharacters have no special meaning.

This is deliberately a documented subset, not a claim of full Scoop format
compatibility. Use MHX's `${...}` substitutions instead of `%VAR%` / `%~dp0`.
Legacy unprefixed environment keys, elevation, `env!`, and `workdir` are rejected.
Use `.mhx` for environment removal/allowlisting and the .NET-tool backend.

## Process contract

- stdin, stdout, and stderr go directly to the target; successful launches are silent.
- Caller argv, empty strings, Unicode, and exit codes are preserved.
- On Windows, the child joins a kill-on-close job **during creation**. Ordinary
  descendants stay in that job. Killing MHX or finishing the direct child closes
  the job and terminates remaining descendants. Intentional background daemons
  therefore require a separate launch arrangement.
- Console control events reach the Windows child; MHX waits for its exit.
- On Unix, `execve` replaces MHX with the target, preserving its PID and stdio.
  Descendant lifecycle follows the target and the caller's normal process-group policy.
- Launch/configuration failures go to stderr, normally with exit code 1;
  missing executables use 127, Unix exec failures 126, missing option values 2.
  Target exit codes are passed through, including full 32-bit Windows statuses.
