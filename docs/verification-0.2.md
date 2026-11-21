# Release/update verification: 0.2.0

The update extension targets .NET 11. It selects latest stable releases, with no
user version pin. Installed-version records exist only for integrity and downgrade
prevention.

## Executed checks

- 74 automated tests (platform-specific tests skip on other operating systems).
- Windows native replacement was exercised while the old executable was running.
- Corrupt downloads, unsafe redirects, missing/invalid metadata, path traversal,
  incorrect hashes, and interrupted replacement recovery were tested.
- API Authorization headers were verified to stay off asset-CDN requests.
- The installer was tested twice against the same destination and with a corrupted
  download, preserving the working installation on failure.
- A real installed .NET tool was upgraded from `0.2.0` to a locally built `0.2.1`
  fixture through the parent-exit helper, with final installed version/status checked.
- Framework-dependent and Native AOT tests passed on Windows x64, Linux x64,
  and macOS arm64 in [CI run 44912138360](https://github.com/MoaidHathot/MHX/actions/runs/44912138360).

That CI run also caught and drove fixes for canonical directory identity on macOS,
including links whose targets themselves contain aliased ancestors.

## AOT startup sample

The two Windows executables built by the same CI run were compared locally using
a generic profile running `dotnet --list-sdks`, redirected stdio, and closed stdin.
There were two discarded warmups and eleven measured launches per case, alternating
the three cases.

| Case | Median total launch/exit | Executable bytes |
|---|---:|---:|
| Direct dotnet host | 28.7 ms | 146,728 |
| Framework-dependent MHX | 129.9 ms | 215,757 |
| Native AOT MHX | 74.7 ms | 5,705,216 |

AOT reduced the observed MHX overhead from about 101 ms to 46 ms, at the cost of
a larger self-contained executable. This supports selecting AOT for direct release
downloads. These are one machine's warmed startup measurements, not a throughput
benchmark or a guarantee about other machines.

## Publication boundary

GitHub releases are private because the repository is private; use GitHub
authentication when installing/updating. NuGet.org publication requires the
owner to configure its Trusted Publishing policy and `NUGET_USER`. The GitHub
release's `.nupkg` can be installed through the bootstrap without that setup.
