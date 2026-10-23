import { defineTool } from "@deepseek-ai/dsh-tools";
import z from "@deepseek-ai/schemastery";
import { McpClient } from "./mcp-client.js";

/**
 * Плагин Kouki для DeepSeek Harness.
 *
 * Работает как MCP-клиент: поднимает `Kagedama.mcp` (stdio) и публикует его
 * инструменты в DSH. Через MCP доступны только запросы к локальному окружению —
 * задачи и журнал разбора; сами команды исполняет сервер Kouki.
 */
export const name = "dsh-kouki";

export const inject = ["tools"];

export const Config = z.object({
  mcpCommand: z.string().default("dotnet").description("Исполняемый файл MCP-сервера (обычно dotnet)."),
  mcpArgs: z.array(z.string()).default([]).description("Аргументы запуска, включая путь к Kagedama.mcp.dll."),
  databasePath: z.string().default("").description("Путь к kouki.sqlite; пусто — значение сервера по умолчанию."),
  cwd: z.string().default("").description("Рабочий каталог процесса MCP-сервера."),
  requestTimeoutMs: z.number().default(30000).description("Сколько ждать ответа MCP, миллисекунды."),
});

const textOutput = {
  schema: { type: "object", additionalProperties: true },
  render(_args, value) {
    return [{ type: "text", text: JSON.stringify(value ?? null, null, 2) }];
  },
};

export function apply(ctx, config) {
  const settings = config ?? {};
  const args = [...(settings.mcpArgs ?? [])];

  if (settings.databasePath) {
    args.push(`--database=${settings.databasePath}`);
  }

  const client = new McpClient({
    command: settings.mcpCommand || "dotnet",
    args,
    cwd: settings.cwd || undefined,
    requestTimeoutMs: settings.requestTimeoutMs ?? 30_000,
  });

  let handshake = null;

  const connect = async () => {
    if (!handshake) {
      handshake = client
        .start()
        .initialize()
        .catch((error) => {
          handshake = null;
          throw new Error(
            `Kouki MCP не поднялся. Проверьте mcpArgs (путь к Kagedama.mcp.dll). ${error.message}`,
          );
        });
    }

    return handshake;
  };

  const call = async (tool, toolArgs) => {
    await connect();
    const result = await client.callTool(tool, toolArgs);
    if (result.isError) {
      throw new Error(result.text || `Инструмент ${tool} вернул ошибку`);
    }

    return result.value ?? { text: result.text };
  };

  ctx.effect(() => () => {
    void client.dispose();
  }, "dsh-kouki: остановка MCP-сервера");

  ctx.inject(["tools"], (sctx) => {
    sctx.effect(
      () =>
        sctx.tools.register(
          defineTool({
            name: "kouki_add_task",
            description:
              "Добавляет задачу в Kouki из свободной фразы: классифицирует её, при необходимости исполняет команду и всегда сохраняет задачу.",
            parameters: {
              text: { type: "string", description: "Фраза человека, например «запиши задачу купить хлеб»." },
            },
            output: textOutput,
            execute: (toolArgs) => call("kouki_add_task", toolArgs),
          }),
        ),
      "dsh-kouki: kouki_add_task",
    );

    sctx.effect(
      () =>
        sctx.tools.register(
          defineTool({
            name: "kouki_list_tasks",
            description: "Показывает последние задачи Kouki.",
            parameters: {
              status: { type: "string", description: "Фильтр: new, in_progress, done, cancelled, needs_review." },
              limit: { type: "number", description: "Сколько задач вернуть, по умолчанию 10, максимум 100." },
            },
            output: textOutput,
            execute: (toolArgs) => call("kouki_list_tasks", toolArgs),
          }),
        ),
      "dsh-kouki: kouki_list_tasks",
    );

    sctx.effect(
      () =>
        sctx.tools.register(
          defineTool({
            name: "kouki_complete_task",
            description: "Закрывает задачу Kouki. Без taskId закрывается последняя открытая задача.",
            parameters: {
              taskId: { type: "string", description: "Идентификатор задачи (GUID)." },
            },
            output: textOutput,
            execute: (toolArgs) => call("kouki_complete_task", toolArgs),
          }),
        ),
      "dsh-kouki: kouki_complete_task",
    );

    sctx.effect(
      () =>
        sctx.tools.register(
          defineTool({
            name: "kouki_unresolved_classifications",
            description: "Показывает записи журнала Kouki, которые ждут ручного разбора (low confidence и ошибки).",
            parameters: {
              limit: { type: "number", description: "Сколько записей вернуть, по умолчанию 20, максимум 200." },
            },
            output: textOutput,
            execute: (toolArgs) => call("kouki_unresolved_classifications", toolArgs),
          }),
        ),
      "dsh-kouki: kouki_unresolved_classifications",
    );

    sctx.effect(
      () =>
        sctx.tools.register(
          defineTool({
            name: "kouki_label_classification",
            description:
              "Помечает запись журнала правильным интентом — так набирается датасет для будущей модели распознавания.",
            parameters: {
              id: { type: "string", description: "Идентификатор записи журнала (GUID)." },
              label: { type: "string", description: "Правильный интент, например add_task." },
            },
            output: textOutput,
            execute: (toolArgs) => call("kouki_label_classification", toolArgs),
          }),
        ),
      "dsh-kouki: kouki_label_classification",
    );
  });
}
