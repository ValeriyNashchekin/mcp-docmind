using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using McpDocMind.Lite.Models;

namespace McpDocMind.Lite.Ingestion;

/// <summary>
/// Parses .NET assemblies using System.Reflection.Metadata (low-level, no reflection).
/// Extracts types, methods, properties, events, constructors, enums, interfaces.
/// </summary>
public sealed class DllParser
{
    /// <summary>
    /// Parse a .NET DLL and return API nodes + relations.
    /// </summary>
    public (List<ApiNode> Nodes, List<ApiRelation> Relations) ParseDll(
        string dllPath, string libraryName, string apiVersion,
        Dictionary<string, MemberDoc>? xmlDocs = null)
    {
        var nodes = new List<ApiNode>();
        var relations = new List<ApiRelation>();

        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);

        if (!peReader.HasMetadata) return (nodes, relations);

        var mdReader = peReader.GetMetadataReader();

        foreach (var typeHandle in mdReader.TypeDefinitions)
        {
            var typeDef = mdReader.GetTypeDefinition(typeHandle);
            var ns = mdReader.GetString(typeDef.Namespace);
            var name = mdReader.GetString(typeDef.Name);

            // Skip compiler-generated types
            if (string.IsNullOrEmpty(name) || name.StartsWith("<") || name.StartsWith("__"))
                continue;

            // Skip non-public types
            var visibility = typeDef.Attributes & System.Reflection.TypeAttributes.VisibilityMask;
            if (visibility != System.Reflection.TypeAttributes.Public &&
                visibility != System.Reflection.TypeAttributes.NestedPublic)
                continue;

            var fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            var nodeType = GetNodeType(typeDef);
            var declaration = BuildTypeDeclaration(mdReader, typeDef, nodeType);

            var xmlKey = $"T:{fullName}";
            var summary = xmlDocs?.GetValueOrDefault(xmlKey)?.Summary;

            var typeNode = new ApiNode
            {
                LibraryName = libraryName,
                ApiVersion = apiVersion,
                NodeType = nodeType,
                FullName = fullName,
                Name = name,
                Namespace = ns,
                Summary = summary,
                Declaration = declaration
            };
            nodes.Add(typeNode);

            // Base type relation
            if (!typeDef.BaseType.IsNil)
            {
                var baseName = GetTypeReferenceName(mdReader, typeDef.BaseType);
                if (baseName is not null && baseName != "System.Object" && baseName != "System.ValueType" && baseName != "System.Enum")
                {
                    typeNode.ParentType = baseName;
                    // Relations with proper IDs will be resolved after all nodes are inserted
                }
            }

            // Parse members (pass the actual namespace, not the full type name)
            ParseMethods(mdReader, typeDef, fullName, ns, libraryName, apiVersion, xmlDocs, nodes);
            ParseProperties(mdReader, typeDef, fullName, ns, libraryName, apiVersion, xmlDocs, nodes);
            ParseEvents(mdReader, typeDef, fullName, ns, libraryName, apiVersion, xmlDocs, nodes);
            ParseFields(mdReader, typeDef, fullName, ns, libraryName, apiVersion, xmlDocs, nodes, nodeType);
        }

