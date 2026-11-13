using System.Text;
using System.Text.Json;

namespace MHX.Configuration;

internal static class ProfileReader
{
    internal const int MaximumBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly ProfileJsonContext JsonContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        RespectNullableAnnotations = true,
        MaxDepth = 16,
    });

    public static LaunchProfile Read(string path)
    {
        // Bound the actual read, including files that grow after being opened.
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes)
            throw new MhxException("Configuration exceeds the 1 MiB size limit.");
        using MemoryStream data = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            if (data.Length + read > MaximumBytes)
                throw new MhxException("Configuration exceeds the 1 MiB size limit.");
            data.Write(buffer, 0, read);
        }

        string text;
        try { text = StrictUtf8.GetString(data.GetBuffer(), 0, (int)data.Length).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new MhxException("Configuration must be valid UTF-8."); }

        LaunchProfile profile;
        if (Path.GetExtension(path).Equals(".shim", StringComparison.OrdinalIgnoreCase))
            profile = ShimReader.Parse(text);
        else
        {
            try
            {
                profile = JsonSerializer.Deserialize(text, JsonContext.LaunchProfile)
                    ?? throw new MhxException("Configuration must be a JSON object.");
            }
            catch (JsonException)
            {
                throw new MhxException("Invalid .mhx configuration: check JSON types, duplicate/unknown fields, and nesting depth.");
            }
        }
        Validate(profile);
        return profile;
    }

    internal static void Validate(LaunchProfile profile)
    {
        if (profile.Version != 1)
            throw new MhxException("Unsupported configuration version; expected version 1.");
        if (profile.Kind is not ("command" or "dotnet-tool"))
            throw new MhxException("Configuration kind must be 'command' or 'dotnet-tool'.");
        if (profile.Arguments is null || profile.Environment is null)
            throw new MhxException("Arguments and environment must not be null.");
        if (profile.Arguments.Length > 4096)
            throw new MhxException("Configuration contains too many arguments.");
        foreach (string? argument in profile.Arguments)
            ValidateText(argument, "Configured arguments", allowEmpty: true);

        HashSet<string> names = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string name, string? value) in profile.Environment)
        {
            ValidateText(name, "Environment names", allowEmpty: false);
            if (name.Contains('=') || !names.Add(name))
                throw new MhxException("Environment names must be unique and must not contain '='.");
            if (value is not null) ValidateText(value, "Environment values", allowEmpty: true);
        }
        if (profile.WorkingDirectory is not null)
            ValidateText(profile.WorkingDirectory, "Working directory", allowEmpty: false);

        if (profile.Kind == "command")
        {
            ValidateText(profile.Command, "Command", allowEmpty: false);
            if (profile.Dotnet is not null || profile.Sources is not null || profile.NugetConfig is not null)
                throw new MhxException("dotnet, sources, and nugetConfig require kind 'dotnet-tool'.");
        }
        else
        {
            if (profile.Command is not null)
                throw new MhxException("Use dotnet, rather than command, with kind 'dotnet-tool'.");
            if (profile.Dotnet is not null) ValidateText(profile.Dotnet, "Dotnet executable", false);
            if (profile.NugetConfig is not null) ValidateText(profile.NugetConfig, "NuGet configuration", false);
            if (profile.Sources is { Length: 0 })
                throw new MhxException("Sources must contain at least one source when specified.");
            if (profile.Sources is not null)
                foreach (string source in profile.Sources) ValidateText(source, "NuGet sources", false);
        }
    }

    internal static void ValidateText(string? value, string subject, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Contains('\0'))
            throw new MhxException($"{subject} must be valid strings without NUL characters.");
    }
}
