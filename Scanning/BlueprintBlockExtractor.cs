using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using minecraft_nbt_tool.Models;

namespace minecraft_nbt_tool.Scanning;

/// <summary>
/// Extracts normalized block records from supported blueprint data layouts.
/// </summary>
internal static class BlueprintBlockExtractor
{
    private static readonly Dictionary<string, string> SpecialDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blockhutplantationfield"] = "Plantation Field",
        ["blockminecoloniesnamedgrave"] = "Named Grave",
        ["blockminecoloniesrack"] = "Rack",
        ["minecoloniesnamedgrave"] = "Named Grave",
        ["minecoloniesrack"] = "Rack"
    };

    private static readonly HashSet<string> PlaceholderBlockIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "air",
        "solidsubstitution",
        "tagsubstitution"
    };

    private static readonly string[] CommonSuffixWords =
    [
        "field",
        "house",
        "hall",
        "tower",
        "school",
        "mine",
        "quarry",
        "farm",
        "hut"
    ];

    /// <summary>
    /// Enumerates block counts by detecting known blueprint schema formats.
    /// </summary>
    public static IEnumerable<BlockRecord> Extract(object root)
    {
        if (TryExtractLegacyStructurizeBlueprint(root, out var legacyBlocks))
        {
            foreach (var block in legacyBlocks)
            {
                yield return block;
            }

            yield break;
        }

        foreach (var candidate in Traverse(root))
        {
            if (TryGetNamedChild(candidate, "palette", out var paletteNode)
                && TryGetNamedChild(candidate, "blocks", out var blocksNode)
                && TryAsList(paletteNode!, out var paletteItems)
                && TryAsList(blocksNode!, out var blockItems))
            {
                foreach (var block in ExtractFromPaletteBlocks(paletteItems, blockItems))
                {
                    yield return block;
                }

                yield break;
            }

            if (TryGetNamedChild(candidate, "Palette", out paletteNode)
                && TryGetNamedChild(candidate, "BlockData", out var blockDataNode)
                && TryAsList(paletteNode!, out paletteItems)
                && TryAsArray(blockDataNode!, out var rawData)
                && TryGetDimensions(candidate, out var width, out var height, out var length))
            {
                foreach (var block in ExtractFromPackedData(paletteItems, rawData, width, height, length))
                {
                    yield return block;
                }

                yield break;
            }
        }
    }

    /// <summary>
    /// Try extract legacy structurize blueprint.
    /// </summary>
    private static bool TryExtractLegacyStructurizeBlueprint(object root, out List<BlockRecord> blocks)
    {
        blocks = [];

        if (!TryGetNamedChild(root, "palette", out var paletteNode)
            || !TryGetNamedChild(root, "blocks", out var blocksNode)
            || !TryGetNamedChild(root, "size_x", out var sizeXNode)
            || !TryGetNamedChild(root, "size_y", out var sizeYNode)
            || !TryGetNamedChild(root, "size_z", out var sizeZNode)
            || !TryAsList(paletteNode!, out var paletteItems)
            || !TryAsList(blocksNode!, out var packedBlocks))
        {
            return false;
        }

        var sizeX = GetInt(TryGetValueMember(sizeXNode!) ?? sizeXNode!, null) ?? 0;
        var sizeY = GetInt(TryGetValueMember(sizeYNode!) ?? sizeYNode!, null) ?? 0;
        var sizeZ = GetInt(TryGetValueMember(sizeZNode!) ?? sizeZNode!, null) ?? 0;
        if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0)
        {
            return false;
        }

        var palette = paletteItems.Select(BuildBlockDefinition).ToList();
        var decodedShorts = new List<int>(sizeX * sizeY * sizeZ);

        foreach (var packed in packedBlocks)
        {
            if (decodedShorts.Count >= sizeX * sizeY * sizeZ)
            {
                break;
            }

            if (!TryGetPackedInt(packed, out var packedValue))
            {
                continue;
            }

            decodedShorts.Add(unchecked((short)(packedValue >> 16)));
            if (decodedShorts.Count < sizeX * sizeY * sizeZ)
            {
                decodedShorts.Add(unchecked((short)packedValue));
            }
        }

        var counts = new Dictionary<string, BlockRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var paletteIndex in decodedShorts)
        {
            if (paletteIndex >= 0 && paletteIndex < palette.Count)
            {
                AddCount(counts, palette[paletteIndex], 1);
            }
        }

        blocks = counts.Values.ToList();
        return blocks.Count > 0;
    }

    /// <summary>
    /// Extract from palette blocks.
    /// </summary>
    private static IEnumerable<BlockRecord> ExtractFromPaletteBlocks(IReadOnlyList<object> paletteItems, IReadOnlyList<object> blockItems)
    {
        var palette = paletteItems.Select(BuildBlockDefinition).ToList();
        var counts = new Dictionary<string, BlockRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in blockItems)
        {
            if (TryGetNamedChild(item, "state", out var stateNode))
            {
                var stateIndex = GetInt(stateNode!, null);
                if (stateIndex.HasValue && stateIndex.Value >= 0 && stateIndex.Value < palette.Count)
                {
                    AddCount(counts, palette[stateIndex.Value], 1);
                    continue;
                }
            }

            AddCount(counts, BuildBlockDefinition(item), 1);
        }

        return counts.Values;
    }

    /// <summary>
    /// Extract from packed data.
    /// </summary>
    private static IEnumerable<BlockRecord> ExtractFromPackedData(IReadOnlyList<object> paletteItems, IReadOnlyList<long> rawData, int width, int height, int length)
    {
        var palette = paletteItems.Select(BuildBlockDefinition).ToList();
        var expectedCount = width * height * length;
        var bitsPerBlock = Math.Max(4, (int)Math.Ceiling(Math.Log2(Math.Max(1, palette.Count))));
        var counts = new Dictionary<string, BlockRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var stateIndex in UnpackIndices(rawData, bitsPerBlock, expectedCount))
        {
            if (stateIndex >= 0 && stateIndex < palette.Count)
            {
                AddCount(counts, palette[stateIndex], 1);
            }
        }

        return counts.Values;
    }

    /// <summary>
    /// Unpack indices.
    /// </summary>
    private static IEnumerable<int> UnpackIndices(IReadOnlyList<long> rawData, int bitsPerBlock, int expectedCount)
    {
        var mask = (1UL << bitsPerBlock) - 1UL;
        var produced = 0;
        var bitPosition = 0;

        while (produced < expectedCount)
        {
            var dataIndex = bitPosition / 64;
            var bitOffset = bitPosition % 64;
            var current = dataIndex < rawData.Count ? (ulong)rawData[dataIndex] >> bitOffset : 0UL;
            var bitsFromCurrent = Math.Min(64 - bitOffset, bitsPerBlock);

            if (bitsFromCurrent < bitsPerBlock && dataIndex + 1 < rawData.Count)
            {
                current |= (ulong)rawData[dataIndex + 1] << bitsFromCurrent;
            }

            yield return (int)(current & mask);
            produced++;
            bitPosition += bitsPerBlock;
        }
    }

    /// <summary>
    /// Build block definition.
    /// </summary>
    private static BlockRecord BuildBlockDefinition(object source)
    {
        var name = TryGetNamedChild(source, "Name", out var nameTag)
            ? GetScalarString(TryGetValueMember(nameTag!))
            : TryGetNamedChild(source, "name", out nameTag)
                ? GetScalarString(TryGetValueMember(nameTag!))
                : TryGetNamedChild(source, "id", out nameTag)
                    ? GetScalarString(TryGetValueMember(nameTag!))
                    : TryGetNamedChild(source, "Id", out nameTag)
                        ? GetScalarString(TryGetValueMember(nameTag!))
                        : null;

        name ??= "unknown";
        var coreName = name.Contains('[', StringComparison.Ordinal)
            ? name[..name.IndexOf('[')]
            : name;

        var mod = coreName.Contains(':', StringComparison.Ordinal) ? coreName[..coreName.IndexOf(':')] : "minecraft";
        var blockName = coreName.Contains(':', StringComparison.Ordinal) ? coreName[(coreName.IndexOf(':') + 1)..] : coreName;
        var canonicalId = $"{mod}:{blockName}";
        var displayName = FormatDisplayName(blockName);

        return new BlockRecord
        {
            Key = canonicalId,
            Name = displayName,
            Id = blockName,
            Mod = mod,
            Count = 0
        };
    }

    /// <summary>
    /// Format display name.
    /// </summary>
    private static string FormatDisplayName(string blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName))
        {
            return "Unknown";
        }

        if (SpecialDisplayNames.TryGetValue(blockName, out var specialDisplayName))
        {
            return specialDisplayName;
        }

        var candidate = blockName.Trim();

        if (candidate.StartsWith("blockhut", StringComparison.OrdinalIgnoreCase) && candidate.Length > "blockhut".Length)
        {
            candidate = candidate["blockhut".Length..];
        }
        else if (candidate.StartsWith("block", StringComparison.OrdinalIgnoreCase)
                 && candidate.Length > "block".Length
                 && candidate.IndexOf('_') < 0
                 && candidate.IndexOf('-') < 0)
        {
            candidate = candidate["block".Length..];
        }

        candidate = candidate.Replace('_', ' ').Replace('-', ' ');
        candidate = Regex.Replace(candidate, "(?<=[a-z0-9])(?=[A-Z])", " ");
        candidate = Regex.Replace(candidate, "(?<=[A-Za-z])(?=[0-9])", " ");
        candidate = Regex.Replace(candidate, "(?<=[0-9])(?=[A-Za-z])", " ");

        if (candidate.IndexOf(' ') < 0 && Regex.IsMatch(candidate, "^[a-z]+$"))
        {
            foreach (var suffix in CommonSuffixWords)
            {
                if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && candidate.Length > suffix.Length)
                {
                    candidate = $"{candidate[..^suffix.Length]} {suffix}";
                    break;
                }
            }
        }

        var parts = candidate
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant())
            .ToList();

        return parts.Count == 0 ? "Unknown" : string.Join(" ", parts);
    }

    /// <summary>
    /// Add count.
    /// </summary>
    private static void AddCount(IDictionary<string, BlockRecord> counts, BlockRecord block, int count)
    {
        if (IsPlaceholderBlock(block))
        {
            return;
        }

        if (counts.TryGetValue(block.Key, out var existing))
        {
            existing.Count += count;
        }
        else
        {
            counts[block.Key] = block.WithCount(count);
        }
    }

    /// <summary>
    /// Is placeholder block.
    /// </summary>
    private static bool IsPlaceholderBlock(BlockRecord block)
    {
        return PlaceholderBlockIds.Contains(block.Id)
               || PlaceholderBlockIds.Contains(block.Name.Replace(" ", string.Empty, StringComparison.Ordinal));
    }

    /// <summary>
    /// Traverse.
    /// </summary>
    private static IEnumerable<object> Traverse(object root)
    {
        var stack = new Stack<object>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;

            if (TryAsNamedChildren(current, out var namedChildren))
            {
                foreach (var (_, child) in namedChildren)
                {
                    if (child is not null)
                    {
                        stack.Push(child);
                    }
                }
            }
            else if (TryAsList(current, out var listItems))
            {
                foreach (var child in listItems)
                {
                    if (child is not null)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Try get dimensions.
    /// </summary>
    private static bool TryGetDimensions(object source, out int width, out int height, out int length)
    {
        width = GetInt(source, "Width") ?? GetInt(source, "width") ?? 0;
        height = GetInt(source, "Height") ?? GetInt(source, "height") ?? 0;
        length = GetInt(source, "Length") ?? GetInt(source, "length") ?? 0;

        if (width > 0 && height > 0 && length > 0)
        {
            return true;
        }

        if (TryGetNamedChild(source, "size", out var sizeNode) || TryGetNamedChild(source, "Size", out sizeNode))
        {
            if (sizeNode is not null && TryAsList(sizeNode, out var sizeList) && sizeList.Count >= 3)
            {
                var values = sizeList.Take(3).Select(item => GetInt(item, null) ?? 0).ToArray();
                width = values[0];
                height = values[1];
                length = values[2];
                return width > 0 && height > 0 && length > 0;
            }
        }

        return false;
    }

    /// <summary>
    /// Try as named children.
    /// </summary>
    private static bool TryAsNamedChildren(object node, out IReadOnlyList<(string Name, object? Value)> children)
    {
        var result = new List<(string Name, object? Value)>();

        if (node is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not null)
                {
                    result.Add((entry.Key.ToString()!, entry.Value));
                }
            }

            children = result;
            return result.Count > 0;
        }

        if (node is not IEnumerable enumerable || node is string)
        {
            children = Array.Empty<(string Name, object? Value)>();
            return false;
        }

        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            if (item is DictionaryEntry entry && entry.Key is not null)
            {
                result.Add((entry.Key.ToString()!, entry.Value));
                continue;
            }

            var key = TryGetString(item, "Name") ?? TryGetString(item, "name") ?? TryGetString(item, "Key") ?? TryGetString(item, "key");
            if (key is null)
            {
                children = Array.Empty<(string Name, object? Value)>();
                return false;
            }

            result.Add((key, TryGetValueMember(item) ?? item));
        }

        children = result;
        return result.Count > 0;
    }

    /// <summary>
    /// Try as list.
    /// </summary>
    private static bool TryAsList(object node, out IReadOnlyList<object> items)
    {
        if (node is string)
        {
            items = Array.Empty<object>();
            return false;
        }

        if (node is IEnumerable enumerable)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    list.Add(TryGetValueMember(item) ?? item);
                }
            }

            items = list;
            return list.Count > 0;
        }

        items = Array.Empty<object>();
        return false;
    }

    /// <summary>
    /// Try as array.
    /// </summary>
    private static bool TryAsArray(object node, out IReadOnlyList<long> values)
    {
        switch (node)
        {
            case long[] longArray:
                values = longArray;
                return true;
            case int[] intArray:
                values = intArray.Select(value => (long)value).ToArray();
                return true;
            case byte[] byteArray:
                values = byteArray.Select(value => (long)value).ToArray();
                return true;
            default:
                values = Array.Empty<long>();
                return false;
        }
    }

    /// <summary>
    /// Try get packed int.
    /// </summary>
    private static bool TryGetPackedInt(object node, out int value)
    {
        value = 0;

        var unwrapped = TryGetValueMember(node) ?? node;
        switch (unwrapped)
        {
            case int intValue:
                value = intValue;
                return true;
            case uint uintValue:
                value = unchecked((int)uintValue);
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case ushort ushortValue:
                value = ushortValue;
                return true;
            case long longValue:
                value = unchecked((int)longValue);
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            case sbyte sbyteValue:
                value = sbyteValue;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return int.TryParse(unwrapped?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// Try get named child.
    /// </summary>
    private static bool TryGetNamedChild(object node, string name, out object? child)
    {
        child = null;

        if (TryAsNamedChildren(node, out var children))
        {
            foreach (var (childName, value) in children)
            {
                if (string.Equals(childName, name, StringComparison.OrdinalIgnoreCase))
                {
                    child = value;
                    return true;
                }
            }
        }

        var type = node.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            child = property.GetValue(node);
            return child is not null;
        }

        return false;
    }

    /// <summary>
    /// Try get string.
    /// </summary>
    private static string? TryGetString(object node, string name)
    {
        var value = TryGetMemberValue(node, name);
        return GetScalarString(value);
    }

    /// <summary>
    /// Get int.
    /// </summary>
    private static int? GetInt(object node, string? name)
    {
        var value = name is null ? node : TryGetMemberValue(node, name);
        return value switch
        {
            null => null,
            int intValue => intValue,
            byte byteValue => byteValue,
            short shortValue => shortValue,
            long longValue => (int)longValue,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue) ? parsedValue : null
        };
    }

    /// <summary>
    /// Try get value member.
    /// </summary>
    private static object? TryGetValueMember(object node)
    {
        return TryGetMemberValue(node, "Value")
            ?? TryGetMemberValue(node, "value")
            ?? TryGetMemberValue(node, "Tag")
            ?? TryGetMemberValue(node, "tag")
            ?? TryGetMemberValue(node, "Item")
            ?? TryGetMemberValue(node, "Data")
            ?? TryGetMemberValue(node, "data");
    }

    /// <summary>
    /// Try get member value.
    /// </summary>
    private static object? TryGetMemberValue(object node, string memberName)
    {
        var type = node.GetType();
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            return property.GetValue(node);
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return field?.GetValue(node);
    }

    /// <summary>
    /// Get scalar string.
    /// </summary>
    private static string? GetScalarString(object? value)
    {
        return value switch
        {
            null => null,
            string text => text,
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToString(value, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
    }
}