        return (nodes, relations);
    }

    private void ParseMethods(MetadataReader r, TypeDefinition typeDef, string parentFullName,
        string parentNamespace, string lib, string ver, Dictionary<string, MemberDoc>? docs, List<ApiNode> nodes)
    {
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var method = r.GetMethodDefinition(methodHandle);
            var name = r.GetString(method.Name);

            // Skip special names (property accessors, event accessors) unless constructors
            if (name.StartsWith("get_") || name.StartsWith("set_") ||
                name.StartsWith("add_") || name.StartsWith("remove_"))
                continue;

            // Skip non-public
            var access = method.Attributes & System.Reflection.MethodAttributes.MemberAccessMask;
            if (access != System.Reflection.MethodAttributes.Public) continue;

            var isConstructor = name == ".ctor" || name == ".cctor";
            var nodeType = isConstructor ? "Constructor" : "Method";
            var displayName = isConstructor ? parentFullName.Split('.').Last() : name;
            var fullName = $"{parentFullName}.{displayName}";

            var sig = BuildMethodSignature(r, method, displayName);
            var xmlKey = $"M:{parentFullName}.{name}";
            var summary = docs?.GetValueOrDefault(xmlKey)?.Summary;

            nodes.Add(new ApiNode
            {
                LibraryName = lib, ApiVersion = ver,
                NodeType = nodeType, FullName = fullName, Name = displayName,
                Namespace = parentNamespace,
                Summary = summary, Declaration = sig,
                ParentType = parentFullName
            });
        }
    }

    private void ParseProperties(MetadataReader r, TypeDefinition typeDef, string parentFullName,
        string parentNamespace, string lib, string ver, Dictionary<string, MemberDoc>? docs, List<ApiNode> nodes)
    {
        foreach (var propHandle in typeDef.GetProperties())
        {
            var prop = r.GetPropertyDefinition(propHandle);
            var name = r.GetString(prop.Name);
            var fullName = $"{parentFullName}.{name}";
            var xmlKey = $"P:{fullName}";
            var summary = docs?.GetValueOrDefault(xmlKey)?.Summary;

            var accessors = prop.GetAccessors();
            var hasGetter = !accessors.Getter.IsNil;
            var hasSetter = !accessors.Setter.IsNil;
            var accessorStr = (hasGetter, hasSetter) switch
            {
                (true, true) => "{ get; set; }",
                (true, false) => "{ get; }",
                (false, true) => "{ set; }",
                _ => ""
            };

            nodes.Add(new ApiNode
            {
                LibraryName = lib, ApiVersion = ver,
                NodeType = "Property", FullName = fullName, Name = name,
                Namespace = parentNamespace,
                Summary = summary, Declaration = $"{name} {accessorStr}",
                ParentType = parentFullName
            });
        }
    }

    private void ParseEvents(MetadataReader r, TypeDefinition typeDef, string parentFullName,
        string parentNamespace, string lib, string ver, Dictionary<string, MemberDoc>? docs, List<ApiNode> nodes)
    {
        foreach (var eventHandle in typeDef.GetEvents())
        {
            var evt = r.GetEventDefinition(eventHandle);
            var name = r.GetString(evt.Name);
            var fullName = $"{parentFullName}.{name}";
            var xmlKey = $"E:{fullName}";
            var summary = docs?.GetValueOrDefault(xmlKey)?.Summary;

            nodes.Add(new ApiNode
            {
                LibraryName = lib, ApiVersion = ver,
                NodeType = "Event", FullName = fullName, Name = name,
                Namespace = parentNamespace,
                Summary = summary, Declaration = $"event {name}",
                ParentType = parentFullName
            });
        }
    }

    private void ParseFields(MetadataReader r, TypeDefinition typeDef, string parentFullName,
        string parentNamespace, string lib, string ver, Dictionary<string, MemberDoc>? docs, List<ApiNode> nodes,
        string parentNodeType)
    {
        foreach (var fieldHandle in typeDef.GetFields())
        {
            var field = r.GetFieldDefinition(fieldHandle);
            var name = r.GetString(field.Name);

            // Skip non-public and compiler-generated
            var access = field.Attributes & System.Reflection.FieldAttributes.FieldAccessMask;
            if (access != System.Reflection.FieldAttributes.Public) continue;
            if (name.StartsWith("<")) continue;

            var fullName = $"{parentFullName}.{name}";
            var xmlKey = $"F:{fullName}";
            var summary = docs?.GetValueOrDefault(xmlKey)?.Summary;

            nodes.Add(new ApiNode
            {
                LibraryName = lib, ApiVersion = ver,
                NodeType = "Field", FullName = fullName, Name = name,
                Namespace = parentNamespace,
                Summary = summary,
                ParentType = parentFullName
            });
        }
    }

    private static string GetNodeType(TypeDefinition typeDef)
    {
        if (typeDef.Attributes.HasFlag(System.Reflection.TypeAttributes.Interface))
            return "Interface";

        var baseType = typeDef.BaseType;
        // We can't easily resolve base type names without loading,
        // but enums and delegates have distinct characteristics
        if (typeDef.Attributes.HasFlag(System.Reflection.TypeAttributes.Sealed) &&
            typeDef.GetFields().Count > 0 && typeDef.GetMethods().Count <= 4)
            return "Enum"; // Heuristic: sealed with mostly fields

        return "Class"; // Default
    }

    private static string BuildTypeDeclaration(MetadataReader r, TypeDefinition typeDef, string nodeType)
    {
        var sb = new StringBuilder();
        var vis = (typeDef.Attributes & System.Reflection.TypeAttributes.VisibilityMask) switch
        {
            System.Reflection.TypeAttributes.Public => "public ",
            System.Reflection.TypeAttributes.NestedPublic => "public ",
            _ => ""
        };
        sb.Append(vis);

        if (typeDef.Attributes.HasFlag(System.Reflection.TypeAttributes.Abstract) &&
            !typeDef.Attributes.HasFlag(System.Reflection.TypeAttributes.Interface))
            sb.Append("abstract ");

        if (typeDef.Attributes.HasFlag(System.Reflection.TypeAttributes.Sealed) && nodeType == "Class")
            sb.Append("sealed ");

        sb.Append(nodeType.ToLowerInvariant());
        sb.Append(' ');
        sb.Append(r.GetString(typeDef.Name));

        return sb.ToString();
    }

    private static string BuildMethodSignature(MetadataReader r, MethodDefinition method, string displayName)
    {
        var sb = new StringBuilder();
        sb.Append(displayName);
        sb.Append('(');

        var paramHandles = method.GetParameters().ToList();
        var first = true;
        foreach (var paramHandle in paramHandles)
        {
            var param = r.GetParameter(paramHandle);
            if (param.SequenceNumber == 0) continue; // Return parameter

            if (!first) sb.Append(", ");
            first = false;

            var paramName = r.GetString(param.Name);
            sb.Append(paramName);
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static string? GetTypeReferenceName(MetadataReader r, EntityHandle handle)
    {
        try
        {
            if (handle.Kind == HandleKind.TypeReference)
            {
                var typeRef = r.GetTypeReference((TypeReferenceHandle)handle);
                var ns = r.GetString(typeRef.Namespace);
                var name = r.GetString(typeRef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
            if (handle.Kind == HandleKind.TypeDefinition)
            {
                var typeDef = r.GetTypeDefinition((TypeDefinitionHandle)handle);
                var ns = r.GetString(typeDef.Namespace);
                var name = r.GetString(typeDef.Name);
                return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
            }
        }
        catch { /* graceful fallback */ }
        return null;
    }
}
