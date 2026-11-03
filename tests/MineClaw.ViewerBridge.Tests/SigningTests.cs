using MineClaw.ViewerBridge.Security;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>The two signing vectors of docs/web/api.md §3 (also checked by web/packages/contracts/test/bridge.test.ts).</summary>
public class SigningTests
{
    public const string Key = "test-key-not-a-secret-0123456789abcdef";
    public const string PostBody = """{"bot":"maid_chan","text":"claude, set it to day","request_id":"r-8f14e45fceea"}""";

    [Fact]
    public void Get_vector_reproduces_the_documented_signature()
    {
        var s = BridgeSigning.SigningString("GET", "/v1/memory/facts?q=fox&offset=0&limit=50", "1762077600", "00112233445566778899aabbccddeeff",
            BridgeSigning.Sha256Hex([]), "fox", "poofimafox", "admin,chat,commands,viewer");
        Assert.Equal(BridgeSigning.EmptyBodySha256, BridgeSigning.Sha256Hex([]));
        Assert.Equal("675be9cf1726a3d05bd596235a6f55f84d52c0982a7f2c5450a6a221ebff20b5", BridgeSigning.Sign(Key, s));
        Assert.True(BridgeSigning.SignatureMatches(Key, s, "675be9cf1726a3d05bd596235a6f55f84d52c0982a7f2c5450a6a221ebff20b5"));
    }

    [Fact]
    public void Post_vector_reproduces_the_documented_body_hash_and_signature()
    {
        var hash = BridgeSigning.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(PostBody));
        Assert.Equal("1799cd6a496b955c3b09614f6ea849740d8a960cf4953323d89de775321e6da9", hash);
        var s = BridgeSigning.SigningString("post", "/v1/sup/viewer/chat", "1762077600", "ffeeddccbbaa99887766554433221100", hash, "robin", "kiwi", "chat,viewer");
        Assert.Equal("c13b3496a88ed8926bf6f910f93443800d5d0f0a56c77a545280da5e11b505d0", BridgeSigning.Sign(Key, s));
    }

    [Fact]
    public void Signing_string_is_eight_lines_without_a_trailing_newline()
    {
        var s = BridgeSigning.SigningString("GET", "/v1/health", "1", "00112233445566778899aabbccddeeff", BridgeSigning.EmptyBodySha256, "fox", "poofimafox", "viewer");
        Assert.Equal(8, s.Split('\n').Length);
        Assert.EndsWith("\nviewer", s);
        // Absent identity headers are empty lines (still eight of them).
        Assert.Equal(8, BridgeSigning.SigningString("GET", "/", "1", "n", "h", "", "", "").Split('\n').Length);
        Assert.Throws<ArgumentException>(() => BridgeSigning.SigningString("GET", "/", "1", "n", "h", "fox\nadmin", "", ""));
    }

    [Fact]
    public void Wrong_or_malformed_signatures_do_not_match()
    {
        var s = BridgeSigning.SigningString("GET", "/v1/health", "1", "00112233445566778899aabbccddeeff", BridgeSigning.EmptyBodySha256, "", "", "");
        var good = BridgeSigning.Sign(Key, s);
        Assert.False(BridgeSigning.SignatureMatches(Key, s, good.ToUpperInvariant()));
        Assert.False(BridgeSigning.SignatureMatches(Key, s, good[..^1] + (good[^1] == '0' ? '1' : '0')));
        Assert.False(BridgeSigning.SignatureMatches(Key, s, null));
        Assert.False(BridgeSigning.SignatureMatches("other-key-other-key-other-key-0000", s, good));
    }

    [Fact]
    public void Canonical_roles_are_lowercase_unique_sorted()
    {
        Assert.Equal("admin,chat,commands,viewer", BridgeSigning.CanonicalRoles(["viewer", "Chat", "admin", "commands", "chat"]));
        Assert.Equal("", BridgeSigning.CanonicalRoles([]));
    }

    [Fact]
    public void Nonce_cache_rejects_replays_within_the_window_and_forgets_after_it()
    {
        var time = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(1762077600));
        var cache = new NonceCache(time, ttlSeconds: 300);
        Assert.True(cache.TryAdd("00112233445566778899aabbccddeeff"));
        Assert.False(cache.TryAdd("00112233445566778899aabbccddeeff"));
        time.Advance(TimeSpan.FromSeconds(299));
        Assert.False(cache.TryAdd("00112233445566778899aabbccddeeff"));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(cache.TryAdd("00112233445566778899aabbccddeeff"));
    }

    [Fact]
    public void Only_loopback_sources_are_accepted()
    {
        Assert.True(BridgeAuthMiddleware.IsLoopback(System.Net.IPAddress.Loopback));
        Assert.True(BridgeAuthMiddleware.IsLoopback(System.Net.IPAddress.IPv6Loopback));
        Assert.True(BridgeAuthMiddleware.IsLoopback(System.Net.IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(BridgeAuthMiddleware.IsLoopback(System.Net.IPAddress.Parse("10.1.2.73")));
        Assert.False(BridgeAuthMiddleware.IsLoopback(System.Net.IPAddress.Parse("::ffff:10.1.2.73")));
        Assert.False(BridgeAuthMiddleware.IsLoopback(null));
    }
}

public sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan d) => _now += d;
}
