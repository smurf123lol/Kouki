namespace Kouki.Kagedama.Contracts;

/// <summary>
/// Виды действий: что именно делал хост.
/// </summary>
public static class ActionKinds
{
    /// <summary>Обработка входящей фразы: распознавание и решение, что с ней делать.</summary>
    public const string Intake = "intake";

    /// <summary>Вызов команды (может быть частью приёма или прямым вызовом).</summary>
    public const string Command = "command";
}

/// <summary>
/// Итоги действия — те же значения, что у приёма задач, чтобы отладка не переводила словари.
/// </summary>
/// <remarks>
/// Словарь один на весь домен: приём фразы, журнал действий и ответ API используют
/// эти строки. Второго «словаря исходов» быть не должно — иначе дашборд и MCP
/// начинают сравнивать разные значения.
/// </remarks>
public static class ActionOutcomes
{
    /// <summary>Задача создана.</summary>
    public const string Created = "created";

    /// <summary>Команда исполнена.</summary>
    public const string Executed = "executed";

    /// <summary>Фраза отправлена на разбор.</summary>
    public const string NeedsReview = "needs_review";

    /// <summary>Действие завершилось ошибкой.</summary>
    public const string Failed = "failed";

    /// <summary>Все известные исходы.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Created, Executed, NeedsReview, Failed };

    /// <summary>
    /// Известен ли исход. Нужен при чтении чужих данных (журнал, база после обновления).
    /// </summary>
    /// <param name="outcome">Проверяемое значение.</param>
    public static bool IsKnown(string? outcome)
        => outcome is Created or Executed or NeedsReview or Failed;
}

/// <summary>
/// Запись о выполненном действии: для отладки и для истории задачи.
/// Хранит ссылки на задачу и на запись журнала распознавания.
/// </summary>
/// <param name="Id">Идентификатор действия.</param>
/// <param name="CreatedAt">Момент выполнения (ISO 8601).</param>
/// <param name="TaskId">Связанная задача, если действие её создало или изменило.</param>
/// <param name="ClassificationId">Запись журнала распознавания, по которой принято решение.</param>
/// <param name="Source">Источник: telegram, voice, dashboard, mcp, api.</param>
/// <param name="Kind">Вид действия: см. <see cref="ActionKinds"/>.</param>
/// <param name="Intent">Распознанный интент.</param>
/// <param name="Command">Команда, если исполнялась.</param>
/// <param name="Outcome">Итог: см. <see cref="ActionOutcomes"/>.</param>
/// <param name="ArgsJson">Аргументы команды в JSON.</param>
/// <param name="ResultJson">Ответ команды в JSON.</param>
/// <param name="Error">Текст ошибки, если была.</param>
/// <param name="DurationMs">Длительность в миллисекундах.</param>
public sealed record ActionRecord(
    Guid Id,
    string CreatedAt,
    Guid? TaskId,
    Guid? ClassificationId,
    string Source,
    string Kind,
    string? Intent,
    string? Command,
    string Outcome,
    string? ArgsJson = null,
    string? ResultJson = null,
    string? Error = null,
    long? DurationMs = null);

/// <summary>
/// Журнал действий.
/// </summary>
public interface IActionLog
{
    /// <summary>Записывает действие и возвращает его идентификатор.</summary>
    /// <param name="record">Запись о действии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<Guid> RecordAsync(ActionRecord record, CancellationToken cancellationToken = default);

    /// <summary>Последние действия — все или только по задаче.</summary>
    /// <param name="limit">Сколько записей вернуть (1…500).</param>
    /// <param name="taskId">Задача, историю которой нужно показать, или <c>null</c> для всех.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<ActionRecord>> ListAsync(int limit, Guid? taskId = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Журнал-пустышка: нужен там, где хранилища нет (тесты, консоль).
/// </summary>
public sealed class NullActionLog : IActionLog
{
    /// <summary>Единственный экземпляр.</summary>
    public static NullActionLog Instance { get; } = new();

    /// <inheritdoc />
    public Task<Guid> RecordAsync(ActionRecord record, CancellationToken cancellationToken = default)
        => Task.FromResult(Guid.Empty);

    /// <inheritdoc />
    public Task<IReadOnlyList<ActionRecord>> ListAsync(int limit, Guid? taskId = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActionRecord>>(Array.Empty<ActionRecord>());
}
