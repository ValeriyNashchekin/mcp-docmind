namespace McpDocMind.Setup;

public enum LibraryType { NuGetDll, GitRepo, Separator }

public record LibraryEntry(
    string DisplayName,
    string NuGetId,
    string VersionPrefix,  // Year prefix like "2026" or "latest" for auto-resolve
    string Category,
    LibraryType Type,
    string? DllName = null,
    string? RepoUrl = null,
    string? DocsPath = null,
    string? LibraryName = null,
    string? ApiVersion = null)
{
    /// <summary>Resolved exact version, filled at runtime by NuGet API lookup.</summary>
    public string? ResolvedVersion { get; set; }
}

/// <summary>
/// Pre-configured catalog of installable libraries.
/// Version is resolved at runtime via NuGet API — no hardcoded sub-versions.
/// </summary>
public static class LibraryCatalog
{
    public static List<LibraryEntry> GetAll() =>
    [
        // --- Revit API (version prefix = year, resolved to latest 2026.x.x) ---
        Revit("RevitAPI", "2026"), Revit("RevitAPIUI", "2026"),
        Revit("RevitAPI", "2025"), Revit("RevitAPIUI", "2025"),
        Revit("RevitAPI", "2024"), Revit("RevitAPIUI", "2024"),
        Revit("RevitAPI", "2023"), Revit("RevitAPIUI", "2023"),
        Revit("RevitAPI", "2022"), Revit("RevitAPIUI", "2022"),
        Revit("RevitAPI", "2021"), Revit("RevitAPIUI", "2021"),

        // --- AutoCAD (latest stable version) ---
        Nuget("AutoCAD .NET 2026", "AutoCAD.NET", "latest", "AutoCAD", "AutoCAD.NET", "2026"),
        Nuget("AutoCAD .NET Core 2026", "AutoCAD.NET.Core", "latest", "AutoCAD", "AutoCAD.NET.Core", "2026"),
        Nuget("AutoCAD .NET Model 2026", "AutoCAD.NET.Model", "latest", "AutoCAD", "AutoCAD.NET.Model", "2026"),

        // --- APS SDK (latest stable) ---
        Nuget("APS Authentication", "Autodesk.Authentication", "latest", "APS", "APS.Authentication"),    
        Nuget("APS Model Derivative", "Autodesk.ModelDerivative", "latest", "APS", "APS.ModelDerivative"),
        Nuget("APS Data Management", "Autodesk.DataManagement", "latest", "APS", "APS.DataManagement"),   
        Nuget("APS OSS (Storage)", "Autodesk.Oss", "latest", "APS", "APS.Oss"),
        Nuget("APS Design Automation", "Autodesk.Forge.DesignAutomation", "latest", "APS", "APS.DesignAutomation"),

        // --- Other ---
        Nuget("NetTopologySuite", "NetTopologySuite", "latest", "Other", "NetTopologySuite"),
        Nuget("RevitToolkit", "Nice3point.Revit.Toolkit", "latest", "Other", "RevitToolkit"),
        Nuget("RevitExtensions", "Nice3point.Revit.Extensions", "latest", "Other", "RevitExtensions"),
    ];

    private static LibraryEntry Revit(string dllName, string year) => new(
        $"{dllName} {year}",
        $"Nice3point.Revit.Api.{dllName}",
        year,            // version prefix = "2026", resolved to latest 2026.x.x
        "Revit",
        LibraryType.NuGetDll,
        DllName: dllName,
        LibraryName: dllName,  // \"RevitAPI\" or \"RevitAPIUI\"
        ApiVersion: year);

    private static LibraryEntry Nuget(string display, string nugetId, string versionPrefix,
        string category, string libName, string? apiVersion = null) => new(
        display,
        nugetId,
        versionPrefix,   // \"latest\" = resolve to latest stable
        category,
        LibraryType.NuGetDll,
        LibraryName: libName,
        ApiVersion: apiVersion);
}
