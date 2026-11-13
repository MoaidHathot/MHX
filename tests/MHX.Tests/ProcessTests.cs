using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MHX.Tests;

public sealed class ProcessTests
{
    public static bool WindowsOnly => OperatingSystem.IsWindows();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NativeArgumentsSurviveMetacharactersUnicodeEmptyAndRandomQuoting()
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = TestFiles.Probe, arguments = new[] { "prefix", "" } });
        List<string> args = ["", "two words", "雪é", "a\"b", "tail\\", "--", "--%", "%PATH%", "${env:SECRET}", "a&b|c>file", "$(exit)"];
        Random random = new(2048);
        const string alphabet = "abc XYZ\t\n\\\"&|<>%^!雪";
        for (int i = 0; i < 80; i++) args.Add(new string(Enumerable.Range(0, random.Next(1, 25)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray()));
        var result = await files.Run(["--mhx-config", config, .. args]);
        Assert.Equal(23, result.Exit);
        Assert.Empty(result.Output);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal(new[] { "prefix", "" }.Concat(args), report.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task StdioStreamsBeforeEofAndPreservesBinaryData()
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = TestFiles.Probe });
        using Process process = files.Start(["--mhx-config", config]);
        try
        {
            string? ready = await process.StandardError.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.StartsWith("{", ready);
            byte[] first = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"héllo 世界\"}\n");
            await process.StandardInput.BaseStream.WriteAsync(first, Token);
            await process.StandardInput.BaseStream.FlushAsync(Token);
            byte[] echoed = new byte[first.Length];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(echoed, Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.Equal(first, echoed);
            byte[] bytes = new byte[256 * 1024];
            new Random(12).NextBytes(bytes);
            using MemoryStream output = new();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(output, Token);
            await process.StandardInput.BaseStream.WriteAsync(bytes, Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token);
            await copy.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(bytes, output.ToArray());
            Assert.Equal(23, process.ExitCode);
        }
        finally { if (!process.HasExited) process.Kill(true); process.WaitForExit(); }
    }

    [Fact]
    public async Task EnvironmentExpansionRemovalAndWorkingDirectoryAreExplicit()
    {
        using TestFiles files = new();
        string config = files.Profile(new
        {
            command = TestFiles.Probe, workingDirectory = "${configDir}/work",
            environment = new Dictionary<string, string?> { ["PROBE_VALUE"] = "prefix:${env:SOURCE_VALUE}", ["PROBE_REMOVE"] = null },
        });
        Directory.CreateDirectory(Path.Combine(files.Directory, "work"));
        string markerName = "cwd-" + Guid.NewGuid().ToString("N");
        string markerValue = Guid.NewGuid().ToString("N");
        files.Write(Path.Combine("work", markerName), markerValue);
        var result = await files.Run(["--mhx-config", config], new() { ["SOURCE_VALUE"] = "secret", ["PROBE_REMOVE"] = "remove-me" });
        Assert.Equal(23, result.Exit);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal("prefix:secret", report.RootElement.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("removed").ValueKind);
        Assert.Equal(markerValue, File.ReadAllText(Path.Combine(report.RootElement.GetProperty("cwd").GetString()!, markerName)));
    }

    [Fact]
    public async Task CanStartWithAnExplicitEnvironmentAllowlist()
    {
        using TestFiles files = new();
        Dictionary<string, string?> env = new() { ["PROBE_VALUE"] = "only-this" };
        foreach (string name in new[] { "SYSTEMROOT", "DOTNET_ROOT" })
            if (Environment.GetEnvironmentVariable(name) is string value) env[name] = value;
        string config = files.Profile(new { command = TestFiles.Probe, inheritEnvironment = false, environment = env });
        var result = await files.Run(["--mhx-config", config], new() { ["PROBE_REMOVE"] = "private" });
        Assert.Equal(23, result.Exit);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal("only-this", report.RootElement.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("removed").ValueKind);
    }

    [Fact]
    public async Task ErrorsDoNotPrintArgumentOrEnvironmentSecrets()
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = "${env:MISSING_EXE}", arguments = new[] { "argument-secret" } });
        var result = await files.Run(["--mhx-config", config], new() { ["MISSING_EXE"] = "credential-secret-does-not-exist" });
        Assert.NotEqual(0, result.Exit);
        Assert.Empty(result.Output);
        Assert.DoesNotContain("credential-secret", result.Error);
        Assert.DoesNotContain("argument-secret", result.Error);
        Assert.Contains("mhx:", result.Error);
    }

    [Fact]
    public async Task NamedProfilesWorkWithoutAProfileOrShell()
    {
        using TestFiles files = new();
        files.Profile(new { command = TestFiles.Probe, arguments = new[] { "fixed" } }, "profiles/recall.mhx");
        var result = await files.Run(["--mhx-profile", "recall", "--", "mcp"]);
        Assert.Equal(23, result.Exit);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal(["fixed", "--", "mcp"], report.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task RenamedExecutableFindsItsOwnSidecar()
    {
        using TestFiles files = new();
        string launcherDirectory = Path.GetDirectoryName(TestFiles.Launcher)!;
        foreach (string source in Directory.EnumerateFiles(launcherDirectory))
            File.Copy(source, Path.Combine(files.Directory, Path.GetFileName(source)));
        string alias = Path.Combine(files.Directory, OperatingSystem.IsWindows() ? "recall.exe" : "recall");
        File.Copy(TestFiles.Launcher, alias);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(alias, File.GetUnixFileMode(TestFiles.Launcher));
        files.Profile(new { command = TestFiles.Probe, arguments = new[] { "alias" } }, "recall.mhx");
        var result = await files.Run(["mcp"], executable: alias);
        Assert.Equal(23, result.Exit);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal(["alias", "mcp"], report.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task DotnetBackendSelectsSdkAndPassesSourcesAndArguments()
    {
        using TestFiles files = new();
        string sdk = files.Write("sdk/11.0.100/dotnet.dll", "test");
        string config = files.Profile(new { kind = "dotnet-tool", dotnet = TestFiles.Probe });
        var result = await files.Run(["--mhx-config", config, "Package@1.2.3", "--", "mcp"], new()
        {
            ["PROBE_SDK_LIST"] = $"10.0.404 [{Path.Combine(files.Directory, "sdk")}]\n11.0.100 [{Path.Combine(files.Directory, "sdk")}]",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = null,
        });
        Assert.Equal(23, result.Exit);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal(["exec", sdk, "tool", "exec", "--source", "https://www.nuget.org/api/v2/", "Package@1.2.3", "--", "mcp"],
            report.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("1", report.RootElement.GetProperty("telemetry").GetString());
    }

    [Fact]
    public async Task DotnetBackendNormalizesLocalFeedUrisAndUsesExplicitNugetConfig()
    {
        using TestFiles files = new();
        string sdk = files.Write("sdk/11.0.100/dotnet.dll", "fixture");
        string nuget = files.Write("nuget.config", "<configuration/>");
        string feed = Path.Combine(files.Directory, "feed space");
        string config = files.Profile(new
        {
            kind = "dotnet-tool", dotnet = TestFiles.Probe, nugetConfig = "nuget.config",
            sources = new[] { new Uri(feed).AbsoluteUri, "./relative-feed" },
        });
        var result = await files.Run(["--mhx-config", config, "Package"],
            new() { ["PROBE_SDK_LIST"] = $"11.0.100 [{Path.Combine(files.Directory, "sdk")}]" });
        Assert.Equal(23, result.Exit);
        using JsonDocument report = JsonDocument.Parse(result.Error);
        Assert.Equal(["exec", sdk, "tool", "exec", "--configfile", nuget, "--source", feed,
                "--source", Path.Combine(files.Directory, "relative-feed"), "Package"],
            report.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task RecursiveLaunchesAreBounded()
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = TestFiles.Probe });
        var result = await files.Run(["--mhx-config", config], new() { ["MHX_LAUNCH_DEPTH"] = "16" });
        Assert.Equal(1, result.Exit);
        Assert.Contains("recursion", result.Error);
    }

    [Fact]
    public async Task DotnetSelfReferenceFailsBeforeSdkDiscovery()
    {
        using TestFiles files = new();
        string config = files.Profile(new { kind = "dotnet-tool", dotnet = TestFiles.Launcher });
        var result = await files.Run(["--mhx-config", config, "Package"]);
        Assert.Equal(1, result.Exit);
        Assert.Contains("cannot directly launch itself", result.Error);
    }

    [Fact]
    public async Task SdkDiscoveryErrorsArePrivate()
    {
        using TestFiles files = new();
        string config = files.Profile(new { kind = "dotnet-tool", dotnet = TestFiles.Probe });
        var result = await files.Run(["--mhx-config", config, "Package"], new() { ["PROBE_SDK_MODE"] = "fail" });
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Contains("SDK discovery failed", result.Error);
        Assert.DoesNotContain("private-sdk-diagnostic", result.Error);
    }

    [Fact(SkipUnless = nameof(WindowsOnly), Skip = "Windows bounded SDK capture")]
    public async Task SdkDiscoveryOutputIsBounded()
    {
        using TestFiles files = new();
        string config = files.Profile(new { kind = "dotnet-tool", dotnet = TestFiles.Probe });
        var result = await files.Run(["--mhx-config", config, "Package"], new() { ["PROBE_SDK_MODE"] = "flood" });
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Contains("output limit", result.Error);
    }

    [Fact(SkipUnless = nameof(WindowsOnly), Skip = "Windows job objects")]
    public async Task DiscoveryChildAlsoDiesWithLauncher()
    {
        using TestFiles files = new();
        string pidFile = Path.Combine(files.Directory, "child.pid");
        string config = files.Profile(new { kind = "dotnet-tool", dotnet = TestFiles.Probe });
        using Process launcher = files.Start(["--mhx-config", config, "Package"],
            new() { ["PROBE_SDK_MODE"] = "hold", ["PROBE_PID_FILE"] = pidFile });
        Process? child = null;
        try
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            int pid;
            while (!File.Exists(pidFile) || !int.TryParse(await File.ReadAllTextAsync(pidFile, deadline.Token), out pid))
                await Task.Delay(20, deadline.Token);
            child = Process.GetProcessById(pid);
            launcher.Kill(entireProcessTree: false);
            await launcher.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally
        {
            if (!launcher.HasExited) launcher.Kill(true);
            launcher.WaitForExit();
            if (child is not null) { if (!child.HasExited) child.Kill(true); child.WaitForExit(); child.Dispose(); }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    [InlineData(255)]
    public async Task ExitCodesArePassedThrough(int exit)
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = TestFiles.Probe });
        var result = await files.Run(["--mhx-config", config], new() { ["PROBE_EXIT"] = exit.ToString() });
        Assert.Equal(exit, result.Exit);
    }

    [Theory(SkipUnless = nameof(WindowsOnly), Skip = "Windows exit code semantics")]
    [InlineData(1024)]
    [InlineData(-123)]
    public async Task WindowsExitCodesAreNotTruncated(int exit)
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = TestFiles.Probe });
        var result = await files.Run(["--mhx-config", config], new() { ["PROBE_EXIT"] = exit.ToString() });
        Assert.Equal(exit, result.Exit);
    }

    [Fact(SkipUnless = nameof(WindowsOnly), Skip = "Windows explicit command interpreter")]
    public async Task ExplicitCommandInterpreterSupportsFixedCommands()
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = "cmd.exe", arguments = new[] { "/d", "/c", "exit", "37" } });
        var result = await files.Run(["--mhx-config", config]);
        Assert.Equal(37, result.Exit);
    }

    [Fact(SkipUnless = nameof(WindowsOnly), Skip = "Windows job objects")]
    public async Task KillingLauncherTerminatesChildAndGrandchild()
    {
        using TestFiles files = new();
        string config = files.Profile(new { command = TestFiles.Probe, arguments = new[] { "tree" } });
        using Process launcher = files.Start(["--mhx-config", config]);
        Process? child = null, grandchild = null;
        try
        {
            string? line = await launcher.StandardError.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
            using JsonDocument report = JsonDocument.Parse(line!);
            child = Process.GetProcessById(report.RootElement.GetProperty("pid").GetInt32());
            grandchild = Process.GetProcessById(report.RootElement.GetProperty("descendant").GetInt32());
            launcher.Kill(entireProcessTree: false);
            await launcher.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
            await grandchild.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally
        {
            if (!launcher.HasExited) launcher.Kill(true);
            launcher.WaitForExit();
            if (child is not null) { if (!child.HasExited) child.Kill(true); child.WaitForExit(); child.Dispose(); }
            if (grandchild is not null) { if (!grandchild.HasExited) grandchild.Kill(true); grandchild.WaitForExit(); grandchild.Dispose(); }
        }
    }
}
