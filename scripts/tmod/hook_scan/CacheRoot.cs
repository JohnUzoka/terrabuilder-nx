// Build cache root: TERRARIA_BUILD_CACHE, else ~/.cache/terraria-switch-build.
static class CacheRoot
{
    public static readonly string Path = System.Environment.GetEnvironmentVariable("TERRARIA_BUILD_CACHE")
        ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache/terraria-switch-build");
}
