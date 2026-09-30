using Shimi.Context;

/// <summary>
/// Реестр предопределённых контекстов.
/// </summary>
public interface IContextRegistry
{
    /// <summary>Получить контекст по имени (если не найден — возвращает default).</summary>
    IExecutionContext GetContext(string name);

    /// <summary>Зарегистрировать контекст.</summary>
    void RegisterContext(string name, IExecutionContext context);

    /// <summary>Установить контекст по умолчанию.</summary>
    void SetDefaultContext(IExecutionContext context);
    void SetDefaultContext(string context);

    /// <summary>Получить контекст по умолчанию.</summary>
    IExecutionContext GetDefaultContext();
}