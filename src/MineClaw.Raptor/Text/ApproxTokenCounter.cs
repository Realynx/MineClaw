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
        int tokens = 0, i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (char.IsLetter(c))
            {
                int s = i;
                while (i < n && char.IsLetter(text[i])) i++;
                int len = i - s;
                tokens += len <= 6 ? 1 : (len + 3) / 4;
            }
            else if (char.IsDigit(c))
            {
                int s = i;
                while (i < n && char.IsDigit(text[i])) i++;
                tokens += (i - s + 2) / 3;
            }
            else if (c == '\n')
            {
                while (i < n && (text[i] == '\n' || text[i] == '\r')) i++;
                tokens++;
            }
            else if (char.IsWhiteSpace(c))
            {
                int s = i;
                while (i < n && char.IsWhiteSpace(text[i]) && text[i] != '\n') i++;
                if (i - s > 1) tokens++; // single spaces merge into the next word
            }
            else
            {
                int s = i;
                while (i < n && !char.IsLetterOrDigit(text[i]) && !char.IsWhiteSpace(text[i])) i++;
                tokens += (i - s + 1) / 2;
            }
        }
        return tokens;
    }
}
