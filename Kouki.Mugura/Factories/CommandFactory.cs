using Kouki.Mugura.Commands;
using Kouki.Mugura.Registry;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Factories;

/// <summary>
/// Реализация <see cref="ICommandFactory"/>, создающая DTO команды из <see cref="JsonNode"/>
/// через <see cref="JsonSerializer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Если аргумент равен <c>null</c>, используется пустой <see cref="JsonObject"/>,
/// поэтому DTO без обязательных параметров конструктора создаётся успешно.
/// </para>
/// <para>
/// Ошибки десериализации не выбрасываются наружу: <see cref="TryCreate"/> возвращает <c>false</c>
/// и текст ошибки, пригодный для ответа клиенту.
/// </para>
/// <para>
/// <see cref="JsonSerializerOptions"/> статический и общий для всех вызовов — System.Text.Json кеширует
/// метаданные типов внутри экземпляра опций, поэтому повторная сериализация того же DTO не платит за рефлексию.
/// </para>
/// </remarks>
public class CommandFactory : ICommandFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <inheritdoc />
    public ICommand? Create(CommandEntry entry, JsonNode? argument)
        => TryCreate(entry, argument, out var command, out _) ? command : null;

    /// <inheritdoc />
    public bool TryCreate(CommandEntry entry, JsonNode? argument, out ICommand? command, out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            var node = argument ?? new JsonObject();
            var result = JsonSerializer.Deserialize(node, entry.CommandType, SerializerOptions);

            if (result is ICommand typed)
            {
                command = typed;
                error = null;
                return true;
            }

            command = null;
            error = $"Argument for command '{entry.Name}' must describe a '{entry.CommandType.Name}' object.";
            return false;
        }
        catch (JsonException ex)
        {
            command = null;
            error = $"Invalid argument for command '{entry.Name}': {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            command = null;
            error = $"Command '{entry.Name}' cannot be created from JSON: {ex.Message}";
            return false;
        }
    }
}
