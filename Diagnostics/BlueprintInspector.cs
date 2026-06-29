using System.Collections;
using System.Reflection;
using SharpNBT.SNBT;

/// <summary>
/// Provides a lightweight inspection mode for printing NBT or SNBT node structure.
/// </summary>
internal static class BlueprintInspector
{
    /// <summary>
    /// Loads and prints a file structure preview for quick diagnostics.
    /// </summary>
    public static async Task RunAsync(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File does not exist: {path}");
            return;
        }

        object root = Path.GetExtension(path).Equals(".snbt", StringComparison.OrdinalIgnoreCase)
            ? StringNbt.Parse(await File.ReadAllTextAsync(path))
            : ReadBinary(path);

        PrintNode(root, 0, 2);
    }

    /// <summary>
    /// Attempts binary NBT parsing with known format options.
    /// </summary>
    private static object ReadBinary(string path)
    {
        foreach (var options in new[] { SharpNBT.FormatOptions.Java, SharpNBT.FormatOptions.BedrockFile, SharpNBT.FormatOptions.BedrockNetwork })
        {
            try
            {
                return SharpNBT.NbtFile.Read(path, options, SharpNBT.CompressionType.AutoDetect);
            }
            catch
            {
            }
        }

        throw new InvalidDataException($"Unable to parse binary NBT file: {path}");
    }

    /// <summary>
    /// Prints the current node and a bounded preview of descendants.
    /// </summary>
    private static void PrintNode(object? node, int depth, int maxDepth)
    {
        if (node is null || depth > maxDepth)
        {
            return;
        }

        var indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}{node.GetType().FullName}");

        if (node.GetType().Namespace == "SharpNBT"
            && node.GetType().Name is not "CompoundTag"
            && node.GetType().Name is not "ListTag")
        {
            return;
        }

        if (depth == 0)
        {
            foreach (var iface in node.GetType().GetInterfaces())
            {
                Console.WriteLine($"{indent}  iface: {iface.FullName}");
            }
        }

        if (node is IEnumerable enumerable)
        {
            var previewIndex = 0;
            foreach (var item in enumerable)
            {
                if (previewIndex++ >= 32)
                {
                    break;
                }

                var keyProperty = item?.GetType().GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                var valueProperty = item?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (keyProperty is not null && valueProperty is not null)
                {
                    var key = keyProperty.GetValue(item);
                    var value = valueProperty.GetValue(item);
                    Console.WriteLine($"{indent}  enum: {key} => {value?.GetType().FullName ?? "null"}");
                }
                else
                {
                    Console.WriteLine($"{indent}  enum: {item?.GetType().FullName ?? "null"}");
                }
            }
        }

        foreach (var child in EnumerateChildren(node).Take(12))
        {
            Console.WriteLine($"{indent}- {child.Name}: {child.Value?.GetType().FullName ?? "null"}");
            if (depth == 0 && string.Equals(child.Name, "palette", StringComparison.OrdinalIgnoreCase) && child.Value is IEnumerable paletteEnumerable)
            {
                var paletteIndex = 0;
                foreach (var paletteItem in paletteEnumerable)
                {
                    if (paletteIndex++ >= 4)
                    {
                        break;
                    }

                    Console.WriteLine($"{indent}  palette-item: {paletteItem?.GetType().FullName ?? "null"}");
                    if (paletteItem is not null)
                    {
                        foreach (var subChild in EnumerateChildren(paletteItem).Take(8))
                        {
                            Console.WriteLine($"{indent}    {subChild.Name}: {subChild.Value?.GetType().FullName ?? "null"}");
                        }
                    }
                }
            }
            if (depth > 0 && depth < maxDepth && child.Value is not null && !string.Equals(child.Name, "palette", StringComparison.OrdinalIgnoreCase))
            {
                PrintNode(child.Value, depth + 1, maxDepth);
            }
        }
    }

    /// <summary>
    /// Enumerates readable child values from dictionaries and public properties.
    /// </summary>
    private static IEnumerable<(string Name, object? Value)> EnumerateChildren(object node)
    {
        if (node is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not null)
                {
                    yield return (entry.Key.ToString()!, entry.Value);
                }
            }

            yield break;
        }

        var properties = node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0);

        foreach (var property in properties)
        {
            object? value;
            try
            {
                value = property.GetValue(node);
            }
            catch
            {
                continue;
            }

            yield return (property.Name, value);
        }
    }
}
