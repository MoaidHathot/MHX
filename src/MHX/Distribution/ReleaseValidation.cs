using System.Runtime.InteropServices;

namespace MHX.Distribution;

internal static class ReleaseValidation
{
    public const long MaximumDownload = 200 * 1024 * 1024;

    public static Version StableVersion(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new MhxException("Release version is missing.");
        string[] parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Length > 9 ||
                (p.Length > 1 && p[0] == '0') || p.Any(c => !char.IsAsciiDigit(c))) ||
            !Version.TryParse(text, out Version? version))
            throw new MhxException("The release version is not a supported stable version.");
        return version;
    }

    public static void Manifest(ReleaseManifest manifest, GitHubRelease release)
    {
        if (release.Draft || release.Prerelease || string.IsNullOrEmpty(release.TagName) || !release.TagName.StartsWith('v'))
            throw new MhxException("Expected a published stable MHX release.");
        StableVersion(release.TagName[1..]);
        StableVersion(manifest.Version);
        if (manifest.SchemaVersion != 1 || release.TagName != "v" + manifest.Version ||
            manifest.Files is null || manifest.Files.Length is < 1 or > 32)
            throw new MhxException("Release manifest version or format is invalid.");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ReleaseFile file in manifest.Files)
        {
            if (file is null || string.IsNullOrEmpty(file.Name) || file.Name.Length > 160 ||
                file.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
                file.Name.StartsWith('.') || !names.Add(file.Name) || !ValidHash(file.Sha256) ||
                file.Size <= 0 || file.Size > MaximumDownload)
                throw new MhxException("Release manifest contains an invalid file entry.");
        }
    }

    public static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit);

    public static string Runtime()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
            OperatingSystem.IsMacOS() ? "osx" : throw new MhxException("Unsupported update platform.");
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64", Architecture.Arm64 => "arm64",
            _ => throw new MhxException("Unsupported update architecture."),
        };
        return os + "-" + arch;
    }

    public static ReleaseFile Select(ReleaseManifest manifest, bool tool, string runtime)
    {
        string name = tool ? $"MHX.{manifest.Version}.nupkg" : "mhx-" + runtime + (runtime.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "");
        ReleaseFile[] matches = manifest.Files.Where(f => f.Name == name &&
            f.Kind == (tool ? "dotnet-tool" : "native-aot") && (tool || f.Runtime == runtime)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new MhxException("This release has no matching MHX distribution for this platform.");
    }
}
