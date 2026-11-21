using System.Diagnostics;
using System.Text.Json;
using MHX.Execution;

namespace MHX.Distribution;

internal static class UpdateCommand
{
    public static async Task<int> Run(bool checkOnly, CancellationToken cancellationToken)
    {
        string executable = Environment.ProcessPath ?? throw new MhxException("Cannot locate the current executable.");
        string? toolDirectory = AppInfo.IsTool ? Installation.ToolDirectory(executable, AppContext.BaseDirectory) : null;
        if (AppInfo.IsTool && toolDirectory is null)
            throw new MhxException("This is a local, ephemeral, or development .NET tool. Update its manifest/package with dotnet tool update, or install a standalone release.");
        using FileStream? gate = toolDirectory is null && !checkOnly ? AtomicInstall.Lock(Path.GetDirectoryName(executable)!) : null;
        if (toolDirectory is null && !checkOnly) AtomicInstall.Recover(Path.GetDirectoryName(executable)!);
        Version currentVersion = toolDirectory is null ? InstalledVersion.Read(executable, AppInfo.Version) : ReleaseValidation.StableVersion(AppInfo.Version);
        using GitHubDownloads downloads = new(GitHubAuth.Token());
        var (release, manifest) = await downloads.Latest(cancellationToken);
        if (ReleaseValidation.StableVersion(manifest.Version) <= currentVersion)
        {
            Console.Error.WriteLine($"MHX {currentVersion} is up to date (latest stable: {manifest.Version}).");
            return 0;
        }
        if (checkOnly)
        {
            Console.Error.WriteLine($"MHX {manifest.Version} is available (installed: {AppInfo.Version}). Run mhx update to install it.");
            return 0;
        }
        ReleaseFile file = ReleaseValidation.Select(manifest, AppInfo.IsTool, ReleaseValidation.Runtime());
        if (toolDirectory is not null)
            return await QueueToolUpdate(downloads, release, manifest, file, toolDirectory, cancellationToken);

        string directory = Path.GetDirectoryName(executable)!;
        string staged = Path.Combine(directory, ".mhx-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            await downloads.Download(release, file, staged, cancellationToken);
            AtomicInstall.Replace(staged, executable, file.Sha256);
            InstalledVersion.Write(executable, manifest.Version, file.Sha256);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
        Console.Error.WriteLine($"Updated MHX to {manifest.Version}. Existing instances continue with their loaded version.");
        return 0;
    }

    private static async Task<int> QueueToolUpdate(GitHubDownloads downloads, GitHubRelease release, ReleaseManifest manifest,
        ReleaseFile file, string installDirectory, CancellationToken cancellationToken)
    {
        string staging = Installation.NewPrivateDirectory();
        bool launched = false;
        try
        {
            await downloads.Download(release, file, Path.Combine(staging, file.Name), cancellationToken);
            foreach (string name in new[] { "mhx.dll", "mhx.deps.json", "mhx.runtimeconfig.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(staging, name));
            Dictionary<string, string> environment = PlanBuilder.ReadEnvironment();
            environment.Remove("GH_TOKEN"); environment.Remove("GITHUB_TOKEN");
            environment.TryAdd("DOTNET_NOLOGO", "1"); environment.TryAdd("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
            string dotnet = ExecutableResolver.Resolve("dotnet", AppContext.BaseDirectory, environment);
            string sdk = DotnetSdk.Find(dotnet, environment);
            using Process parent = Process.GetCurrentProcess();
            ToolUpdateRequest request = new()
            {
                ParentId = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
                InstallDirectory = installDirectory, Version = manifest.Version, Sha256 = file.Sha256, Dotnet = dotnet, Sdk = sdk,
            };
            string requestPath = Path.Combine(staging, "request.json");
            File.WriteAllText(Path.Combine(staging, ".mhx-tool-update"), "1");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request, ReleaseJsonContext.Default.ToolUpdateRequest));
            ProcessStartInfo start = new(dotnet) { UseShellExecute = false, WorkingDirectory = staging };
            foreach (string argument in new[] { Path.Combine(staging, "mhx.dll"), "--mhx-complete-tool-update", requestPath }) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach ((string key, string value) in environment) start.Environment[key] = value;
            using Process helper = Process.Start(start) ?? throw new MhxException("Cannot start the .NET-tool updater.");
            launched = true;
            Console.Error.WriteLine($"Updating the .NET tool after this process exits. Progress follows; final status: {Path.Combine(staging, "status.json")}");
            return 0; // Explicitly means handoff started, not installation completed.
        }
        finally { if (!launched) Directory.Delete(staging, true); }
    }
}
