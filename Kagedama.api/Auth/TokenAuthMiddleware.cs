using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kouki.Kagedama.Api.Auth;

/// <summary>
/// Проверка bearer-токена на пути <c>/api/*</c>.
/// </summary>
/// <remarks>
/// Если токен не настроен, API закрыт: «забыли задать токен» не должно означать
/// «пускать всех». Открытый API возможен только явным флагом <c>Kouki:AllowAnonymous</c>.
/// </remarks>
/// <param name="next">Следующий обработчик конвейера.</param>
/// <param name="options">Настройки хоста.</param>
/// <param name="logger">Логгер.</param>
public sealed class TokenAuthMiddleware(
    RequestDelegate next,
    IOptions<KoukiServerOptions> options,
    ILogger<TokenAuthMiddleware> logger)
{
    private const string Scheme = "Bearer ";

    // Счётчик отказов: при переборе токена не заливаем лог одной строкой на запрос.
    private int _rejections;

    /// <summary>
    /// Пропускает запрос, если токен верный или проверка отключена явно.
    /// </summary>
    /// <param name="context">Контекст запроса.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsPublicPath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string token = options.Value.Token;

        if (string.IsNullOrWhiteSpace(token))
        {
            if (options.Value.AllowAnonymous)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            await RejectAsync(context, "token-not-configured").ConfigureAwait(false);
            return;
        }

        string? provided = ReadToken(context.Request);

        if (provided is null || !FixedTimeEquals(provided, token))
        {
            await RejectAsync(context, "unauthorized").ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private async Task RejectAsync(HttpContext context, string error)
    {
        int count = Interlocked.Increment(ref _rejections);

        if (count <= 10 || count % 100 == 0)
        {
            logger.LogWarning(
                "Запрос без действительного токена отклонён ({Count} за сеанс): {Path}",
                count,
                context.Request.Path.Value);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"kouki\"";
        await context.Response.WriteAsJsonAsync(new { error }).ConfigureAwait(false);
    }

    // Токен закрывает API, но не сам дашборд: иначе браузер не смог бы загрузить страницу
    // и скрипты, а без них неоткуда взять и передать токен. Дополнительные открытые
    // пути приходят из настроек (Kouki:PublicPaths), а не правятся в коде.
    private bool IsPublicPath(PathString path)
    {
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string open in options.Value.PublicPaths)
        {
            if (!string.IsNullOrWhiteSpace(open)
                && path.StartsWithSegments(open.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadToken(HttpRequest request)
    {
        string? header = request.Headers.Authorization;

        if (header is not null && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return header[Scheme.Length..].Trim();
        }

        return null;
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        byte[] left = Encoding.UTF8.GetBytes(provided);
        byte[] right = Encoding.UTF8.GetBytes(expected);

        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
