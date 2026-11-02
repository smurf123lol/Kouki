using Kouki.Kagedama.Api.Api;
using Kouki.Kagedama.Commands;
using Kouki.Mugura.Registry;

using Microsoft.AspNetCore.Http;

namespace Kouki.Tests;

/// <summary>
/// Подтверждение опасных команд в API-хосте: cascade — только по confirm=true,
/// detach — без подтверждения, всё остальное — отказ (fail-closed).
/// </summary>
public sealed class ApiCommandConfirmationTests
{
    private static readonly CommandRegistry Registry = new(new[] { typeof(CancelReminderHandler).Assembly });

    [Fact]
    public async Task Cascade_without_confirm_is_rejected()
    {
        ApiCommandConfirmation confirmation = Create(query: null);
        CommandEntry entry = Entry("delete_task_cascade");

        Assert.True(entry.RequiresConfirmation);
        Assert.False(await confirmation.ConfirmAsync(
            entry,
            new DeleteTaskCascadeCommand(Guid.NewGuid().ToString()),
            context: null));
    }

    [Fact]
    public async Task Cascade_with_confirm_true_is_allowed()
    {
        ApiCommandConfirmation confirmation = Create("?confirm=true");
        CommandEntry entry = Entry("delete_task_cascade");

        Assert.True(await confirmation.ConfirmAsync(
            entry,
            new DeleteTaskCascadeCommand(Guid.NewGuid().ToString()),
            context: null));
    }

    [Fact]
    public async Task Detach_without_confirm_is_allowed()
    {
        ApiCommandConfirmation confirmation = Create(query: null);
        CommandEntry entry = Entry("delete_task_detach");

        Assert.True(entry.RequiresConfirmation);
        Assert.True(await confirmation.ConfirmAsync(
            entry,
            new DeleteTaskDetachCommand(Guid.NewGuid().ToString()),
            context: null));
    }

    [Fact]
    public async Task Other_dangerous_command_is_rejected()
    {
        ApiCommandConfirmation confirmation = Create("?confirm=true");
        CommandEntry entry = Entry("cancel_reminder");

        Assert.True(entry.RequiresConfirmation);
        Assert.False(await confirmation.ConfirmAsync(
            entry,
            new CancelReminderCommand(Guid.NewGuid()),
            context: null));
    }

    [Fact]
    public async Task Cascade_without_http_context_is_rejected()
    {
        var confirmation = new ApiCommandConfirmation(new StubHttpContextAccessor());
        CommandEntry entry = Entry("delete_task_cascade");

        Assert.False(await confirmation.ConfirmAsync(
            entry,
            new DeleteTaskCascadeCommand(Guid.NewGuid().ToString()),
            context: null));
    }

    private static ApiCommandConfirmation Create(string? query)
    {
        var httpContext = new DefaultHttpContext();
        if (query is not null)
        {
            httpContext.Request.QueryString = new QueryString(query);
        }

        return new ApiCommandConfirmation(new StubHttpContextAccessor { HttpContext = httpContext });
    }

    private static CommandEntry Entry(string name)
        => Registry.GetEntry(name)
            ?? throw new InvalidOperationException($"Команда '{name}' не зарегистрирована.");

    /// <summary>Поддельный доступ к запросу: <c>null</c> — запроса нет.</summary>
    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
