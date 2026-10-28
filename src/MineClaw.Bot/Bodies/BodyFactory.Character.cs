using MineClaw.Agent;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Character;
using MineClaw.Agent.Tools;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// The reactive character on main's body (docs/design/reactive-character.md): the typed game-event signals, the binding runner
/// (standing bindings and learned reactions, no model in the loop), the <c>character</c> SDK port for scripts, the line
/// prefetcher's context and timer, and the instant "ow" when a player hits the bot.
/// </summary>
public static partial class BodyFactory
{
    internal static void AttachCharacter(BrainContext brain, Body body)
    {
        var rt = brain.Runtime;
        var client = body.Adapter;
        var time = brain.Time ?? TimeProvider.System;
        var log = brain.Loggers?.CreateLogger("MineClaw.Character");
        IEnumerable<string> Names() => [rt.Chat.Username, rt.Chat.DisplayName, .. rt.Chat.Aliases];

        var signals = new Perception.GameSignals(client, brain.IsBot, time) { Intent = t => ChatIntent.Classify(t, Names()).Intent };
        signals.Attach();
        rt.Chat.AddressDecided += (m, _) =>
        {
            try { if (m.Player is { Length: > 0 } p && m.Kind is Agent.Chat.ChatKind.Public or Agent.Chat.ChatKind.Whisper) signals.OnChat(p, m.Text); }
            catch (Exception) { /* chat never breaks */ }
        };
        body.Signals = signals;

        // Bindings live in the shared db: only the main process runs them (a clone process would fire main's on its own body).
        bool mainProcess = rt.Options.Hive.ScopeFor(rt.Options.Chat.Username) == Agent.Self.SelfSchema.MainScope;
        if (body.ScriptBridge is { } lane)
        {
            lane.Controller.Host.Character = new GameTools.CharacterPort(rt, client, () => body.Gestures, time);
            if (mainProcess)
            {
                var runner = new Brainstem.BindingRunner(rt, client, lane, time, log);
                signals.Raised += runner.OnSignal;
                body.Bindings = runner;
            }
            else signals.Raised += s => { try { lane.Controller.Deliver(s.ToScriptEvent()); } catch (Exception) { } };
        }

        // Hit by a player: an instant "ow" (prefetched, typed like a human), at most every 20 s; the brain hears the hurt as usual.
        var lastOw = DateTimeOffset.MinValue;
        signals.Raised += s =>
        {
            if (s.Name != "me.hurt" || Scripting.Events.EventFilter.Str(s.Data, "by") is not { } by || client.World.Entities.FindPlayer(by) == null) return;
            var now = time.GetUtcNow();
            if (now - lastOw < TimeSpan.FromSeconds(20) || rt.Agent.Voice is not { } voice) return;
            lastOw = now;
            try { voice.SayInstant(by, "hurt", "hit by a player", rt.Activities.Shutdown); } catch (Exception) { }
        };

        if (rt.Agent.Voice?.Prefetcher is { } prefetch)
            prefetch.Context = () => ContextOf(rt, client, body, brain.IsBot);

        if (brain.Persist)
        {
            var tick = new System.Threading.Timer(_ => { try { signals.Tick(); } catch (Exception) { } }, null, 1000, 250);
            var o = rt.Options.Brainstem.Character;
            var period = TimeSpan.FromSeconds(Math.Clamp(o.RefreshSeconds, 5, 600));
            var refresh = new System.Threading.Timer(_ =>
            {
                if (rt.Agent.Voice?.Prefetcher is { } p) _ = p.RefreshAsync(rt.Activities.Shutdown);
            }, null, TimeSpan.FromSeconds(10), period);
            body.Owned.Add(tick);
            body.Owned.Add(refresh);
        }
    }

    /// <summary>The wording context: what the body is doing, who is on, time of day, mood, health (a line bank tag).</summary>
    internal static CharacterContext ContextOf(AgentRuntime rt, Game.IGameClient client, Body body, Func<string, bool> isBot)
    {
        var w = client.World;
        string activity = body.ScriptBridge?.Controller.Current is { IsLive: true } s ? "script " + s.Name
            : body.Activities.Current is { } a ? a.Label : "idle";
        var players = w.Entities.OnlinePlayers.Where(p => !p.Equals(client.Username, StringComparison.OrdinalIgnoreCase) && !isBot(p)).Take(6).ToList();
        long t = w.TimeOfDay;
        string tod = t < 12000 ? "day" : t < 13800 ? "dusk" : t < 22500 ? "night" : "dawn";
        string mood = "";
        try { mood = rt.Growth?.Mood().Words() ?? ""; } catch (Exception) { }
        int hp = w.Player.Health;
        string health = hp >= 14 ? "ok" : hp >= 8 ? "hurt" : "low";
        return new CharacterContext(activity, players, tod, mood, health);
    }
}
