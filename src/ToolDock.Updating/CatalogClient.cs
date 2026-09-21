using System.Text.Json;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class CatalogClient(HttpClient http)
{
    public async Task<ToolCatalog> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Catalog URL must be an absolute HTTPS URL.");
        }

        await using var stream = await http.GetStreamAsync(uri, cancellationToken);
        var catalog = await JsonSerializer.DeserializeAsync<ToolCatalog>(
            stream,
            JsonFiles.Options,
            cancellationToken) ?? throw new InvalidDataException("Catalog is empty.");
        Validation.ValidateCatalog(catalog);
        return catalog;
    }
}
