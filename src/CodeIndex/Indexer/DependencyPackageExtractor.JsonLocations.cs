using System.Text;
using System.Text.Json;

namespace CodeIndex.Indexer;

internal static partial class DependencyPackageExtractor
{
    private sealed class JsonObjectLocations
    {
        public List<JsonPropertyLocation> Properties { get; } = [];
    }

    private sealed class JsonPropertyLocation(string name, int line, int column)
    {
        public string Name { get; } = name;
        public int Line { get; } = line;
        public int Column { get; } = column;
        public int EndLine { get; set; } = line;
        public JsonObjectLocations? ObjectValue { get; set; }
    }

    private readonly record struct JsonLocationContainer(
        JsonObjectLocations? ObjectValue,
        JsonPropertyLocation? OwningProperty);

    private static JsonObjectLocations? TryParseJsonObjectLocations(string content)
    {
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(content);
            var locator = new Utf8JsonSourceLocator(utf8);
            var reader = new Utf8JsonReader(
                utf8,
                new JsonReaderOptions
                {
                    MaxDepth = MaxJsonLockParseDepth,
                });
            var containers = new Stack<JsonLocationContainer>();
            JsonObjectLocations? root = null;
            JsonPropertyLocation? pendingProperty = null;

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        {
                            var objectLocations = new JsonObjectLocations();
                            if (pendingProperty is not null)
                            {
                                pendingProperty.ObjectValue = objectLocations;
                            }

                            root ??= objectLocations;
                            containers.Push(new JsonLocationContainer(objectLocations, pendingProperty));
                            pendingProperty = null;
                            break;
                        }

                    case JsonTokenType.EndObject:
                        {
                            if (containers.Count == 0)
                            {
                                return null;
                            }

                            var container = containers.Pop();
                            if (container.OwningProperty is not null)
                            {
                                container.OwningProperty.EndLine = locator.GetLine(reader.TokenStartIndex);
                            }

                            pendingProperty = null;
                            break;
                        }

                    case JsonTokenType.StartArray:
                        containers.Push(new JsonLocationContainer(null, pendingProperty));
                        pendingProperty = null;
                        break;

                    case JsonTokenType.EndArray:
                        {
                            if (containers.Count == 0)
                            {
                                return null;
                            }

                            var container = containers.Pop();
                            if (container.OwningProperty is not null)
                            {
                                container.OwningProperty.EndLine = locator.GetLine(reader.TokenStartIndex);
                            }

                            pendingProperty = null;
                            break;
                        }

                    case JsonTokenType.PropertyName:
                        {
                            if (containers.Count == 0 || containers.Peek().ObjectValue is not { } parent)
                            {
                                return null;
                            }

                            var (line, column) = locator.GetLocation(reader.TokenStartIndex);
                            var property = new JsonPropertyLocation(reader.GetString() ?? string.Empty, line, column);
                            parent.Properties.Add(property);
                            pendingProperty = property;
                            break;
                        }

                    case JsonTokenType.String:
                    case JsonTokenType.Number:
                    case JsonTokenType.True:
                    case JsonTokenType.False:
                    case JsonTokenType.Null:
                        if (pendingProperty is not null)
                        {
                            pendingProperty.EndLine = locator.GetLine(reader.TokenStartIndex);
                        }

                        pendingProperty = null;
                        break;
                }
            }

            return containers.Count == 0 ? root : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonPropertyLocation? FindLocatedProperty(
        JsonObjectLocations? objectLocations,
        string name)
    {
        if (objectLocations is null)
        {
            return null;
        }

        return objectLocations.Properties.FirstOrDefault(
            property => string.Equals(property.Name, name, StringComparison.Ordinal));
    }

    private static IEnumerable<(JsonProperty Property, JsonPropertyLocation? Location)> EnumerateLocatedProperties(
        JsonElement element,
        JsonObjectLocations? objectLocations)
    {
        var locations = objectLocations?.Properties;
        var locationIndex = 0;
        foreach (var property in element.EnumerateObject())
        {
            JsonPropertyLocation? location = null;
            if (locations is not null)
            {
                while (locationIndex < locations.Count)
                {
                    var candidate = locations[locationIndex++];
                    if (string.Equals(candidate.Name, property.Name, StringComparison.Ordinal))
                    {
                        location = candidate;
                        break;
                    }
                }
            }

            yield return (property, location);
        }
    }

    private sealed class Utf8JsonSourceLocator
    {
        private readonly byte[] _utf8;
        private readonly List<int> _lineStarts = [0];

        public Utf8JsonSourceLocator(byte[] utf8)
        {
            _utf8 = utf8;
            for (var index = 0; index < utf8.Length; index++)
            {
                if (utf8[index] == (byte)'\n')
                {
                    _lineStarts.Add(index + 1);
                }
            }
        }

        public (int Line, int Column) GetLocation(long byteOffset)
        {
            var offset = checked((int)byteOffset);
            var search = _lineStarts.BinarySearch(offset);
            var lineIndex = search >= 0 ? search : ~search - 1;
            var lineStart = _lineStarts[lineIndex];
            var column = Encoding.UTF8.GetCharCount(_utf8, lineStart, offset - lineStart) + 1;
            return (lineIndex + 1, column);
        }

        public int GetLine(long byteOffset) => GetLocation(byteOffset).Line;
    }
}
