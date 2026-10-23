using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Shimi.Context;

/// <summary>
/// Контекст выполнения: специфичное для окружения «железо» — файловая система, терминал,
/// хранилище состояния — и доступ к любым дополнительным сервисам по типу.
/// </summary>
/// <remarks>
/// <para>
/// Четыре «железных» сервиса объявлены свойствами, потому что нужны почти всегда:
/// <see cref="FileSystem"/>, <see cref="Terminal"/>, <see cref="StateStore"/>.
/// Всё остальное — через <see cref="GetService{T}"/>:
/// </para>
/// <code>
/// IStateStore store = context.GetRequiredService&lt;IStateStore&gt;("кэш последнего ответа");
/// </code>
/// <para>
/// Если у контекста есть <see cref="Services"/>, поиск идёт сначала по объявленным сервисам,
/// затем по провайдеру DI.
/// </para>
/// </remarks>
public interface IExecutionContext
{
    /// <summary>Имя контекста (для идентификации и сообщений об ошибках).</summary>
    string Name { get; }

    /// <summary>Файловая система, специфичная для этого контекста.</summary>
    IFileSystem FileSystem { get; }

    /// <summary>Терминал, специфичный для этого контекста.</summary>
    ITerminal Terminal { get; }

    /// <summary>Хранилище состояния, специфичное для этого контекста.</summary>
    IStateStore StateStore { get; }

    /// <summary>
    /// Провайдер DI, который использует контекст, если он ему известен.
    /// По умолчанию <c>null</c>: контекст живёт и без контейнера.
    /// </summary>
    IServiceProvider? Services => null;

    /// <summary>
    /// Дополнительный сервис по типу: сначала объявленные сервисы, затем <see cref="Services"/>.
    /// Возвращает <c>null</c>, если сервиса нет; если сервис обязателен — используйте
    /// <see cref="ExecutionContextExtensions.GetRequiredService{T}"/>.
    /// </summary>
    /// <typeparam name="T">Тип сервиса.</typeparam>
    T? GetService<T>() where T : class => null;

    /// <summary>
    /// Пробует получить сервис: удобно, когда отсутствие — нормальная ситуация.
    /// </summary>
    /// <param name="service">Найденный сервис или <c>null</c>.</param>
    /// <typeparam name="T">Тип сервиса.</typeparam>
    /// <returns>Найден ли сервис.</returns>
    bool TryGetService<T>(out T? service) where T : class
    {
        service = GetService<T>();
        return service is not null;
    }

    /// <summary>
    /// Типы сервисов, которые контекст объявляет явно. Нужно для диагностики и для
    /// <c>GET /api/context</c>.
    /// </summary>
    IReadOnlyCollection<Type> ServiceTypes => Array.Empty<Type>();
}
