using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MineClaw.Agent.Tests")]

// The vision history test checks the captured sheet against the bytes AnthropicLlmClient.BuildParams serializes.
[assembly: InternalsVisibleTo("MineClaw.Bot.Tests")]
