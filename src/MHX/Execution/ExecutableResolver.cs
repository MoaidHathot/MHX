using MHX.Configuration;

namespace MHX.Execution;

internal static class ExecutableResolver
{
    public static string Resolve(string command, string baseDirectory, IReadOnlyDictionary<string, string> environment)
    {
        ProfileReader.ValidateText(command, "Executable", false);
        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
        {
            string path = Path.GetFullPath(command, baseDirectory);
            if (IsExecutable(path)) return path;
        }
        else if (environment.TryGetValue("PATH", out string? path))
        {
            foreach (string item in path.Split(Path.PathSeparator))
            {
                string directory = item.Trim('"');
                // Empty/relative PATH entries must not make a checkout executable.
                if (!Path.IsPathFullyQualified(directory)) continue;
                foreach (string suffix in OperatingSystem.IsWindows() && !Path.HasExtension(command) ? new[] { ".exe", ".com" } : new[] { "" })
                {
                    string candidate = Path.Combine(directory, command + suffix);
                    if (IsExecutable(candidate)) return Path.GetFullPath(candidate);
                }
            }
        }
        throw new MhxException("Executable not found. Use an executable path or an absolute PATH entry; scripts require an explicit interpreter.", 127);
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows())
            return Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetExtension(path).Equals(".com", StringComparison.OrdinalIgnoreCase);
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
