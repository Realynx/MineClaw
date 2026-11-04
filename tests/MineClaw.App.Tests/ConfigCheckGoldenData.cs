namespace MineClaw.App.Tests;

/// <summary>ConfigCheckGoldenData's dump (generated from the pre-refactor code).</summary>
internal static class ConfigCheckGoldenData
{
    public const string Expected = """"""
== valid
== empty host
Minecraft:Host is empty
== port 0
Minecraft:Port 0 is not a valid port
== port 65536
Minecraft:Port 65536 is not a valid port
== port 65535
== empty username
Minecraft:Username must be 1-16 characters
== long username
Minecraft:Username must be 1-16 characters
== 16-char username
== ollama tier without endpoint
Agent:Llm:MainProvider is ollama but Agent:Llm:Ollama:Endpoint is empty
== unknown provider
Agent:Llm:CheapProvider 'nope' is not a provider (anthropic|ollama|mock)
== mock tier
== mock tier, missing script
Agent:Llm:MockScriptPath: <the IO message for the missing path>
== old perception model
Agent:Llm:Perception:Model 'claude-3-5-sonnet' is older than the 5.5 generation
== old perception model, padded provider
Agent:Llm:Perception:Model 'claude-3-5-sonnet' is older than the 5.5 generation
== old perception model off anthropic
== no api key
Agent:Llm:ApiKey is missing (or set ANTHROPIC_API_KEY)
== blank api key
Agent:Llm:ApiKey is missing (or set ANTHROPIC_API_KEY)
== not an anthropic key
Agent:Llm:ApiKey doesn't look like an Anthropic key (sk-ant-...)
== no key, all tiers on ollama
== no cheap model
Agent:Llm:CheapModel '' is older than the 5.5 generation (use one of claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5, claude-fable-5-1, claude-mythos-5-1)
Agent:Llm:CheapModel/MainModel must be set
== no main model
Agent:Llm:MainModel ' ' is older than the 5.5 generation (use one of claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5, claude-fable-5-1, claude-mythos-5-1)
Agent:Llm:CheapModel/MainModel must be set
== zero daily cap
Agent:Budget:DailyCapUsd must be > 0
== unpriced cheap model
no price for model claude-haiku-5-5-x: add Agent:Prices:Models:claude-haiku-5-5-x
== unpriced main model
no price for model claude-opus-5-5-x: add Agent:Prices:Models:claude-opus-5-5-x
== unpriced main model on ollama
== ollama embeddings, nothing set
Agent:Embeddings:Provider is ollama but Agent:Embeddings:Model is empty
Agent:Embeddings:Dimensions must be the Ollama model's vector size
Agent:Embeddings:Provider is ollama but neither Agent:Embeddings:Endpoint nor Agent:Llm:Ollama:Endpoint is set
== ollama embeddings, rag off
== ollama embeddings via the llm endpoint
== ollama embeddings with endpoint
Agent:Embeddings:Dimensions must be the Ollama model's vector size
== chat username differs
Agent:Chat:Username (someone) should equal Minecraft:Username (maid_chan)
== chat username case
== everything wrong
Minecraft:Host is empty
Minecraft:Port -1 is not a valid port
Minecraft:Username must be 1-16 characters
Agent:Llm:WorkerProvider is ollama but Agent:Llm:Ollama:Endpoint is empty
Agent:Llm:CheapModel '' is older than the 5.5 generation (use one of claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5, claude-fable-5-1, claude-mythos-5-1)
Agent:Llm:Perception:Model 'claude-3-opus' is older than the 5.5 generation
Agent:Llm:ApiKey doesn't look like an Anthropic key (sk-ant-...)
Agent:Llm:CheapModel/MainModel must be set
Agent:Budget:DailyCapUsd must be > 0
no price for model claude-opus-5-5-x: add Agent:Prices:Models:claude-opus-5-5-x
Agent:Embeddings:Provider is ollama but Agent:Embeddings:Model is empty
Agent:Embeddings:Dimensions must be the Ollama model's vector size
Agent:Embeddings:Provider is ollama but neither Agent:Embeddings:Endpoint nor Agent:Llm:Ollama:Endpoint is set
Agent:Chat:Username (x) should equal Minecraft:Username ()
"""""";
}
