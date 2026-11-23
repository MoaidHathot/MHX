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
        string directory = PhysicalDirectory(Path.GetDirectoryName(Path.GetFullPath(executable))!);
        string store = Path.Combine(directory, ".store") + Path.DirectorySeparatorChar;
        string assemblyDirectory = PhysicalDirectory(baseDirectory);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Path.GetFileNameWithoutExtension(executable).Equals("mhx", comparison) && assemblyDirectory.StartsWith(store, comparison)
            ? directory : null;
    }

    internal static string PhysicalDirectory(string path) => PhysicalDirectory(path, 0);

    private static string PhysicalDirectory(string path, int depth)
    {
        if (depth > 40) throw new MhxException("Too many directory links in the installation path.");
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo directory = new(Path.Combine(current, part));
            FileSystemInfo? target = directory.Exists ? directory.ResolveLinkTarget(true) : null;
            // A link can point back through another aliased ancestor, e.g.
            // /private/var/.../alias -> /var/.../real on macOS.
            current = target is null ? directory.FullName : PhysicalDirectory(target.FullName, depth + 1);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    public static void Register(string bin, string? profileDirectory)
    {
        if (profileDirectory is not null)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(profileDirectory);
            else Directory.CreateDirectory(profileDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (OperatingSystem.IsWindows())
        {
            string old = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            Environment.SetEnvironmentVariable("PATH", PreferredUserPath(old, bin), EnvironmentVariableTarget.User);
            if (profileDirectory is not null) Environment.SetEnvironmentVariable("MHX_HOME", profileDirectory, EnvironmentVariableTarget.User);
            Console.Error.WriteLine("Restart existing terminals and background harnesses to inherit the registered PATH.");
        }
        else
        {
            Console.Error.WriteLine("Add this directory to the PATH of your terminal and GUI applications: " + bin);
            if (profileDirectory is not null) Console.Error.WriteLine("Set MHX_HOME to: " + profileDirectory);
        }
    }

    internal static string PreferredUserPath(string old, string bin)
    {
        IEnumerable<string> others = old.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.TrimEnd('\\', '/').Equals(bin.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
        return string.Join(';', new[] { bin }.Concat(others));
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
