using System.Text.Json.Nodes;
using Kouki.Shimi.Context;

namespace Kouki.Yasabi;

/// <summary>
/// Результат маршрутизации текста: что поняли, что исполнили и почему нет.
/// </summary>
/// <param name="Intent">Вердикт классификатора.</param>
/// <param name="CommandName">Команда, в которую превратился интент.</param>
/// <param name="Result">Ответ команды, если она исполнялась.</param>
/// <param name="Executed">Была ли исполнена команда.</param>
/// <param name="Error">Причина, по которой команда не исполнена.</param>
public sealed record IntentRoute(
    IntentResult Intent,
    string? CommandName,
    JsonNode? Result,
    bool Executed,
    string? Error);

/// <summary>
/// Точка входа для естественного языка: текст → интент → команда → маршрут.
/// Не является реализацией <c>IRouter</c>: у того в контракте имя команды,
/// а не свободная фраза.
/// </summary>
public interface IIntentRouter
{
    /// <summary>
    /// Распознаёт фразу и, если уверенности достаточно, исполняет команду.
    /// </summary>
    /// <param name="text">Фраза пользователя.</param>
    /// <param name="context">Контекст исполнения; при null берётся контекст по умолчанию.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IntentRoute> RouteAsync(string text, IExecutionContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Исполняет уже распознанный интент. Нужен хосту, который классифицирует фразу
    /// сам (например, чтобы записать её в журнал ровно один раз и подставить свои слоты).
    /// </summary>
    /// <param name="intent">Вердикт классификатора.</param>
    /// <param name="context">Контекст исполнения; при null берётся контекст по умолчанию.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IntentRoute> RouteAsync(IntentResult intent, IExecutionContext? context = null, CancellationToken cancellationToken = default);
}
