using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MHX.Distribution;

namespace MHX.Tests;

public sealed class DistributionTests
{
    public static bool WindowsOnly => OperatingSystem.IsWindows();
    public static bool UnixOnly => !OperatingSystem.IsWindows();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-preview")]
    [InlineData("../1.2.3")]
    [InlineData("")]
    public void StableVersionsAreStrict(string value) => Assert.Throws<MhxException>(() => ReleaseValidation.StableVersion(value));

    [Fact]
    public async Task LatestReleaseUsesAuthenticatedApiAndValidatesManifest()
    {
        using Fixture fixture = new();
        var (_, manifest) = await fixture.Client.Latest(Token);
        Assert.Equal("9.0.0", manifest.Version);
        Assert.All(fixture.Handler.Requests, request => Assert.Equal("Bearer test-token", request.Authorization));
    }

    [Fact]
    public async Task DownloadDoesNotForwardCredentialsToCdn()
    {
        using Fixture fixture = new();
        fixture.Handler.Redirect = "https://release-assets.githubusercontent.com/binary";
        var (release, manifest) = await fixture.Client.Latest(Token);
        using TestFiles files = new();
        string target = Path.Combine(files.Directory, "download");
        await fixture.Client.Download(release, manifest.Files[0], target, Token);
        Assert.Equal(fixture.Payload, File.ReadAllBytes(target));
        Assert.Null(fixture.Handler.Requests.Last().Authorization);
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://api.github.com.attacker.test/file")]
    [InlineData("https://attacker.test/file")]
    [InlineData("https://github.com:444/file")]
    [InlineData("https://secret@github.com/file")]
    public async Task UnsafeRedirectsFailBeforeRequest(string destination)
    {
        using Fixture fixture = new();
        fixture.Handler.Redirect = destination;
        var (release, manifest) = await fixture.Client.Latest(Token);
        using TestFiles files = new();
        await Assert.ThrowsAsync<MhxException>(() => fixture.Client.Download(release, manifest.Files[0], Path.Combine(files.Directory, "download"), Token));
        Assert.DoesNotContain(fixture.Handler.Requests, r => r.Uri == destination);
    }

    [Fact]
    public async Task CorruptDownloadIsRemovedAndExistingFilesArePreserved()
    {
        using Fixture fixture = new();
        var (release, manifest) = await fixture.Client.Latest(Token);
        fixture.Handler.Payload = Encoding.UTF8.GetBytes("bad-payload");
        using TestFiles files = new();
        string destination = Path.Combine(files.Directory, "download");
        await Assert.ThrowsAsync<MhxException>(() => fixture.Client.Download(release, manifest.Files[0], destination, Token));
        Assert.False(File.Exists(destination));
        File.WriteAllText(destination, "existing");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.Download(release, manifest.Files[0], destination, Token));
        Assert.Equal("existing", File.ReadAllText(destination));
    }

    [Fact]
    public async Task MetadataFailuresDoNotExposeResponseOrToken()
    {
        using RecordingHandler handler = new() { Status = HttpStatusCode.Unauthorized, Payload = Encoding.UTF8.GetBytes("private-body") };
        using GitHubDownloads client = new("secret-token", handler);
        MhxException exception = await Assert.ThrowsAsync<MhxException>(() => client.Latest(Token));
        Assert.DoesNotContain("private-body", exception.Message);
        Assert.DoesNotContain("secret-token", exception.Message);
    }

    [Fact]
    public void ManifestRejectsTraversalAndMismatchedVersions()
    {
        GitHubRelease release = new() { TagName = "v1.2.3" };
        ReleaseManifest manifest = new() { SchemaVersion = 1, Version = "1.2.3", Files = [new() { Name = "../mhx.exe", Sha256 = new string('a', 64), Size = 1 }] };
        Assert.Throws<MhxException>(() => ReleaseValidation.Manifest(manifest, release));
        manifest.Files[0].Name = "mhx.exe";
        manifest.Version = "1.2.4";
        Assert.Throws<MhxException>(() => ReleaseValidation.Manifest(manifest, release));
    }

