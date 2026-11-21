namespace MHX.Distribution;

internal static class Installation
{
    public static string StateRoot
    {
        get
        {
            string root = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MHX")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "mhx");
            return Path.IsPathFullyQualified(root) ? root : throw new MhxException("Cannot determine a private user installation directory.");
        }
    }

    public static string DefaultBin => Path.Combine(StateRoot, "bin");
    public static string ExecutableName => OperatingSystem.IsWindows() ? "mhx.exe" : "mhx";

    public static string NewPrivateDirectory()
    {
        string directory = Path.Combine(StateRoot, "updates", Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    public static string? ToolDirectory(string executable, string baseDirectory)
    {
        // A tool apphost lives beside .store, whereas dotnet/dnx/local-manifest
        // execution has no installation-owned apphost to update.
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        string store = Path.Combine(directory, ".store") + Path.DirectorySeparatorChar;
        string assemblyDirectory = Path.GetFullPath(baseDirectory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFileNameWithoutExtension(executable).Equals("mhx", comparison) && assemblyDirectory.StartsWith(store, comparison)
            ? directory : null;
    }

    public static void Register(string bin, string? profileDirectory)
    {
        if (profileDirectory is not null) Directory.CreateDirectory(profileDirectory);
        if (OperatingSystem.IsWindows())
        {
            string old = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            List<string> entries = old.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!entries.Any(p => p.TrimEnd('\\', '/').Equals(bin.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))) entries.Add(bin);
            Environment.SetEnvironmentVariable("PATH", string.Join(';', entries), EnvironmentVariableTarget.User);
            if (profileDirectory is not null) Environment.SetEnvironmentVariable("MHX_HOME", profileDirectory, EnvironmentVariableTarget.User);
            Console.Error.WriteLine("Restart existing terminals and background harnesses to inherit the registered PATH.");
        }
        else
        {
            Console.Error.WriteLine("Add this directory to the PATH of your terminal and GUI applications: " + bin);
            if (profileDirectory is not null) Console.Error.WriteLine("Set MHX_HOME to: " + profileDirectory);
        }
    }

    public static int InstallNative(string source, string directory, string? profileDirectory, bool register)
    {
        directory = Path.GetFullPath(directory);
        string target = Path.Combine(directory, ExecutableName);
        using FileStream gate = AtomicInstall.Lock(directory);
        AtomicInstall.Recover(directory);
        string sourceHash = AtomicInstall.Hash(source);
        if (File.Exists(target) && AtomicInstall.Hash(target).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
        {
            InstalledVersion.Write(target, AppInfo.Version, sourceHash);
            if (register) Register(directory, profileDirectory);
            Console.Error.WriteLine($"MHX {AppInfo.Version} is already installed at {target}");
            return 0;
        }
        if (File.Exists(target) && InstalledVersion.Read(target, "0.0.0") > ReleaseValidation.StableVersion(AppInfo.Version))
            throw new MhxException("A newer MHX is already installed; installation will not downgrade it.");
        if (!Path.GetFullPath(source).Equals(target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            string staged = Path.Combine(directory, ".mhx-download-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(source, staged);
                AtomicInstall.Replace(staged, target, sourceHash);
                InstalledVersion.Write(target, AppInfo.Version, sourceHash);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
        if (register) Register(directory, profileDirectory);
        Console.Error.WriteLine($"Installed MHX {AppInfo.Version} at {target}");
        return 0;
    }
}
