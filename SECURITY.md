# Security and privacy

Release authorization, fork/PR isolation, and repository privacy checks are
documented in [publishing security](docs/publishing-security.md).

MHX launches commands with the current account's permissions. Selected profiles
are executable configuration: their owner chooses programs, arguments, working
directories, and environment values. Child programs retain their own filesystem,
network, and telemetry behavior.

## Launcher boundaries

- MHX profile discovery never searches the current directory or ancestor repositories.
- Only explicitly selected files, executable sidecars, and the user's configured
  profile home can supply behavior.
- Bare executable lookup uses absolute child-PATH entries. Empty/relative entries,
  implicit current-directory search, and Windows file associations are not used.
- No implicit command shell, elevation fallback, remote profile loading, dynamic
  plugins, or script evaluation. Interpreters must be explicitly configured.
- Strict bounded UTF-8/JSON input, rejected duplicate/unknown fields, and single-pass
  substitutions. Missing environment references fail rather than becoming empty.
- Windows startup specifies the exact executable, correctly quotes arguments,
  inherits only the three standard handles, and assigns process supervision at
  creation. This is lifecycle containment, not an isolation boundary against a
  deliberately hostile program running as the same user.
- No certificate/signature-validation switches, proxy overrides, or NuGet trust
  modifications. NuGet operations remain in the installed SDK.

The delegated SDK can still honor a repository's .NET tool manifest, as `dnx`
does. Use `Package@Version` when a repository should not select the tool version.

## Privacy

Ordinary MHX launches make no network requests or update checks of their own.
Explicit install/update commands contact the fixed MHX GitHub repository over
HTTPS and verify release metadata, asset sizes, and SHA-256 before installation.
There is no telemetry, usage history, or background updater. Errors omit resolved arguments and environment
values. Successful launches emit nothing. SDK discovery captures diagnostics
without echoing them; target stdout/stderr are passed through unchanged.

GitHub credentials come from environment variables or the local GitHub CLI,
remain in memory, and are attached only to GitHub API requests. They are not
forwarded to asset CDN hosts or to the .NET-tool update helper. Installation
state contains versions/hashes, recovery paths, and update status, not secrets.

The .NET backend defaults to disabling SDK telemetry in the child environment
when the caller has not explicitly set `DOTNET_CLI_TELEMETRY_OPTOUT`. No global
environment or NuGet settings are changed by ordinary execution.

Supply secrets through inherited environment variables instead of checking
them into profiles. Command-line arguments may be visible to OS process tools,
and the child may log its own inputs. Environment allowlisting and removal are
available through `inheritEnvironment` and `environment`.

## Distribution

Runtime dependencies are exclusively .NET platform libraries. The test runner
is a development dependency. Published artifacts have SHA-256 manifests from
the build script; hashes detect changes but are not a publisher signature.
Build from reviewed source when provenance matters. No signing identity or
external publishing credentials are required by the build.
