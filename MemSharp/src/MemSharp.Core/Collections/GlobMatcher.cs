namespace MemSharp.Collections;

/// <summary>
/// Redis-style glob matching for <c>KEYS</c>, <c>SCAN</c> and <c>PSUBSCRIBE</c>.
/// </summary>
/// <remarks>
/// Supports <c>*</c>, <c>?</c>, character classes <c>[abc]</c> / <c>[a-z]</c> / <c>[^abc]</c>, and
/// <c>\</c> escaping. The original engine translated patterns into a <see cref="System.Text.RegularExpressions.Regex"/>
/// per call, which allocated a compiled state machine on every <c>KEYS</c> - the cost was quadratic
/// in the keyspace and showed up as the slowest operation in the benchmark by two orders of
/// magnitude. This is an iterative backtracking matcher over spans: no allocation, no cache.
/// </remarks>
public static class GlobMatcher
{
    /// <summary>True if <paramref name="value"/> matches <paramref name="pattern"/>.</summary>
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        int p = 0, v = 0;
        int starPattern = -1, starValue = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length)
            {
                char pc = pattern[p];

                if (pc == '*')
                {
                    // Remember where the star was so a later mismatch can resume by letting the
                    // star swallow one more character, instead of recursing.
                    starPattern = p++;
                    starValue = v;
                    continue;
                }

                if (pc == '?')
                {
                    p++; v++;
                    continue;
                }

                if (pc == '[')
                {
                    int close = FindClassEnd(pattern, p);
                    if (close > 0 && MatchesClass(pattern[(p + 1)..close], value[v]))
                    {
                        p = close + 1; v++;
                        continue;
                    }
                }
                else
                {
                    if (pc == '\\' && p + 1 < pattern.Length) pc = pattern[++p];
                    if (pc == value[v])
                    {
                        p++; v++;
                        continue;
                    }
                }
            }

            if (starPattern >= 0)
            {
                p = starPattern + 1;
                v = ++starValue;
                continue;
            }

            return false;
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>True if the pattern can only match itself, so callers can take a direct lookup.</summary>
    public static bool IsLiteral(ReadOnlySpan<char> pattern)
    {
        foreach (char c in pattern)
        {
            if (c is '*' or '?' or '[' or '\\') return false;
        }
        return true;
    }

    /// <summary>Translates a SQL <c>LIKE</c> pattern (<c>%</c>, <c>_</c>) into glob syntax.</summary>
    public static string FromSqlLike(string like)
    {
        var buffer = new System.Text.StringBuilder(like.Length + 4);
        foreach (char c in like)
        {
            switch (c)
            {
                case '%': buffer.Append('*'); break;
                case '_': buffer.Append('?'); break;
                case '*' or '?' or '[' or '\\': buffer.Append('\\').Append(c); break;
                default: buffer.Append(c); break;
            }
        }
        return buffer.ToString();
    }

    private static int FindClassEnd(ReadOnlySpan<char> pattern, int open)
    {
        int i = open + 1;
        if (i < pattern.Length && pattern[i] is '^' or '!') i++;
        if (i < pattern.Length && pattern[i] == ']') i++;   // a leading ] is a literal
        while (i < pattern.Length && pattern[i] != ']') i++;
        return i < pattern.Length ? i : -1;
    }

    private static bool MatchesClass(ReadOnlySpan<char> body, char c)
    {
        bool negate = body.Length > 0 && (body[0] == '^' || body[0] == '!');
        if (negate) body = body[1..];

        bool hit = false;
        for (int i = 0; i < body.Length; i++)
        {
            if (i + 2 < body.Length && body[i + 1] == '-')
            {
                if (c >= body[i] && c <= body[i + 2]) hit = true;
                i += 2;
            }
            else if (body[i] == c)
            {
                hit = true;
            }
        }
        return hit ^ negate;
    }
}
