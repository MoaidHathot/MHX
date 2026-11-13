using System.Diagnostics;

namespace MHX.Execution;

internal static class DotnetSdk
{
    public static string Find(string dotnet, Dictionary<string, string> environment)
    {
        if (OperatingSystem.IsWindows())
            return Select(WindowsCapture.SdkList(new LaunchPlan(dotnet, ["--list-sdks"], Directory.GetCurrentDirectory(), environment)));
        ProcessStartInfo start = new(dotnet)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add("--list-sdks");
        start.Environment.Clear();
        foreach ((string key, string value) in environment) start.Environment[key] = value;
        using Process process = Process.Start(start) ?? throw new MhxException("Unable to start .NET SDK discovery.");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new MhxException(".NET SDK discovery timed out.");
        }
        Task.WaitAll(output, errors);
        if (process.ExitCode != 0) throw new MhxException(".NET SDK discovery failed.");
        return Select(output.Result);
    }

    internal static string Select(string listing)
    {
        // The host returns SDKs in version order, as used by the official dnx
        // scripts. Invoke the selected SDK explicitly to bypass global.json.
        string? selected = null;
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            int bracket = line.IndexOf('[');
            if (space <= 0 || bracket <= space || !line.EndsWith(']')) continue;
            string version = line[..space];
            if (!Version.TryParse(version.Split('-')[0], out Version? parsed) || parsed.Major < 11) continue;
            if (version.IndexOfAny(['/', '\\', ':']) >= 0) continue;
            string directory = line[(bracket + 1)..^1];
            if (!Path.IsPathFullyQualified(directory)) continue;
            string dll = Path.Combine(directory, version, "dotnet.dll");
            if (File.Exists(dll)) selected = dll;
        }
        return selected ?? throw new MhxException("The dotnet-tool backend requires an installed .NET SDK 11 or later.");
    }
}
