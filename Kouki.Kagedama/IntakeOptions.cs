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
    /// Что делать с тегами во фразе: <see cref="TagModes.Extract"/> вырезает и сохраняет,
    /// <see cref="TagModes.Keep"/> оставляет их частью формулировки.
    /// </summary>
    public string TagMode { get; set; } = TagModes.Extract;

    /// <summary>
    /// Режим тегов на каждый источник: ключ — <see cref="Kouki.Kagedama.Contracts.TaskSources"/>,
    /// значение — <see cref="TagModes"/>. Источник без переопределения берёт <see cref="TagMode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> TagModeBySource { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Режим тегов для конкретного источника: переопределение, иначе общий <see cref="TagMode"/>.
    /// </summary>
    /// <remarks>
    /// Режим живёт на источник, потому что «#» — часть формулировки не везде: со станции
    /// человек диктует метку голосом, а в API решётка чаще всего именно тег.
    /// </remarks>
    /// <param name="source">Источник фразы.</param>
    /// <returns>Режим тегов, см. <see cref="TagModes"/>.</returns>
    public string ResolveTagMode(string? source)
    {
        if (!string.IsNullOrWhiteSpace(source) && TagModeBySource.TryGetValue(source, out string? mode))
        {
            return mode;
        }

        return TagMode;
    }

    /// <summary>
    /// Назначать теги модели в фоновом режиме (асинхронно), не задерживая отдачу ответа клиенту.
    /// По умолчанию false (для синхронных тестов); в KoukiHosting включается в true.
    /// </summary>
    public bool AsyncModelTagging { get; set; } = false;

    /// <summary>
    /// Падать ли, если не удалось записать журнал действий.
    /// По умолчанию нет: отладочный журнал не должен ронять приём фразы.
    /// </summary>
    public bool ActionFailuresAreFatal { get; set; }

    /// <summary>
    /// Интенты, для которых приём сам создаёт задачу-запись (в T105 вопросы вынесены в IQuestionStore, список по умолчанию пуст).
    /// </summary>
    public IReadOnlyCollection<string> RecordedIntents { get; set; } = Array.Empty<string>();
}
