using System.Text.Json;
using IncidentCompass.Application.Core.Serialization;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class MemoryWorkerOutputParser
{
    public static MemoryWorkerOutput Parse(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = JsonElementReader.RequireObject(
            document.RootElement,
            "Memory worker output must be an object.",
            CreateException);
        if (!root.TryGetProperty("matched", out var matchedElement) ||
            matchedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException("Memory worker output is missing boolean matched.");
        }

        var items = ReadItems(root);
        var matched = matchedElement.GetBoolean();
        var noMatchReason = JsonElementReader.ReadOptionalString(root, "noMatchReason", CreateException);
        return new MemoryWorkerOutput(matched, items, noMatchReason);
    }

    private static List<MemoryWorkerOutputItem> ReadItems(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var itemsElement) ||
            itemsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Memory worker output is missing items array.");
        }

        var items = new List<MemoryWorkerOutputItem>();
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            var item = JsonElementReader.RequireObject(
                itemElement,
                "Memory worker output items must be objects.",
                CreateException);
            items.Add(new MemoryWorkerOutputItem(
                ReadMemoryString(item, "artifactId"),
                ReadMemoryString(item, "title"),
                JsonElementReader.ReadOptionalString(item, "quote", CreateException) ?? string.Empty,
                ReadOptionalDouble(item, "score"),
                JsonElementReader.ReadOptionalString(item, "documentationStatus", CreateException)));
        }

        return items;
    }

    private static string ReadMemoryString(JsonElement root, string propertyName)
    {
        return JsonElementReader.ReadRequiredString(
            root,
            propertyName,
            $"Memory worker output is missing string {propertyName}.",
            CreateException,
            trim: false);
    }

    private static double? ReadOptionalDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static Exception CreateException(string message)
    {
        return new InvalidOperationException(message);
    }
}
