using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CodeIndex.Indexer.Extensibility;

internal readonly record struct PluginMetadataInspection(
    bool HasMarker,
    int MinApiVersion,
    int MaxApiVersion);

internal static class PluginMetadataInspector
{
    internal static bool TryInspect(
        string assemblyPath,
        out PluginMetadataInspection inspection,
        out string error)
    {
        inspection = default;
        error = string.Empty;
        try
        {
            using var stream = new FileStream(
                assemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                error = "Plugin metadata is not a managed assembly.";
                return false;
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                error = "Plugin metadata does not contain an assembly definition.";
                return false;
            }

            foreach (var attributeHandle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                if (!IsPluginMarker(reader, attribute.Constructor))
                    continue;

                var value = reader.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1 || value.RemainingBytes < sizeof(int) * 2)
                {
                    error = "CdidxPluginAttribute metadata has an invalid value blob.";
                    return false;
                }

                inspection = new(
                    HasMarker: true,
                    MinApiVersion: value.ReadInt32(),
                    MaxApiVersion: value.ReadInt32());
                return true;
            }

            inspection = new(HasMarker: false, MinApiVersion: 0, MaxApiVersion: 0);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"Plugin metadata inspection failed ({ex.GetType().Name}).";
            return false;
        }
    }

    internal static bool TryReadAssemblyReferences(
        string assemblyPath,
        out IReadOnlyList<string> references,
        out string error)
    {
        references = [];
        error = string.Empty;
        try
        {
            using var stream = new FileStream(
                assemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.RandomAccess);
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                error = "Plugin dependency metadata is not a managed assembly.";
                return false;
            }

            var reader = peReader.GetMetadataReader();
            references = reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error = $"Plugin dependency metadata inspection failed ({ex.GetType().Name}).";
            return false;
        }
    }

    private static bool IsPluginMarker(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle declaringType;
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
                declaringType = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                declaringType = reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType();
                break;
            default:
                return false;
        }

        return declaringType.Kind switch
        {
            HandleKind.TypeReference => IsPluginMarker(reader, reader.GetTypeReference((TypeReferenceHandle)declaringType)),
            _ => false,
        };
    }

    private static bool IsPluginMarker(MetadataReader reader, TypeReference type)
        => reader.StringComparer.Equals(type.Name, nameof(CdidxPluginAttribute))
           && reader.StringComparer.Equals(type.Namespace, typeof(CdidxPluginAttribute).Namespace!)
           && type.ResolutionScope.Kind == HandleKind.AssemblyReference
           && reader.StringComparer.Equals(
               reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name,
               typeof(CdidxPluginAttribute).Assembly.GetName().Name!);
}
