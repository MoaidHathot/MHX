# Local verification: 0.1.0 (.NET 11)

Verified November 14, 2026 on Windows win-x64, .NET SDK 11.0.100 /
runtime 11.0.0, PowerShell 7.6.9. These are local results, not claims about
unexecuted CI jobs.

## Passed

- 46 tests against the ordinary Release build.
- The same 46 tests against the published single-file executable.
- Packaging checks reject platform apphosts and PDBs in the portable tool package.
- Package runtime metadata explicitly verifies `net11.0` and runtime major 11,
  preventing a stale .NET 10 package from satisfying the installation checks.
- SDK selection rejects an otherwise valid installed SDK 10 when no SDK 11+
  is available.
- Installed `MHX.0.1.0.nupkg` into an isolated tool directory using a local-only
  feed. Both that installed executable and the dotfiles executable ran a local
  test tool with cold and warm caches.
- Those executions preserved UTF-8 MCP-style stdout, empty/quoted/metacharacter
  arguments, working directory, and exit status, while running inside a project
  pinned to an older SDK.
- The built-in v2 backend downloaded and executed `Zakira.Recall@0.6.1 -- --help`
  with isolated, fresh SDK/package/HTTP caches.
- The registration script's `-WhatIf` path completed successfully.

Commands:

```powershell
dotnet test MHX.slnx -c Release
./scripts/Publish.ps1 -Runtime win-x64
$env:MHX_TEST_EXE = "$PWD/artifacts/publish/win-x64/mhx.exe"
dotnet test MHX.slnx -c Release --no-restore
./scripts/Verify-Packages.ps1 -OnlineTool Zakira.Recall@0.6.1
```

The tests cover parser defaults, invalid UTF-8 and oversized input, unknown and
duplicate fields, discovery boundaries, source URI normalization, environment
allowlisting/removal, SDK discovery failures/output limits, recursion, randomized
native argument forwarding, streaming before EOF, binary data, aliases, Windows
32-bit exit codes, explicit command interpreters, and forced termination of both
the target process tree and an in-progress SDK-discovery subprocess.

## Size and startup observations

Current .NET 11 artifacts:

| Artifact | Bytes |
|---|---:|
| Default win-x64 single-file executable | 146,577 |
| Portable NuGet tool package | 35,367 |

### Historical .NET 10 startup comparison

The following measurements belong to the initial .NET 10 implementation
(SDK 10.0.404 / runtime 10.0.15), before the .NET 11 migration. They are retained
as a baseline and do **not** describe .NET 11 startup performance. That build's
single-file executable was 232,081 bytes, its NuGet package 35,324 bytes, and
its ReadyToRun executable 320,130 bytes. All 46 tests also passed against that
experimental .NET 10 ReadyToRun build.

A warmed startup sample used a generic profile running `dotnet --list-sdks`,
redirected stdio, and closed stdin. Each case had two discarded warmups and
11 measured launches, alternating cases:

| Case | Median total launch/exit time |
|---|---:|
| Direct `dotnet --list-sdks` | 50.5 ms |
| Default single-file MHX wrapper | 229.2 ms |
| ReadyToRun MHX wrapper | 217.9 ms |

This is one machine's warmed process-launch sample, not a throughput benchmark
or a measurement of NuGet downloads. The roughly 11 ms ReadyToRun improvement
cost about 38% more executable size, so the smaller publish remains the default.
No claim of outperforming a C++ shim is made. Native AOT is the next startup
optimization to measure when its compiler prerequisites are available.

The same command can reproduce a ReadyToRun build on the current target framework:

```powershell
dotnet publish src/MHX/MHX.csproj -c Release -r win-x64 --self-contained false `
    -p:PackAsTool=false -p:PublishSingleFile=true -p:PublishReadyToRun=true `
    -o artifacts/publish/win-x64-r2r
```

## Unverified locally

- .NET 11 Native AOT publishing reached the SDK's prerequisite check, where
  `vswhere.exe` could not locate Visual Studio's C++ x64 toolchain. The required
  Visual C++ linker/workload is absent here.
  No successful local Native AOT build or Native AOT startup measurement is claimed.
- Linux/macOS execution and the GitHub Actions workflow were not run on this
  Windows machine. The workflow includes both normal and Native AOT artifact tests
  on Windows, Linux, and macOS.
- Windows console Ctrl+C handling follows the documented console-event contract;
  an interactive console/PTY test was not performed. Pipe EOF and forced process
  termination are covered by automated tests.
