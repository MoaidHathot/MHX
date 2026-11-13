using System.Diagnostics;
using System.Text.Json;

namespace MHX.Tests;

internal sealed class TestFiles : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "opencode", "mhx-tests-" + Guid.NewGuid().ToString("N"));
    public static string Root { get; } = FindRoot();
    public static string Configuration => AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Release" : "Debug";
    public static string Launcher => Environment.GetEnvironmentVariable("MHX_TEST_EXE") ??
        Path.Combine(Root, "src", "MHX", "bin", Configuration, "net11.0", OperatingSystem.IsWindows() ? "mhx.exe" : "mhx");
    public static string Probe => Path.Combine(Root, "tests", "MHX.TestProbe", "bin", Configuration, "net11.0", OperatingSystem.IsWindows() ? "MHX.TestProbe.exe" : "MHX.TestProbe");

    public TestFiles() => System.IO.Directory.CreateDirectory(Directory);
    public string Write(string name, string content)
    {
        string path = Path.GetFullPath(Path.Combine(Directory, name));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.Replace("\r\n", "\n", StringComparison.Ordinal));
        return path;
    }
    public string Profile(object profile, string name = "test.mhx") => Write(name, JsonSerializer.Serialize(profile));
    public Process Start(string[] args, Dictionary<string, string?>? environment = null, string? executable = null)
    {
        ProcessStartInfo start = new(executable ?? Launcher)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Directory, CreateNoWindow = true,
        };
        start.Environment.Remove("MHX_CONFIG");
        start.Environment.Remove("MHX_LAUNCH_DEPTH");
        start.Environment["MHX_HOME"] = Path.Combine(Directory, "profiles");
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach ((string key, string? value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        return Process.Start(start)!;
    }
    public async Task<(int Exit, string Output, string Error)> Run(string[] args, Dictionary<string, string?>? environment = null, string? executable = null)
    {
        using Process process = Start(args, environment, executable);
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) process.Kill(true); process.WaitForExit(); }
        return (process.ExitCode, await output.WaitAsync(TimeSpan.FromSeconds(5)), await error.WaitAsync(TimeSpan.FromSeconds(5)));
    }
    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MHX.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Cannot locate MHX source root.");
    }
}
