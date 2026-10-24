namespace Kouki.Kagedama;

/// <summary>
/// Настройки приёма задач: порог уверенности и судьба журнала действий.
/// </summary>
/// <remarks>
/// Хосты (HTTP-сервер, MCP, дашборд) читают свои конфиги и передают сюда
/// только то, что нужно прикладному слою, — поэтому тип не зависит
/// от <c>Microsoft.Extensions.Options</c>.
/// </remarks>
public sealed class IntakeOptions
{
    /// <summary>
    /// Значение по умолчанию для <see cref="ConfidenceThreshold"/>.
    /// </summary>
    public const double DefaultConfidenceThreshold = 0.75d;

    /// <summary>
    /// Минимальная уверенность распознавания, при которой фраза уходит в диспатчер.
    /// Ниже порога задача создаётся со статусом «на разборе».
    /// </summary>
    public double ConfidenceThreshold { get; set; } = DefaultConfidenceThreshold;

    /// <summary>
    /// Источник по умолчанию, если хост его не передал.
    /// </summary>
    public string DefaultSource { get; set; } = "api";

    /// <summary>
    /// Падать ли, если не удалось записать журнал действий.
    /// По умолчанию нет: отладочный журнал не должен ронять приём фразы.
    /// </summary>
    public bool ActionFailuresAreFatal { get; set; }

    /// <summary>
    /// Интенты, для которых приём сам создаёт задачу-запись, даже если её создаёт не команда:
    /// вопрос (<c>ask</c>) сохраняется вместе с ответом, чтобы его было где искать.
    /// </summary>
    /// <remarks>
    /// Список намеренно короткий: всё остальное либо создаёт задачу командой (<c>add_task</c>),
    /// либо не создаёт её вовсе (<c>list_tasks</c>, <c>complete_task</c>).
    /// </remarks>
    public IReadOnlyCollection<string> RecordedIntents { get; set; } = new[] { "ask" };
}
