using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

public class OllamaEmbeddingTests
{
    private static EmbeddingOptions Opts(int dims = 3) => new() { Provider = "ollama", Model = "nomic-embed-text", Dimensions = dims };

    [Fact]
    public async Task Embed_endpoint_maps_request_and_response()
    {
        var fake = new FakeOllama { Embed = _ => FakeOllama.Json(new { model = "nomic-embed-text", embeddings = new[] { new[] { 0.1f, 0.2f, 0.3f }, new[] { 1f, 0f, 0f } }, prompt_eval_count = 17 }) };
        var server = new OllamaOptions { Endpoint = "http://ollama.test:11434/", KeepAlive = "1h" };
        var p = new OllamaEmbeddingProvider(Opts(), server, new HttpClient(fake));
        Assert.True(p.IsAvailable);
        Assert.Equal("nomic-embed-text", p.Model);
        var r = await p.EmbedAsync(["cobble gen", "oak tree"], default);
        Assert.Equal(2, r.Vectors.Count);
        Assert.Equal(0.2f, r.Vectors[0][1]);
        Assert.Equal(17, r.Tokens);
        var req = fake.Requests.Single();
        Assert.Equal("/api/embed", req.Path);
        Assert.Equal("nomic-embed-text", req.Json.GetProperty("model").GetString());
        Assert.Equal(["cobble gen", "oak tree"], req.Json.GetProperty("input").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("1h", req.Json.GetProperty("keep_alive").GetString());
        Assert.Equal(0m, Opts().PricePerMTok); // local embeddings are free unless priced
    }

    [Fact]
    public async Task A_dimension_mismatch_is_refused()
    {
        var fake = new FakeOllama { Embed = _ => FakeOllama.Json(new { embeddings = new[] { new[] { 0.1f, 0.2f } } }) };
        var p = new OllamaEmbeddingProvider(Opts(dims: 1024), new OllamaOptions { Endpoint = "http://ollama.test:11434" }, new HttpClient(fake));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => p.EmbedAsync(["x"], default));
        Assert.Contains("2 dimensions but Agent:Embeddings:Dimensions is 1024", ex.Message);
    }

    [Fact]
    public void Provider_selection_keeps_azure_the_default_and_needs_a_model_and_endpoint_for_ollama()
    {
        Assert.Same(NullEmbeddingProvider.Instance, EmbeddingProviders.Create(new EmbeddingOptions()));          // Azure, unconfigured
        Assert.IsType<AzureOpenAIEmbeddingProvider>(EmbeddingProviders.Create(new EmbeddingOptions { Endpoint = "https://x.openai.azure.com/", ApiKey = "k" }));
        Assert.Same(NullEmbeddingProvider.Instance, EmbeddingProviders.Create(Opts()));                           // ollama, no endpoint
        Assert.IsType<OllamaEmbeddingProvider>(EmbeddingProviders.Create(Opts(), new OllamaOptions { Endpoint = "http://o:11434" }));
        Assert.Same(NullEmbeddingProvider.Instance, EmbeddingProviders.Create(new EmbeddingOptions { Provider = "ollama", Dimensions = 768 },
            new OllamaOptions { Endpoint = "http://o:11434" })); // no model
    }

    private sealed class Fixed(int dims) : IEmbeddingProvider
    {
        public string Model => "m" + dims;
        public int Dimensions => dims;
        public bool IsAvailable => true;
        public Task<EmbeddingResponse> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            Task.FromResult(new EmbeddingResponse(inputs.Select(_ => new float[dims]).ToList(), 1));
    }

    [Fact]
    public void One_db_never_mixes_dimensions()
    {
        using var db = AgentDb.Open(null);
        db.Exec("INSERT INTO docs(id, ref, kind, title, body, hash, created, updated) VALUES (1, 'note:k', 'note', 't', 'b', 'h', 0, 0)");
        db.Exec("INSERT INTO vectors(doc_id, model, dims, hash, vec) VALUES (1, 'text-embedding-3-large', 1024, 'h', $v)", ("$v", new byte[16]));

        var (same, note0) = EmbeddingDimensionGuard.Check(db, new Fixed(1024), reembed: false);
        Assert.IsType<Fixed>(same);
        Assert.Null(note0);

        var (refused, note) = EmbeddingDimensionGuard.Check(db, new Fixed(768), reembed: false);
        Assert.Same(NullEmbeddingProvider.Instance, refused);
        Assert.Contains("1024 dimensions", note);
        Assert.Contains("Reembed", note);
        Assert.Equal(1, db.ScalarLong("SELECT COUNT(*) FROM vectors"));

        var (kept, note2) = EmbeddingDimensionGuard.Check(db, new Fixed(768), reembed: true);
        Assert.IsType<Fixed>(kept);
        Assert.Contains("dropped 1", note2);
        Assert.Equal(0, db.ScalarLong("SELECT COUNT(*) FROM vectors"));
    }
}
