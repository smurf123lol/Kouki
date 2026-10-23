namespace Kouki.Mugura.Scope;

/// <summary>
/// Создаёт DI-скоуп на время выполнения действия (например, вызова команды).
/// </summary>
public interface IScopeManager
{
    /// <summary>Выполнить <paramref name="action"/> внутри нового DI-скоупа.</summary>
    Task<T> RunInScopeAsync<T>(Func<IServiceProvider, Task<T>> action);
}
