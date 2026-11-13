using System.Collections;
using MHX.Configuration;

namespace MHX.Execution;

internal sealed record LaunchPlan(string Executable, string[] Arguments, string WorkingDirectory, Dictionary<string, string> Environment);

internal static class PlanBuilder
{
    public static StringComparer EnvironmentComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static Dictionary<string, string> ReadEnvironment()
    {
        Dictionary<string, string> result = new(EnvironmentComparer);
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (item.Key is string key && item.Value is string value) result[key] = value;
        return result;
    }

    public static LaunchPlan Build(LaunchProfile profile, string? configPath, LauncherLocation launcher,
        string[] callerArguments, Dictionary<string, string> inherited)
    {
        string cwd = Directory.GetCurrentDirectory();
        string baseDirectory = configPath is null ? launcher.Directory : Path.GetDirectoryName(configPath)!;
        Expansion expansion = new(baseDirectory, launcher.Directory, cwd,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), inherited);
        Dictionary<string, string> environment = profile.InheritEnvironment
            ? new(inherited, EnvironmentComparer) : new(EnvironmentComparer);
        foreach ((string name, string? value) in profile.Environment)
        {
            if (value is null) environment.Remove(name);
            else environment[name] = expansion.Expand(value);
        }
        int depth = 0;
        if (inherited.TryGetValue("MHX_LAUNCH_DEPTH", out string? text) &&
            (!int.TryParse(text, out depth) || depth < 0))
            throw new MhxException("Invalid MHX launch depth.");
        if (depth >= 16) throw new MhxException("MHX launch recursion limit reached.");
        environment["MHX_LAUNCH_DEPTH"] = (depth + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        string workingDirectory = profile.WorkingDirectory is null ? cwd :
            Path.GetFullPath(expansion.Expand(profile.WorkingDirectory), baseDirectory);
        if (!Directory.Exists(workingDirectory)) throw new MhxException("Configured working directory does not exist.");
        foreach (string argument in callerArguments) ProfileReader.ValidateText(argument, "Arguments", true);
        List<string> arguments = [.. profile.Arguments.Select(expansion.Expand), .. callerArguments];
        string executable;
        if (profile.Kind == "dotnet-tool")
        {
            environment.TryAdd("DOTNET_NOLOGO", "1");
            environment.TryAdd("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
            executable = ExecutableResolver.Resolve(expansion.Expand(profile.Dotnet ?? "dotnet"), baseDirectory, environment);
            RejectSelf(executable, launcher);
            string sdk = DotnetSdk.Find(executable, environment);
            List<string> prefix = ["exec", sdk, "tool", "exec"];
            // SDK informational messages can otherwise precede MCP JSON on
            // stdout (notably signature-verification notices on macOS).
            if (!HasSdkVerbosity(arguments)) prefix.AddRange(["--verbosity", "quiet"]);
            if (profile.NugetConfig is not null)
            {
                string path = Path.GetFullPath(expansion.Expand(profile.NugetConfig), baseDirectory);
                if (!File.Exists(path)) throw new MhxException("Configured NuGet configuration file does not exist.");
                prefix.AddRange(["--configfile", path]);
            }
            string[]? sources = profile.Sources ?? (profile.NugetConfig is null ? ["https://www.nuget.org/api/v2/"] : null);
            if (sources is not null)
                foreach (string source in sources)
                {
                    string expanded = expansion.Expand(source);
                    if (Uri.TryCreate(expanded, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                        expanded = Path.GetFullPath(uri.LocalPath, baseDirectory);
                    else if (uri is null)
                        expanded = Path.GetFullPath(expanded, baseDirectory);
                    prefix.AddRange(["--source", expanded]);
                }
            arguments.InsertRange(0, prefix);
        }
        else executable = ExecutableResolver.Resolve(expansion.Expand(profile.Command!), baseDirectory, environment);

        RejectSelf(executable, launcher);
        return new(executable, [.. arguments], workingDirectory, environment);
    }

    private static void RejectSelf(string executable, LauncherLocation launcher)
    {
        if (Path.GetFullPath(executable).Equals(launcher.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new MhxException("A launcher cannot directly launch itself.");
    }

    private static bool HasSdkVerbosity(IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument == "--") break;
            if (argument is "-v" or "--verbosity" ||
                argument.StartsWith("-v:", StringComparison.Ordinal) ||
                argument.StartsWith("-v=", StringComparison.Ordinal) ||
                argument.StartsWith("--verbosity:", StringComparison.Ordinal) ||
                argument.StartsWith("--verbosity=", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
