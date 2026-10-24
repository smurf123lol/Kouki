using System.Text.Json.Nodes;
using Kouki.Kagedama;
using Kouki.Kagedama.Contracts;
using Kouki.Kagedama.Data;
using Kouki.Kagedama.Hosting;
using Kouki.Kagedama.Mcp;
using Kouki.Shimi.Context;
using Kouki.Ubusuna.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Kouki.Tests;

/// <summary>
/// Протокол MCP: сессия разбирает JSON-RPC, отвечает ошибками и не падает
/// от сбоя отдельного инструмента.
/// </summary>
public sealed class McpSessionTests : IDisposable
{
    private readonly string _directory;
    private readonly ServiceProvider _provider;
    private readonly McpSession _session;

    public McpSessionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kouki-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var services = new ServiceCollection();
        services.AddLogging();
        // Адаптеры консоли — то же, что получает настоящий MCP-хост: контексту нужны файлы и терминал.
        services.AddUbusuna();
        services.AddKoukiCore(new KoukiHostSettings
        {
            DatabasePath = Path.Combine(_directory, "kouki.sqlite"),
            DefaultSource = TaskSources.Mcp,
        });

        _provider = services.BuildServiceProvider();
        KoukiHosting.InitializeKouki(_provider, "mcp");

        _session = new McpSession(new KoukiMcpTools(
            _provider.GetRequiredService<TaskIntakeService>(),
            _provider.GetRequiredService<Kouki.Mugura.Dispatcher.IRouter>(),
            _provider.GetRequiredService<IContextRegistry>()));
    }

    [Fact]
    public async Task Initialize_reports_protocol_and_server()
    {
        JsonNode response = await CallAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(McpSession.ProtocolVersion, response["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal(McpSession.ServerName, response["result"]!["serverInfo"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Notification_has_no_response()
    {
        string? response = await _session.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Null(response);
    }

    [Fact]
    public async Task Unknown_method_is_method_not_found()
    {
        JsonNode response = await CallAsync("""{"jsonrpc":"2.0","id":2,"method":"no/such/method"}""");

        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Broken_json_is_parse_error()
    {
        JsonNode response = await CallAsync("""{"jsonrpc": "2.0", "id": 3, "method": """);

        Assert.Equal(-32700, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Tools_list_contains_domain_tools()
    {
        JsonNode response = await CallAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/list"}""");
        JsonArray tools = response["result"]!["tools"]!.AsArray();

        Assert.Contains(tools, tool => tool!["name"]!.GetValue<string>() == "kouki_add_task");
        Assert.Contains(tools, tool => tool!["name"]!.GetValue<string>() == "kouki_task_history");
        Assert.Equal(KoukiMcpTools.Tools.Count, tools.Count);
    }

    [Fact]
    public async Task Tool_error_is_returned_as_isError_not_an_exception()
    {
        JsonNode response = await CallAsync(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"kouki_complete_task","arguments":{"taskId":"00000000-0000-0000-0000-000000000001"}}}""");

        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Session_survives_a_failed_tool_call()
    {
        var input = new StringReader(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"kouki_complete_task","arguments":{}}}""" + "\n" +
            """{"jsonrpc":"2.0","id":7,"method":"ping"}""" + "\n");
        var output = new StringWriter();

        await _session.RunAsync(input, output);

        string[] lines = output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(2, lines.Length);
        Assert.Equal(7, JsonNode.Parse(lines[1])!["id"]!.GetValue<int>());
    }

    private async Task<JsonNode> CallAsync(string json)
    {
        string? response = await _session.HandleAsync(json);
        Assert.NotNull(response);
        return JsonNode.Parse(response!)!;
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог: если не удаляется, это не повод валить тесты.
        }
    }
}
