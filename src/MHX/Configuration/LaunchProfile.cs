using System.Text.Json.Serialization;

namespace MHX.Configuration;

internal sealed class LaunchProfile
{
    // Setters preserve field initializers for omitted properties with generated
    // metadata. Keep regression coverage for defaults when changing this model.
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }
    public int Version { get; set; } = 1;
    public string Kind { get; set; } = "command";
    public string? Command { get; set; }
    public string[] Arguments { get; set; } = [];
    public string? WorkingDirectory { get; set; }
    public bool InheritEnvironment { get; set; } = true;
    public Dictionary<string, string?> Environment { get; set; } = [];
    public string? Dotnet { get; set; }
    public string[]? Sources { get; set; }
    public string? NugetConfig { get; set; }

    public static LaunchProfile DefaultDotnetTool() => new() { Kind = "dotnet-tool" };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LaunchProfile))]
internal partial class ProfileJsonContext : JsonSerializerContext;
