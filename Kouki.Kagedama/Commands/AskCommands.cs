using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;

namespace Kouki.Kagedama.Commands;

/// <summary>
/// Вопрос ассистенту.
/// </summary>
/// <param name="Text">Текст вопроса.</param>
/// <param name="TaskId">Задача-запись, созданная приёмом: в неё же попадёт ответ.</param>
public record AskCommand(string? Text = null, string? TaskId = null) : ICommand;

/// <summary>
/// Отвечает на вопрос через <see cref="IAnswerer"/>.
/// </summary>
/// <remarks>
/// Если ответчик не настроен (нет ключа, провайдер выключен) — отвечает
/// <see cref="StubAnswer"/>, а причина попадает в поле <c>stubReason</c>. Если модель
/// отказала, в <c>error</c> будет её текст, а человек увидит короткое объяснение.
/// Ответ пишется и в результат команды, и в мету задачи — см. <c>TaskIntakeService</c>.
/// </remarks>
/// <param name="answerer">Ответчик; может отсутствовать в контейнере.</param>
[Command(
    "ask",
    Description = "Отвечает на вопрос",
    Category = CommandCategory.Navigation,
    Example = "Сколько минут варить пельмени?")]
public sealed class AskHandler(IAnswerer? answerer = null) : ICommandHandler<AskCommand>
{
    /// <summary>Текст заглушки: он же попадает в задачу как ответ, если модели нет.</summary>
    public const string StubAnswer =
        "Отвечать на вопросы я пока не умею: вопрос сохранён и попал в разбор. " +
        "Ответы появятся, когда подключим модель.";

    /// <inheritdoc />
    public async Task<JsonNode> HandleAsync(
        AskCommand command,
        IExecutionContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var payload = new JsonObject
        {
            ["question"] = command.Text,
        };

        if (command.TaskId is not null)
        {
            payload["taskId"] = command.TaskId;
        }

        var question = command.Text?.Trim();
        if (string.IsNullOrEmpty(question) || answerer is null || !answerer.IsConfigured)
        {
            payload["answer"] = StubAnswer;
            payload["stub"] = true;

            var reason = answerer?.UnavailableReason;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                payload["stubReason"] = reason;
            }

            return payload;
        }

        try
        {
            var answer = await answerer.AnswerAsync(question, cancellationToken).ConfigureAwait(false);

            payload["answer"] = answer.Text;
            payload["stub"] = false;
            payload["answerer"] = answer.Signature;
            payload["answerMs"] = answer.ElapsedMs;
        }
        catch (AnswererException ex)
        {
            // Отказ модели — это не пустой ответ: вопрос остаётся в задаче, а человек
            // видит, почему ответа нет. Текст исключения уже человекочитаемый.
            payload["answer"] = $"Ответить не получилось: {ex.Message}";
            payload["stub"] = false;
            payload["error"] = ex.Message;
        }

        return payload;
    }
}
