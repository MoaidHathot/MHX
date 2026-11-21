using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MHX.Distribution;

internal sealed class GitHubDownloads : IDisposable
{
    internal const string Api = "https://api.github.com/repos/" + AppInfo.Repository;
    private readonly HttpClient client;
    private readonly string? token;
    private static readonly ReleaseJsonContext Json = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowDuplicateProperties = false,
        MaxDepth = 16,
    });

    public GitHubDownloads(string? token, HttpMessageHandler? handler = null)
    {
        this.token = token;
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<(GitHubRelease Release, ReleaseManifest Manifest)> Latest(CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadSmall(new Uri(Api + "/releases/latest"), "application/vnd.github+json", cancellationToken);
        GitHubRelease release;
        try { release = JsonSerializer.Deserialize(bytes, Json.GitHubRelease) ?? throw new JsonException(); }
        catch (JsonException) { throw new MhxException("Invalid GitHub release response."); }
        GitHubAsset manifestAsset = Asset(release, AppInfo.ManifestName);
        if (manifestAsset.Size > 1024 * 1024) throw new MhxException("Release manifest is too large.");
        byte[] manifestBytes = await ReadSmall(AssetUri(manifestAsset), "application/octet-stream", cancellationToken);
        if (manifestBytes.LongLength != manifestAsset.Size) throw new MhxException("Release manifest download is incomplete.");
        if (manifestAsset.Digest is { } digest &&
            (!digest.StartsWith("sha256:", StringComparison.Ordinal) ||
             !Convert.ToHexStringLower(SHA256.HashData(manifestBytes)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)))
            throw new MhxException("Release manifest digest does not match GitHub metadata.");
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize(manifestBytes, Json.ReleaseManifest) ?? throw new JsonException(); }
        catch (JsonException) { throw new MhxException("Invalid release manifest."); }
        ReleaseValidation.Manifest(manifest, release);
        return (release, manifest);
    }

    public async Task Download(GitHubRelease release, ReleaseFile file, string destination, CancellationToken cancellationToken)
    {
        GitHubAsset asset = Asset(release, file.Name);
        if (asset.Size != file.Size || !ReleaseValidation.ValidHash(file.Sha256) || file.Size > ReleaseValidation.MaximumDownload)
            throw new MhxException("Release asset metadata does not match the manifest.");
        bool created = false;
        try
        {
            using HttpResponseMessage response = await Get(AssetUri(asset), "application/octet-stream", cancellationToken);
            if (response.Content.Headers.ContentLength is long length && length != file.Size)
                throw new MhxException("Release asset size does not match the manifest.");
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                total += count;
                if (total > file.Size) throw new MhxException("Release download exceeded its expected size.");
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            if (total != file.Size || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MhxException("Release download failed SHA-256 verification.");
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            if (created) File.Delete(destination);
            throw;
        }
    }

    private async Task<byte[]> ReadSmall(Uri uri, string accept, CancellationToken token)
    {
        using HttpResponseMessage response = await Get(uri, accept, token);
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > 1024 * 1024) throw new MhxException("Release metadata is too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private async Task<HttpResponseMessage> Get(Uri initial, string accept, CancellationToken cancellationToken)
    {
        Uri uri = initial;
        for (int redirects = 0; redirects < 6; redirects++)
        {
            if (!Allowed(uri)) throw new MhxException("Release download redirected outside the allowed HTTPS hosts.");
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("MHX/" + AppInfo.Version);
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            // Never forward API credentials to an asset CDN or another host.
            if (!string.IsNullOrWhiteSpace(token) && uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? next = response.Headers.Location;
                response.Dispose();
                if (next is null) throw new MhxException("Release download returned an invalid redirect.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                response.Dispose();
                throw new MhxException(status is 401 or 403 or 404
                    ? "Release is unavailable. For private releases, set GH_TOKEN/GITHUB_TOKEN or sign in with gh; also check release availability and rate limits."
                    : $"Release request failed (HTTP {status}).");
            }
            return response;
        }
        throw new MhxException("Release download exceeded the redirect limit.");
    }

    internal static bool Allowed(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        uri.Host.ToLowerInvariant() is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    private static GitHubAsset Asset(GitHubRelease release, string name)
    {
        if (release.Assets is null || release.Assets.Length > 64) throw new MhxException("Invalid release asset list.");
        GitHubAsset[] matches = release.Assets.Where(a => a is not null && a.Name == name && a.Id > 0).ToArray();
        return matches.Length == 1 ? matches[0] : throw new MhxException("The required release asset is missing or ambiguous.");
    }
    private static Uri AssetUri(GitHubAsset asset) => new(Api + "/releases/assets/" + asset.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void Dispose() => client.Dispose();
}
