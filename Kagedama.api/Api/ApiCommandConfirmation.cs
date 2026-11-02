using Kouki.Mugura.Commands;
using Kouki.Mugura.Dispatcher;
using Kouki.Mugura.Registry;
using Kouki.Shimi.Context;

namespace Kouki.Kagedama.Api.Api;

/// <summary>
/// Подтверждение опасных команд в HTTP-хосте API: только то, что разрешено явно.
/// </summary>
/// <remarks>
/// <para>
/// Эндпоинт удаления сам проверяет <c>confirm=true</c> до вызова роутера (§2.5); эта
/// реализация — второй пояс: если опасную команду добавят в другой эндпоинт, роутер
/// откажет, а не выполнит её молча.
/// </para>
/// <para>
/// <c>delete_task_detach</c> разрешён всегда: спека требует подтверждение только для
/// каскада. <c>RequiresConfirmation</c> на команде остаётся, чтобы MCP и бот могли
/// спросить подтверждение по-своему (сейчас у них <c>RejectAllConfirmation</c>).
/// Все остальные опасные команды в API запрещены.
/// </para>
/// </remarks>
public sealed class ApiCommandConfirmation : ICommandConfirmation
{
    /// <summary>Имя команды, которой нужно явное <c>confirm=true</c> в запросе.</summary>
    private const string CascadeCommand = "delete_task_cascade";

    /// <summary>Имя команды, которой подтверждение не нужно по спеке.</summary>
    private const string DetachCommand = "delete_task_detach";

    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Создаёт реализацию, читающую подтверждение из текущего HTTP-запроса.
    /// </summary>
    /// <param name="httpContextAccessor">Доступ к текущему запросу.</param>
    public ApiCommandConfirmation(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor
            ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(
        CommandEntry entry,
        ICommand command,
        IExecutionContext? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Task.FromResult(entry.Name switch
        {
            CascadeCommand => IsConfirmedInCurrentRequest(),
            DetachCommand => true,
            _ => false,
        });
    }

    /// <summary>
    /// Читает <c>confirm</c> из query текущего запроса. Без <see cref="HttpContext"/>
    /// (фоновая обработка, другой транспорт) подтверждения нет — fail-closed.
    /// </summary>
    private bool IsConfirmedInCurrentRequest()
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null)
        {
            return false;
        }

        string? confirm = httpContext.Request.Query["confirm"];
        return bool.TryParse(confirm, out bool confirmed) && confirmed;
    }
}
