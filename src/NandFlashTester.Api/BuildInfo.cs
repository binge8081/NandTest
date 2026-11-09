using System.Text.Json;
using NandFlashTester.Core;
using NandFlashTester.Core.Scope;

namespace NandFlashTester.Api.Platform;

public static class BuildInfo
{
    public static object Create(string contentRoot, string? webRoot, string runtimeMode)
    {
        var api = Read(Path.Combine(contentRoot, "build-info.json"));
        var ui = string.IsNullOrWhiteSpace(webRoot) ? null : ReadOptional(Path.Combine(webRoot, "build-info.json"));
        var matches = ui is not null
            && ui.Commit == api.Commit
            && ui.Version == api.Version
            && ui.BuildTimeUtc == api.BuildTimeUtc
            && ui.PackagedMode == api.PackagedMode;
        return new
        {
            product = api.Product,
            version = api.Version,
            commit = api.Commit,
            buildTimeUtc = api.BuildTimeUtc,
            packagedMode = api.PackagedMode,
            runtimeMode,
            entry = api.Entry,
            uiCommit = ui?.Commit,
            uiVersion = ui?.Version,
            uiBuildTimeUtc = ui?.BuildTimeUtc,
            uiMatchesApi = matches,
            hardwareAccepted = false,
            windowsExecutionVerified = PhaseScope.WindowsExecutionVerified,
            fallbackToFixture = false,
            source = "build-info.json"
        };
    }

    private static Stamp Read(string path) => ReadOptional(path) ?? Missing();

    private static Stamp? ReadOptional(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new Stamp(
            root.GetProperty("product").GetString() ?? ProductIdentity.Name,
            root.GetProperty("version").GetString() ?? ProductIdentity.Version,
            root.GetProperty("commit").GetString() ?? "unknown",
            root.GetProperty("buildTimeUtc").GetString() ?? "",
            root.GetProperty("packagedMode").GetString() ?? "Production",
            root.GetProperty("entry").GetString() ?? "http://127.0.0.1:5080/");
    }

    private static Stamp Missing() => new(ProductIdentity.Name, ProductIdentity.Version, "unknown", "", "Production", "http://127.0.0.1:5080/");

    private sealed record Stamp(string Product, string Version, string Commit, string BuildTimeUtc, string PackagedMode, string Entry);
}
