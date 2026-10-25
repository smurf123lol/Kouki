# dsh-kouki

Плагин DeepSeek Harness: подключает задачи Kouki к агенту через MCP.

Плагин поднимает `Kagedama.mcp` как дочерний процесс и регистрирует пять инструментов,
которые проксируются в MCP-сервер:

| Инструмент | Что делает |
|---|---|
| `kouki_add_task` | принимает фразу («запиши задачу купить хлеб»), создаёт задачу и запись журнала |
| `kouki_list_tasks` | возвращает последние задачи, опционально по статусу |
| `kouki_complete_task` | закрывает задачу по `taskId`; без него — последнюю открытую |
| `kouki_unresolved_classifications` | неразобранные классификации — будущий датасет для BERT |
| `kouki_label_classification` | размечает запись журнала вручную |
| `kouki_task_history` | журнал действий по задаче: что с ней делали и с каким результатом |
| `kouki_context` | окружение сервера: активный контекст, сервисы, пути хранилищ |

## Установка

1. Соберите сервер:

   ```bash
   dotnet build ..\Kagedama.mcp\Kagedama.mcp.csproj -m:1 -nodeReuse:false
   ```

2. Поставьте плагин в отдельный профиль (не в `desktop`, чтобы не трогать основную сессию):

   ```bash
   dsh plugin --profile kouki add E:\путь\к\Kouki\dsh-kouki
   ```

3. В конфиге профиля (`cordis.patch.yml`) задайте путь к сборке MCP-сервера:

   ```yaml
   plugins:
     dsh-kouki:
       mcpCommand: dotnet
       mcpArgs:
         - E:\путь\к\Kouki\Kagedama.mcp\bin\Debug\net10.0\Kagedama.mcp.dll
         - --database=E:\kouki\kouki.sqlite
   ```

   `databasePath` должен указывать на тот же `kouki.sqlite`, что и `Kagedama.api` —
   тогда агент увидит задачи, созданные из Telegram и дашборда.

## Конфигурация

| Ключ | По умолчанию | Смысл |
|---|---|---|
| `mcpCommand` | `dotnet` | команда запуска MCP-сервера |
| `mcpArgs` | `[]` | аргументы; обязателен путь к `Kagedama.mcp.dll` |
| `databasePath` | `""` | если задан, добавляется `--database=<путь>` |
| `cwd` | `""` | рабочий каталог дочернего процесса |
| `requestTimeoutMs` | `30000` | таймаут ожидания ответа JSON-RPC |

## Проверка

```bash
node test/mcp-client.test.mjs
```

Тест использует эмулятор транспорта: в песочнице DSH дочерние процессы с pipe
запрещены (`spawn EPERM`). Сам плагин в реальном Harness работает — процесс MCP
поднимается вне песочницы инструментов.

## Как это устроено

`mcp-client.js` — минимальный JSON-RPC 2.0 клиент над stdio: `initialize`,
`notifications/initialized`, `tools/list`, `tools/call`, таймауты и корректное
закрытие. `index.js` — только склейка с DSH: `inject = ["tools"]`, ленивое
подключение MCP, `ctx.effect(...)` для остановки процесса при выгрузке плагина.
