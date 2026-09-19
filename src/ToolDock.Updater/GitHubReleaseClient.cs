using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace ToolDock.Updater;

internal sealed class GitHubReleaseClient
{
    private readonly HttpClient _http;

    public GitHubReleaseClient(HttpClient http)
    {
        _http = http;
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ToolDock", "1.0"));
        }
    }

    public async Task<ReleaseAsset> GetLatestAsync(
        string repository,
        string assetName,
        CancellationToken cancellationToken)
    {
        var parts = repository.Split('/');
        var endpoint = new Uri(
            $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/latest");
        var release = await _http.GetFromJsonAsync<GitHubRelease>(endpoint, cancellationToken)
            ?? throw new InvalidDataException($"GitHub returned an empty release for {repository}.");
        var asset = release.Assets.SingleOrDefault(item => string.Equals(item.Name, assetName, StringComparison.Ordinal));
        if (asset is null)
        {
            throw new InvalidDataException($"Release {release.TagName} of {repository} has no asset named {assetName}.");
        }

        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"GitHub returned an invalid download URL for {repository}/{assetName}.");
        }

        return new ReleaseAsset(release.TagName, downloadUri);
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public required string TagName { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public required string DownloadUrl { get; init; }
    }
}

internal sealed record ReleaseAsset(string Version, Uri DownloadUri);
