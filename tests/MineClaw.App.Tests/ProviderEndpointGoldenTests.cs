using MineClaw.App.Supervisor.Admin;

namespace MineClaw.App.Tests;

/// <summary>Pins <see cref="LiveProviderTester.CheckEndpoint"/> (WP07): every refusal and its text, and the addresses that pass.</summary>
public class ProviderEndpointGoldenTests
{
    [Theory]
    [InlineData(null, "an endpoint is required")]
    [InlineData("", "an endpoint is required")]
    [InlineData("   ", "an endpoint is required")]
    [InlineData("localhost:11434", "must be an absolute http:// or https:// URL")]
    [InlineData("ftp://10.0.0.5/", "must be an absolute http:// or https:// URL")]
    [InlineData("/relative", "must be an absolute http:// or https:// URL")]
    [InlineData("file:///etc/passwd", "must be an absolute http:// or https:// URL")]
    [InlineData("http://user:pw@10.0.0.5:11434", "credentials don't go in the URL")]
    [InlineData("https://user@example.com", "credentials don't go in the URL")]
    [InlineData("http://metadata.google.internal/computeMetadata", "that address is not allowed")]
    [InlineData("http://METADATA.google.internal", "that address is not allowed")]
    [InlineData("http://169.254.169.254/latest", "link-local addresses are not allowed")]
    [InlineData("http://169.254.0.1", "link-local addresses are not allowed")]
    [InlineData("http://[fe80::1]:11434", "link-local addresses are not allowed")]
    [InlineData("http://169.253.1.1", null)]
    [InlineData("http://170.254.1.1", null)]
    [InlineData("http://[::1]:11434", null)]
    [InlineData("http://[::ffff:169.254.1.1]", null)]
    [InlineData("http://10.0.0.5:11434", null)]
    [InlineData("  https://api.anthropic.com  ", null)]
    [InlineData("http://ollama.local:11434/v1", null)]
    public void Endpoint_check(string? endpoint, string? expected)
    {
        Assert.Equal(expected, LiveProviderTester.CheckEndpoint(endpoint));
    }
}
