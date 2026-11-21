using System.Text.Json.Serialization;

namespace MHX.Distribution;

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public GitHubAsset[] Assets { get; set; } = [];
}

internal sealed class GitHubAsset
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string? Digest { get; set; }
}

internal sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public ReleaseFile[] Files { get; set; } = [];
}

internal sealed class ReleaseFile
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Runtime { get; set; }
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class ToolUpdateRequest
{
    public int ParentId { get; set; }
    public long ParentStartTicks { get; set; }
    public string InstallDirectory { get; set; } = "";
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Dotnet { get; set; } = "";
    public string Sdk { get; set; } = "";
}

internal sealed class RecoveryRecord
{
    public string Target { get; set; } = "";
    public string Backup { get; set; } = "";
    public string OldHash { get; set; } = "";
    public string NewHash { get; set; } = "";
}

internal sealed class NativeInstallState
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(ReleaseManifest))]
[JsonSerializable(typeof(ToolUpdateRequest))]
[JsonSerializable(typeof(RecoveryRecord))]
[JsonSerializable(typeof(NativeInstallState))]
internal partial class ReleaseJsonContext : JsonSerializerContext;

internal static class AppInfo
{
    public static string Version => typeof(AppInfo).Assembly.GetName().Version!.ToString(3);
#if MHX_TOOL
    public static bool IsTool => true;
#else
    public static bool IsTool => false;
#endif
    public const string Repository = "MoaidHathot/MHX";
    public const string ManifestName = "mhx-release.json";
}
