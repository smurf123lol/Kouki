// Статика дашборда: один источник настроек и проводки для боевого Program.cs и для
// тестов, которые поднимают настоящий Kestrel. Иначе тест проверяет копию настроек,
// а не то, что реально отдаётся браузеру (ревью хотфикса T78, 02.11).
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Kouki.Kagedama.Api;

/// <summary>Отдача <c>wwwroot</c> дашборда.</summary>
public static class DashboardStaticFiles
{
    /// <summary>
    /// Браузер обязан спрашивать сервер: у файлов нет хешей в имени, и эвристический
    /// кэш после деплоя исполняет старый <c>app.js</c> поверх нового HTML.
    /// </summary>
    public const string CacheControl = "no-cache";

    /// <summary>Провайдер файлов дашборда из его каталога.</summary>
    public static PhysicalFileProvider Provider(string webRoot) => new(webRoot);

    /// <summary>
    /// Боевая проводка: <c>index.html</c> на <c>/</c> и статика с <c>no-cache</c>.
    /// В <c>Program.cs</c> вызвать на этапе сборки конвейера, тестам — на своём хосте.
    /// </summary>
    public static void Use(WebApplication app, PhysicalFileProvider files)
    {
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = CacheControl,
        });
    }
}