    [Fact]
    public void NativeReplacementVerifiesBeforeChangingInstalledFile()
    {
        using TestFiles files = new();
        string target = files.Write("mhx.exe", "old");
        string stage = files.Write("stage", "new");
        Assert.Throws<MhxException>(() => AtomicInstall.Replace(stage, target, new string('a', 64)));
        Assert.Equal("old", File.ReadAllText(target));
        using (AtomicInstall.Lock(files.Directory)) AtomicInstall.Replace(stage, target, AtomicInstall.Hash(stage));
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(stage));
        Assert.False(File.Exists(Path.Combine(files.Directory, AtomicInstall.JournalName)));
    }

    [Fact]
    public void InterruptedReplacementRecoversOnlyVerifiedBackup()
    {
        using TestFiles files = new();
        string backup = files.Write(".mhx-backup-test.exe", "old");
        RecoveryRecord recovery = new() { Target = "mhx.exe", Backup = Path.GetFileName(backup), OldHash = AtomicInstall.Hash(backup), NewHash = Hash(Encoding.UTF8.GetBytes("new")) };
        files.Write(AtomicInstall.JournalName, JsonSerializer.Serialize(recovery, ReleaseJsonContext.Default.RecoveryRecord));
        AtomicInstall.Recover(files.Directory);
        Assert.Equal("old", File.ReadAllText(Path.Combine(files.Directory, "mhx.exe")));
        Assert.False(File.Exists(Path.Combine(files.Directory, AtomicInstall.JournalName)));
    }

    [Fact]
    public void RecoveryRejectsUntrustedPathsAndConcurrentUpdateLocks()
    {
        using TestFiles files = new();
        RecoveryRecord recovery = new() { Target = "../outside", Backup = ".mhx-backup-test", OldHash = new string('a', 64), NewHash = new string('b', 64) };
        files.Write(AtomicInstall.JournalName, JsonSerializer.Serialize(recovery, ReleaseJsonContext.Default.RecoveryRecord));
        Assert.Throws<MhxException>(() => AtomicInstall.Recover(files.Directory));
        using FileStream gate = AtomicInstall.Lock(files.Directory);
        Assert.Throws<MhxException>(() => AtomicInstall.Lock(files.Directory));
    }

    [Fact]
    public void DotnetToolDetectionRequiresInstallationOwnedApphost()
    {
        using TestFiles files = new();
        string bin = Path.Combine(files.Directory, "tools");
        string exe = Path.Combine(bin, OperatingSystem.IsWindows() ? "mhx.exe" : "mhx");
        Assert.Equal(bin, Installation.ToolDirectory(exe, Path.Combine(bin, ".store", "mhx", "0.2.0", "tools")));
        Assert.Null(Installation.ToolDirectory(exe, Path.Combine(files.Directory, "nuget", "mhx", "tools")));
    }

    [Fact(SkipUnless = nameof(UnixOnly), Skip = "Unix directory symlink identity")]
    public void PhysicalDirectoriesResolveAncestorSymlinks()
    {
        using TestFiles files = new();
        string real = Path.Combine(files.Directory, "real");
        string nested = Path.Combine(real, "nested");
        Directory.CreateDirectory(nested);
        string alias = Path.Combine(files.Directory, "alias");
        Directory.CreateSymbolicLink(alias, real);
        Assert.Equal(Installation.PhysicalDirectory(nested), Installation.PhysicalDirectory(Path.Combine(alias, "nested")));
    }

    [Fact]
    public void InstallationStateDetectsChangedExecutables()
    {
        using TestFiles files = new();
        string target = files.Write("mhx.exe", "version-one");
        InstalledVersion.Write(target, "1.2.3", AtomicInstall.Hash(target));
        Assert.Equal(new Version(1, 2, 3), InstalledVersion.Read(target, "0.0.0"));
        File.WriteAllText(target, "changed");
        Assert.Throws<MhxException>(() => InstalledVersion.Read(target, "0.0.0"));
    }

    [Fact]
    public void HelperRejectsForeignRequestWithoutDeletingAnything()
    {
        using TestFiles files = new();
        string package = files.Write("precious.nupkg", "keep");
        string request = files.Write("request.json", "{}");
        Assert.Throws<MhxException>(() => ToolUpdateHelper.Run(request));
        Assert.Equal("keep", File.ReadAllText(package));
        Assert.True(File.Exists(request));
    }

    [Fact(SkipUnless = nameof(WindowsOnly), Skip = "Windows running executable replacement")]
    public async Task NativeSwapWorksWhileOldExecutableIsRunning()
    {
        using TestFiles files = new();
        string directory = Path.Combine(files.Directory, "running");
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(TestFiles.Launcher)!))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        string target = Path.Combine(directory, "mhx.exe");
        string config = files.Profile(new { command = TestFiles.Probe });
        using Process process = files.Start(["--mhx-config", config], executable: target);
        try
        {
            string? ready = await process.StandardError.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.StartsWith("{", ready);
            string staged = Path.Combine(directory, "new-image");
            File.Copy(TestFiles.Launcher, staged);
            using (AtomicInstall.Lock(directory)) AtomicInstall.Replace(staged, target, AtomicInstall.Hash(staged));
            Assert.False(process.HasExited);
            var result = await files.Run(["--mhx-version"], executable: target);
            Assert.Equal(0, result.Exit);
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
        finally { if (!process.HasExited) process.Kill(true); process.WaitForExit(); }
    }

    private sealed class Fixture : IDisposable
    {
        public byte[] Payload { get; } = Encoding.UTF8.GetBytes("new-payload");
        public RecordingHandler Handler { get; }
        public GitHubDownloads Client { get; }
        public Fixture()
        {
            ReleaseManifest manifest = new() { SchemaVersion = 1, Version = "9.0.0", Files = [new() { Name = "mhx-win-x64.exe", Kind = "native-aot", Runtime = "win-x64", Sha256 = Hash(Payload), Size = Payload.Length }] };
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(manifest, ReleaseJsonContext.Default.ReleaseManifest);
            GitHubRelease release = new() { TagName = "v9.0.0", Assets = [new() { Id = 1, Name = AppInfo.ManifestName, Size = data.Length, Digest = "sha256:" + Hash(data) }, new() { Id = 2, Name = manifest.Files[0].Name, Size = Payload.Length }] };
            Handler = new() { Release = JsonSerializer.SerializeToUtf8Bytes(release, ReleaseJsonContext.Default.GitHubRelease), Manifest = data, Payload = Payload };
            Client = new("test-token", Handler);
        }
        public void Dispose() => Client.Dispose();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Uri, string? Authorization)> Requests { get; } = [];
        public byte[] Release { get; set; } = [];
        public byte[] Manifest { get; set; } = [];
        public byte[] Payload { get; set; } = [];
        public string? Redirect { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string uri = request.RequestUri!.AbsoluteUri;
            Requests.Add((uri, request.Headers.Authorization?.ToString()));
            if (Redirect is not null && uri.EndsWith("/assets/2", StringComparison.Ordinal))
            {
                HttpResponseMessage redirect = new(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(Redirect);
                return Task.FromResult(redirect);
            }
            byte[] bytes = uri.EndsWith("/latest", StringComparison.Ordinal) ? Release : uri.EndsWith("/assets/1", StringComparison.Ordinal) ? Manifest : Payload;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(bytes) });
        }
    }
}
