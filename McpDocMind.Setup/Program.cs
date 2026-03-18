using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Spectre.Console;
using McpDocMind.Setup;

// Global settings
const string ExeName = "McpDocMind.Lite.exe";
string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McpDocMind");

try
{
    if (args.Contains("--clear-cache"))
    {
        ClearCache();
        return;
    }

    if (args.Contains("--status"))
    {
        ShowStatus();
        return;
    }

    if (args.Contains("--uninstall"))
    {
        Uninstall();
        return;
    }

    AnsiConsole.Write(new FigletText("McpDocMind").Color(Color.Cyan1));
    AnsiConsole.MarkupLine("[grey]v1.0.0 - Interactive Installer[/]\n");

    var mode = AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("What would you like to do?")
            .AddChoices("Full Install / Update", "Install Libraries", "Show Status", "Uninstall", "Exit"));

    switch (mode)
    {
        case "Full Install / Update":
            await FullInstallAsync();
            await InstallLibrariesAsync();
            Done();
            break;
        case "Install Libraries":
            await InstallLibrariesAsync();
            Done();
            break;
        case "Show Status":
            ShowStatus();
            break;
        case "Uninstall":
            if (AnsiConsole.Confirm("Are you sure you want to uninstall?"))
                Uninstall();
            break;
    }
}
catch (Exception ex)
{
    AnsiConsole.WriteException(ex);
}
finally
{
    AnsiConsole.MarkupLine("\n[grey]Press any key to exit...[/]");
    Console.ReadKey(true);
}

async Task FullInstallAsync()
{
    AnsiConsole.MarkupLine("[bold]Phase 1: Files[/]");

    if (!Directory.Exists(InstallDir))
        Directory.CreateDirectory(InstallDir);

    var assembly = typeof(Program).Assembly;
    var resourceName = "McpDocMind.Lite.zip";

    using var stream = assembly.GetManifestResourceStream(resourceName);
    if (stream is null)
    {
        AnsiConsole.MarkupLine($"  [red]✘[/] Embedded resource '{resourceName}' not found in assembly.");
        return;
    }

    await AnsiConsole.Progress()
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("[grey]Extracting files...[/]");
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var count = 0;
            foreach (var entry in entries)
            {
                var target = Path.Combine(InstallDir, entry.FullName);
                var dir = Path.GetDirectoryName(target);
                if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(target, overwrite: true);
                count++;
                task.Value = (double)count / entries.Count * 100;
            }
        });

    var fileCount = Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories).Length;
    AnsiConsole.MarkupLine($"  [green]✔[/] Extracted {fileCount} files to {InstallDir}");

    // Phase 2: Configuration
    AnsiConsole.MarkupLine("\n[bold]Phase 2: Tool Registration[/]");

    var exePath = Path.Combine(InstallDir, ExeName);
    var patcher = new ConfigPatcher(exePath);

    foreach (var (name, path, format) in GetConfigTargets())
    {
        var result = patcher.Patch(path, format);
        var icon = result switch
        {
            PatchResult.Created or PatchResult.Added or PatchResult.Updated => "[green]✔[/]",
            PatchResult.Skipped => "[grey]✔[/]",
            PatchResult.Error => "[red]✘ Error[/]",
            _ => "[grey]?[/]"
        };
        AnsiConsole.MarkupLine($"  {icon} {name} [grey]({path})[/]");
    }
}

async Task InstallLibrariesAsync()
{
    AnsiConsole.MarkupLine("\n[bold]Phase 3: Install Libraries[/]");

    var exePath = Path.Combine(InstallDir, ExeName);
    if (!File.Exists(exePath))
    {
        AnsiConsole.MarkupLine("[red]  ✘ McpDocMind.Lite.exe not found. Run Full Install first.[/]");
        return;
    }

    var installer = new LibraryInstaller(exePath, InstallDir);

    // Query already-installed libraries
    var installed = installer.GetInstalledLibraries();
    var libraries = LibraryCatalog.GetAll();

    List<LibraryEntry> selected;

    if (AnsiConsole.Profile.Capabilities.Interactive)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title("Select libraries to install:")
            .InstructionsText("[grey](Space=toggle, Enter=accept)[/]");

        foreach (var category in new[] { "Revit", "AutoCAD", "APS", "Other" })
        {
            var categoryLibs = libraries.Where(l => l.Category == category).ToList();
            if (categoryLibs.Count == 0) continue;

            var items = categoryLibs.Select(l =>
            {
                var key = $"{l.LibraryName ?? l.NuGetId}:{l.ApiVersion ?? l.VersionPrefix}";
                var marker = installed.Contains(key) ? " [green]✔[/]" : "";
                return $"{l.DisplayName}{marker}";
            }).ToArray();

            prompt.AddChoiceGroup($"-- {category} --", items);
        }

        var selectedNames = AnsiConsole.Prompt(prompt)
            .Where(s => !s.StartsWith("--"))
            .Select(s => s.Replace(" [green]✔[/]", ""))
            .ToHashSet();

        selected = libraries.Where(l => selectedNames.Contains(l.DisplayName)).ToList();
    }
    else
    {
        selected = libraries
            .Where(l => l.Type != LibraryType.Separator)
            .Where(l =>
            {
                var key = $"{l.LibraryName ?? l.NuGetId}:{l.ApiVersion ?? l.VersionPrefix}";
                return !installed.Contains(key);
            })
            .ToList();

        if (selected.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]  ✔ All libraries already installed.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]  Installing {selected.Count} new libraries...[/]");
    }

    if (selected.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]  No libraries selected.[/]");
        return;
    }

    var succeeded = 0;
    var failed = 0;
    var skipped = 0;

    await AnsiConsole.Progress()
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var downloadSem = new SemaphoreSlim(3);
            var tasks = selected.Select(async lib =>
            {
                var task = ctx.AddTask(Markup.Escape(lib.DisplayName));
                var key = $"{lib.LibraryName ?? lib.NuGetId}:{lib.ApiVersion ?? lib.VersionPrefix}";
                
                await downloadSem.WaitAsync();
                try
                {
                    await installer.InstallAsync(lib, p => task.Value = p);
                    task.Description = $"[green]✔ {Markup.Escape(lib.DisplayName)}[/]";
                    task.Value = 100;
                    Interlocked.Increment(ref succeeded);
                }
                catch (SkipException)
                {
                    task.Description = $"[grey]✔ {Markup.Escape(lib.DisplayName)} (up to date)[/]";
                    task.Value = 100;
                    Interlocked.Increment(ref skipped);
                }
                catch (Exception ex)
                {
                    task.Description = $"[red]✘ {Markup.Escape(lib.DisplayName)}: {Markup.Escape(ex.Message)}[/]";
                    task.Value = 100;
                    Interlocked.Increment(ref failed);
                }
                finally
                {
                    downloadSem.Release();
                }
            });

            await Task.WhenAll(tasks);
        });

    var parts = new List<string>();
    if (succeeded > 0) parts.Add($"[green]✔ {succeeded} installed[/]");
    if (failed > 0) parts.Add($"[red]✘ {failed} failed[/]");
    if (skipped > 0) parts.Add($"[grey]✔ {skipped} already installed[/]");
    AnsiConsole.MarkupLine($"\n  {string.Join(", ", parts)}");
}

