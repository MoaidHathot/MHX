using System.Text;

namespace MHX.Configuration;

internal static class WindowsArguments
{
    // CRT quoting: leave plain arguments alone; quote empty/whitespace/quoted
    // arguments and double backslashes before quotes and the closing quote.
    // MHX never introduces a shell. A configured interpreter has its own grammar.
    public static string Encode(IEnumerable<string> arguments)
    {
        StringBuilder result = new();
        foreach (string argument in arguments)
        {
            ProfileReader.ValidateText(argument, "Arguments", true);
            if (result.Length != 0) result.Append(' ');
            if (argument.Length != 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            {
                result.Append(argument);
                continue;
            }
            result.Append('"');
            int slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
        }
        return result.ToString();
    }

    // The .shim compatibility format uses Windows argv syntax on every OS.
    // No shell operators, variable expansion, or command substitution are run.
    public static string[] Parse(string text)
    {
        List<string> arguments = [];
        int index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && text[index] is ' ' or '\t') index++;
            if (index == text.Length) break;
            StringBuilder argument = new();
            bool quoted = false;
            while (index < text.Length && (quoted || text[index] is not (' ' or '\t')))
            {
                int slashes = 0;
                while (index < text.Length && text[index] == '\\') { slashes++; index++; }
                if (index < text.Length && text[index] == '"')
                {
                    argument.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) { argument.Append('"'); index++; }
                    else if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                    { argument.Append('"'); index += 2; }
                    else { quoted = !quoted; index++; }
                }
                else
                {
                    argument.Append('\\', slashes);
                    if (index < text.Length && (quoted || text[index] is not (' ' or '\t')))
                        argument.Append(text[index++]);
                }
            }
            if (quoted) throw new MhxException("Unclosed quote in .shim arguments.");
            arguments.Add(argument.ToString());
        }
        return [.. arguments];
    }
}
