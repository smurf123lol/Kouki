namespace Kouki.Shimi.Context;

/// <summary>
/// Расширения для <see cref="IExecutionContext"/>: явный запрос сервиса с понятной ошибкой.
/// </summary>
public static class ExecutionContextExtensions
{
    /// <summary>
    /// Возвращает сервис или бросает <see cref="InvalidOperationException"/> с описанием,
    /// где именно он понадобился. Такой запрос не маскирует «тихий null» в глубине команды.
    /// </summary>
    /// <typeparam name="T">Тип сервиса.</typeparam>
    /// <param name="context">Контекст.</param>
    /// <param name="usedFor">Короткое пояснение, зачем сервис нужен — попадает в текст ошибки.</param>
    /// <returns>Найденный сервис.</returns>
    public static T GetRequiredService<T>(this IExecutionContext context, string? usedFor = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.TryGetService<T>(out T? service))
        {
            return service!;
        }

        string usage = string.IsNullOrWhiteSpace(usedFor) ? string.Empty : $" ({usedFor})";
        throw new InvalidOperationException(
            $"Контекст '{context.Name}'{usage} не содержит сервис {typeof(T).Name}. " +
            $"Добавьте его через BaseExecutionContext.AddService<{typeof(T).Name}>(...) " +
            "или передайте IServiceProvider в конструктор контекста.");
    }

    /// <summary>
    /// Проверяет, что сервис доступен в контексте, — без исключения и без получения экземпляра.
    /// </summary>
    /// <typeparam name="T">Тип сервиса.</typeparam>
    /// <param name="context">Контекст.</param>
    public static bool HasService<T>(this IExecutionContext context) where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetService<T>() is not null;
    }

    /// <summary>
    /// Имена объявленных сервисов — для логов, диагностики и <c>GET /api/context</c>.
    /// </summary>
    /// <param name="context">Контекст.</param>
    public static IReadOnlyList<string> DescribeServices(this IExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.ServiceTypes
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .Select(type => type.Name)
            .ToList();
    }
}
