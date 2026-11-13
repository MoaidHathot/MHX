namespace MHX;

// Messages are deliberately authored here rather than copied from exceptions
// that may contain expanded arguments, environment values, or credential URLs.
internal sealed class MhxException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
