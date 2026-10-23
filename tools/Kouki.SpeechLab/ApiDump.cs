#if KOUKI_SHERPA
namespace Kouki.SpeechLab;

using System.Reflection;

/// <summary>Печатает фактические сигнатуры sherpa-onnx: связка версионируется, память подводит.</summary>
internal static class ApiDump
{
    public static int Print(string[] patterns)
    {
        Assembly assembly = typeof(SherpaOnnx.OfflineTts).Assembly;

        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName))
        {
            if (patterns.Length > 0 && !patterns.Any(p => type.Name.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Console.WriteLine($"== {type.FullName}");

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                string parameters = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                Console.WriteLine($"   {method.ReturnType.Name} {method.Name}({parameters})");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Console.WriteLine($"   prop {property.PropertyType.Name} {property.Name}");
            }
        }

        return 0;
    }
}
#endif
