using System.Text;

namespace Kouki.Yasabi;

/// <summary>
/// Легковесный токенизатор WordPiece для моделей RuBERT.
/// Не имеет внешних зависимостей, работает in-memory за субмиллисекунды.
/// </summary>
public sealed class BertTokenizer
{
    public const string PadToken = "[PAD]";
    public const string UnkToken = "[UNK]";
    public const string ClsToken = "[CLS]";
    public const string SepToken = "[SEP]";

    private readonly Dictionary<string, long> _vocab = new(StringComparer.Ordinal);
    private readonly long _padId;
    private readonly long _unkId;
    private readonly long _clsId;
    private readonly long _sepId;

    public BertTokenizer(string vocabPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabPath);
        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException($"Файл словаря токенизатора не найден: {vocabPath}", vocabPath);
        }

        string[] lines = File.ReadAllLines(vocabPath);
        for (int i = 0; i < lines.Length; i++)
        {
            string token = lines[i].Trim();
            if (token.Length > 0 && !_vocab.ContainsKey(token))
            {
                _vocab[token] = i;
            }
        }

        _padId = _vocab.GetValueOrDefault(PadToken, 0);
        _unkId = _vocab.GetValueOrDefault(UnkToken, 1);
        _clsId = _vocab.GetValueOrDefault(ClsToken, 2);
        _sepId = _vocab.GetValueOrDefault(SepToken, 3);
    }

    public BertTokenizer(IEnumerable<string> vocabLines)
    {
        ArgumentNullException.ThrowIfNull(vocabLines);
        int i = 0;
        foreach (string line in vocabLines)
        {
            string token = line.Trim();
            if (token.Length > 0 && !_vocab.ContainsKey(token))
            {
                _vocab[token] = i;
            }
            i++;
        }

        _padId = _vocab.GetValueOrDefault(PadToken, 0);
        _unkId = _vocab.GetValueOrDefault(UnkToken, 1);
        _clsId = _vocab.GetValueOrDefault(ClsToken, 2);
        _sepId = _vocab.GetValueOrDefault(SepToken, 3);
    }

    /// <summary>
    /// Токенизирует текст фразы в input_ids, attention_mask и token_type_ids.
    /// </summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Tokenize(string text, int maxTokens = 64)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (new[] { _clsId, _sepId }, new long[] { 1, 1 }, new long[] { 0, 0 });
        }

        List<long> tokenIds = new() { _clsId };

        string[] words = BasicTokenize(text);
        foreach (string word in words)
        {
            if (tokenIds.Count >= maxTokens - 1)
            {
                break;
            }

            TokenizeWord(word, tokenIds, maxTokens - 1);
        }

        tokenIds.Add(_sepId);

        int count = tokenIds.Count;
        long[] inputIds = tokenIds.ToArray();
        long[] attentionMask = new long[count];
        Array.Fill(attentionMask, 1L);
        long[] tokenTypeIds = new long[count]; // Все нули для single sentence

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private void TokenizeWord(string word, List<long> output, int maxAllowed)
    {
        int start = 0;
        while (start < word.Length && output.Count < maxAllowed)
        {
            int end = word.Length;
            string? curSubstr = null;
            long matchedId = -1;

            while (start < end)
            {
                string substr = word[start..end];
                if (start > 0)
                {
                    substr = "##" + substr;
                }

                if (_vocab.TryGetValue(substr, out long id))
                {
                    curSubstr = substr;
                    matchedId = id;
                    break;
                }

                end--;
            }

            if (curSubstr is null)
            {
                output.Add(_unkId);
                return;
            }

            output.Add(matchedId);
            start = end;
        }
    }

    private static string[] BasicTokenize(string text)
    {
        StringBuilder sb = new();
        string lower = text.ToLowerInvariant();

        foreach (char c in lower)
        {
            if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                sb.Append(' ');
                sb.Append(c);
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }
}
