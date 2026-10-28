using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// 1.12.2 JSON chat components (decompiled <c>ITextComponent.Serializer</c>) flattened to the §-coded legacy text the b1.8.1
/// chat pipeline reads (<c>ChatText.TryParsePlayerChat</c> strips the codes and matches "&lt;name&gt; text"). Like
/// <c>ITextComponent.getFormattedText</c>: each piece gets its inherited style's codes (colour first, then §l §o §n §m §k)
/// and a §r after it; plain pieces get none. <c>translate</c> resolves through a <see cref="LangTable"/>, with its
/// <c>with</c> arguments substituted for <c>%s</c> / <c>%1$s</c>; <c>score</c> shows its value, <c>selector</c> its pattern,
/// <c>keybind</c> its key name. Malformed JSON falls back to the raw string (a server plugin's plain text).
/// </summary>
public static partial class ChatComponent
{
    private static readonly Dictionary<string, char> Colors = new(StringComparer.Ordinal)
    {
        ["black"] = '0', ["dark_blue"] = '1', ["dark_green"] = '2', ["dark_aqua"] = '3', ["dark_red"] = '4', ["dark_purple"] = '5',
        ["gold"] = '6', ["gray"] = '7', ["dark_gray"] = '8', ["blue"] = '9', ["green"] = 'a', ["aqua"] = 'b', ["red"] = 'c',
        ["light_purple"] = 'd', ["yellow"] = 'e', ["white"] = 'f', ["reset"] = 'r',
    };

    private readonly record struct Style(char? Color, bool Bold, bool Italic, bool Underlined, bool Strikethrough, bool Obfuscated)
    {
        public bool IsEmpty => Color is null && !Bold && !Italic && !Underlined && !Strikethrough && !Obfuscated;

        public string Codes()
        {
            if (IsEmpty) return "";
            var sb = new StringBuilder();
            if (Color is { } c) sb.Append('§').Append(c);
            if (Bold) sb.Append("§l");
            if (Italic) sb.Append("§o");
            if (Underlined) sb.Append("§n");
            if (Strikethrough) sb.Append("§m");
            if (Obfuscated) sb.Append("§k");
            return sb.ToString();
        }
    }

