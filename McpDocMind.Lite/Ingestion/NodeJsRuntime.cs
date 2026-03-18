using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Manages Node.js runtime: resolves system install or downloads embedded binary.
/// Extracts bundled scripts from embedded resources.
/// </summary>
public sealed class NodeJsRuntime
{
    private const string NodeVersion = "22.15.0";
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "McpDocMind");

    private string? _resolvedNodePath;
    private string? _resolvedScriptPath;

    public static string? ConfiguredNodePath { get; set; }

    /// <summary>
    /// Ensures Node.js is available and returns path to the executable.
    /// Resolution order: explicit config → system PATH → common Windows locations.
    /// </summary>
    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        _resolvedNodePath ??= ResolveNodePath();
        if (_resolvedNodePath is null)
        {
            throw new FileNotFoundException(
                "Node.js not found. Please install Node.js (v18+) and ensure 'node' is in PATH, " +
                "or specify path via --node-path argument.");
        }

        // Extract bundled script
        _resolvedScriptPath = ExtractBundledScript();
        await Task.CompletedTask;
    }

    internal static string? ResolveNodePath()
    {
        var logPath = Path.Combine(AppContext.BaseDirectory, "debug.log");
        void Log(string msg) {
            try { File.AppendAllText(logPath, $"[NodeDiscovery] {msg}\n"); } catch { }
            Console.Error.WriteLine($"[DocMind] {msg}");
        }

        // 1. Explicitly configured path
        if (!string.IsNullOrEmpty(ConfiguredNodePath))
        {
            if (File.Exists(ConfiguredNodePath)) return ConfiguredNodePath;
            Log($"Configured node-path does not exist: {ConfiguredNodePath}");
        }

        // 2. Try 'node' in PATH
        try
        {
            var nodeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = nodeName, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                }
            };
            process.Start();
            if (process.WaitForExit(TimeSpan.FromSeconds(2)) && process.ExitCode == 0)
                return nodeName;
        }
        catch { /* not in path */ }

        // 3. Common Windows locations
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var candidates = new[]
            {
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Program Files (x86)\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"nvs\node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".nvm\versions\node\v22.15.0\bin\node.exe")
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs the extraction script with given arguments.
    /// Returns (stdout, stderr, exitCode).
    /// </summary>
    public async Task<(string Stdout, string Stderr, int ExitCode)> RunScriptAsync(
        string arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (_resolvedNodePath is null || _resolvedScriptPath is null)
            throw new InvalidOperationException("Call EnsureInstalledAsync() first.");

        timeout ??= TimeSpan.FromMinutes(5);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _resolvedNodePath,
                Arguments = $"\"{_resolvedScriptPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Exited += (_, _) => tcs.TrySetResult(true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Wait for exit, respecting both timeout and cancellation
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout.Value);

        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
            if (completed != tcs.Task)
            {
                process.Kill(entireProcessTree: true);
                return ("", $"Process timed out after {timeout.Value.TotalSeconds}s", -1);
            }
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        await process.WaitForExitAsync(ct);
        return (stdout.ToString().Trim(), stderr.ToString().Trim(), process.ExitCode);
    }

    private static (string os, string arch, string ext) GetPlatformInfo()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.OSArchitecture}")
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ("win", arch, "zip");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ("linux", arch, "tar.gz");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ("darwin", arch, "tar.gz");

        throw new PlatformNotSupportedException("Unsupported OS");
    }

    /// <summary>
    /// Extracts the bundled extraction script from embedded resources.
    /// Uses hash-based versioning to detect changes.
    /// </summary>
    private static string ExtractBundledScript()
    {
        var scriptsDir = Path.Combine(AppDataDir, "scripts");
        Directory.CreateDirectory(scriptsDir);

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("extract-ts-api.bundle.js"));

        if (resourceName is null)
        {
            // Fallback: look for the script file next to the executable
            var exeDir = AppContext.BaseDirectory;
            var localScript = Path.Combine(exeDir, "Resources", "extract-ts-api.bundle.js");
            if (File.Exists(localScript)) return localScript;

            // Or in Scripts directory (development mode)
            var devScript = Path.Combine(exeDir, "..", "..", "..", "..", "Scripts", "extract-ts-api.js");
            if (File.Exists(devScript)) return Path.GetFullPath(devScript);

            throw new FileNotFoundException(
                "Extraction script not found. Run 'npm run build' in McpDocMind.Lite/Scripts/ first.");
        }

        // Read resource and compute hash
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

        var targetPath = Path.Combine(scriptsDir, $"extract-ts-api.{hash}.js");
        if (!File.Exists(targetPath))
        {
            // Clean old versions
            foreach (var old in Directory.GetFiles(scriptsDir, "extract-ts-api.*.js"))
                try { File.Delete(old); } catch { /* best effort */ }

            File.WriteAllBytes(targetPath, bytes);
        }

        return targetPath;
    }
}
