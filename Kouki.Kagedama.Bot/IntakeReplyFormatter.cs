using System.Text.Json.Nodes;

namespace Kouki.Kagedama.Bot;

/// <summary>
/// Текст ответа человеку в Telegram: коротко, по-русски, без JSON.
/// </summary>
public static class IntakeReplyFormatter
{
    private const int TaskListLimit = 10;

    /// <summary>
    /// Собирает ответ по вердикту сервера.
    /// </summary>
    /// <param name="response">Ответ приёма задач.</param>
    public static string Format(IntakeResponseDto response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Task is not null)
        {
            return FormatTask(response);
        }

        return FormatCommand(response);
    }

    /// <summary>
    /// Переводит статус задачи в человекочитаемый вид.
    /// </summary>
    /// <param name="status">Статус из сервера.</param>
    public static string StatusRu(string? status) => status switch
    {
        "new" => "новая",
        "in_progress" => "в работе",
        "done" => "выполнена",
        "cancelled" => "отменена",
        "needs_review" => "на разборе",
        _ => status ?? "неизвестно",
    };

    /// <summary>
    /// Текст ответа, когда сервер не принял фразу. Без HTTP-жаргона и без
    /// текста исключения: человеку нужно понять, что делать.
    /// </summary>
    /// <param name="failure">Причина отказа.</param>
    /// <param name="statusCode">HTTP-код (0 — ответа не было).</param>
    public static string FormatFailure(IntakeFailure failure, int statusCode) => failure switch
    {
        IntakeFailure.Unauthorized =>
            "Сервер Kouki не принял токен бота. Проверьте KOUKI_SERVER_TOKEN — он должен совпадать с Kouki__Token на сервере.",
        IntakeFailure.ServerUnavailable =>
            "Сервер Kouki недоступен. Проверьте, что он запущен и адрес в KOUKI_SERVER_URL верный; эту фразу можно повторить позже.",
        IntakeFailure.ServerError =>
            $"Сервер Kouki ответил ошибкой {statusCode}. Фраза не сохранена — попробуйте ещё раз позже.",
        IntakeFailure.BadRequest =>
            $"Сервер Kouki отклонил фразу (HTTP {statusCode}). Скорее всего она слишком длинная или пустая.",
        _ =>
            "Сервер Kouki ответил чем-то непонятным. Фраза не сохранена — попробуйте ещё раз.",
    };

    private static string FormatTask(IntakeResponseDto response)
    {
        var lines = new List<string>
        {
            $"Задача: {response.Task!.Title}",
            $"Статус: {StatusRu(response.Task.Status)}",
        };

        if (!string.IsNullOrWhiteSpace(response.Command))
        {
            lines.Add($"Команда: {response.Command}");
        }

        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            lines.Add($"Замечание: {response.Error}");
        }
        else if (response.Intent is { Confident: false })
        {
            lines.Add("Понял неуверенно — отправил на разбор.");
        }

        return string.Join('\n', lines);
    }

    private static string FormatCommand(IntakeResponseDto response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            return $"Не вышло: {response.Error}";
        }

        JsonNode? result = response.Result;

        if (result is JsonObject payload)
        {
            if (payload["error"] is JsonValue error)
            {
                return $"Не вышло: {error}";
            }

            if (payload["tasks"] is JsonArray tasks)
            {
                return FormatTasks(tasks);
            }

            if (payload["status"] is JsonValue status && payload["title"] is JsonValue title)
            {
                return $"Задача «{title}»: {StatusRu(status.ToString())}";
            }
        }

        return string.IsNullOrWhiteSpace(response.Command)
            ? "Готово."
            : $"Готово: {response.Command}.";
    }

    private static string FormatTasks(JsonArray tasks)
    {
        if (tasks.Count == 0)
        {
            return "Задач нет.";
        }

        var lines = new List<string> { $"Задачи ({tasks.Count}):" };

        foreach (JsonNode? node in tasks.Take(TaskListLimit))
        {
            if (node is not JsonObject task)
            {
                continue;
            }

            string title = task["title"]?.ToString() ?? "(без названия)";
            string? status = task["status"]?.ToString();
            lines.Add($"• {title} — {StatusRu(status)}");
        }

        if (tasks.Count > TaskListLimit)
        {
            lines.Add($"…и ещё {tasks.Count - TaskListLimit}.");
        }

        return string.Join('\n', lines);
    }
}
