using System.Text.Json.Nodes;
using Kouki.Kagedama.Contracts;
using Kouki.Mugura.Attributes;
using Kouki.Mugura.Commands;
using Kouki.Mugura.Handlers;
using Kouki.Shimi.Context;
using Kouki.Shimi.ServiceAbstractions;

namespace Kouki.Kagedama.Commands;

/// <summary>
/// Вопрос ассистенту.
/// </summary>
/// <param name="Text">Текст вопроса.</param>
/// <param name="TaskId">Задача-запись, созданная приёмом: в неё же попадёт ответ.</param>
/// <param name="ContinueDialogue">Признак продолжения диалога: учитывать предыдущий вопрос и ответ.</param>
/// <param name="Source">Источник запроса (station, telegram и т.д.) для ключа памяти диалога.</param>
public record AskCommand(
    string? Text = null,
    string? TaskId = null,
    bool ContinueDialogue = false,
    string? Source = null) : ICommand;

/// <summary>
/// Запись одного хода диалога для хранения в <see cref="IStateStore"/>.
/// </summary>
/// <param name="Question">Вопрос пользователя.</param>
/// <param name="Answer">Ответ модели.</param>
/// <param name="At">Время ответа (UTC).</param>
public sealed record LastDialogueTurn(string Question, string Answer, DateTimeOffset At);

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
    Example = "Сколько минут варить пельмени?",
    PhrasesFile = "Phrases/ask.txt")]
public sealed class AskHandler(IAnswerer? answerer = null) : ICommandHandler<AskCommand>
{
    /// <summary>Срок годности памяти предыдущего хода диалога.</summary>
    public static readonly TimeSpan DialogueTtl = TimeSpan.FromMinutes(10);

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

        bool dialogueFound = false;
        AnswerContext answerContext = AnswerContext.Empty;

        if (command.ContinueDialogue && !string.IsNullOrWhiteSpace(command.Source) && context?.StateStore is not null)
        {
            string stateKey = $"dialogue:last:{command.Source.Trim()}";
            var lastTurn = await context.StateStore.GetAsync<LastDialogueTurn>(stateKey, cancellationToken).ConfigureAwait(false);
            if (lastTurn is not null && DateTimeOffset.UtcNow - lastTurn.At <= DialogueTtl)
            {
                dialogueFound = true;
                answerContext = new AnswerContext(lastTurn.Question, lastTurn.Answer);
            }
        }

        payload["dialogueContinue"] = command.ContinueDialogue;
        payload["dialogueFound"] = dialogueFound;

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
            var answer = await answerer.AnswerAsync(question, answerContext, cancellationToken).ConfigureAwait(false);

            payload["answer"] = answer.Text;
            payload["stub"] = false;
            payload["answerer"] = answer.Signature;
            payload["answerMs"] = answer.ElapsedMs;

            if (!string.IsNullOrWhiteSpace(command.Source) && context?.StateStore is not null)
            {
                string stateKey = $"dialogue:last:{command.Source.Trim()}";
                var turn = new LastDialogueTurn(question, answer.Text, DateTimeOffset.UtcNow);
                await context.StateStore.SetAsync(stateKey, turn, cancellationToken).ConfigureAwait(false);
            }
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
