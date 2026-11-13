namespace MHX.Configuration;

internal static class ShimReader
{
    public static LaunchProfile Parse(string text)
    {
        string? command = null;
        string? cwd = null;
        List<string> arguments = [];
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        HashSet<string> scalars = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            int separator = line.IndexOf('=');
            if (separator <= 0) throw new MhxException("Malformed .shim field; expected key = value.");
            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key.Equals("args", StringComparison.OrdinalIgnoreCase))
            {
                arguments.AddRange(WindowsArguments.Parse(value));
                continue;
            }
            if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase) && key.Length > 4)
            {
                if (!environment.TryAdd(key[4..], Unquote(value))) throw new MhxException("Duplicate .shim environment variable.");
                continue;
            }
            if (!scalars.Add(key)) throw new MhxException("Duplicate .shim field.");
            if (key.Equals("path", StringComparison.OrdinalIgnoreCase)) command = Unquote(value);
            else if (key.Equals("cwd", StringComparison.OrdinalIgnoreCase)) cwd = Unquote(value);
            else
                throw new MhxException("Unsupported .shim field. MHX supports path, args, cwd, and env:NAME; use .mhx for other options.");
        }
        return new LaunchProfile { Command = command, Arguments = [.. arguments], WorkingDirectory = cwd, Environment = environment };
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
