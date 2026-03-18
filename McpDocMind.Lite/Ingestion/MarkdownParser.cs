using System.Text;
using System.Text.RegularExpressions;
using McpDocMind.Lite.Constants;
using McpDocMind.Lite.Models;
using Markdig;
using Markdig.Syntax;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Parses Markdown files into DocChunks using Markdig AST.
/// </summary>
public sealed partial class MarkdownParser
{
    private int _chunkOrder;

    [GeneratedRegex(@"<[A-Z][a-zA-Z]*[^>]*/>", RegexOptions.Compiled)]
    private static partial Regex MdxTagRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled)]
    private static partial Regex LinkPatternRegex();

    public async Task<List<DocChunk>> ParseFileAsync(string filePath, string libraryName,
        string version, CancellationToken ct = default)
    {
        _chunkOrder = 0;
        var content = await File.ReadAllTextAsync(filePath, ct);
        var fileName = Path.GetFileNameWithoutExtension(filePath);

        var frontmatter = ExtractFrontmatter(ref content);
        content = RemoveMdxTags(content);

        if (IsTocFile(content)) return [];

        var sections = SplitByH2Headers(content, fileName, frontmatter);
        var chunks = new List<DocChunk>();

        foreach (var (title, body, _) in sections)
        {
            var tokens = EstimateTokens(body);
            if (tokens < IngestionConstants.MinChunkTokens) continue;

            if (tokens > IngestionConstants.HardLimitTokens)
            {
                chunks.AddRange(SplitLargeSection(title, body, libraryName, version, filePath));
            }
            else
            {
                chunks.Add(CreateChunk(title, body, libraryName, version, filePath, tokens));
            }
        }

        return chunks;
    }

    public async Task<List<DocChunk>> ParseDirectoryAsync(string directoryPath, string libraryName,
        string version, CancellationToken ct = default)
    {
        var allChunks = new List<DocChunk>();
        var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => IngestionConstants.MarkdownExtensions
                .Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(directoryPath, file);
            var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => IngestionConstants.IgnoredDirs.Contains(p))) continue;

            try
            {
                allChunks.AddRange(await ParseFileAsync(file, libraryName, version, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to parse {file}: {ex.Message}");
            }
        }

        return allChunks;
    }

    private List<(string Title, string Content, int Level)> SplitByH2Headers(
        string content, string defaultTitle, Dictionary<string, string> frontmatter)
    {
        var pipeline = new MarkdownPipelineBuilder().UseYamlFrontMatter().Build();
        var document = Markdown.Parse(content, pipeline);

        var docTitle = frontmatter.TryGetValue("title", out var fmTitle) && !string.IsNullOrEmpty(fmTitle)
            ? fmTitle : defaultTitle;

        var sections = new List<(string Title, string Content, int Level)>();
        var currentTitle = docTitle;
        var currentContent = new StringBuilder();
        var currentLevel = 1;
        var lines = content.Split('\n');

        foreach (var block in document)
        {
            if (block is HeadingBlock heading && heading.Level <= 2)
            {
                if (currentContent.Length > 0)
                {
                    sections.Add((currentTitle, currentContent.ToString().Trim(), currentLevel));
                    currentContent.Clear();
                }

                var titleLine = heading.Line < lines.Length ? lines[heading.Line].TrimStart('#', ' ') : docTitle;
                currentTitle = $"{docTitle} > {titleLine}";
                currentLevel = heading.Level;
            }
        }

        // Remaining content
        if (sections.Count == 0)
        {
            sections.Add((docTitle, content.Trim(), 1));
        }
        else
        {
            // Add final section from last heading to end
            var lastHeadingLine = 0;
            foreach (var block in document)
            {
                if (block is HeadingBlock h && h.Level <= 2)
                    lastHeadingLine = h.Line;
            }

            var remaining = string.Join('\n', lines.Skip(lastHeadingLine));
            if (!string.IsNullOrWhiteSpace(remaining))
                sections.Add((currentTitle, remaining.Trim(), currentLevel));
        }

        return sections;
    }

    private List<DocChunk> SplitLargeSection(string title, string content,
        string libraryName, string version, string filePath)
    {
        var chunks = new List<DocChunk>();
        var paragraphs = content.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();
        var currentTokens = 0;

        foreach (var para in paragraphs)
        {
            var paraTokens = EstimateTokens(para);
            if (currentTokens + paraTokens > IngestionConstants.MaxChunkTokens && current.Length > 0)
            {
                chunks.Add(CreateChunk(title, current.ToString().Trim(),
                    libraryName, version, filePath, currentTokens));
                current.Clear();
                currentTokens = 0;
            }
            current.AppendLine(para).AppendLine();
            currentTokens += paraTokens;
        }

        if (current.Length > 0)
        {
            chunks.Add(CreateChunk(title, current.ToString().Trim(),
                libraryName, version, filePath, currentTokens));
        }

        return chunks;
    }

    private DocChunk CreateChunk(string title, string content,
        string libraryName, string version, string filePath, int tokens)
    {
        var hasCode = content.Contains("```");
        return new DocChunk
        {
            LibraryName = libraryName,
            ApiVersion = version,
            Source = filePath,
            Section = title,
            Content = content,
            ChunkIndex = _chunkOrder++,
            HasCode = hasCode,
            Tokens = tokens,
            ContentHash = ComputeHash(content)
        };
    }

    private static Dictionary<string, string> ExtractFrontmatter(ref string content)
    {
        var fm = new Dictionary<string, string>();
        if (!content.StartsWith("---")) return fm;

        var endIndex = content.IndexOf("\n---", 3);
        if (endIndex == -1) return fm;

        var yaml = content[3..endIndex];
        content = content[(endIndex + 4)..].TrimStart();

        foreach (var line in yaml.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                fm[key] = value;
            }
        }
        return fm;
    }

    private static string RemoveMdxTags(string content)
    {
        var sb = new StringBuilder();
        var inCodeBlock = false;
        foreach (var line in content.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```")) inCodeBlock = !inCodeBlock;
            sb.AppendLine(inCodeBlock ? line : MdxTagRegex().Replace(line, ""));
        }
        return sb.ToString();
    }

    private static bool IsTocFile(string content)
    {
        var lines = content.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return false;
        var linkLines = lines.Count(l => LinkPatternRegex().IsMatch(l));
        return (double)linkLines / lines.Count > 0.5;
    }

    public static int EstimateTokens(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    private static string ComputeHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