    /// <summary>The legacy text of a JSON component (null/empty → "").</summary>
    public static string ToLegacy(string? json, LangTable? lang = null)
    {
        if (string.IsNullOrEmpty(json)) return "";
        lang ??= LangTable.Builtin;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var sb = new StringBuilder();
            Append(sb, doc.RootElement, default, lang, 0);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return json; // not JSON: vanilla's lenient Gson reads a bare word as a string component
        }
    }

    /// <summary>The text with every § code removed.</summary>
    public static string ToPlain(string? json, LangTable? lang = null) => StripCodes(ToLegacy(json, lang));

    public static string StripCodes(string s) => Codes().Replace(s, "");

    /// <summary>A plain-text component as the server writes it (<c>{"text":"..."}</c>): fixtures, the fake server.</summary>
    public static string Text(string text) => JsonSerializer.Serialize(new { text });

    /// <summary>A translate component (<c>{"translate":key,"with":[…]}</c>) whose arguments are plain strings.</summary>
    public static string Translate(string key, params string[] with) => JsonSerializer.Serialize(new { translate = key, with });

    private static void Append(StringBuilder sb, JsonElement e, Style parent, LangTable lang, int depth)
    {
        if (depth > 64) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                Piece(sb, parent, e.GetString() ?? "");
                return;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                Piece(sb, parent, e.GetRawText());
                return;
            case JsonValueKind.Array:
            {
                // Gson: the first element is the component, the rest are its siblings (extra) and inherit its style.
                bool first = true;
                Style st = parent;
                foreach (var item in e.EnumerateArray())
                {
                    if (first) { st = item.ValueKind == JsonValueKind.Object ? StyleOf(item, parent) : parent; Append(sb, item, parent, lang, depth + 1); first = false; }
                    else Append(sb, item, st, lang, depth + 1);
                }
                return;
            }
            case JsonValueKind.Object:
                break;
            default:
                return;
        }

        var style = StyleOf(e, parent);
        string own;
        if (e.TryGetProperty("text", out var t)) own = t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : t.GetRawText();
        else if (e.TryGetProperty("translate", out var tr) && tr.ValueKind == JsonValueKind.String)
        {
            var args = new List<(JsonElement El, string Legacy)>();
            if (e.TryGetProperty("with", out var with) && with.ValueKind == JsonValueKind.Array)
                foreach (var a in with.EnumerateArray())
                {
                    var asb = new StringBuilder();
                    Append(asb, a, style, lang, depth + 1);
                    args.Add((a, asb.ToString()));
                }
            // Arguments carry their own codes; the format's literal text gets this component's style.
            TranslateInto(sb, lang, tr.GetString()!, args.Select(a => a.Legacy).ToList(), style);
            own = "";
            AppendExtra(sb, e, style, lang, depth);
            return;
        }
        else if (e.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Object)
            own = score.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        else if (e.TryGetProperty("selector", out var sel)) own = sel.GetString() ?? "";
        else if (e.TryGetProperty("keybind", out var kb)) own = kb.GetString() ?? "";
        else own = "";
        Piece(sb, style, own);
        AppendExtra(sb, e, style, lang, depth);
    }

    private static void AppendExtra(StringBuilder sb, JsonElement e, Style style, LangTable lang, int depth)
    {
        if (e.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            foreach (var x in extra.EnumerateArray()) Append(sb, x, style, lang, depth + 1);
    }

    private static void Piece(StringBuilder sb, Style st, string text)
    {
        if (text.Length == 0) return;
        if (st.IsEmpty) { sb.Append(text); return; }
        sb.Append(st.Codes()).Append(text).Append("§r");
    }

    private static Style StyleOf(JsonElement e, Style parent)
    {
        var s = parent;
        if (e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String && Colors.TryGetValue(c.GetString()!, out var code))
            s = s with { Color = code == 'r' ? null : code };
        bool Flag(string name, bool inherited) =>
            e.TryGetProperty(name, out var f) ? f.ValueKind == JsonValueKind.True || (f.ValueKind == JsonValueKind.String && f.GetString() == "true") : inherited;
        return s with
        {
            Bold = Flag("bold", s.Bold), Italic = Flag("italic", s.Italic), Underlined = Flag("underlined", s.Underlined),
            Strikethrough = Flag("strikethrough", s.Strikethrough), Obfuscated = Flag("obfuscated", s.Obfuscated),
        };
    }

    /// <summary>TextComponentTranslation.initializeFromFormat: <c>%s</c> takes the next argument, <c>%2$s</c> a given one,
    /// <c>%%</c> is a percent sign. A key the table doesn't know shows as itself followed by its arguments (vanilla shows
    /// just the key; the arguments keep a player name or death cause readable).</summary>
    private static void TranslateInto(StringBuilder sb, LangTable lang, string key, List<string> args, Style style)
    {
        if (!lang.TryGet(key, out var format))
        {
            Piece(sb, style, key);
            foreach (var a in args) sb.Append(' ').Append(a);
            return;
        }
        int next = 0, last = 0;
        foreach (Match m in FormatSpec().Matches(format))
        {
            Piece(sb, style, format[last..m.Index]);
            last = m.Index + m.Length;
            string conv = m.Groups[2].Value;
            if (conv == "%") { Piece(sb, style, "%"); continue; }
            int idx = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) - 1 : next++;
            if (idx >= 0 && idx < args.Count) sb.Append(args[idx]);
        }
        Piece(sb, style, format[last..]);
    }

    [GeneratedRegex(@"%(?:(\d+)\$)?([A-Za-z%]|$)")]
    private static partial Regex FormatSpec();

    [GeneratedRegex("§.?")]
    private static partial Regex Codes();
}

/// <summary>
/// A translation table (<c>assets/minecraft/lang/en_us.lang</c>: <c>key=value</c> lines). <see cref="Builtin"/> holds the keys
/// the bot's chat handling needs (player chat, emotes, joins/leaves, common kicks and deaths) for when no client jar is on the
/// machine; <see cref="LoadDefault"/> reads the whole table from the user's 1.12.2 client jar (a zip; nothing in it runs).
/// </summary>
public sealed class LangTable
{
    private readonly Dictionary<string, string> _map;

    public LangTable(Dictionary<string, string> map) => _map = map;

    public int Count => _map.Count;
    public bool TryGet(string key, out string value) => _map.TryGetValue(key, out value!);

