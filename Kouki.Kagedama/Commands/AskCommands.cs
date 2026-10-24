using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;

namespace Kouki.Kagedama.Commands;

/// <summary>
/// Вопрос, на который ассистент пока не умеет отвечать.
/// </summary>
/// <param name="Text">Текст вопроса.</param>
/// <param name="TaskId">Задача-запись, созданная приёмом: в неё же попадёт ответ.</param>
public record AskCommand(string? Text = null, string? TaskId = null) : ICommand;

/// <summary>
/// Заглушка ответа на вопрос.
/// </summary>
/// <remarks>
/// Команда существует с самого начала намеренно: когда появится настоящий ответчик
/// (локальная модель или внешний сервис), он подменит только тело <c>answer</c>,
/// а маршрут «вопрос → команда <c>ask</c> → ответ в задаче» останется тем же.
/// Ответ пишется и в результат команды, и в мету задачи — см. <c>TaskIntakeService</c>.
/// </remarks>
[Command(
    "ask",
    Description = "Отвечает на вопрос (пока заглушкой)",
    Category = CommandCategory.Navigation,
    Example = "Сколько минут варить пельмени?")]
public sealed class AskHandler : ICommandHandler<AskCommand>
{
    /// <summary>Текст заглушки: он же попадает в задачу как ответ.</summary>
    public const string StubAnswer =
        "Отвечать на вопросы я пока не умею: вопрос сохранён и попал в разбор. " +
        "Ответы появятся, когда подключим модель.";

    /// <inheritdoc />
    public Task<JsonNode> HandleAsync(
        AskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var payload = new JsonObject
        {
            ["answer"] = StubAnswer,
            ["question"] = command.Text,
            ["stub"] = true,
        };

        if (command.TaskId is not null)
        {
            payload["taskId"] = command.TaskId;
        }

        return Task.FromResult<JsonNode>(payload);
    }
}
