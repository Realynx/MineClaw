namespace MineClaw.Scripting;

/// <summary>
/// Reliability helpers, defined in Lua before every script on top of the gated API (so they go through exactly the same
/// actions, limits and pauses as hand-written code). Run as a separate chunk ("prelude"), so user line numbers are untouched.
/// <list type="bullet">
/// <item><c>retry(n, fn)</c>: calls fn until it returns ok (at most n times), waiting 1, 2, 4... s (≤ 20 s) between tries; returns fn's last ok,msg,n.</item>
/// <item><c>upto(n, fn)</c>: fn(i) for i = 1..n, stopping early when fn returns false; a short wait after each pass (yields); returns passes.</item>
/// <item><c>expect(cond, msg)</c>: fails the script with msg when cond is falsy; returns cond.</item>
/// <item><c>room(n)</c>: makes sure n inventory slots are free (deposit{} if not), else fails with the reason.</item>
/// <item><c>ensure"pickaxe"</c> / <c>ensure{"pickaxe","food"}</c>: have one (any tier) in the inventory, else withdraw it from
/// a chest, else craft it from what's at hand/in chests; else fails clearly. Returns true, "stone_pickaxe,bread".</item>
/// </list>
/// </summary>
public static class ScriptPrelude
{
    public static readonly IReadOnlyList<string> Names = ["retry", "upto", "expect", "room", "ensure"];

    public const string Lua = """
local _wait, _fail, _inv, _floor, _max, _min = wait, fail, inv, math.floor, math.max, math.min
local _cat = {
  pickaxe = {"diamond_pickaxe","iron_pickaxe","stone_pickaxe","wood_pickaxe"},
  axe = {"diamond_axe","iron_axe","stone_axe","wood_axe"},
  shovel = {"diamond_shovel","iron_shovel","stone_shovel","wood_shovel"},
  sword = {"diamond_sword","iron_sword","stone_sword","wood_sword"},
  food = {"cooked_porkchop","bread","cooked_fish","apple","raw_porkchop","raw_fish"},
}
local _craft = {
  pickaxe = {"stone_pickaxe","wood_pickaxe"}, axe = {"stone_axe","wood_axe"}, shovel = {"stone_shovel","wood_shovel"},
  sword = {"stone_sword","wood_sword"}, food = {"bread"},
}
function retry(n, fn)
  n = _max(1, _floor(n or 3))
  local ok, m, c
  for i = 1, n do
    ok, m, c = fn(i)
    if ok then return ok, m, c end
    if i < n then _wait(_min(2 ^ (i - 1), 20)) end
  end
  return ok, m, c
end
function upto(n, fn)
  n = _max(0, _floor(n or 1))
  for i = 1, n do
    if fn(i) == false then return i end
    _wait(0.05)
  end
  return n
end
function expect(c, msg)
  if not c then _fail(msg ~= nil and tostring(msg) or "expectation failed") end
  return c
end
function room(n)
  n = _max(1, _floor(n or 1))
  if _inv.free() >= n then return true end
  local ok, m = false, "no deposit"
  if deposit then ok, m = deposit{} end
  if _inv.free() < n then _fail("room: need " .. n .. " free slots, have " .. _inv.free() .. (ok and "" or " (deposit: " .. tostring(m) .. ")")) end
  return true
end
local function _have(names)
  for _, x in ipairs(names) do if _inv(x) > 0 then return x end end
end
function ensure(what)
  local list = type(what) == "table" and what or {what}
  local got = {}
  for _, w in ipairs(list) do
    local names = _cat[w] or {w}
    local x = _have(names)
    if not x and take then
      for _, y in ipairs(names) do local ok = take{y, 1} if ok and _inv(y) > 0 then x = y break end end
    end
    if not x and craft then
      for _, y in ipairs(_craft[w] or {w}) do local ok = craft{y, 1} if ok and _inv(y) > 0 then x = y break end end
    end
    if not x then _fail("ensure: no " .. w .. " (not in inventory or chests, can't craft)") end
    got[#got + 1] = x
  end
  return true, table.concat(got, ",")
end
""";
}
