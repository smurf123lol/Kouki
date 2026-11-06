using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kouki.IntentDataset;

/// <summary>Пишет датасет в JSONL: одна запись на строку, кириллица без \u-экранирования.</summary>
public static class DatasetWriter
{
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Пишет записи в файл, создавая каталог при необходимости.</summary>
    public static void Write(string path, IReadOnlyList<DatasetRecord> records)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (DatasetRecord record in records)
        {
            // Словарь, а не анонимный тип: порядок ключей в строке фиксирован спецификацией.
            var line = new Dictionary<string, object>(4, StringComparer.Ordinal)
            {
                ["text"] = record.Text,
                ["label"] = record.Label,
                ["source"] = record.Source,
                ["weight"] = record.Weight,
            };

            stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, LineOptions)));
            stream.WriteByte((byte)'\n');
        }
    }
}
