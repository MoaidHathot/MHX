# Design and research

## Goal and implementation stages

The stable public contract is `mhx Package -- arguments`, usable by direct
process launchers without any shell profile. Profiles and copied executable
aliases extend that same contract to other commands.

1. Strict configuration/selection with tests for defaults, malformed data,
   ambiguity, substitution, and discovery boundaries.
2. Transparent native execution, tested with real child processes and pipes.
3. SDK delegation and v2 defaults, tested using fake SDK discovery plus real
   local tool packages and an older `global.json`.
4. Both single-file executable and .NET-tool distributions from one project.
5. Verification of shipped artifacts, CI, examples, and measured sizes.

## Decisions

- .NET 11; runtime code uses only the BCL. JSON metadata is source-generated,
  reflection-based JSON serialization is disabled, and AOT analysis is enabled.
- Framework-dependent single-file publishing is the smallest baseline when the
  matching .NET runtime is already present. Native AOT is a separate, optional,
  self-contained build; neither form requires Scoop or PowerShell to execute.
- `.mhx` uses argv arrays instead of shell command strings. `.shim` supports a
  conservative, explicit subset. Configuration errors never invoke a shell.
- MHX owns configuration and execution, while the SDK owns NuGet resolution,
  signatures, package layout, cache locking, runtime selection, and tool execution.
- The .NET backend lists installed SDKs and invokes the last compatible SDK
  reported by the host, matching modern `dnx` selection. No ambient `global.json`
  can downgrade the backend below SDK 11. Discovery is bounded by a timeout;
  Windows discovery also uses the supervised native execution path and bounded output.
- The execution path has no HTTP client, package downloader, plugin loader,
  dynamic compilation, or telemetry. Explicit release installation/update has a
  separate bounded GitHub client and transactional installation state. New execution backends should produce a
  `LaunchPlan`, retaining the shared argv/stdio/security contract.

The release/update extension is described in [releases.md](releases.md). Windows
experiments confirmed that a mapped native executable can be renamed/replaced,
while an installed .NET tool cannot uninstall its own loaded package. The latter
therefore uses an explicit parent-exit handoff rather than modifying SDK stores.
- Windows process creation specifies the executable separately from its quoted
  argv and restricts inherited handles to stdin/stdout/stderr. A JOB_LIST creation
  attribute associates the process atomically with a kill-on-close job. This avoids
  the create/resume-then-assign window present in some shim implementations.
- Unix process replacement avoids an additional resident wrapper. Unix supervision
  is explicitly different from Windows job-object supervision.

## Sources consulted

- [Scoop Shim specification](https://github.com/ScoopInstaller/Shim/blob/main/SPEC.md)
- [Earlier C++ shim reference](https://github.com/kiennq/scoop-better-shimexe)
- [CreateProcessW and executable search/quoting](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)
- [Handle and job-list creation attributes](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Windows job lifetime](https://learn.microsoft.com/windows/win32/procthread/job-objects)
- [dnx / dotnet tool exec](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-exec)
- [dnx SDK selection](https://learn.microsoft.com/dotnet/core/compatibility/sdk/11/dnx-scripts-bypass-global-json)
- [Single-file deployment](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview)
- [Native AOT size optimization](https://learn.microsoft.com/dotnet/core/deploying/native-aot/optimizing)
- [JSON duplicate-property handling in .NET 10](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/libraries)
- [MCP stdio transport](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)

MHX's implementation is original code; no Scoop binary or source is bundled.

## Research evidence and boundaries

Before implementation, the Windows test environment successfully installed
public .NET tools, including a freshly published version, through the SDK using
only NuGet v2 in isolated temporary directories. Raw
Windows process spawning rejected `.ps1` and `.cmd`, while direct executable
launching and explicit interpreter invocation worked. These findings motivated
the executable entry point and SDK delegation.

Tests are the ongoing source of implementation evidence. Network availability,
absolute startup timings, and package freshness are environment-dependent;
they are not universal guarantees. Run package verification against each
published artifact. GitHub CI describes cross-platform checks; adding a workflow
is not evidence that a remote run has completed.

See [local verification results](verification.md) for the executed checks,
artifact sizes, startup sample, and remaining platform/toolchain boundaries.
