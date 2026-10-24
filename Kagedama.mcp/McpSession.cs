using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Kouki.Kagedama.Mcp;

/// <summary>
/// Одна MCP-сессия: читает JSON-RPC 2.0 построчно из входа и отвечает в выход.
/// </summary>
/// <remarks>
/// Транспорт — stdio (по одному сообщению в строке). Класс не зависит от консоли,
/// поэтому его можно прогнать на любых <see cref="TextReader"/>/<see cref="TextWriter"/>.
/// </remarks>
public sealed class McpSession
{
    /// <summary>
    /// Версия протокола, которую объявляет сервер.
    /// </summary>
    public const string ProtocolVersion = "2024-11-05";

    /// <summary>
    /// Имя сервера для <c>initialize</c>.
    /// </summary>
    public const string ServerName = "kouki";

    /// <summary>
    /// Версия сервера для <c>initialize</c>.
    /// </summary>
    public const string ServerVersion = "0.3.0";

    /// <summary>
    /// Предел длины одного сообщения, символов. JSON-RPC-сообщения Kouki — килобайты,
    /// так что мегабайтный кадр заведомо не наш.
    /// </summary>
    public const int MaxMessageLength = 1_048_576;

    private readonly KoukiMcpTools _tools;
    private readonly ILogger? _logger;

    /// <summary>
    /// Создаёт сессию поверх набора инструментов.
    /// </summary>
    /// <param name="tools">Инструменты Kouki.</param>
    /// <param name="logger">Логгер (по умолчанию — без логов).</param>
    public McpSession(KoukiMcpTools tools, ILogger? logger = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _logger = logger;
    }

    /// <summary>
    /// Крутит цикл чтения до конца входного потока.
    /// </summary>
    /// <param name="input">Поток запросов (обычно stdin).</param>
    /// <param name="output">Поток ответов (обычно stdout).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        while (true)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Одна гигантская строка от клиента не должна съесть память процесса.
            if (line.Length > MaxMessageLength)
            {
                await output.WriteLineAsync(ErrorResponse(null, -32600, $"Invalid Request: message is longer than {MaxMessageLength} characters")).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            string? response;
            try
            {
                response = await HandleAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Ошибка обработки одного сообщения не завершает сеанс: клиент получает -32603.
                _logger?.LogError(ex, "Ошибка обработки сообщения MCP");
                response = ErrorResponse(null, -32603, "Internal error");
            }

            if (response is null)
            {
                continue;
            }

            await output.WriteLineAsync(response).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Обрабатывает одно сообщение и возвращает ответ или <c>null</c> для уведомлений.
    /// </summary>
    /// <param name="json">Строка JSON-RPC.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public async Task<string?> HandleAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonNode? parsed;
        try
        {
            // Некоторые клиенты всё же присылают BOM в начале первого сообщения.
            parsed = JsonNode.Parse(json.TrimStart('\uFEFF'));
        }
        catch (JsonException)
        {
            return ErrorResponse(null, -32700, "Parse error");
        }

        if (parsed is not JsonObject request)
        {
            return ErrorResponse(null, -32600, "Invalid request");
        }

        JsonNode? id = request["id"]?.DeepClone();
        string? method = request["method"] is JsonValue methodValue && methodValue.TryGetValue<string>(out string? name)
            ? name
            : null;

        if (string.IsNullOrWhiteSpace(method))
        {
            return id is null ? null : ErrorResponse(id, -32600, "Invalid request: method is missing");
        }

        // Уведомления (без id) ответа не требуют.
        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return null;
        }

        switch (method)
        {
            case "initialize":
                return SuccessResponse(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["title"] = "Kouki (MCP)",
                        ["version"] = ServerVersion,
                    },
                });

            case "ping":
                return SuccessResponse(id, new JsonObject());

            case "tools/list":
                return SuccessResponse(id, new JsonObject { ["tools"] = DescribeTools() });

            case "tools/call":
                return await CallToolAsync(id, request["params"] as JsonObject, cancellationToken).ConfigureAwait(false);

            default:
                return id is null ? null : ErrorResponse(id, -32601, $"Method not found: {method}");
        }
    }

    private async Task<string?> CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null)
        {
            return ErrorResponse(id, -32602, "Invalid params: params object is required");
        }

        string? toolName = parameters["name"] is JsonValue nameValue && nameValue.TryGetValue<string>(out string? name)
            ? name
            : null;

        if (string.IsNullOrWhiteSpace(toolName) || KoukiMcpTools.Find(toolName) is null)
        {
            return ErrorResponse(id, -32602, $"Unknown tool '{toolName}'");
        }

        var arguments = parameters["arguments"] as JsonObject;

        try
        {
            string result = await _tools.InvokeAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);

            return SuccessResponse(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = result },
                },
                ["isError"] = false,
            });
        }
        catch (ArgumentException ex)
        {
            return ToolError(id, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Любая другая ошибка инструмента (SQLite, роутер, десериализация) не должна
            // ронять процесс: клиент получает isError=true и остаётся на связи.
            _logger?.LogError(ex, "Инструмент {Tool} упал", toolName);

            return ToolError(id, $"Инструмент '{toolName}' завершился ошибкой: {ex.Message}");
        }
    }

    private static string ToolError(JsonNode? id, string message) => SuccessResponse(id, new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = message },
        },
        ["isError"] = true,
    });

    private static JsonArray DescribeTools()
    {
        var tools = new JsonArray();

        foreach (McpToolDescriptor tool in KoukiMcpTools.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return tools;
    }

    private static string SuccessResponse(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    }.ToJsonString(KoukiMcpTools.JsonOptions);

    private static string ErrorResponse(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    }.ToJsonString(KoukiMcpTools.JsonOptions);
}
