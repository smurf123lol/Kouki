using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kouki.Kagedama.Api.Auth;

/// <summary>
/// Проверка bearer-токена. Пустой токен в настройках отключает проверку.
/// </summary>
/// <param name="next">Следующий обработчик конвейера.</param>
/// <param name="options">Настройки хоста.</param>
/// <param name="logger">Логгер.</param>
public sealed class TokenAuthMiddleware(
    RequestDelegate next,
    IOptions<KoukiServerOptions> options,
    ILogger<TokenAuthMiddleware> logger)
{
    private const string Scheme = "Bearer ";

    /// <summary>
    /// Пропускает запрос, если токен верный или проверка отключена.
    /// </summary>
    /// <param name="context">Контекст запроса.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string token = options.Value.Token;

        if (string.IsNullOrWhiteSpace(token) || IsPublicPath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string? provided = ReadToken(context.Request);

        if (provided is null || !FixedTimeEquals(provided, token))
        {
            logger.LogWarning("Запрос без действительного токена отклонён: {Path}", context.Request.Path.Value);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    // Токен закрывает API, но не сам дашборд: иначе браузер не смог бы загрузить страницу
    // и скрипты, а без них неоткуда взять и передать токен.
    private static bool IsPublicPath(PathString path)
        => path.StartsWithSegments("/api/health", StringComparison.OrdinalIgnoreCase)
           || !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

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
