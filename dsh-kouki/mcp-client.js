import { spawn } from "node:child_process";

/**
 * Минимальный MCP-клиент поверх stdio: JSON-RPC 2.0, по одному сообщению в строке.
 *
 * Клиент намеренно без зависимостей — его можно прогнать обычным `node --test`
 * против настоящего сервера `Kagedama.mcp`.
 */
export class McpClient {
  #child = null;
  #buffer = "";
  #nextId = 0;
  #pending = new Map();
  #stderr = "";
  #closed = false;

  /**
   * @param {object} options
   * @param {string} options.command — исполняемый файл (по умолчанию dotnet).
   * @param {string[]} [options.args] — аргументы, включая путь к сборке MCP-сервера.
   * @param {string} [options.cwd] — рабочий каталог процесса.
   * @param {Record<string,string>} [options.env] — переменные окружения.
   * @param {number} [options.requestTimeoutMs] — сколько ждать ответа, по умолчанию 30 с.
   * @param {typeof import("node:child_process").spawn} [options.spawnImpl] — подмена запуска
   *   для тестов: в песочнице DSH дочерние процессы с pipe запрещены.
   */
  constructor({ command, args = [], cwd, env, requestTimeoutMs = 30_000, spawnImpl = spawn }) {
    if (!command) throw new Error("McpClient: command is required");
    this.command = command;
    this.args = args;
    this.cwd = cwd;
    this.env = env;
    this.requestTimeoutMs = requestTimeoutMs;
    this.spawnImpl = spawnImpl;
  }

  /** Последние строки stderr — удобно показывать в ошибках. */
  get stderr() {
    return this.#stderr;
  }

  /** Запускает процесс и подписывается на поток. */
  start() {
    if (this.#child) return this;

    this.#child = this.spawnImpl(this.command, this.args, {
      cwd: this.cwd,
      env: this.env ? { ...process.env, ...this.env } : process.env,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });

    this.#child.stdout.setEncoding("utf8");
    this.#child.stdout.on("data", (chunk) => this.#onData(chunk));

    this.#child.stderr.setEncoding("utf8");
    this.#child.stderr.on("data", (chunk) => {
      this.#stderr = (this.#stderr + chunk).slice(-4_000);
    });

    this.#child.on("error", (error) => this.#fail(new Error(`MCP-процесс не запустился: ${error.message}`)));
    this.#child.on("exit", (code) => {
      if (this.#closed) return;
      this.#fail(new Error(`MCP-процесс завершился с кодом ${code}. ${this.#stderr.trim()}`.trim()));
    });

    return this;
  }

  /** Handshake: initialize + notifications/initialized. */
  async initialize() {
    const result = await this.request("initialize", {
      protocolVersion: "2024-11-05",
      capabilities: {},
      clientInfo: { name: "dsh-kouki", version: "0.1.0" },
    });

    this.#write({ jsonrpc: "2.0", method: "notifications/initialized" });
    return result;
  }

  /** Список инструментов сервера. */
  async listTools() {
    const result = await this.request("tools/list", {});
    return Array.isArray(result?.tools) ? result.tools : [];
  }

  /**
   * Вызывает инструмент. Возвращает разобранный JSON, если сервер прислал JSON-текст,
   * иначе — исходный текст и флаг `isError`.
   */
  async callTool(name, args = {}) {
    const result = await this.request("tools/call", { name, arguments: args });
    const text = (result?.content ?? [])
      .filter((item) => item?.type === "text" && typeof item.text === "string")
      .map((item) => item.text)
      .join("\n");

    if (result?.isError) {
      return { isError: true, text };
    }

    try {
      return { isError: false, value: JSON.parse(text) };
    } catch {
      return { isError: false, text };
    }
  }

  /** Отправляет запрос и ждёт ответ с тем же id. */
  request(method, params) {
    if (!this.#child) this.start();
    if (this.#closed) return Promise.reject(new Error("MCP-клиент уже закрыт"));

    const id = ++this.#nextId;
    const message = { jsonrpc: "2.0", id, method };
    if (params !== undefined) message.params = params;

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`MCP: нет ответа на ${method} за ${this.requestTimeoutMs} мс`));
      }, this.requestTimeoutMs);

      this.#pending.set(id, { resolve, reject, timer });
      this.#write(message);
    });
  }

  /** Гасит процесс. */
  async dispose() {
    this.#closed = true;

    for (const [, pending] of this.#pending) {
      clearTimeout(pending.timer);
      pending.reject(new Error("MCP-клиент закрыт"));
    }
    this.#pending.clear();

    const child = this.#child;
    this.#child = null;
    if (!child) return;

    child.stdin.end();
    await new Promise((resolve) => {
      const timer = setTimeout(() => {
        child.kill();
        resolve();
      }, 2_000);

      child.once("exit", () => {
        clearTimeout(timer);
        resolve();
      });
    });
  }

  #write(message) {
    this.#child?.stdin.write(`${JSON.stringify(message)}\n`);
  }

  #onData(chunk) {
    this.#buffer += chunk;

    let index;
    while ((index = this.#buffer.indexOf("\n")) >= 0) {
      const line = this.#buffer.slice(0, index).trim();
      this.#buffer = this.#buffer.slice(index + 1);
      if (line) this.#onLine(line);
    }
  }

  #onLine(line) {
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      return;
    }

    if (message.id === undefined || message.id === null) return;

    const pending = this.#pending.get(message.id);
    if (!pending) return;

    this.#pending.delete(message.id);
    clearTimeout(pending.timer);

    if (message.error) {
      pending.reject(new Error(`MCP ${message.error.code}: ${message.error.message}`));
      return;
    }

    pending.resolve(message.result);
  }

  #fail(error) {
    this.#closed = true;
    for (const [, pending] of this.#pending) {
      clearTimeout(pending.timer);
      pending.reject(error);
    }
    this.#pending.clear();
  }
}
