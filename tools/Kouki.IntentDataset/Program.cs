namespace Kouki.IntentDataset;

/// <summary>Точка входа сборщика датасета.</summary>
public static class Program
{
    /// <summary>Запускает CLI и возвращает код выхода.</summary>
    public static int Main(string[] args)
    {
        return IntentDatasetCli.Run(args, Console.Out, Console.Error);
    }
}
