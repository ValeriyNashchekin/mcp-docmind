using System.Xml;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Parses .NET XML documentation files (*.xml) that accompany assemblies.
/// Maps member IDs (e.g., "T:System.String", "M:System.String.Concat") to summaries.
/// </summary>
public sealed class XmlDocParser
{
    /// <summary>
    /// Loads XML documentation and returns a dictionary of member ID → summary text.
    /// </summary>
    public static Dictionary<string, MemberDoc> Parse(string xmlPath)
    {
        var docs = new Dictionary<string, MemberDoc>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(xmlPath)) return docs;

        try
        {
            var doc = new XmlDocument();
            doc.Load(xmlPath);

            var members = doc.SelectNodes("//doc/members/member");
            if (members is null) return docs;

            foreach (XmlNode member in members)
            {
                var name = member.Attributes?["name"]?.Value;
                if (string.IsNullOrEmpty(name)) continue;

                var summary = CleanXml(member.SelectSingleNode("summary")?.InnerXml);
                var returns = CleanXml(member.SelectSingleNode("returns")?.InnerXml);
                var remarks = CleanXml(member.SelectSingleNode("remarks")?.InnerXml);

                var parameters = new Dictionary<string, string>();
                var paramNodes = member.SelectNodes("param");
                if (paramNodes is not null)
                {
                    foreach (XmlNode param in paramNodes)
                    {
                        var paramName = param.Attributes?["name"]?.Value;
                        if (!string.IsNullOrEmpty(paramName))
                            parameters[paramName] = CleanXml(param.InnerXml) ?? "";
                    }
                }

                docs[name] = new MemberDoc(summary, returns, remarks, parameters);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to parse XML doc {xmlPath}: {ex.Message}");
        }

        return docs;
    }

    private static string? CleanXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        // Remove <see cref="..."/> and <paramref name="..."/> tags, keeping text
        return System.Text.RegularExpressions.Regex
            .Replace(xml, @"<(?:see|paramref|typeparamref)\s+(?:cref|name)=""([^""]+)""\s*/?>",
                m => m.Groups[1].Value.Split('.').Last())
            .Replace("<c>", "`").Replace("</c>", "`")
            .Replace("<para>", "\n").Replace("</para>", "")
            .Replace("<code>", "```\n").Replace("</code>", "\n```")
            .Trim();
    }
}

public record MemberDoc(string? Summary, string? Returns, string? Remarks,
    Dictionary<string, string> Parameters);
