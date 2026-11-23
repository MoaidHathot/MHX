using MHX.Configuration;
using MHX.Execution;

namespace MHX.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"command\":\"a\",\"command\":\"b\"}")]
    [InlineData("{\"command\":\"a\",\"unknown\":true}")]
    [InlineData("{\"command\":\"a\",\"version\":2}")]
    [InlineData("{\"command\":\"a\",\"arguments\":[null]}")]
    [InlineData("{\"command\":\"a\",\"arguments\":[\"\\u0000\"]}")]
    [InlineData("{\"command\":\"a\",\"environment\":{\"a=b\":\"x\"}}")]
    [InlineData("{\"command\":\"a\",\"sources\":[\"https://example.test\"]}")]
    [InlineData("{\"kind\":\"dotnet-tool\",\"sources\":[]}")]
    public void RejectsInvalidProfiles(string json)
    {
        using TestFiles files = new();
        Assert.Throws<MhxException>(() => ProfileReader.Read(files.Write("bad.mhx", json)));
    }

    [Fact]
    public void AcceptsCommentsAndTrailingCommas()
    {
        using TestFiles files = new();
        LaunchProfile profile = ProfileReader.Read(files.Write("ok.mhx", "{ // comment\n\"kind\":\"dotnet-tool\",}"));
        Assert.Equal("dotnet-tool", profile.Kind);
        Assert.Equal(1, profile.Version);
        Assert.True(profile.InheritEnvironment);
        Assert.Empty(profile.Arguments);
        Assert.Empty(profile.Environment);
    }

    [Fact]
    public void RejectsOversizedAndInvalidUtf8Files()
    {
        using TestFiles files = new();
        string path = files.Write("bad.mhx", new string(' ', ProfileReader.MaximumBytes + 1));
        Assert.Throws<MhxException>(() => ProfileReader.Read(path));
        File.WriteAllBytes(path, [0xff, 0xfe, 0xff]);
        Assert.Throws<MhxException>(() => ProfileReader.Read(path));
    }

    [Fact]
    public void ExpansionIsSinglePassAndDoesNotInterpretCode()
    {
        Expansion expansion = new("config", "launcher", "cwd", "home", new Dictionary<string, string>
        { ["VALUE"] = "${env:SECRET}", ["CODE"] = "$(Write-Host nope); & exit" });
        Assert.Equal("${env:SECRET}", expansion.Expand("${env:VALUE}"));
        Assert.Equal("${env:VALUE}", expansion.Expand("$${env:VALUE}"));
        Assert.Equal("$(Write-Host nope); & exit", expansion.Expand("${env:CODE}"));
        Assert.Throws<MhxException>(() => expansion.Expand("${env:MISSING}"));
        Assert.Throws<MhxException>(() => expansion.Expand("${unknown}"));
    }

    [Fact]
    public void ShimHasExplicitRestrictedFieldsAndLiteralArguments()
    {
        LaunchProfile profile = ShimReader.Parse("path = \"some tool.exe\"\nargs = \"\" \"two words\" \"a\\\"b\"\nargs = & | %PATH%\nenv:KEY = value\n");
        Assert.Equal("some tool.exe", profile.Command);
        Assert.Equal(["", "two words", "a\"b", "&", "|", "%PATH%"], profile.Arguments);
        Assert.Throws<MhxException>(() => ShimReader.Parse("path = x\nelevate = true"));
        Assert.Throws<MhxException>(() => ShimReader.Parse("path = x\npath = y"));
        Assert.Throws<MhxException>(() => ShimReader.Parse("path = x\nargs = \"unclosed"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("/absolute")]
    [InlineData("C:\\escape")]
    [InlineData("a/b")]
    public void RejectsProfileTraversal(string name) => Assert.Throws<MhxException>(() => ProfileLocator.ValidateName(name));

    [Fact]
    public void ConfigLocationDoesNotUseCurrentDirectory()
    {
        using TestFiles files = new();
        string workingConfig = files.Write("mhx.mhx", "{}");
        string bin = Path.Combine(files.Directory, "bin");
        LauncherLocation location = new(Path.Combine(bin, "mhx.exe"), bin, "mhx");
        Dictionary<string, string> env = new() { ["MHX_HOME"] = Path.Combine(files.Directory, "home") };
        Assert.Null(ProfileLocator.Locate(location, null, null, env));
        Assert.Equal(workingConfig, ProfileLocator.Locate(location, workingConfig, null, env));
        files.Write("bin/mhx.mhx", "{}"); files.Write("bin/mhx.shim", "path = x");
        Assert.Throws<MhxException>(() => ProfileLocator.Locate(location, null, null, env));
    }

    [Fact]
    public void ExecutableSearchSkipsRelativeAndEmptyPathEntries()
    {
        using TestFiles files = new();
        Assert.Throws<MhxException>(() => ExecutableResolver.Resolve("not-a-real-executable", files.Directory,
            new Dictionary<string, string> { ["PATH"] = Path.PathSeparator + "." }));
    }

    [Fact]
    public void DirectorySidecarsAndRelativeHomeFailClosed()
    {
        using TestFiles files = new();
        LauncherLocation location = new(Path.Combine(files.Directory, "mhx.exe"), files.Directory, "mhx");
        Assert.Throws<MhxException>(() => ProfileLocator.Home(new Dictionary<string, string> { ["MHX_HOME"] = "relative" }));
        Directory.CreateDirectory(Path.Combine(files.Directory, "mhx.mhx"));
        Assert.Throws<MhxException>(() => ProfileLocator.Locate(location, null, null, new Dictionary<string, string>()));
    }

    [Fact]
    public void ProfileHomeUsesXdgOnEveryPlatform()
    {
        using TestFiles files = new();
        Dictionary<string, string> environment = new() { ["XDG_CONFIG_HOME"] = Path.Combine(files.Directory, "config") };
        Assert.Equal(Path.Combine(files.Directory, "config", "mhx"), ProfileLocator.Home(environment));
        environment["MHX_HOME"] = "";
        Assert.Equal(Path.Combine(files.Directory, "config", "mhx"), ProfileLocator.Home(environment));
    }

    [Fact]
    public void ApplicationHomeOverridesXdgAndRejectsInvalidOverrides()
    {
        using TestFiles files = new();
        Dictionary<string, string> environment = new()
        {
            ["MHX_HOME"] = Path.Combine(files.Directory, "custom"),
            ["XDG_CONFIG_HOME"] = Path.Combine(files.Directory, "config"),
        };
        Assert.Equal(environment["MHX_HOME"], ProfileLocator.Home(environment));
        environment["MHX_HOME"] = "relative";
        Assert.Throws<MhxException>(() => ProfileLocator.Home(environment));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(" ")]
    public void InvalidXdgHomeUsesPlatformFallback(string xdg)
    {
        string fallback = ProfileLocator.Home(new Dictionary<string, string>());
        Assert.True(Path.IsPathFullyQualified(fallback));
        Assert.Equal(fallback, ProfileLocator.Home(new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = xdg }));
    }

    [Fact]
    public void AllShippedProfilesAreValid()
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(TestFiles.Root, "examples")))
            ProfileReader.Read(path);
    }

    [Fact]
    public void LauncherNamesPreserveDotsAndUnderstandDllHosting()
    {
        using TestFiles files = new();
        Assert.Equal("my.tool", LauncherLocation.FromPaths(Path.Combine(files.Directory, "my.tool"), "ignored").Name);
        Assert.Equal("my.tool", LauncherLocation.FromPaths(Path.Combine(files.Directory, "my.tool.exe"), "ignored").Name);
        Assert.Equal("mhx", LauncherLocation.FromPaths(Path.Combine(files.Directory, "dotnet.exe"), Path.Combine(files.Directory, "mhx.dll")).Name);
    }

    [Fact]
    public void SdkSelectionIgnoresOldMissingAndMalformedEntries()
    {
        using TestFiles files = new();
        files.Write("sdk/10.0.404/dotnet.dll", "older SDK fixture");
        string valid = files.Write("sdk/11.0.100/dotnet.dll", "fixture");
        string root = Path.Combine(files.Directory, "sdk");
        string listing = $"8.0.428 [{root}]\n10.0.404 [{root}]\n11.0.100 [{root}]\n99.0.0 [{root}]\n99.0.0 [relative]\n";
        Assert.Equal(valid, DotnetSdk.Select(listing));
        Assert.Throws<MhxException>(() => DotnetSdk.Select("8.0.428 [missing]"));
        Assert.Contains("SDK 11", Assert.Throws<MhxException>(() => DotnetSdk.Select($"10.0.404 [{root}]\n")).Message);
    }
}
