using Kouki.Mugura.Commands;
using Kouki.Mugura.Registry;
using System.Text.Json.Nodes;

namespace Kouki.Mugura.Factories;

/// <summary>
/// Фабрика DTO команды из JSON-аргумента.
/// </summary>
public interface ICommandFactory
{
    /// <summary>
    /// Создаёт DTO команды из <paramref name="argument"/>. При ошибке возвращает <c>null</c>.
    /// </summary>
    ICommand? Create(CommandEntry entry, JsonNode? argument);

    /// <summary>
    /// Создаёт DTO команды из <paramref name="argument"/> и сообщает причину ошибки без исключений.
    /// </summary>
    /// <param name="entry">Запись команды из реестра.</param>
    /// <param name="argument">JSON-аргумент команды (может быть <c>null</c> — тогда используется пустой объект).</param>
    /// <param name="command">Созданный DTO или <c>null</c>.</param>
    /// <param name="error">Текст ошибки или <c>null</c>.</param>
    /// <returns><c>true</c>, если DTO создан.</returns>
    bool TryCreate(CommandEntry entry, JsonNode? argument, out ICommand? command, out string? error);
}
