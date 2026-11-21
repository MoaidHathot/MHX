using System.Diagnostics;
using MHX.Execution;

namespace MHX.Distribution;

internal static class GitHubAuth
{
    public static string? Token()
    {
        foreach (string name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is string value && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        string gh;
        try { gh = ExecutableResolver.Resolve("gh", AppContext.BaseDirectory, PlanBuilder.ReadEnvironment()); }
        catch (MhxException) { return null; }
        ProcessStartInfo start = new(gh)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
        };
        foreach (string argument in new[] { "auth", "token", "--hostname", "github.com" }) start.ArgumentList.Add(argument);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        using Process process = Process.Start(start) ?? throw new MhxException("Cannot read GitHub CLI authentication.");
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(true); process.WaitForExit(); return null; }
        Task.WaitAll(output, error);
        return process.ExitCode == 0 && output.Result.Length < 16384 ? output.Result.Trim() : null;
    }
}
