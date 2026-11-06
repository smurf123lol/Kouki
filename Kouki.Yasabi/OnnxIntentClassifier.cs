using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Kouki.Yasabi;

/// <summary>
/// Классификатор 1 уровня на RuBERT-tiny2 через ONNX Runtime.
/// Выдаёт честное распределение вероятностей (Softmax) по 8 классам интентов Kouki.
/// Инференс выполняется внутри процесса .NET за ≤ 8 мс на CPU.
/// </summary>
public sealed class OnnxIntentClassifier : IIntentClassifier, IDisposable
{
    public const string ModelName = "rubert-tiny2-onnx";

    public static readonly string[] DefaultClasses =
    [
        "add_task",
        "list_tasks",
        "complete_task",
        "add_reminder",
        "list_reminders",
        "complete_reminder",
        "ask",
        "chat"
    ];

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly string[] _classes;
    private readonly bool _ownsSession;

    public OnnxIntentClassifier(string modelPath, string vocabPath, string[]? classes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"ONNX модель интентов не найдена: {modelPath}", modelPath);
        }

        var sessionOptions = new SessionOptions();
        sessionOptions.AppendExecutionProvider_CPU(0);
        sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;

        _session = new InferenceSession(modelPath, sessionOptions);
        _ownsSession = true;
        _tokenizer = new BertTokenizer(vocabPath);
        _classes = classes is { Length: > 0 } ? classes : DefaultClasses;
    }

    public OnnxIntentClassifier(InferenceSession session, BertTokenizer tokenizer, string[]? classes = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _classes = classes is { Length: > 0 } ? classes : DefaultClasses;
        _ownsSession = false;
    }

    /// <summary>Список классов модели в порядке выходов Softmax.</summary>
    public IReadOnlyList<string> Classes => _classes;

    /// <inheritdoc />
    public Task<IntentResult> ClassifyAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(IntentResult.Unknown(ModelName));
        }

        string trimmed = text.Trim();
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Tokenize(trimmed, maxTokens: 64);

        int seqLength = inputIds.Length;
        var inputIdsTensor = new DenseTensor<long>(inputIds, [1, seqLength]);
        var attentionMaskTensor = new DenseTensor<long>(attentionMask, [1, seqLength]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIds, [1, seqLength]);

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        Tensor<float> logits = results.First().AsTensor<float>();

        int numClasses = _classes.Length;
        float maxLogit = float.NegativeInfinity;
        for (int i = 0; i < numClasses; i++)
        {
            float val = logits[0, i];
            if (val > maxLogit)
            {
                maxLogit = val;
            }
        }

        float sumExp = 0f;
        float[] expValues = new float[numClasses];
        for (int i = 0; i < numClasses; i++)
        {
            float exp = MathF.Exp(logits[0, i] - maxLogit);
            expValues[i] = exp;
            sumExp += exp;
        }

        int bestIndex = 0;
        float bestProb = 0f;
        int secondIndex = -1;
        float secondProb = 0f;

        JsonObject probabilities = new();

        for (int i = 0; i < numClasses; i++)
        {
            float prob = sumExp > 0f ? expValues[i] / sumExp : 0f;
            probabilities[_classes[i]] = Math.Round(prob, 4);

            if (prob > bestProb)
            {
                secondProb = bestProb;
                secondIndex = bestIndex;
                bestProb = prob;
                bestIndex = i;
            }
            else if (prob > secondProb)
            {
                secondProb = prob;
                secondIndex = i;
            }
        }

        string bestIntent = _classes[bestIndex];
        double score = Math.Round((double)bestProb, 4);

        JsonObject? slots = null;
        // Если разница между топ-1 и топ-2 < 0.05, фиксируем неопределённость в слотах
        if (secondIndex >= 0 && (bestProb - secondProb) < 0.05f)
        {
            slots = new JsonObject
            {
                ["clarification_needed"] = true,
                ["alternative_intent"] = _classes[secondIndex],
                ["alternative_score"] = Math.Round((double)secondProb, 4),
                ["probabilities"] = probabilities
            };
        }

        var result = new IntentResult(bestIntent, score, slots, ModelName);
        return Task.FromResult(result);
    }

    public void Dispose()
    {
        if (_ownsSession)
        {
            _session.Dispose();
        }
    }
}
