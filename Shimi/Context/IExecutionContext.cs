using Shimi.ServiceAbstractions;

namespace Shimi.Context;

/// <summary>
/// Контекст выполнения, содержащий специфичные для окружения реализации сервисов.
/// </summary>
public interface IExecutionContext
{
    /// <summary>Имя контекста (для идентификации).</summary>
    string Name { get; }

    /// <summary>Файловая система, специфичная для этого контекста.</summary>
    IFileSystem FileSystem { get; }

    /// <summary>Терминал, специфичный для этого контекста.</summary>
    ITerminal Terminal { get; }

    /// <summary>Хранилище состояния, специфичное для этого контекста.</summary>
    IStateStore StateStore { get; }

    /// <summary>Дополнительные сервисы можно получать по типу (опционально).</summary>
    T? GetService<T>() where T : class;
}