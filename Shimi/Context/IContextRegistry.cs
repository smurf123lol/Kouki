using System.Diagnostics.CodeAnalysis;

namespace Kouki.Shimi.Context;

/// <summary>
/// Реестр предопределённых контекстов (файловая система, терминал, состояние, сервисы).
/// Реализация обязана быть потокобезопасной: регистрация и выбор контекста
/// по умолчанию могут происходить из разных потоков.
/// </summary>
public interface IContextRegistry
{
    /// <summary>Получить контекст по имени; если не найден — контекст по умолчанию (может быть <c>null</c>).</summary>
    IExecutionContext? GetContext(string name);

    /// <summary>Зарегистрировать контекст под именем.</summary>
    void RegisterContext(string name, IExecutionContext context);

    /// <summary>Сделать контекст контекстом по умолчанию.</summary>
    void SetDefaultContext(IExecutionContext context);

    /// <summary>Сделать контекст по умолчанию по имени; бросает, если имя не зарегистрировано.</summary>
    void SetDefaultContext(string context);

    /// <summary>Получить контекст по умолчанию (может быть <c>null</c>, если ничего не задано).</summary>
    IExecutionContext? GetDefaultContext();

    /// <summary>
    /// Попробовать получить контекст по умолчанию. Предпочтительный способ: не требует
    /// отдельной проверки на <c>null</c> и работает с анализом nullable-ссылок.
    /// </summary>
    bool TryGetDefaultContext([NotNullWhen(true)] out IExecutionContext? context);
}
