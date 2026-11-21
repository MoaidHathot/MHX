using System.Text.Json;

namespace MHX.Distribution;

internal static class InstalledVersion
{
    public static Version Read(string target, string fallback)
    {
        string statePath = target + ".install.json";
        if (!File.Exists(statePath)) return ReleaseValidation.StableVersion(fallback);
        if (new FileInfo(statePath).Length > 8192) throw new MhxException("Invalid installation state.");
        NativeInstallState state = JsonSerializer.Deserialize(File.ReadAllText(statePath), ReleaseJsonContext.Default.NativeInstallState)
            ?? throw new MhxException("Invalid installation state.");
        if (state.SchemaVersion != 1 || !ReleaseValidation.ValidHash(state.Sha256) ||
            !AtomicInstall.Hash(target).Equals(state.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new MhxException("Installed executable differs from its installation record. Reinstall the matching release before updating.");
        return ReleaseValidation.StableVersion(state.Version);
    }

    public static void Write(string target, string version, string hash)
    {
        string path = target + ".install.json";
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new NativeInstallState { Version = version, Sha256 = hash }, ReleaseJsonContext.Default.NativeInstallState);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
