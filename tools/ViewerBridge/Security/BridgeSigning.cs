using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.ViewerBridge.Security;

/// <summary>
/// Nest ↔ bridge request signing, exactly per docs/web/api.md §3 (and <c>bridgeSigningString</c> in @mineclaw/contracts):
/// HMAC-SHA256 (key = UTF-8 of Bridge:Key) over eight lines joined by "\n", no trailing newline:
/// METHOD, PATH?QUERY (the raw request target), TIMESTAMP (unix s), NONCE (32 lowercase hex), SHA256(BODY) (lowercase hex),
/// X-MCX-User, X-MCX-Player, X-MCX-Roles ("" when absent). Signature = lowercase hex.
/// </summary>
public static partial class BridgeSigning
{
    public const string TsHeader = "X-MCX-Ts";
    public const string NonceHeader = "X-MCX-Nonce";
    public const string SigHeader = "X-MCX-Sig";
    public const string UserHeader = "X-MCX-User";
    public const string PlayerHeader = "X-MCX-Player";
    public const string RolesHeader = "X-MCX-Roles";
    public const string RequestIdHeader = "X-MCX-Request-Id";
    public const int MaxSkewSeconds = 30;
    public const int NonceTtlSeconds = 300;
    public const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [GeneratedRegex("^[0-9a-f]{32}$")]
    public static partial Regex NonceShape();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex SigShape();

    public static string Sha256Hex(ReadOnlySpan<byte> body) => Convert.ToHexStringLower(SHA256.HashData(body));

    public static string SigningString(string method, string pathAndQuery, string timestamp, string nonce, string bodySha256, string user, string player, string roles)
    {
        foreach (var v in new[] { user, player, roles })
            if (v.Contains('\r') || v.Contains('\n')) throw new ArgumentException("identity headers must not contain CR or LF");
        return string.Join('\n', method.ToUpperInvariant(), pathAndQuery, timestamp, nonce, bodySha256, user, player, roles);
    }

    public static string Sign(string key, string signingString)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(signingString));
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>Constant-time compare of a lowercase hex signature against the expected one.</summary>
    public static bool SignatureMatches(string key, string signingString, string? given)
    {
        if (given == null || !SigShape().IsMatch(given)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(signingString));
        var g = Convert.FromHexString(given);
        return CryptographicOperations.FixedTimeEquals(expected, g);
    }

    /// <summary>The <c>X-MCX-Roles</c> form: lowercase, de-duplicated, sorted (ordinal), comma-joined.</summary>
    public static string CanonicalRoles(IEnumerable<string> roles) =>
        string.Join(',', roles.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).Distinct().Order(StringComparer.Ordinal));
}

/// <summary>Seen nonces for the replay window: a time-ordered queue plus a set, bounded (oldest dropped first).</summary>
public sealed class NonceCache(TimeProvider time, int ttlSeconds = BridgeSigning.NonceTtlSeconds, int capacity = 200_000)
{
    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly Queue<(string Nonce, long Expires)> _order = new();
    private readonly object _lock = new();

    /// <summary>True when the nonce is new (and records it); false = a replay within the window.</summary>
    public bool TryAdd(string nonce)
    {
        long now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_lock)
        {
            while (_order.Count > 0 && (_order.Peek().Expires <= now || _order.Count >= capacity))
            {
                var (n, exp) = _order.Dequeue();
                if (_seen.TryGetValue(n, out var e) && e == exp) _seen.Remove(n);
            }
            if (_seen.TryGetValue(nonce, out var expires) && expires > now) return false;
            long until = now + ttlSeconds * 1000L;
            _seen[nonce] = until;
            _order.Enqueue((nonce, until));
            return true;
        }
    }

    public int Count { get { lock (_lock) return _seen.Count; } }
}

/// <summary>Who Nest says is asking (covered by the signature).</summary>
public sealed record BridgeIdentity(string User, string Player, IReadOnlySet<string> Roles, string? RequestId)
{
    public static readonly BridgeIdentity Anonymous = new("", "", new HashSet<string>(), null);
    public bool Has(string role) => Roles.Contains(role);
    public bool IsAdmin => Has("admin");
    /// <summary>The <c>who</c> the bridge writes on admin calls: "&lt;user&gt; (web)".</summary>
    public string Who => (User.Length > 0 ? User : "unknown") + " (web)";

    public static IReadOnlySet<string> ParseRoles(string header) =>
        header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
}
