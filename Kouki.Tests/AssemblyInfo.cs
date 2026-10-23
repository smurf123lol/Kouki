// Общие using'и для всех тестов проекта.
global using Xunit;

// Хендлеры в тестах считают вызовы в статических полях — параллельный прогон их бы сломал.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
