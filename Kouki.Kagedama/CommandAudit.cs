using System.Diagnostics;
using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;

namespace Kouki.Kagedama;

/// <summary>
/// Запись в журнал действий из команд, которые вызывают не через приём фразы
/// (например <c>PATCH /api/tasks</c> или MCP-инструмент).
/// </summary>
/// <remarks>
/// Приём фразы пишет собственные записи; здесь — только прямые вызовы команд,
/// чтобы изменение задачи не оставалось без следа. Сбой журнала не ломает команду:
/// журнал вспомогательный.
/// </remarks>
internal static class CommandAudit
{
    public static async Task RecordAsync(
        IActionLog? log,
        string source,
        string command,
        Guid? taskId,
        JsonNode? args,
        JsonNode? result,
        string? error,
        CancellationToken cancellationToken)
    {
        if (log is null)
        {
            return;
        }

        var record = new ActionRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.ToString("O"),
            taskId,
            null,
            source,
            ActionKinds.Command,
            null,
            command,
            error is null ? ActionOutcomes.Executed : ActionOutcomes.Failed,
            args?.ToJsonString(),
            result?.ToJsonString(),
            error,
            0);

        try
        {
            await log.RecordAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Журнал не должен превращать успешную команду в ошибку.
        }
    }
}
