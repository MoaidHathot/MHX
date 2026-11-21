using System.Diagnostics;
using System.Xml;
using MHX.Execution;

namespace MHX.Distribution;

internal static class ToolPackageInstall
{
    public static int Install(string directory, string packageDirectory, string version, string dotnet, string sdk)
    {
        string config = Path.Combine(packageDirectory, "nuget.config");
        using (XmlWriter writer = XmlWriter.Create(config, new XmlWriterSettings { Indent = true, NewLineChars = "\n" }))
        {
            writer.WriteStartElement("configuration"); writer.WriteStartElement("packageSources");
            writer.WriteStartElement("clear"); writer.WriteEndElement();
            writer.WriteStartElement("add"); writer.WriteAttributeString("key", "verified-release"); writer.WriteAttributeString("value", packageDirectory); writer.WriteEndElement();
            writer.WriteEndElement(); writer.WriteEndElement();
        }
        string[] arguments = ["exec", sdk, "tool", "update", "MHX", "--tool-path", directory, "--version", version, "--configfile", config];
        Dictionary<string, string> environment = PlanBuilder.ReadEnvironment();
        environment.Remove("GH_TOKEN"); environment.Remove("GITHUB_TOKEN");
        environment["DOTNET_NOLOGO"] = "1"; environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        if (OperatingSystem.IsWindows()) return WindowsProcess.Run(new(dotnet, arguments, packageDirectory, environment), cancellationToken: timeout.Token);
        ProcessStartInfo start = new(dotnet) { UseShellExecute = false, WorkingDirectory = packageDirectory };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach ((string key, string value) in environment) start.Environment[key] = value;
        using Process process = Process.Start(start) ?? throw new MhxException("Cannot start SDK tool installation.");
        if (!process.WaitForExit(120000)) { process.Kill(true); process.WaitForExit(); throw new MhxException("SDK tool installation timed out."); }
        return process.ExitCode;
    }

    public static async Task<int> FromRelease(string directory, string? profileDirectory, bool register, CancellationToken token)
    {
        directory = Path.GetFullPath(directory);
        string staging = Installation.NewPrivateDirectory();
        try
        {
            using GitHubDownloads downloads = new(GitHubAuth.Token());
            var (release, manifest) = await downloads.Latest(token);
            ReleaseFile file = ReleaseValidation.Select(manifest, tool: true, ReleaseValidation.Runtime());
            await downloads.Download(release, file, Path.Combine(staging, file.Name), token);
            Dictionary<string, string> environment = PlanBuilder.ReadEnvironment();
            string dotnet = ExecutableResolver.Resolve("dotnet", AppContext.BaseDirectory, environment);
            string sdk = DotnetSdk.Find(dotnet, environment);
            using FileStream gate = AtomicInstall.Lock(directory);
            int result = Install(directory, staging, manifest.Version, dotnet, sdk);
            if (result == 0 && register) Installation.Register(directory, profileDirectory);
            return result;
        }
        finally { Directory.Delete(staging, true); }
    }
}
