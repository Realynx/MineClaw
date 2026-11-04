using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// A work role: a named priority template (docs/web/story-mode.md "Roles"). A pawn with a role works by the role's priorities; editing
/// the role changes every pawn that has it. Saved with the colony's meta.
/// </summary>
public sealed class WorkRole
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>A short icon key the browser draws (wheat, pickaxe, axe, …); null = the name's initial.</summary>
    public string? Icon { get; set; }
    /// <summary>work type → 0 (off) .. 4; a work type missing here is off.</summary>
    public Dictionary<string, int> Priorities { get; set; } = new();
}

/// <summary>
/// Roles (Fox 11-04: "make predefined roles that have specific priorities — e.g. a Farmer whose biggest priority is farming"). A pawn's
/// <see cref="PawnState.Role"/> names one; its effective priorities are the role's. They are kept in <see cref="PawnState.Priorities"/>
/// (rewritten whenever the role or the assignment changes), so the job picker, hauling, gear and every other reader see the effective
/// values with no change. Editing a cell of a pawn with a role detaches it: the role is cleared, the values stay (Custom).
/// </summary>
public sealed partial class StoryColony
{
    public static readonly IReadOnlyList<string> RoleOps = ["add", "update", "remove", "reset_defaults"];

    /// <summary>The built-in roles (editable, removable; <c>reset_defaults</c> brings them back as they were).</summary>
    public static IReadOnlyList<WorkRole> DefaultRoles()
    {
        static WorkRole R(string id, string name, string icon, params (string T, int P)[] ps) => new()
        {
            Id = id, Name = name, Icon = icon,
            Priorities = WorkTypes.All.ToDictionary(t => t, t => ps.FirstOrDefault(x => x.T == t).P),
        };
        const string G = WorkTypes.Guard, D = WorkTypes.Doctor, Co = WorkTypes.Cook, Hu = WorkTypes.Hunt, Fi = WorkTypes.Fish, B = WorkTypes.Build,
            Fa = WorkTypes.Farm, M = WorkTypes.Mine, Ch = WorkTypes.Chop, Cr = WorkTypes.Craft, Ha = WorkTypes.Haul;
        var generalist = R("generalist", "Generalist", "shapes");
        foreach (var t in WorkTypes.All) generalist.Priorities[t] = t == Ha ? 2 : 3;
        return
        [
            R("farmer", "Farmer", "wheat", (Fa, 1), (Co, 3), (Ha, 3), (D, 4)),
            R("miner", "Miner", "pickaxe", (M, 1), (Ha, 3), (B, 4)),
            R("woodcutter", "Woodcutter", "axe", (Ch, 1), (Ha, 3)),
            R("builder", "Builder", "brick", (B, 1), (Ha, 2), (Cr, 3)),
            R("hauler", "Hauler", "package", (Ha, 1), (Ch, 3)),
            R("crafter", "Crafter", "hammer", (Cr, 1), (Co, 2), (Ha, 3)),
            R("cook", "Cook", "pot", (Co, 1), (Fa, 3), (Ha, 3), (D, 4)),
            R("fisher", "Fisher", "fish", (Fi, 1), (Co, 3), (Ha, 3)),
            R("guard", "Guard / Hunter", "shield", (G, 1), (Hu, 1), (Ha, 3)),
            generalist,
        ];
    }

    private static bool IsDefaultRole(string id) => DefaultRoles().Any(r => r.Id == id);

    /// <summary>The colony's roles, the built-in ones seeded on first use (an older save has none).</summary>
    private List<WorkRole> RolesList()
    {
        if (Meta.Roles == null) { Meta.Roles = DefaultRoles().ToList(); _dirty = true; }
        return Meta.Roles;
    }

    private WorkRole? RoleById(string? id) => id == null ? null : RolesList().FirstOrDefault(r => r.Id == id);

