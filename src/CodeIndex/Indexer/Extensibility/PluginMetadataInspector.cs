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

            PluginMetadataInspection? marker = null;
            foreach (var attributeHandle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(attributeHandle);
                if (!TryValidatePluginMarkerConstructor(
                        reader,
                        attribute.Constructor,
                        out var isPluginMarker,
                        out error))
                {
                    return false;
                }
                if (!isPluginMarker)
                    continue;

                if (marker.HasValue)
                {
                    error = "Plugin metadata contains duplicate CdidxPluginAttribute markers.";
                    return false;
                }

                var value = reader.GetBlobReader(attribute.Value);
                if (value.RemainingBytes != sizeof(ushort) + (sizeof(int) * 2) + sizeof(ushort)
                    || value.ReadUInt16() != 1)
                {
                    error = "CdidxPluginAttribute metadata has an invalid value blob.";
                    return false;
                }

                marker = new(
                    HasMarker: true,
                    MinApiVersion: value.ReadInt32(),
                    MaxApiVersion: value.ReadInt32());
                if (value.ReadUInt16() != 0 || value.RemainingBytes != 0)
                {
                    error = "CdidxPluginAttribute metadata has unsupported named arguments.";
                    return false;
                }
            }

            inspection = marker ?? new(HasMarker: false, MinApiVersion: 0, MaxApiVersion: 0);
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

    internal static bool IsExpectedMarkerConstructorSignatureForTests(string name, byte[] signature)
        => IsExpectedMarkerConstructorSignature(name, signature);

    private static bool TryValidatePluginMarkerConstructor(
        MetadataReader reader,
        EntityHandle constructor,
        out bool isPluginMarker,
        out string error)
    {
        isPluginMarker = false;
        error = string.Empty;
        if (constructor.Kind != HandleKind.MemberReference)
            return true;

        var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
        if (member.Parent.Kind != HandleKind.TypeReference
            || !IsPluginMarker(reader, reader.GetTypeReference((TypeReferenceHandle)member.Parent)))
        {
            return true;
        }

        isPluginMarker = true;
        if (IsExpectedMarkerConstructorSignature(
                reader.GetString(member.Name),
                reader.GetBlobBytes(member.Signature)))
        {
            return true;
        }

        error = "CdidxPluginAttribute constructor metadata is invalid.";
        return false;
    }

    private static bool IsExpectedMarkerConstructorSignature(string name, ReadOnlySpan<byte> signature)
        => StringComparer.Ordinal.Equals(name, ".ctor")
           && signature.SequenceEqual(new byte[] { 0x20, 0x02, 0x01, 0x08, 0x08 });

    private static bool IsPluginMarker(MetadataReader reader, TypeReference type)
        => reader.StringComparer.Equals(type.Name, nameof(CdidxPluginAttribute))
           && reader.StringComparer.Equals(type.Namespace, typeof(CdidxPluginAttribute).Namespace!)
           && type.ResolutionScope.Kind == HandleKind.AssemblyReference
           && reader.StringComparer.Equals(
               reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name,
               typeof(CdidxPluginAttribute).Assembly.GetName().Name!);
}
