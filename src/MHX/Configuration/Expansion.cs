using System.Text;

namespace MHX.Configuration;

internal sealed class Expansion(string configDirectory, string launcherDirectory, string workingDirectory,
    string homeDirectory, IReadOnlyDictionary<string, string> environment)
{
    public string Expand(string text)
    {
        StringBuilder result = new();
        for (int index = 0; index < text.Length;)
        {
            if (text[index] == '$' && index + 1 < text.Length && text[index + 1] == '$')
            { result.Append('$'); index += 2; continue; }
            if (text[index] != '$' || index + 1 >= text.Length || text[index + 1] != '{')
            { result.Append(text[index++]); continue; }
            int end = text.IndexOf('}', index + 2);
            if (end < 0) throw new MhxException("Unclosed configuration placeholder.");
            string name = text[(index + 2)..end];
            string value = name switch
            {
                "configDir" => configDirectory,
                "launcherDir" => launcherDirectory,
                "cwd" => workingDirectory,
                "home" => homeDirectory,
                _ when name.StartsWith("env:", StringComparison.Ordinal) && name.Length > 4 =>
                    environment.TryGetValue(name[4..], out string? variable) ? variable :
                    throw new MhxException("A required configuration environment variable is not set."),
                _ => throw new MhxException("Unknown configuration placeholder; use configDir, launcherDir, cwd, home, or env:NAME."),
            };
            // Deliberately single-pass: substituted content is always literal.
            result.Append(value);
            index = end + 1;
        }
        string expanded = result.ToString();
        ProfileReader.ValidateText(expanded, "Expanded configuration", true);
        return expanded;
    }
}
