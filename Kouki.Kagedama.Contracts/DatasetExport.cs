using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Выгрузка журнала разметки в датасет для обучения классификатора.
/// </summary>
/// <remarks>
/// Формат — JSON Lines, по одному примеру на строку: его читают и fastText, и простой
/// скрипт на Python, и сама модель через few-shot. Пишем только размеченные записи:
/// без метки пример бесполезен для обучения, а «догадка» модели в датасет попадать не должна.
/// </remarks>
public static class DatasetExport
{
    /// <summary>Сколько примеров отдаём за один запрос.</summary>
    public const int MaxEntries = 5000;

    private static readonly JsonSerializerOptions Serializer = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Строки датасета: текст, интент, источник и вид ввода.</summary>
    /// <param name="entries">Размеченные записи журнала.</param>
    /// <returns>Текст в формате JSON Lines (последняя строка без перевода).</returns>
    public static string ToJsonLines(IEnumerable<ClassificationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.InputText) || string.IsNullOrWhiteSpace(entry.Label))
            {
                continue;
            }

            var line = new JsonObject
            {
                ["text"] = entry.InputText,
                ["intent"] = entry.Label,
                ["source"] = entry.Source,
                ["kind"] = entry.InputKind,
            };

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line.ToJsonString(Serializer));
        }

        return builder.ToString();
    }

    /// <summary>Сколько примеров попадёт в датасет.</summary>
    /// <param name="entries">Записи журнала.</param>
    /// <returns>Число строк с текстом и меткой.</returns>
    public static int Count(IEnumerable<ClassificationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries.Count(static entry => !string.IsNullOrWhiteSpace(entry.InputText)
            && !string.IsNullOrWhiteSpace(entry.Label));
    }
}
