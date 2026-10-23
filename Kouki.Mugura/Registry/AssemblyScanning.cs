using System.Reflection;

namespace Kouki.Mugura.Registry;

/// <summary>
/// Безопасное перечисление типов сборки: частично загруженные сборки (плагины, приложения)
/// не должны ронять сканирование целиком.
/// </summary>
internal static class AssemblyScanning
{
    public static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static t => t is not null)!;
        }
    }
}