    /// <summary>The pawn's effective priorities: its role's when it has one, else its own.</summary>
    public IReadOnlyDictionary<string, int> EffectivePriorities(PawnState p)
    {
        lock (_lock)
            return RoleById(p.Role) is { } r
                ? WorkTypes.All.ToDictionary(t => t, t => r.Priorities.GetValueOrDefault(t))
                : WorkTypes.All.ToDictionary(t => t, t => p.Priorities.GetValueOrDefault(t));
    }

    /// <summary>Writes the role's priorities into the pawn (the values the readers use). True when one changed.</summary>
    private static bool ApplyRole(PawnState p, WorkRole r)
    {
        bool changed = false;
        foreach (var t in WorkTypes.All)
        {
            int v = r.Priorities.GetValueOrDefault(t);
            if (p.Priorities.GetValueOrDefault(t) == v && p.Priorities.ContainsKey(t)) continue;
            p.Priorities[t] = v;
            changed = true;
        }
        return changed;
    }

    /// <summary>Every pawn with this role takes its priorities again (after an edit). True when any pawn's changed.</summary>
    private bool ReapplyRole(WorkRole r)
    {
        bool changed = false;
        foreach (var p in Pawns.Values.Where(p => p.Role == r.Id))
        {
            if (ApplyRole(p, r)) changed = true;
            p.Rev = Touch();
        }
        return changed;
    }

    /// <summary>
    /// <c>POST …/priorities {pawns, role}</c>: gives the pawns a role (its priorities from now on), or null for Custom (they keep the
    /// values they have).
    /// </summary>
    public string? SetRole(IReadOnlyList<PawnState> pawns, string? roleId)
    {
        lock (_lock)
        {
            WorkRole? r = null;
            if (roleId != null && (r = RoleById(roleId)) == null) return $"role: no such role '{roleId}'";
            bool changed = false;
            foreach (var p in pawns)
            {
                p.Role = r?.Id;
                if (r != null && ApplyRole(p, r)) changed = true;
                p.Rev = Touch();
            }
            if (changed) WakeIdle();
        }
        return null;
    }

    /// <summary>On load: a pawn whose role is gone is Custom; the others take their role's priorities (the effective values).</summary>
    internal bool MigrateRoles()
    {
        lock (_lock)
        {
            bool seeded = Meta.Roles == null, changed = false;
            foreach (var p in Pawns.Values.Where(p => p.Role != null))
            {
                if (RoleById(p.Role) is { } r) changed |= ApplyRole(p, r);
                else { p.Role = null; changed = true; }
            }
            return seeded || changed;
        }
    }

