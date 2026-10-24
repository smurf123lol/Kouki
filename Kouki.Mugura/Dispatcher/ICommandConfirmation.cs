using Kouki.Mugura.Commands;
using Kouki.Mugura.Registry;
using Kouki.Shimi.Context;

namespace Kouki.Mugura.Dispatcher;

/// <summary>
/// Подтверждение опасных команд.
/// </summary>
/// <remarks>
/// <para>
/// Команда, помеченная <c>RequiresConfirmation = true</c>, меняет состояние — удаляет файлы,
/// запускает процессы, пишет в базу. Такие команды нельзя исполнять «просто потому, что фраза
/// похожа»: между разбором фразы и действием нужен человек.
/// </para>
/// <para>
/// Реализация решает, как спросить. Сервер по умолчанию отказывает (см.
/// <see cref="RejectAllConfirmation"/>): лучше вернуть «требуется подтверждение», чем
/// выполнить опасное действие без согласия.
/// </para>
/// </remarks>
public interface ICommandConfirmation
{
    /// <summary>
    /// Спрашивает подтверждение на исполнение команды.
    /// </summary>
    /// <param name="entry">Запись команды (имя, описание, категория).</param>
    /// <param name="command">Созданный аргумент команды.</param>
    /// <param name="context">Контекст исполнения: интерфейс может спросить через него.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns><c>true</c>, если исполнять можно.</returns>
    Task<bool> ConfirmAsync(
        CommandEntry entry,
        ICommand command,
        IExecutionContext? context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Поведение по умолчанию: опасные команды не исполняются.
/// </summary>
/// <remarks>
/// Хост, который умеет спрашивать человека (диалог в приложении, кнопка в интерфейсе,
/// подтверждение в чате), регистрирует свою реализацию до <c>AddMugura</c>.
/// </remarks>
public sealed class RejectAllConfirmation : ICommandConfirmation
{
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(
        CommandEntry entry,
        ICommand command,
        IExecutionContext? context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// Разрешает всё: для сценариев, где подтверждение уже получено выше
/// (например, пользователь нажал «да» в диалоге до вызова роутера).
/// </summary>
public sealed class AllowAllConfirmation : ICommandConfirmation
{
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(
        CommandEntry entry,
        ICommand command,
        IExecutionContext? context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}
