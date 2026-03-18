using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpDocMind.Setup;

public enum ConfigFormat { ClaudeCode, GeminiCli, Antigravity }
public enum PatchResult { Created, Updated, Added, Skipped, Error }

/// <summary>
/// Detects and patches AI tool config files to register the MCP server.
/// Smart upsert: creates, adds, or updates mcp-docmind entry without touching other servers.
/// Creates .bak backup before modifying existing files.
/// </summary>
public sealed class ConfigPatcher(string exePath)
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    public static string ClaudeCodePath => Path.Combine(Home, ".claude.json");
    public static string GeminiCliPath => Path.Combine(Home, ".gemini", "settings.json");
    public static string AntigravityPath => Path.Combine(Home, ".gemini", "antigravity", "mcp_config.json");

    public PatchResult Patch(string configPath, ConfigFormat format)
    {
        try
        {
            var entry = BuildEntry(format);

            if (!File.Exists(configPath))
            {
                var dir = Path.GetDirectoryName(configPath);
                if (dir is not null) Directory.CreateDirectory(dir);

                var root = new JsonObject
                {
                    ["mcpServers"] = new JsonObject { ["mcp-docmind"] = entry }
                };
                WriteJson(configPath, root);
                return PatchResult.Created;
            }

            // Backup existing config
            File.Copy(configPath, configPath + ".bak", overwrite: true);

            var rootObj = ReadJson(configPath);
            if (rootObj is null) return PatchResult.Error;

            if (rootObj["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                rootObj["mcpServers"] = servers;
            }

            if (servers["mcp-docmind"] is not null)
            {
                servers.Remove("mcp-docmind");
                servers.Add("mcp-docmind", entry);
                WriteJson(configPath, rootObj);
                return PatchResult.Updated;
            }

            servers.Add("mcp-docmind", entry);
            WriteJson(configPath, rootObj);
            return PatchResult.Added;
        }
        catch
        {
            RestoreBackup(configPath);
            return PatchResult.Error;
        }
    }

    /// <summary>
    /// Remove mcp-docmind entry from config file. Returns true if removed.
    /// </summary>
    public static bool Unpatch(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return false;

            File.Copy(configPath, configPath + ".bak", overwrite: true);

            var rootObj = ReadJson(configPath);
            if (rootObj is null) return false;

            if (rootObj["mcpServers"] is not JsonObject servers) return false;
            if (servers["mcp-docmind"] is null) return false;

            servers.Remove("mcp-docmind");
            WriteJson(configPath, rootObj);
            return true;
        }
        catch
        {
            RestoreBackup(configPath);
            return false;
        }
    }

    private JsonObject BuildEntry(ConfigFormat format) => format switch
    {
        ConfigFormat.ClaudeCode => new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = exePath,
            ["cwd"] = Path.GetDirectoryName(exePath)
        },
        ConfigFormat.GeminiCli or ConfigFormat.Antigravity => new JsonObject
        {
            ["command"] = exePath,
            ["args"] = new JsonArray()
        },
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static JsonObject? ReadJson(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..]; // strip BOM

        var doc = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return doc as JsonObject;
    }

    private static void WriteJson(string path, JsonNode node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        File.WriteAllText(path, node.ToJsonString(options), Utf8NoBom);
    }

    private static void RestoreBackup(string configPath)
    {
        var bak = configPath + ".bak";
        if (File.Exists(bak))
        {
            try { File.Copy(bak, configPath, overwrite: true); } catch { /* best effort */ }
        }
    }
}