    /// <summary>
    /// <c>POST …/roles</c>: <c>{op:"add", role:{name, icon?, priorities?}}</c> → {id, roles} | <c>{op:"update", role:{id, name?, icon?,
    /// priorities?}}</c> (priorities merge; every pawn with the role follows) | <c>{op:"remove", id}</c> (its pawns turn Custom with the
    /// same values) | <c>{op:"reset_defaults"}</c> (the built-in roles as shipped, added back if removed; own roles kept).
    /// </summary>
    public (JsonNode? Result, string? Error) RolesOp(JsonObject body)
    {
        var op = Str(body["op"]) ?? "";
        if (!RoleOps.Contains(op)) return (null, $"op must be {string.Join("|", RoleOps)}");
        var spec = body["role"] as JsonObject;
        if (body["role"] != null && spec == null) return (null, "role must be {id?, name?, icon?, priorities?}");
        lock (_lock)
        {
            var roles = RolesList();
            string? id = Str(body["id"]) ?? Str(spec?["id"]);
            bool wake = false;
            switch (op)
            {
                case "add":
                case "update":
                {
                    if (spec == null) return (null, "role required");
                    WorkRole? r = null;
                    if (op == "update" && (r = RoleById(id)) == null) return (null, id == null ? "role.id required" : $"role.id: no such role '{id}'");
                    var name = spec["name"] != null ? Str(spec["name"]) : r?.Name;
                    if (name is not { Length: >= 1 and <= 40 }) return (null, "role.name: 1..40 characters");
                    if (roles.Any(x => x != r && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return (null, $"role.name: '{name}' is taken");
                    string? icon = r?.Icon;
                    if (spec.ContainsKey("icon"))
                    {
                        icon = Str(spec["icon"]);
                        if (spec["icon"] != null && spec["icon"] is not JsonValue) return (null, "role.icon must be a short key");
                        if (icon != null && (icon.Length > 24 || !icon.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-')))
                            return (null, "role.icon: up to 24 of a-z 0-9 -");
                    }
                    var prios = r != null ? new Dictionary<string, int>(r.Priorities) : WorkTypes.All.ToDictionary(t => t, _ => 0);
                    if (spec["priorities"] is JsonObject po)
                    {
                        foreach (var (t, v) in po)
                        {
                            if (!WorkTypes.Known(t)) return (null, $"role.priorities: unknown work type '{t}' ({string.Join("|", WorkTypes.All)})");
                            if (StoryJson.Int(v) is not { } n || n is < 0 or > 4) return (null, "role.priorities: 0 (off) .. 4");
                            prios[t] = n;
                        }
                    }
                    else if (spec["priorities"] != null) return (null, "role.priorities must be {type: 0..4}");
                    if (r == null)
                    {
                        r = new WorkRole { Id = NextRoleId(name) };
                        roles.Add(r);
                    }
                    r.Name = name; r.Icon = icon; r.Priorities = prios;
                    wake = ReapplyRole(r);
                    id = r.Id;
                    break;
                }
                case "remove":
                {
                    if (RoleById(id) is not { } gone) return (null, id == null ? "id required" : $"id: no such role '{id}'");
                    roles.Remove(gone);
                    foreach (var p in Pawns.Values.Where(p => p.Role == gone.Id)) { p.Role = null; p.Rev = Touch(); } // Custom, same values
                    break;
                }
                default: // reset_defaults
                {
                    var defs = DefaultRoles();
                    for (int i = 0; i < defs.Count; i++)
                    {
                        var d = defs[i];
                        // A name an own role took gives way: the own role keeps it, the built-in its id-derived name stays unique.
                        if (roles.Any(x => x.Id != d.Id && x.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))) d.Name += " (built-in)";
                        if (RoleById(d.Id) is { } have) { have.Name = d.Name; have.Icon = d.Icon; have.Priorities = d.Priorities; d = have; }
                        else roles.Insert(Math.Min(i, roles.Count), d);
                        wake |= ReapplyRole(d);
                    }
                    id = null;
                    break;
                }
            }
            Touch();
            if (wake) WakeIdle();
            var o = new JsonObject { ["id"] = id, ["roles"] = RolesJson() };
            if (op == "remove") o["removed"] = true;
            return (o, null);
        }
    }

    /// <summary>A fresh role id from its name (farmer → "farmer", taken → "farmer-2"), never a built-in one.</summary>
    private string NextRoleId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "role";
        if (slug.Length > 24) slug = slug[..24];
        var id = slug;
        for (int n = 2; RolesList().Any(r => r.Id == id) || IsDefaultRole(id); n++) id = $"{slug}-{n}";
        return id;
    }

    internal JsonArray RolesJson() => new(RolesList().Select(r => (JsonNode)new JsonObject
    {
        ["id"] = r.Id, ["name"] = r.Name, ["icon"] = r.Icon, ["builtin"] = IsDefaultRole(r.Id),
        ["priorities"] = new JsonObject(WorkTypes.All.Select(t => new KeyValuePair<string, JsonNode?>(t, r.Priorities.GetValueOrDefault(t)))),
        ["pawns"] = new JsonArray(Pawns.Values.Where(p => p.Role == r.Id).Select(p => (JsonNode)p.Name!).ToArray()),
    }).ToArray());
}
