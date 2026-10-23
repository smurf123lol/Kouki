import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { McpClient } from "../mcp-client.js";

/**
 * Тесты MCP-клиента без запуска процессов: в песочнице DSH дочерние процессы
 * с pipe запрещены (EPERM), поэтому транспорт подменяется эмулятором.
 *
 * Запуск: node test/mcp-client.test.mjs
 */

/** Эмулятор MCP-сервера: отвечает на initialize / tools/list / tools/call. */
function createFakeSpawn() {
  return (_command, _args) => {
    const child = new EventEmitter();
    const stdout = new EventEmitter();
    stdout.setEncoding = () => {};
    child.stdout = stdout;
    child.stderr = new EventEmitter();
    child.stderr.setEncoding = () => {};

    const send = (message) => queueMicrotask(() => stdout.emit("data", `${JSON.stringify(message)}\n`));

    child.stdin = {
      write(line) {
        const request = JSON.parse(line);
        if (request.id === undefined) return true; // уведомление

        switch (request.method) {
          case "initialize":
            send({
              jsonrpc: "2.0",
              id: request.id,
              result: {
                protocolVersion: "2024-11-05",
                capabilities: { tools: {} },
                serverInfo: { name: "kouki", version: "0.3.0" },
              },
            });
            break;
          case "tools/list":
            send({
              jsonrpc: "2.0",
              id: request.id,
              result: {
                tools: [
                  {
                    name: "kouki_add_task",
                    description: "add",
                    inputSchema: { type: "object", properties: { text: { type: "string" } }, required: ["text"] },
                  },
                ],
              },
            });
            break;
          case "tools/call": {
            const { name, arguments: toolArgs } = request.params;
            if (name === "kouki_missing") {
              send({ jsonrpc: "2.0", id: request.id, error: { code: -32602, message: "Unknown tool 'kouki_missing'" } });
              break;
            }

            const payload = name === "kouki_add_task"
              ? { task: { id: "00000000-0000-0000-0000-000000000001", status: "new" }, text: toolArgs.text }
              : { count: 0 };

            send({
              jsonrpc: "2.0",
              id: request.id,
              result: { content: [{ type: "text", text: JSON.stringify(payload) }], isError: false },
            });
            break;
          }
          default:
            send({ jsonrpc: "2.0", id: request.id, error: { code: -32601, message: "Method not found" } });
        }

        return true;
      },
      end() {},
    };

    child.kill = () => {};
    return child;
  };
}

const tests = [];
const test = (name, fn) => tests.push({ name, fn });

test("initialize отдаёт имя сервера и версию протокола", async () => {
  const client = new McpClient({ command: "fake", spawnImpl: createFakeSpawn() });
  const result = await client.start().initialize();

  assert.equal(result.protocolVersion, "2024-11-05");
  assert.equal(result.serverInfo.name, "kouki");
});

test("tools/list отдаёт инструменты сервера", async () => {
  const client = new McpClient({ command: "fake", spawnImpl: createFakeSpawn() });
  await client.start().initialize();

  const tools = await client.listTools();
  assert.equal(tools.length, 1);
  assert.equal(tools[0].name, "kouki_add_task");
  assert.deepEqual(tools[0].inputSchema.required, ["text"]);
});

test("tools/call разбирает JSON-текст и передаёт аргументы", async () => {
  const client = new McpClient({ command: "fake", spawnImpl: createFakeSpawn() });
  await client.start().initialize();

  const result = await client.callTool("kouki_add_task", { text: "купить хлеб" });
  assert.equal(result.isError, false);
  assert.equal(result.value.task.status, "new");
  assert.equal(result.value.text, "купить хлеб");
});

test("ошибка протокола превращается в исключение", async () => {
  const client = new McpClient({ command: "fake", spawnImpl: createFakeSpawn() });
  await client.start().initialize();

  await assert.rejects(() => client.callTool("kouki_missing", {}), /-32602|Unknown tool/);
});

test("таймаут ожидания ответа", async () => {
  const silent = () => {
    const child = new EventEmitter();
    const stdout = new EventEmitter();
    stdout.setEncoding = () => {};
    child.stdout = stdout;
    child.stderr = new EventEmitter();
    child.stderr.setEncoding = () => {};
    child.stdin = { write: () => true, end() {} };
    child.kill = () => {};
    return child;
  };

  const client = new McpClient({ command: "fake", spawnImpl: silent, requestTimeoutMs: 50 });
  await assert.rejects(() => client.start().request("ping", {}), /нет ответа на ping за 50 мс/);
});

test("dispose отклоняет незавершённые запросы", async () => {
  const silent = () => {
    const child = new EventEmitter();
    const stdout = new EventEmitter();
    stdout.setEncoding = () => {};
    child.stdout = stdout;
    child.stderr = new EventEmitter();
    child.stderr.setEncoding = () => {};
    child.stdin = { write: () => true, end() {} };
    child.kill = () => {};
    return child;
  };

  const client = new McpClient({ command: "fake", spawnImpl: silent, requestTimeoutMs: 5_000 });
  client.start();
  const pending = client.request("ping", {}).then(
    () => "resolved",
    (error) => error.message,
  );
  await client.dispose();
  assert.match(await pending, /MCP-клиент (уже )?закрыт/);
});

let failed = 0;

for (const { name, fn } of tests) {
  try {
    await fn();
    console.log(`PASS ${name}`);
  } catch (error) {
    failed += 1;
    console.log(`FAIL ${name}`);
    console.log(`     ${error.message}`);
  }
}

console.log(`\nИТОГО: passed ${tests.length - failed}, failed ${failed}`);
process.exitCode = failed === 0 ? 0 : 1;
