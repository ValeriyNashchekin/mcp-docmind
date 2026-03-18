using System.Text;
using System.Text.RegularExpressions;
using McpDocMind.Lite.Constants;
using McpDocMind.Lite.Models;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Clones a Git repository and parses markdown files.
/// </summary>
public sealed partial class GitParser
{
    private readonly MarkdownParser _mdParser = new();

    [GeneratedRegex(@"^\d+\.\d+", RegexOptions.Compiled)]
    private static partial Regex VersionRegex();

    public async Task<List<DocChunk>> ParseRepositoryAsync(string repoUrl, string libraryName,
        string? version, string? docsPath = null, string? branch = null, CancellationToken ct = default)
    {
        var (chunks, tempDir) = await CloneAndParseAsync(repoUrl, libraryName, version, docsPath, branch, ct);
        try
        {
            return chunks;
        }
        finally
        {
            await CleanupTempDir(tempDir, ct);
        }
    }

    /// <summary>
    /// Clones a repo and parses markdown. Returns chunks + temp directory path.
    /// Caller is responsible for cleanup via <see cref="CleanupTempDir"/>.
    /// </summary>
    public async Task<(List<DocChunk> Chunks, string TempDir)> CloneAndParseAsync(string repoUrl,
        string libraryName, string? version, string? docsPath = null, string? branch = null,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"docmind-{Guid.NewGuid():N}");

        var args = new List<string> { "clone", "--depth", "1", "--tags" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--branch");
            args.Add(branch);
        }
        args.Add(repoUrl);
        args.Add(tempDir);

        var (exitCode, _, error) = await RunGitAsync(args, ct);

        if (exitCode != 0)
        {
            await CleanupTempDir(tempDir, CancellationToken.None);
            throw new InvalidOperationException($"Git clone failed: {error}");
        }

        // Auto-detect version
        if (string.IsNullOrWhiteSpace(version))
            version = await DetectVersionAsync(tempDir, ct) ?? "latest";

        var parseDir = string.IsNullOrEmpty(docsPath) ? tempDir : Path.Combine(tempDir, docsPath);
        if (!Directory.Exists(parseDir))
        {
            await CleanupTempDir(tempDir, CancellationToken.None);
            throw new DirectoryNotFoundException($"Directory not found: {parseDir}");
        }

