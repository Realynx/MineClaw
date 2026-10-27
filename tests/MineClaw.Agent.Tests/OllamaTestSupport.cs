using System.Net;
using System.Text;
using System.Text.Json;

namespace MineClaw.Agent.Tests;

/// <summary>Fake Ollama server (HttpMessageHandler): /api/show, /api/tags, /api/chat, /api/embed; records every request.</summary>
public sealed class FakeOllama : HttpMessageHandler
{
    public sealed record Req(string Method, string Path, string Body, string? Auth)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    public List<Req> Requests { get; } = new();
    public string[]? Capabilities { get; set; } = ["completion", "tools"];
    public string[] Tags { get; set; } = ["qwen3:8b", "llama3.2:latest", "gpt-oss:120b-cloud"];
    public Func<string, CancellationToken, Task<HttpResponseMessage>>? Chat { get; set; }
    public Func<string, HttpResponseMessage>? Embed { get; set; }

    public IEnumerable<Req> ChatRequests { get { lock (Requests) return Requests.Where(r => r.Path.EndsWith("/api/chat")).ToList(); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content != null ? await r.Content.ReadAsStringAsync(ct) : "";
        lock (Requests) Requests.Add(new Req(r.Method.Method, r.RequestUri!.AbsolutePath, body, r.Headers.Authorization?.ToString()));
        var path = r.RequestUri.AbsolutePath;
        if (path.EndsWith("/api/show"))
            return Capabilities == null ? Json(new { modelfile = "" }) : Json(new { capabilities = Capabilities });
        if (path.EndsWith("/api/tags")) return Json(new { models = Tags.Select(t => new { name = t, model = t }) });
        if (path.EndsWith("/api/chat") && Chat != null) return await Chat(body, ct);
        if (path.EndsWith("/api/embed") && Embed != null) return Embed(body);
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not found"}""") };
    }

    public static HttpResponseMessage Json(object o) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Raw(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>NDJSON stream: the given lines, then (optionally) blocks until the request is cancelled.</summary>
    public static HttpResponseMessage Stream(IEnumerable<string> lines, bool hang = false) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new LineStream(lines.Select(l => l + "\n").ToArray(), hang)) };

    private sealed class LineStream(string[] lines, bool hang) : System.IO.Stream
    {
        private int _i;
        private byte[] _cur = [];
        private int _pos;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _cur.Length)
            {
                if (_i >= lines.Length)
                {
                    if (!hang) return 0;
                    await Task.Delay(Timeout.Infinite, ct);
                }
                _cur = Encoding.UTF8.GetBytes(lines[_i++]);
                _pos = 0;
            }
            int n = Math.Min(buffer.Length, _cur.Length - _pos);
            _cur.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
