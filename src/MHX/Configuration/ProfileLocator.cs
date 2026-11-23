namespace MHX.Configuration;

internal sealed record LauncherLocation(string Path, string Directory, string Name)
{
    public static LauncherLocation Current()
    {
        string path = Environment.ProcessPath ?? throw new MhxException("Cannot determine the launcher location.");
        return FromPaths(path, Environment.GetCommandLineArgs()[0]);
    }

    internal static LauncherLocation FromPaths(string path, string entryPoint)
    {
        if (System.IO.Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            System.IO.Path.GetExtension(entryPoint).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            path = entryPoint;
        path = System.IO.Path.GetFullPath(path);
        string name = System.IO.Path.GetFileName(path);
        string extension = System.IO.Path.GetExtension(name);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".com", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            name = System.IO.Path.GetFileNameWithoutExtension(name);
        return new(path, System.IO.Path.GetDirectoryName(path)!, name);
    }
}

internal static class ProfileLocator
{
    public static string Home(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.TryGetValue("MHX_HOME", out string? home) && !string.IsNullOrEmpty(home))
            return RequireAbsolute(home, "MHX_HOME");
        if (environment.TryGetValue("XDG_CONFIG_HOME", out string? xdg) && Path.IsPathFullyQualified(xdg))
            return Path.GetFullPath(Path.Combine(xdg, "mhx"));
        if (OperatingSystem.IsWindows())
            return RequireAbsolute(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MHX"), "User configuration directory");
        return RequireAbsolute(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mhx"), "User configuration directory");
    }

    public static string? Locate(LauncherLocation launcher, string? explicitFile, string? profileName,
        IReadOnlyDictionary<string, string> environment)
    {
        if (explicitFile is not null) return RequireExisting(Path.GetFullPath(explicitFile));
        if (profileName is not null)
        {
            ValidateName(profileName);
            return FindPair(Home(environment), profileName) ?? throw new MhxException("Named MHX profile was not found.");
        }
        if (environment.TryGetValue("MHX_CONFIG", out string? configured))
            return RequireExisting(RequireAbsolute(configured, "MHX_CONFIG"));
        string? sidecar = FindPair(launcher.Directory, launcher.Name);
        if (sidecar is not null) return sidecar;
        string? userProfile = FindPair(Home(environment), launcher.Name);
        if (userProfile is not null) return userProfile;
        if (!launcher.Name.Equals("mhx", StringComparison.OrdinalIgnoreCase))
            throw new MhxException("This named launcher requires a matching .mhx or .shim profile.");
        return null;
    }

    internal static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 128 || name[0] is '.' or '-' ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) || name is "." or "..")
            throw new MhxException("Profile names may contain ASCII letters, numbers, '.', '-', and '_', and must start with a letter, number, or '_'.");
    }

    private static string? FindPair(string directory, string name)
    {
        string mhx = Path.Combine(directory, name + ".mhx");
        string shim = Path.Combine(directory, name + ".shim");
        bool hasMhx = Exists(mhx);
        bool hasShim = Exists(shim);
        if (hasMhx && hasShim) throw new MhxException("Both .mhx and .shim profiles exist for this name; choose one.");
        return hasMhx ? mhx : hasShim ? shim : null;
    }

    private static string RequireAbsolute(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path)) throw new MhxException($"{name} must be an absolute path.");
        return Path.GetFullPath(path);
    }

    private static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new MhxException("A configuration path names a directory rather than a file.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // Access failures must not silently fall back to another profile.
    }

    private static string RequireExisting(string path) => Exists(path) ? path : throw new MhxException("The selected MHX configuration file does not exist.");
}
