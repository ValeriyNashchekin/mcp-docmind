using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace McpDocMind.Setup;

/// <summary>
/// Downloads NuGet packages / Git repos, extracts DLLs+XML, calls McpDocMind.Lite for ingestion.
/// Downloads in parallel, ingests sequentially (SQLite is single-writer).
/// Passes --nuget-version for version-aware skip/upgrade.
/// </summary>
public sealed class LibraryInstaller(string liteExePath, string installDir)
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly TimeSpan IngestionTimeout = TimeSpan.FromMinutes(5);
    private readonly string _cacheDir = Path.Combine(installDir, "cache");
    private readonly SemaphoreSlim _ingestLock = new(1, 1); // serialize DB writes

    // Cache resolved versions to avoid duplicate NuGet API calls
    private readonly Dictionary<string, string> _versionCache = new(StringComparer.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("McpDocMind-Setup/1.0");
        return client;
    }

    // ─── Public API ───

    /// <summary>Semaphore for callers who want to parallelize downloads but serialize ingestion.</summary>
    public SemaphoreSlim IngestLock => _ingestLock;

    public async Task InstallAsync(LibraryEntry lib, Action<double> progress)
    {
        if (lib.Type == LibraryType.NuGetDll)
            await InstallNuGetAsync(lib, progress);
        else if (lib.Type == LibraryType.GitRepo)
            await InstallGitRepoAsync(lib, progress);
    }

    /// <summary>Returns list of installed library identifiers (LibraryName:ApiVersion).</summary>
    public HashSet<string> GetInstalledLibraries()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = liteExePath,
                Arguments = "--ingest list",
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return result;

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (process.ExitCode != 0) return result;

            using var doc = JsonDocument.Parse(stdout);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("LibraryName").GetString();
                var ver = item.GetProperty("ApiVersion").GetString();
                if (name is not null && ver is not null)
                    result.Add($"{name}:{ver}");
            }
        }
        catch { }

        return result;
    }

    public long GetCacheSize()
    {
        if (!Directory.Exists(_cacheDir)) return 0;
        return new DirectoryInfo(_cacheDir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    public void ClearCache()
    {
        if (Directory.Exists(_cacheDir))
            Directory.Delete(_cacheDir, recursive: true);
    }

    // ─── Version Resolution ───

    public async Task<string> ResolveVersionAsync(string packageId, string versionPrefix)
    {
        var cacheKey = $"{packageId}:{versionPrefix}";
        if (_versionCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var id = packageId.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{id}/index.json";

        var response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var versions = doc.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .Select(v => v.GetString()!)
            .ToList();

        string resolved;

        if (versionPrefix == "latest")
        {
            resolved = versions.Where(v => !v.Contains('-')).LastOrDefault()
                ?? versions.Last();
        }
        else
        {
            var prefix = versionPrefix + ".";
            resolved = versions.Where(v => v.StartsWith(prefix) && !v.Contains('-')).LastOrDefault()
                ?? versions.Where(v => v.StartsWith(prefix)).LastOrDefault()
                ?? throw new InvalidOperationException(
                    $"No version for {packageId} with prefix '{versionPrefix}'. Available: {string.Join(", ", versions.TakeLast(5))}");
        }

        _versionCache[cacheKey] = resolved;
        return resolved;
    }

    // ─── NuGet ───

    private async Task InstallNuGetAsync(LibraryEntry lib, Action<double> progress)
    {
        Directory.CreateDirectory(_cacheDir);

        // Step 1: Resolve version (10%)
        progress(2);
        var version = await ResolveVersionAsync(lib.NuGetId, lib.VersionPrefix);
        lib.ResolvedVersion = version;

        var packageDir = Path.Combine(_cacheDir, $"{lib.NuGetId}.{version}");
        var nupkgPath = Path.Combine(_cacheDir, $"{lib.NuGetId}.{version}.nupkg");

        // Step 2: Download — can run in parallel (10-30%)
        progress(10);
        if (!File.Exists(nupkgPath))
        {
            var id = lib.NuGetId.ToLowerInvariant();
            var ver = version.ToLowerInvariant();
            await DownloadWithRetryAsync(
                $"https://api.nuget.org/v3-flatcontainer/{id}/{ver}/{id}.{ver}.nupkg",
                nupkgPath);
        }
        progress(30);

        // Step 3: Extract (30-50%)
        if (!Directory.Exists(packageDir))
            ZipFile.ExtractToDirectory(nupkgPath, packageDir, overwriteFiles: true);
        progress(50);

        // Step 4: Ingest — MUST be sequential (serialize via lock)
        var (dllPath, xmlPath) = FindDllAndXml(packageDir, lib.DllName);
        if (dllPath is not null)
        {
            var libName = lib.LibraryName ?? lib.NuGetId;
            var apiVer = lib.ApiVersion ?? version;

            await _ingestLock.WaitAsync();
            try
            {
                await RunIngestAsync("dll", dllPath, libName, apiVer, xmlPath, version);
            }
            finally
            {
                _ingestLock.Release();
            }
        }
        progress(100);
    }

    // ─── Git Repo ───

    private async Task InstallGitRepoAsync(LibraryEntry lib, Action<double> progress)
    {
        if (lib.RepoUrl is null) return;
        progress(10);

        var repoName = lib.LibraryName ?? "docs";
        var zipDir = Path.Combine(_cacheDir, repoName);
        var zipPath = Path.Combine(_cacheDir, $"{repoName}.zip");

        if (!Directory.Exists(zipDir))
        {
            Directory.CreateDirectory(_cacheDir);
            var baseUrl = lib.RepoUrl.Replace(".git", "");

            var downloaded = false;
            foreach (var branch in new[] { "main", "master" })
            {
                try
                {
                    await DownloadWithRetryAsync($"{baseUrl}/archive/refs/heads/{branch}.zip", zipPath);
                    downloaded = true;
                    break;
                }
                catch { }
            }
            if (!downloaded)
                throw new InvalidOperationException($"Failed to download {lib.RepoUrl} (tried main/master)");

            progress(50);
            ZipFile.ExtractToDirectory(zipPath, zipDir, overwriteFiles: true);
        }
        progress(60);

        var mdFiles = Directory.GetFiles(zipDir, "*.md", SearchOption.AllDirectories);
        if (mdFiles.Length > 0)
        {
            var extractedDirs = Directory.GetDirectories(zipDir);
            var docsDir = extractedDirs.Length > 0 ? extractedDirs[0] : zipDir;

            await _ingestLock.WaitAsync();
            try
            {
                await RunIngestAsync("md", docsDir, lib.LibraryName ?? repoName, lib.ApiVersion ?? "1.0.0");
            }
            finally
            {
                _ingestLock.Release();
            }
        }
        progress(100);
    }

    // ─── Helpers ───

    private static async Task DownloadWithRetryAsync(string url, string destPath, int maxRetries = 3)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(destPath);
                await response.Content.CopyToAsync(fs);
                return;
            }
            catch
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                if (attempt >= maxRetries) throw;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            }
        }
    }

    private static (string? DllPath, string? XmlPath) FindDllAndXml(string packageDir, string? preferredDll)
    {
        string[] tfmPriority = ["net10.0", "net9.0", "net8.0", "net48", "net472", "net461", "netstandard2.1", "netstandard2.0"];

        foreach (var folder in new[] { "lib", "ref" })
        {
            var libDir = Path.Combine(packageDir, folder);
            if (!Directory.Exists(libDir)) continue;

            foreach (var tfm in tfmPriority)
            {
                var tfmDir = Path.Combine(libDir, tfm);
                if (!Directory.Exists(tfmDir)) continue;

                var dlls = Directory.GetFiles(tfmDir, "*.dll");
                if (dlls.Length == 0) continue;

                var dll = preferredDll is not null
                    ? dlls.FirstOrDefault(d => Path.GetFileNameWithoutExtension(d)
                        .Equals(preferredDll, StringComparison.OrdinalIgnoreCase)) ?? dlls[0]
                    : dlls[0];

                var xml = Path.ChangeExtension(dll, ".xml");
                return (dll, File.Exists(xml) ? xml : null);
            }

            var anyDll = Directory.GetFiles(libDir, "*.dll", SearchOption.AllDirectories).FirstOrDefault();
            if (anyDll is not null)
            {
                var anyXml = Path.ChangeExtension(anyDll, ".xml");
                return (anyDll, File.Exists(anyXml) ? anyXml : null);
            }
        }

        return (null, null);
    }

    private async Task RunIngestAsync(string mode, string path, string libName, string apiVersion,
        string? xmlPath = null, string? nugetVersion = null)
    {
        var args = mode == "dll" && xmlPath is not null
            ? $"--ingest dll \"{path}\" \"{libName}\" \"{apiVersion}\" --xml \"{xmlPath}\""
            : $"--ingest {mode} \"{path}\" \"{libName}\" \"{apiVersion}\"";

        // Pass NuGet version for version-aware skip/upgrade
        if (nugetVersion is not null)
            args += $" --nuget-version \"{nugetVersion}\"";

        var psi = new ProcessStartInfo
        {
            FileName = liteExePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ingestion process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var exited = await Task.Run(() => process.WaitForExit((int)IngestionTimeout.TotalMilliseconds));
        if (!exited)
        {
            process.Kill();
            throw new TimeoutException($"Ingestion timed out after {IngestionTimeout.TotalMinutes}min");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Ingestion failed: {stderr}");

        // Check if it was a skip (version already ingested)
        if (stdout.StartsWith("SKIP:"))
            throw new SkipException(stdout.Trim());
    }
}

/// <summary>Thrown when ingestion is skipped because same version already exists.</summary>
public class SkipException(string message) : Exception(message);
