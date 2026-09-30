namespace Kouki.Mugura.Scope;
/// <remarks>
/// <strong>TODO:</strong> Добавить параметр configureScope для передачи контекста в скоуп.
/// Это позволит роутерам настраивать Scoped-сервисы (например, IContext) до резолва хендлера.
/// </remarks>
public interface IScopeManager
{
    Task<T> RunInScopeAsync<T>(Func<IServiceProvider, Task<T>> action);
}