namespace ToolDock.Common;

public sealed record InstalledSnapshot(InstalledState State, ToolCatalog Catalog)
{
    public static async Task<InstalledSnapshot> ReadAsync(ToolDockPaths paths, CancellationToken cancellationToken = default)
    {
        var state = await JsonFiles.ReadOptionalAsync<InstalledState>(paths.InstalledStateFile, cancellationToken)
            ?? new InstalledState();
        var catalog = state.ActiveCatalog
            ?? await JsonFiles.ReadOptionalAsync<ToolCatalog>(paths.CatalogCacheFile, cancellationToken)
            ?? new ToolCatalog();
        Validation.ValidateCatalog(catalog);
        return new InstalledSnapshot(state, catalog);
    }

    public string PackageRoot(ToolDockPaths paths, string name)
    {
        var installed = Validation.FindInstalled(State, name);
        return paths.ResolveUnderRoot(installed.Root ?? Path.Combine("tools", name, installed.Version));
    }
}
