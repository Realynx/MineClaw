namespace MineClaw.Raptor.Text;

/// <summary>
/// Dependency-free approximation of cl100k_base counts (the reference tokenizer): a leading space joins the next
/// word; short words are one token, long words ≈ one per 4 characters; digits group in threes; each punctuation run
/// costs about one token per two characters. Within ~10% of cl100k on English prose (README D7).
/// </summary>
public sealed class ApproxTokenCounter : ITokenCounter
{
    public static readonly ApproxTokenCounter Instance = new();

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int tokens = 0, i = 0;
        while (i < text.Length) tokens += Run(text, ref i);
        return tokens;
    }

    /// <summary>Consumes one run (word, number, newlines, spaces or punctuation) starting at i; returns its tokens.</summary>
    private static int Run(string text, ref int i)
    {
        char c = text[i];
        if (char.IsLetter(c)) return Word(text, ref i);
        if (char.IsDigit(c)) return Digits(text, ref i);
        if (c == '\n') return Newlines(text, ref i);
        if (char.IsWhiteSpace(c)) return Spaces(text, ref i);
        return Punctuation(text, ref i);
    }

    private static int Word(string text, ref int i)
    {
        int s = i;
        while (i < text.Length && char.IsLetter(text[i])) i++;
        int len = i - s;
        return len <= 6 ? 1 : (len + 3) / 4;
    }

    private static int Digits(string text, ref int i)
    {
        int s = i;
        while (i < text.Length && char.IsDigit(text[i])) i++;
        return (i - s + 2) / 3;
    }

    private static int Newlines(string text, ref int i)
    {
        while (i < text.Length && (text[i] == '\n' || text[i] == '\r')) i++;
        return 1;
    }

    /// <summary>Single spaces merge into the next word; a longer run costs one token.</summary>
    private static int Spaces(string text, ref int i)
    {
        int s = i;
        while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n') i++;
        return i - s > 1 ? 1 : 0;
    }

    private static int Punctuation(string text, ref int i)
    {
        int s = i;
        while (i < text.Length && !char.IsLetterOrDigit(text[i]) && !char.IsWhiteSpace(text[i])) i++;
        return (i - s + 1) / 2;
    }
}