        var chunks = await ParseDirectoryFilteredAsync(parseDir, libraryName, version, tempDir, repoUrl, ct);
        return (chunks, tempDir);
    }

    private async Task<List<DocChunk>> ParseDirectoryFilteredAsync(string directory,
        string libraryName, string version, string rootDir, string sourceUrl, CancellationToken ct)
    {
        var chunks = new List<DocChunk>();
        var extensions = new[] { "*.md", "*.mdx", "*.html", "*.htm" };
        var files = extensions.SelectMany(ext => Directory.GetFiles(directory, ext, SearchOption.AllDirectories));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!ShouldIncludeFile(file, rootDir)) continue;

            try
            {
                var fileChunks = await _mdParser.ParseFileAsync(file, libraryName, version, ct);
                foreach (var chunk in fileChunks)
                    chunk.Source = $"git:{sourceUrl}:{Path.GetRelativePath(rootDir, file)}";

                chunks.AddRange(fileChunks);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Console.Error.WriteLine($"Warning: {file}: {ex.Message}"); }
        }

        return chunks;
    }

    private static bool ShouldIncludeFile(string filePath, string rootDir)
    {
        var fileName = Path.GetFileName(filePath);
        if (IngestionConstants.IgnoredFiles.Contains(fileName)) return false;
        if (fileName.EndsWith(".expect.md") || fileName.EndsWith(".test.md") ||
            fileName.EndsWith(".spec.md") || fileName.EndsWith(".example.md")) return false;

        var parts = Path.GetRelativePath(rootDir, filePath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(p => IngestionConstants.IgnoredDirs.Contains(p)
                            || IngestionConstants.LocaleCodes.Contains(p));
    }

    private async Task<string?> DetectVersionAsync(string repoDir, CancellationToken ct)
    {
        try
        {
            var args = new List<string> { "-C", repoDir, "tag", "-l", "--sort=-v:refname" };
            var (exitCode, output, _) = await RunGitAsync(args, ct);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;

            foreach (var tag in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cleaned = tag.Trim().TrimStart('v', 'V');
                if (Version.TryParse(cleaned, out _) || VersionRegex().IsMatch(cleaned))
                    return cleaned;
            }
        }
        catch { /* version detection is best-effort */ }
        return null;
    }

    private static string? _resolvedGitPath;

    private static async Task<(int ExitCode, string Output, string Error)> RunGitAsync(
        IEnumerable<string> arguments, CancellationToken ct)
    {
        _resolvedGitPath ??= ResolveGitPath();
        if (_resolvedGitPath == null)
            return (-1, "", "Git not found in PATH or standard locations.");

        var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
        var cmdLog = $"[GitRun] {_resolvedGitPath} {string.Join(" ", arguments)}";
        try { File.AppendAllText(logPath, $"{DateTime.Now:T} {cmdLog}\n"); } catch { }

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new()
            {
                FileName = _resolvedGitPath,
                UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        // Best Practices: Disable all terminal prompts for Git to prevent hangs
        process.StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.EnvironmentVariables["GIT_ASKPASS"] = "true";
        process.StartInfo.EnvironmentVariables["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";

        // Ensure PATH is available to git even if host environment is restricted
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!currentPath.Contains(@"C:\Program Files\Git\cmd"))
        {
            process.StartInfo.EnvironmentVariables["PATH"] = currentPath +
                @";C:\Program Files\Git\cmd;C:\Program Files\Git\mingw64\bin;C:\WINDOWS\system32;C:\WINDOWS;C:\WINDOWS\System32\Wbem";
        }

        var output = new StringBuilder();
        var error = new StringBuilder();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
        process.Exited += (_, _) => tcs.TrySetResult(true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var isClone = arguments.Contains("clone");
        var timeout = isClone ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30);

        try
        {
            // Use a linked token with timeout for the Task.WhenAny
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed == tcs.Task)
            {
                // Give a small grace period for output to be drained
                await Task.WhenAny(process.WaitForExitAsync(ct), Task.Delay(500, ct));
                return (process.ExitCode, output.ToString(), error.ToString());
            }

            // Timeout or Cancellation happened
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            return (-1, "", $"Git command timed out after {timeout.TotalSeconds}s");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            return (-1, "", $"Process error: {ex.Message}");
        }
    }

    public static string? ConfiguredGitPath { get; set; }

    internal static string? ResolveGitPath()
    {
        if (_resolvedGitPath != null) return _resolvedGitPath;

        var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
        void Log(string msg) {
            try { File.AppendAllText(logPath, $"[GitDiscovery] {msg}\n"); } catch { }
            Console.Error.WriteLine($"[DocMind] {msg}");
        }

        Log("--- Git Discovery Diagnostic ---");

        // 1. Try explicitly configured path first
        if (!string.IsNullOrEmpty(ConfiguredGitPath))
        {
            Log($"Step 1: Checking configured path: {ConfiguredGitPath}");
            if (File.Exists(ConfiguredGitPath))
            {
                Log("Found Git via --git-path argument.");
                return _resolvedGitPath = ConfiguredGitPath;
            }
            Log("Configured path does not exist.");
        }

        // 2. Try GIT_PATH environment variable
        var envPath = Environment.GetEnvironmentVariable("GIT_PATH");
        if (!string.IsNullOrEmpty(envPath))
        {
            Log($"Step 2: Checking GIT_PATH env var: {envPath}");
            if (File.Exists(envPath))
            {
                Log("Found Git via GIT_PATH environment variable.");
                return _resolvedGitPath = envPath;
            }
            Log("GIT_PATH does not point to a valid file.");
        }

        // 3. Try common Windows install locations
        Log("Step 3: Searching standard Windows locations...");
        foreach (var candidate in GetGitCandidates())
        {
            if (candidate == "git") continue; // skip "git" for now, check absolute paths
            
            if (File.Exists(candidate))
            {
                Log($"Found Git at: {candidate}");
                return _resolvedGitPath = candidate;
            }
        }

        // 4. Try "git" in PATH as last resort
        Log("Step 4: Trying 'git' command in system PATH...");
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git", Arguments = "--version",
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            });
            if (p?.WaitForExit(TimeSpan.FromSeconds(2)) == true && p.ExitCode == 0)
            {
                Log("Found 'git' in PATH.");
                return _resolvedGitPath = "git";
            }
        }
        catch (Exception ex)
        {
            Log($"'git' command failed: {ex.Message}");
        }

        Log("ERROR: Git not found after all attempts.");
        Log("---------------------------------");
        return null;
    }

    public static bool IsGitAvailable() => ResolveGitPath() != null;

    private static IEnumerable<string> GetGitCandidates()
    {
        yield return "git";
        // Common Windows install locations
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\Git\cmd\git.exe";
            yield return @"C:\Program Files (x86)\Git\cmd\git.exe";
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");
        }
    }

    internal static async Task CleanupTempDir(string dir, CancellationToken ct)
    {
        if (!Directory.Exists(dir)) return;
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                // Use CancellationToken.None to ensure cleanup delay is NOT skipped if parent task is canceled
                if (retry > 0) await Task.Delay(500, CancellationToken.None);
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
                return;
            }
            catch { if (retry == 2) Console.Error.WriteLine($"Warning: Failed to cleanup {dir}"); }
        }
    }
}