void Uninstall()
{
    AnsiConsole.MarkupLine("[bold]Uninstalling McpDocMind...[/]");
    var processes = Process.GetProcessesByName("McpDocMind.Lite");
    foreach (var p in processes)
    {
        try { p.Kill(); p.WaitForExit(5000); } catch { }
    }

    foreach (var (name, path, _) in GetConfigTargets())
    {
        if (ConfigPatcher.Unpatch(path))
            AnsiConsole.MarkupLine($"  [green]✔[/] Removed from {name}");
    }

    if (Directory.Exists(InstallDir))
    {
        Thread.Sleep(500);
        try
        {
            Directory.Delete(InstallDir, recursive: true);
            AnsiConsole.MarkupLine($"  [green]✔[/] Deleted {InstallDir}");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]✘[/] Failed to delete: {Markup.Escape(ex.Message)}");
        }
    }
    AnsiConsole.MarkupLine("\n[green bold]✔ Uninstalled.[/]");
}

void ShowStatus()
{
    AnsiConsole.MarkupLine("[bold]McpDocMind Status[/]\n");
    var exePath = Path.Combine(InstallDir, ExeName);
    if (File.Exists(exePath))
    {
        var versionFile = Path.Combine(InstallDir, "version.json");
        var ver = "unknown";
        if (File.Exists(versionFile))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(versionFile));
                ver = doc.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?";
            }
            catch { }
        }
        AnsiConsole.MarkupLine($"  [green]✔[/] Installed: v{ver} at {InstallDir}");
    }
    else
    {
        AnsiConsole.MarkupLine("  [red]✘[/] Not installed");
        return;
    }

    var procs = Process.GetProcessesByName("McpDocMind.Lite");
    AnsiConsole.MarkupLine($"  [grey]Running processes: {procs.Length}[/]");

    foreach (var (name, path, _) in GetConfigTargets())
    {
        var exists = File.Exists(path);
        var hasMcp = false;
        if (exists)
        {
            try { var text = File.ReadAllText(path); hasMcp = text.Contains("mcp-docmind"); } catch { }
        }
        var icon = hasMcp ? "[green]✔[/]" : exists ? "[yellow]??[/] not registered" : "[grey]-[/] not found";
        AnsiConsole.MarkupLine($"  {icon} {name}");
    }

    var installer = new LibraryInstaller(exePath, InstallDir);
    var libs = installer.GetInstalledLibraries();
    AnsiConsole.MarkupLine($"\n  [bold]Libraries:[/] {libs.Count} installed");
    foreach (var lib in libs.OrderBy(x => x)) AnsiConsole.MarkupLine($"    [grey]•[/] {lib}");

    var cacheSize = installer.GetCacheSize();
    if (cacheSize > 0) AnsiConsole.MarkupLine($"\n  [grey]Cache: {cacheSize / 1024 / 1024}MB (use --clear-cache to free)[/]");
}

void ClearCache()
{
    var exePath = Path.Combine(InstallDir, ExeName);
    var installer = new LibraryInstaller(exePath, InstallDir);
    var size = installer.GetCacheSize();
    installer.ClearCache();
    AnsiConsole.MarkupLine($"[green]✔ Cleared {size / 1024 / 1024}MB cache[/]");
}

(string Name, string Path, ConfigFormat Format)[] GetConfigTargets() =>
[
    ("Claude Code", ConfigPatcher.ClaudeCodePath, ConfigFormat.ClaudeCode),
    ("Gemini CLI", ConfigPatcher.GeminiCliPath, ConfigFormat.GeminiCli),
    ("Antigravity", ConfigPatcher.AntigravityPath, ConfigFormat.Antigravity),
];

void Done()
{
    AnsiConsole.MarkupLine("\n[green bold]✔ Done![/]");
    AnsiConsole.MarkupLine("[grey]Restart your AI tools to connect.[/]");
}