    /// <summary>The en_us values (verbatim from the 1.12.2 client jar) of the keys chat handling relies on.</summary>
    public static LangTable Builtin { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["chat.type.text"] = "<%s> %s",
        ["chat.type.text.narrate"] = "%s says %s",
        ["chat.type.emote"] = "* %s %s",
        ["chat.type.announcement"] = "[%s] %s",
        ["chat.type.admin"] = "[%s: %s]",
        ["chat.type.advancement.task"] = "%s has made the advancement %s",
        ["chat.type.advancement.challenge"] = "%s has completed the challenge %s",
        ["chat.type.advancement.goal"] = "%s has reached the goal %s",
        ["commands.message.display.incoming"] = "%s whispers to you: %s",
        ["commands.message.display.outgoing"] = "You whisper to %s: %s",
        ["multiplayer.player.joined"] = "%s joined the game",
        ["multiplayer.player.joined.renamed"] = "%s (formerly known as %s) joined the game",
        ["multiplayer.player.left"] = "%s left the game",
        ["multiplayer.disconnect.kicked"] = "Kicked by an operator.",
        ["multiplayer.disconnect.server_shutdown"] = "Server closed",
        ["multiplayer.disconnect.duplicate_login"] = "You logged in from another location",
        ["multiplayer.disconnect.idling"] = "You have been idle for too long!",
        ["multiplayer.disconnect.flying"] = "Flying is not enabled on this server",
        ["multiplayer.disconnect.outdated_client"] = "Outdated client! Please use %s",
        ["multiplayer.disconnect.outdated_server"] = "Outdated server! I'm still on %s",
        ["multiplayer.disconnect.banned"] = "You are banned from this server.",
        ["multiplayer.disconnect.illegal_characters"] = "Illegal characters in chat",
        ["multiplayer.disconnect.invalid_player_movement"] = "Invalid move player packet received",
        ["multiplayer.disconnect.invalid_vehicle_movement"] = "Invalid move vehicle packet received",
        ["disconnect.timeout"] = "Timed out",
        ["disconnect.spam"] = "Kicked for spamming",
        ["disconnect.endOfStream"] = "End of stream",
        ["disconnect.genericReason"] = "%s",
        ["disconnect.lost"] = "Connection Lost",
        ["commands.kick.success"] = "Kicked %s from the game",
        ["death.attack.generic"] = "%1$s died",
        ["death.attack.player"] = "%1$s was slain by %2$s",
        ["death.attack.mob"] = "%1$s was slain by %2$s",
        ["death.attack.arrow"] = "%1$s was shot by %2$s",
        ["death.attack.explosion"] = "%1$s blew up",
        ["death.attack.explosion.player"] = "%1$s was blown up by %2$s",
        ["death.attack.fall"] = "%1$s hit the ground too hard",
        ["death.attack.outOfWorld"] = "%1$s fell out of the world",
        ["death.attack.lava"] = "%1$s tried to swim in lava",
        ["death.attack.inFire"] = "%1$s went up in flames",
        ["death.attack.onFire"] = "%1$s burned to death",
        ["death.attack.drown"] = "%1$s drowned",
        ["death.attack.starve"] = "%1$s starved to death",
        ["death.attack.inWall"] = "%1$s suffocated in a wall",
        ["death.attack.cactus"] = "%1$s was pricked to death",
        ["death.attack.magic"] = "%1$s was killed by magic",
        ["death.fell.accident.generic"] = "%1$s fell from a high place",
    });

    /// <summary>Parses a .lang file (UTF-8, <c>key=value</c>, '#' comments).</summary>
    public static LangTable Parse(TextReader reader)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            map[line[..eq]] = line[(eq + 1)..];
        }
        return new LangTable(map);
    }

    /// <summary>Reads <c>assets/minecraft/lang/en_us.lang</c> from a client jar, over the built-in keys.</summary>
    public static LangTable FromJar(string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        var entry = zip.GetEntry("assets/minecraft/lang/en_us.lang") ?? throw new FileNotFoundException("no en_us.lang in " + jarPath);
        if (entry.Length > 4 << 20) throw new InvalidDataException("en_us.lang too large");
        using var r = new StreamReader(entry.Open(), Encoding.UTF8);
        var t = Parse(r);
        foreach (var (k, v) in Builtin._map) t._map.TryAdd(k, v);
        return t;
    }

    /// <summary>Where a 1.12.2 client jar usually is: env MINECLAW_CLIENT_JAR_1122, then the PrismLauncher libraries and
    /// the vanilla launcher's versions folder.</summary>
    public static IEnumerable<string> CandidateJars()
    {
        if (Environment.GetEnvironmentVariable("MINECLAW_CLIENT_JAR_1122") is { Length: > 0 } env) yield return env;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { appData, Path.Combine(home, ".local", "share"), home }.Where(r => r.Length > 0).Distinct())
        {
            yield return Path.Combine(root, "PrismLauncher", "libraries", "com", "mojang", "minecraft", "1.12.2", "minecraft-1.12.2-client.jar");
            yield return Path.Combine(root, ".minecraft", "versions", "1.12.2", "1.12.2.jar");
        }
    }

    private static LangTable? _default;

    /// <summary>The full table from the first client jar found (read once per process), else <see cref="Builtin"/>.</summary>
    public static LangTable LoadDefault(Action<string>? log = null)
    {
        if (_default is { } d) return d;
        foreach (var jar in CandidateJars())
        {
            try
            {
                if (!File.Exists(jar)) continue;
                var t = FromJar(jar);
                log?.Invoke($"1.12.2 chat translations: {t.Count} keys from {jar}");
                return _default = t;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log?.Invoke($"can't read en_us.lang from {jar}: {ex.Message}");
            }
        }
        return _default = Builtin;
    }
}
