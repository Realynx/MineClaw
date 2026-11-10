namespace MineClaw.Scripting.Tests;

/// <summary>The golden dump of <see cref="LintGoldenTests"/> (generated from the pre-refactor code).</summary>
internal static class LintGoldenData
{
    public const string Expected = """
== 0 Js
  js   E1 missing-arg: gen() needs at= (args in order: at,count,store,stand) | W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  E1 missing-arg: gen needs at= (args in order: at,count,store,stand) | W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 1 Js
  js   W1 no-ensure: harvest_generator() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: harvest_generator() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: harvest_generator{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: harvest_generator{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 2 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 3 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 4 Js
  js   E2 missing-arg: gen() needs at= (args in order: at,count,store,stand)
  js0  -
  lua  E2 missing-arg: gen needs at= (args in order: at,count,store,stand)
  lua0 -
== 5 Js
  js   E1 unknown-global: unknown name 'acquire' | E2 missing-arg: gen() needs at= (args in order: at,count,store,stand)
  js0  E1 unknown-global: unknown name 'acquire'
  lua  E2 missing-arg: gen needs at= (args in order: at,count,store,stand)
  lua0 -
== 6 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 7 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 8 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 9 Js
  js   E1 syntax: syntax: Unexpected identifier 'axe'
  js0  E1 syntax: syntax: Unexpected identifier 'axe'
  lua  -
  lua0 -
== 10 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 11 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 12 Js
  js   E1 syntax: syntax: Unexpected identifier 'dig_some'
  js0  E1 syntax: syntax: Unexpected identifier 'dig_some'
  lua  -
  lua0 -
== 13 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 14 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 15 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 16 Js
  js   E1 syntax: syntax: Unexpected identifier 'old_dig'
  js0  E1 syntax: syntax: Unexpected identifier 'old_dig'
  lua  -
  lua0 -
== 17 Js
  js   -
  js0  E1 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 18 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 19 Js
  js   W2 say-in-loop: say inside a loop (spams chat; use log or prog)
  js0  W2 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua  W2 say-in-loop: say inside a loop (spams chat; use log or prog) | W3 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W2 say-in-loop: say inside a loop (spams chat; use log or prog) | W3 say-in-loop: say inside a loop (spams chat; use log or prog)
== 20 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 21 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 22 Js
  js   E1 syntax: syntax: Unexpected identifier 'is'
  js0  E1 syntax: syntax: Unexpected identifier 'is'
  lua  -
  lua0 -
== 23 Js
  js   W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  js0  W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  lua  -
  lua0 -
== 24 Js
  js   E1 syntax: syntax: Unexpected identifier 'loop'
  js0  E1 syntax: syntax: Unexpected identifier 'loop'
  lua  -
  lua0 -
== 25 Js
  js   E1 unknown-global: unknown name 'L1' (did you mean cp or go?)
  js0  E1 unknown-global: unknown name 'L1' (did you mean cp or me?)
  lua  -
  lua0 -
== 26 Js
  js   E1 syntax: syntax: Unexpected identifier 'loop'
  js0  E1 syntax: syntax: Unexpected identifier 'loop'
  lua  -
  lua0 -
== 27 Js
  js   E1 syntax: syntax: Unexpected identifier 'paused'
  js0  E1 syntax: syntax: Unexpected identifier 'paused'
  lua  -
  lua0 -
== 28 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 29 Js
  js   E1 syntax: syntax: Unexpected identifier 'short'
  js0  E1 syntax: syntax: Unexpected identifier 'short'
  lua  -
  lua0 -
== 30 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 31 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 32 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 33 Js
  js   E1 unknown-global: unknown name 'm' (did you mean r or me?)
  js0  E1 unknown-global: unknown name 'goto' (did you mean upto or ok?)
  lua  -
  lua0 -
== 34 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 35 Js
  js   E1 syntax: syntax: Unexpected identifier 'cobble_loop'
  js0  E1 syntax: syntax: Unexpected identifier 'cobble_loop'
  lua  -
  lua0 -
== 36 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 37 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 38 Js
  js   -
  js0  E1 unknown-global: unknown name 'eat' (did you mean pauseAtNight or Map?)
  lua  -
  lua0 -
== 39 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 40 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 41 Lua
  js   E1 syntax: syntax: Unexpected identifier 'names'
  js0  E1 syntax: syntax: Unexpected identifier 'names'
  lua  E5 removed-global: os.execute is not available (os.time/os.clock only)
  lua0 E5 removed-global: os.execute is not available (os.time/os.clock only)
== 42 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  W1 no-cp: 11 actions and no cp"label" (add one before each long phase) | W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-cp: 3 actions and no cp"label" (add one before each long phase) | W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 43 Lua
  js   E1 syntax: syntax: Unexpected identifier 'n'
  js0  E1 syntax: syntax: Unexpected identifier 'n'
  lua  -
  lua0 -
== 44 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 45 Lua
  js   E1 syntax: syntax: Unexpected identifier 'inv'
  js0  E1 syntax: syntax: Unexpected identifier 'inv'
  lua  W1 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W1 say-in-loop: say inside a loop (spams chat; use log or prog)
== 46 Lua
  js   E1 syntax: syntax: Unexpected identifier 'i'
  js0  E1 syntax: syntax: Unexpected identifier 'i'
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m)) | W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m)) | W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 47 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 48 Lua
  js   E1 syntax: syntax: Unexpected identifier 'eat'
  js0  E1 syntax: syntax: Unexpected identifier 'eat'
  lua  -
  lua0 -
== 49 Lua
  js   E1 syntax: syntax: Unexpected identifier 'inv'
  js0  E1 syntax: syntax: Unexpected identifier 'inv'
  lua  -
  lua0 -
== 50 Lua
  js   E1 syntax: syntax: Unexpected string
  js0  E1 syntax: syntax: Unexpected string
  lua  W4 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m)) | W4 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W4 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m)) | W4 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 51 Lua
  js   E1 syntax: syntax: Unexpected identifier 'mine'
  js0  E1 syntax: syntax: Unexpected identifier 'mine'
  lua  -
  lua0 -
== 52 Js
  js   E1 unknown-global: unknown name 'load' (did you mean log or go?)
  js0  E1 unknown-global: unknown name 'load' (did you mean log or ok?)
  lua  E1 removed-global: load is not available in scripts
  lua0 E1 removed-global: load is not available in scripts
== 53 Js
  js   E1 unknown-global: unknown name 'loadstring' (did you mean String?)
  js0  E1 unknown-global: unknown name 'loadstring' (did you mean String?)
  lua  E1 removed-global: loadstring is not available in scripts
  lua0 E1 removed-global: loadstring is not available in scripts
== 54 Js
  js   E1 unknown-global: unknown name 'dofile' (did you mean hostile?)
  js0  E1 unknown-global: unknown name 'dofile' (did you mean hostile?)
  lua  E1 removed-global: dofile is not available in scripts
  lua0 E1 removed-global: dofile is not available in scripts
== 55 Js
  js   E1 unknown-global: unknown name 'loadfile'
  js0  E1 unknown-global: unknown name 'loadfile'
  lua  E1 removed-global: loadfile is not available in scripts
  lua0 E1 removed-global: loadfile is not available in scripts
== 56 Js
  js   E1 forbidden: require: no modules: everything is a global
  js0  E1 forbidden: require: no modules: everything is a global
  lua  E1 removed-global: require is not available in scripts
  lua0 E1 removed-global: require is not available in scripts
== 57 Lua
  js   E1 syntax: syntax: Unexpected strict mode reserved word
  js0  E1 syntax: syntax: Unexpected strict mode reserved word
  lua  E1 removed-global: package is not available in scripts
  lua0 E1 removed-global: package is not available in scripts
== 58 Js
  js   E1 unknown-global: unknown name 'io' (did you mean go or cp?)
  js0  E1 unknown-global: unknown name 'io' (did you mean cp or me?)
  lua  E1 removed-global: io is not available in scripts
  lua0 E1 removed-global: io is not available in scripts
== 59 Js
  js   E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  js0  E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  lua  E1 removed-global: os.execute is not available (os.time/os.clock only)
  lua0 E1 removed-global: os.execute is not available (os.time/os.clock only)
== 60 Js
  js   E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  js0  E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  lua  E1 removed-global: os.getenv is not available (os.time/os.clock only)
  lua0 E1 removed-global: os.getenv is not available (os.time/os.clock only)
== 61 Js
  js   E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  js0  E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  lua  E1 removed-global: os.remove is not available (os.time/os.clock only)
  lua0 E1 removed-global: os.remove is not available (os.time/os.clock only)
== 62 Js
  js   E1 unknown-global: unknown name 'debug' (did you mean dig?)
  js0  E1 unknown-global: unknown name 'debug'
  lua  E1 removed-global: debug is not available in scripts
  lua0 E1 removed-global: debug is not available in scripts
== 63 Js
  js   E1 unknown-global: unknown name 'debug' (did you mean dig?)
  js0  E1 unknown-global: unknown name 'debug'
  lua  E1 removed-global: debug is not available in scripts
  lua0 E1 removed-global: debug is not available in scripts
== 64 Js
  js   E1 unknown-global: unknown name 'collectgarbage'
  js0  E1 unknown-global: unknown name 'collectgarbage'
  lua  E1 removed-global: collectgarbage is not available in scripts
  lua0 E1 removed-global: collectgarbage is not available in scripts
== 65 Js
  js   E1 unknown-global: unknown name 'string' (did you mean String or print?)
  js0  E1 unknown-global: unknown name 'string' (did you mean String or print?)
  lua  -
  lua0 -
== 66 Js
  js   E1 unknown-global: unknown name 'rawset' (did you mean Set or WeakSet?)
  js0  E1 unknown-global: unknown name 'rawset' (did you mean Set or WeakSet?)
  lua  E1 removed-global: rawset is not available in scripts
  lua0 E1 removed-global: rawset is not available in scripts
== 67 Js
  js   E1 unknown-global: unknown name 'rawget'
  js0  E1 unknown-global: unknown name 'rawget'
  lua  E1 removed-global: rawget is not available in scripts
  lua0 E1 removed-global: rawget is not available in scripts
== 68 Js
  js   E1 unknown-global: unknown name 'setmetatable' (did you mean me or Set?)
  js0  E1 unknown-global: unknown name 'setmetatable' (did you mean me or Set?)
  lua  -
  lua0 -
== 69 Lua
  js   E1 syntax: syntax: Unexpected token 'return'
  js0  E1 syntax: syntax: Unexpected token 'return'
  lua  -
  lua0 -
== 70 Lua
  js   E1 syntax: syntax: Unexpected identifier 'end'
  js0  E1 syntax: syntax: Unexpected identifier 'end'
  lua  E1 removed-global: pcall is not available in scripts (check ok,msg instead)
  lua0 E1 removed-global: pcall is not available in scripts (check ok,msg instead)
== 71 Lua
  js   E1 syntax: syntax: Unexpected identifier 'end'
  js0  E1 syntax: syntax: Unexpected identifier 'end'
  lua  E1 removed-global: xpcall is not available in scripts (check ok,msg instead)
  lua0 E1 removed-global: xpcall is not available in scripts (check ok,msg instead)
== 72 Lua
  js   E1 syntax: syntax: Unexpected identifier 'end'
  js0  E1 syntax: syntax: Unexpected identifier 'end'
  lua  E1 removed-global: coroutine is not available in scripts
  lua0 E1 removed-global: coroutine is not available in scripts
== 73 Js
  js   E1 unknown-global: unknown name 'string' (did you mean String or print?)
  js0  E1 unknown-global: unknown name 'string' (did you mean String or print?)
  lua  -
  lua0 -
== 74 Js
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 75 Js
  js   E1 unknown-global: unknown name 'string' (did you mean String or print?)
  js0  E1 unknown-global: unknown name 'string' (did you mean String or print?)
  lua  -
  lua0 -
== 76 Lua
  js   E1 syntax: syntax: Unexpected identifier 't'
  js0  E1 syntax: syntax: Unexpected identifier 't'
  lua  -
  lua0 -
== 77 Lua
  js   E2 syntax: syntax: Unexpected token '{'
  js0  E2 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 78 Lua
  js   E1 syntax: syntax: Unexpected token '.'
  js0  E1 syntax: syntax: Unexpected token '.'
  lua  -
  lua0 -
== 79 Lua
  js   E1 syntax: syntax: Unexpected token '.'
  js0  E1 syntax: syntax: Unexpected token '.'
  lua  -
  lua0 -
== 80 Lua
  js   E1 syntax: syntax: Unexpected identifier 'i'
  js0  E1 syntax: syntax: Unexpected identifier 'i'
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 81 Lua
  js   E1 syntax: syntax: Unexpected token 'true'
  js0  E1 syntax: syntax: Unexpected token 'true'
  lua  -
  lua0 -
== 82 Lua
  js   E1 syntax: syntax: Unexpected token 'true'
  js0  E1 syntax: syntax: Unexpected token 'true'
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 83 Js
  js   E1 syntax: syntax: Unexpected identifier 'limit'
  js0  E1 syntax: syntax: Unexpected identifier 'limit'
  lua  -
  lua0 -
== 84 Lua
  js   E1 syntax: syntax: Unexpected token 'function'
  js0  E1 syntax: syntax: Unexpected token 'function'
  lua  -
  lua0 -
== 85 Js
  js   E1 syntax: syntax: Unexpected identifier 'err'
  js0  E1 syntax: syntax: Unexpected identifier 'err'
  lua  -
  lua0 -
== 86 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 87 Lua
  js   E2 syntax: syntax: Unexpected token '{'
  js0  E2 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 88 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 89 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 90 Js
  js   E1 syntax: syntax: Unexpected identifier 'trip'
  js0  E1 syntax: syntax: Unexpected identifier 'trip'
  lua  -
  lua0 -
== 91 Js
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 92 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 93 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 94 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 95 Js
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 96 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 97 Js
  js   E1 syntax: syntax: Unexpected identifier 'item'
  js0  E1 syntax: syntax: Unexpected identifier 'item'
  lua  -
  lua0 -
== 98 Js
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 99 Lua
  js   E1 syntax: syntax: Unexpected string
  js0  E1 syntax: syntax: Unexpected string
  lua  -
  lua0 -
== 100 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  W1 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W1 say-in-loop: say inside a loop (spams chat; use log or prog)
== 101 Lua
  js   E1 syntax: syntax: Unexpected identifier 'mine'
  js0  E1 syntax: syntax: Unexpected identifier 'mine'
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 102 Lua
  js   E1 syntax: syntax: Unexpected identifier 'a'
  js0  E1 syntax: syntax: Unexpected identifier 'a'
  lua  -
  lua0 -
== 103 Lua
  js   E1 syntax: syntax: Unexpected identifier 'a'
  js0  E1 syntax: syntax: Unexpected identifier 'a'
  lua  -
  lua0 -
== 104 Js
  js   E1 syntax: syntax: Unexpected identifier 'chop32'
  js0  E1 syntax: syntax: Unexpected identifier 'chop32'
  lua  -
  lua0 -
== 105 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 106 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 107 Js
  js   E1 syntax: syntax: Unexpected identifier 't'
  js0  E1 syntax: syntax: Unexpected identifier 't'
  lua  -
  lua0 -
== 108 Lua
  js   E1 syntax: syntax: Unexpected string
  js0  E1 syntax: syntax: Unexpected string
  lua  -
  lua0 -
== 109 Js
  js   E1 syntax: syntax: Unexpected identifier 'fetch'
  js0  E1 syntax: syntax: Unexpected identifier 'fetch'
  lua  -
  lua0 -
== 110 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 111 Lua
  js   E1 syntax: syntax: Unexpected token 'true'
  js0  E1 syntax: syntax: Unexpected token 'true'
  lua  -
  lua0 -
== 112 Lua
  js   E2 syntax: syntax: Unexpected identifier 'hits'
  js0  E2 syntax: syntax: Unexpected identifier 'hits'
  lua  -
  lua0 -
== 113 Lua
  js   E1 syntax: syntax: Unexpected identifier 'carry'
  js0  E1 syntax: syntax: Unexpected identifier 'carry'
  lua  -
  lua0 -
== 114 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 115 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 116 Lua
  js   E1 syntax: syntax: Unexpected token 'true'
  js0  E1 syntax: syntax: Unexpected token 'true'
  lua  -
  lua0 -
== 117 Js
  js   E1 syntax: syntax: Unexpected identifier 'job'
  js0  E1 syntax: syntax: Unexpected identifier 'job'
  lua  -
  lua0 -
== 118 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 119 Js
  js   E1 syntax: syntax: Unexpected identifier 'checkpoint'
  js0  E1 syntax: syntax: Unexpected identifier 'checkpoint'
  lua  -
  lua0 -
== 120 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 121 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 122 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 123 Js
  js   E1 forbidden: eval: eval is disabled | W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  E1 forbidden: eval: eval is disabled | W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 124 Js
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 125 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 126 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 127 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 128 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 129 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 130 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 131 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 132 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 133 Js
  js   -
  js0  E2 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 134 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 135 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 136 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 137 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 138 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 139 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 140 Js
  js   -
  js0  E2 unknown-global: unknown name 'chopTree' (did you mean r or t?)
  lua  -
  lua0 -
== 141 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 142 Js
  js   E1 syntax: syntax: Unexpected identifier 'log'
  js0  E1 syntax: syntax: Unexpected identifier 'log'
  lua  -
  lua0 -
== 143 Js
  js   -
  js0  E1 unknown-global: unknown name 'chopTree' (did you mean t or chopTrees?)
  lua  -
  lua0 -
== 144 Js
  js   -
  js0  E1 unknown-global: unknown name 'chopTree' (did you mean t or chopTrees?)
  lua  -
  lua0 -
== 145 Js
  js   -
  js0  E1 unknown-global: unknown name 'chopTree' (did you mean t or chopTrees?)
  lua  -
  lua0 -
== 146 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 147 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 148 Js
  js   E1 syntax: syntax: Unexpected identifier 'oak'
  js0  E1 syntax: syntax: Unexpected identifier 'oak'
  lua  -
  lua0 -
== 149 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 150 Js
  js   E1 syntax: syntax: Unexpected identifier 'bodies'
  js0  E1 syntax: syntax: Unexpected identifier 'bodies'
  lua  -
  lua0 -
== 151 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 152 Js
  js   E1 crew-unknown-role: role 'digger' isn't a top-level function (lanes call it by name)
  js0  E1 crew-unknown-role: role 'digger' isn't a top-level function (lanes call it by name)
  lua  -
  lua0 -
== 153 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 154 Js
  js   E1 crew-unknown-role: role 'nope' isn't a top-level function (lanes call it by name)
  js0  E1 crew-unknown-role: role 'nope' isn't a top-level function (lanes call it by name)
  lua  -
  lua0 -
== 155 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 156 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 157 Js
  js   -
  js0  E22 unknown-global: unknown name 'craft' (did you mean c or t?)
  lua  -
  lua0 -
== 158 Js
  js   -
  js0  E17 unknown-global: unknown name 'craft' (did you mean crew or wait?)
  lua  -
  lua0 -
== 159 Js
  js   W13 unchecked: go() result ignored in a loop (const r = go(..); expect(r))
  js0  E13 unknown-global: unknown name 'go' (did you mean to or P?)
  lua  E4 missing-arg: go needs to= (args in order: to,dist) | W13 unchecked: go{} result ignored in a loop (local ok,m=go{..} expect(ok,m))
  lua0 -
== 160 Js
  js   E1 syntax: syntax: Unexpected identifier 'x12'
  js0  E1 syntax: syntax: Unexpected identifier 'x12'
  lua  -
  lua0 -
== 161 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 162 Js
  js   E1 syntax: syntax: Unexpected identifier 'm'
  js0  E1 syntax: syntax: Unexpected identifier 'm'
  lua  E1 missing-arg: go needs to= (args in order: to,dist)
  lua0 -
== 163 Js
  js   E2 forbidden: globalThis: globals are read-only: use carry/args
  js0  E2 forbidden: globalThis: globals are read-only: use carry/args
  lua  -
  lua0 -
== 164 Js
  js   W1 no-cp: 16 actions and no cp("label") (add one before each long phase, or use machine()) | W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?) | E4 unknown-global: unknown name 'dig' (did you mean log or dist?) | W1 no-cp: 6 actions and no cp("label") (add one before each long phase, or use machine()) | W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-cp: 15 actions and no cp"label" (add one before each long phase) | W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-cp: 6 actions and no cp"label" (add one before each long phase) | W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 165 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 166 Js
  js   E1 unknown-global: unknown name 'move' (did you mean me or mine?) | E2 unknown-global: unknown name 'harvest_cane' (did you mean r or harvest_generator?)
  js0  E1 unknown-global: unknown name 'move' (did you mean me or mine?) | E2 unknown-global: unknown name 'harvest_cane' (did you mean r or harvest_generator?)
  lua  -
  lua0 -
== 167 Js
  js   -
  js0  E2 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 168 Js
  js   E1 forbidden: eval: eval is disabled
  js0  E1 forbidden: eval: eval is disabled
  lua  -
  lua0 -
== 169 Js
  js   E1 forbidden: Function: Function() is disabled
  js0  E1 forbidden: Function: Function() is disabled
  lua  -
  lua0 -
== 170 Js
  js   E1 forbidden: Function: Function() is disabled
  js0  E1 forbidden: Function: Function() is disabled
  lua  -
  lua0 -
== 171 Js
  js   E1 forbidden: require: no modules: everything is a global
  js0  E1 forbidden: require: no modules: everything is a global
  lua  E1 removed-global: require is not available in scripts
  lua0 E1 removed-global: require is not available in scripts
== 172 Js
  js   E1 forbidden: importNamespace: no CLR access
  js0  E1 forbidden: importNamespace: no CLR access
  lua  -
  lua0 -
== 173 Js
  js   E1 unknown-global: unknown name 'System' (did you mean item?)
  js0  E1 unknown-global: unknown name 'System' (did you mean item?)
  lua  -
  lua0 -
== 174 Js
  js   E1 forbidden: process: no process
  js0  E1 forbidden: process: no process
  lua  -
  lua0 -
== 175 Js
  js   E1 readonly-global: gen is read-only (SDK/game function): use your own name or carry
  js0  E1 readonly-global: gen is read-only (SDK/game function): use your own name or carry
  lua  -
  lua0 -
== 176 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 177 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 178 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 179 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 180 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 181 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 182 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 183 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 184 Js
  js   E1 forbidden: setTimeout: no timers: use wait(s), or after(ms) in a machine
  js0  E1 forbidden: setTimeout: no timers: use wait(s), or after(ms) in a machine | E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 185 Js
  js   -
  js0  E2 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 186 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  lua  -
  lua0 -
== 187 Js
  js   E1 syntax: syntax: Unexpected identifier 'limit'
  js0  E1 syntax: syntax: Unexpected identifier 'limit'
  lua  -
  lua0 -
== 188 Js
  js   E1 busy-loop: unbounded while loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  js0  E1 busy-loop: unbounded while loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  lua  -
  lua0 -
== 189 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 190 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 191 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 192 Js
  js   W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  js0  W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  lua  -
  lua0 -
== 193 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 194 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 195 Js
  js   E1 syntax: syntax: Unexpected identifier 'err'
  js0  E1 syntax: syntax: Unexpected identifier 'err'
  lua  -
  lua0 -
== 196 Js
  js   E2 unknown-global: unknown name 'notAThing'
  js0  E2 unknown-global: unknown name 'notAThing'
  lua  -
  lua0 -
== 197 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 198 Js
  js   W1 promise: new Promise: host calls are synchronous (no Promise or await needed)
  js0  W1 promise: new Promise: host calls are synchronous (no Promise or await needed)
  lua  -
  lua0 -
== 199 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean r or cp?)
  lua  -
  lua0 -
== 200 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  E2 unknown-global: unknown name 'go' (did you mean cp or me?) | W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 201 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  E2 unknown-global: unknown name 'go' (did you mean cp or me?) | W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 202 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 203 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 204 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 205 Js
  js   W2 say-in-loop: say inside a loop (spams chat; use log or prog)
  js0  W2 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua  W2 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W2 say-in-loop: say inside a loop (spams chat; use log or prog)
== 206 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 207 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 208 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 209 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 210 Js
  js   -
  js0  E4 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 211 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 212 Js
  js   -
  js0  E2 unknown-global: unknown name 'eat' (did you mean e or pauseAtNight?) | E3 unknown-global: unknown name 'go' (did you mean e or cp?)
  lua  -
  lua0 -
== 213 Js
  js   W3 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  js0  W3 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  lua  -
  lua0 -
== 214 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 215 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 216 Js
  js   W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  js0  W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  lua  -
  lua0 -
== 217 Js
  js   -
  js0  E3 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 218 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 219 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 220 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 221 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 222 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 223 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 224 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 225 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 226 Js
  js   E1 unknown-global: unknown name 'minee' (did you mean mine or mineGen?)
  js0  E1 unknown-global: unknown name 'minee' (did you mean mine or mineGen?)
  lua  -
  lua0 -
== 227 Js
  js   E1 forbidden: setTimeout: no timers: use wait(s), or after(ms) in a machine
  js0  E1 forbidden: setTimeout: no timers: use wait(s), or after(ms) in a machine
  lua  -
  lua0 -
== 228 Js
  js   E1 busy-loop: unbounded while loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  js0  E1 busy-loop: unbounded while loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  lua  -
  lua0 -
== 229 Js
  js   E1 busy-loop: unbounded for loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  js0  E1 busy-loop: unbounded for loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  lua  -
  lua0 -
== 230 Js
  js   E1 missing-arg: take() needs item= (args in order: item,count,at)
  js0  E1 unknown-global: unknown name 'take' (did you mean wake or Date?)
  lua  -
  lua0 -
== 231 Js
  js   E1 machine-state: machine: 'nope' isn't a state (a)
  js0  E1 machine-state: machine: 'nope' isn't a state (a)
  lua  -
  lua0 -
== 232 Js
  js   E1 machine-state: machine: initial 'x' isn't a state (a)
  js0  E1 machine-state: machine: initial 'x' isn't a state (a)
  lua  -
  lua0 -
== 233 Js
  js   E2 crew-toplevel: crew script: the top level holds only functions and constants (every lane reloads it); move the work into main(args)
  js0  E2 crew-toplevel: crew script: the top level holds only functions and constants (every lane reloads it); move the work into main(args)
  lua  -
  lua0 -
== 234 Js
  js   E1 crew-unknown-role: role 'digger' isn't a top-level function (lanes call it by name)
  js0  E1 crew-unknown-role: role 'digger' isn't a top-level function (lanes call it by name)
  lua  -
  lua0 -
== 235 Js
  js   E2 crew-dup-body: parallel: body 67 twice (one lane per body)
  js0  E2 crew-dup-body: parallel: body 67 twice (one lane per body)
  lua  -
  lua0 -
== 236 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  lua  -
  lua0 -
== 237 Js
  js   W1 say-in-loop: say inside a loop (spams chat; use log or prog)
  js0  W1 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua  W1 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W1 say-in-loop: say inside a loop (spams chat; use log or prog)
== 238 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 239 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W1 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine()
  lua  -
  lua0 -
== 240 Js
  js   W1 no-cp: 7 actions and no cp("label") (add one before each long phase, or use machine())
  js0  W1 no-cp: 7 actions and no cp("label") (add one before each long phase, or use machine())
  lua  W1 no-cp: 7 actions and no cp"label" (add one before each long phase)
  lua0 W1 no-cp: 7 actions and no cp"label" (add one before each long phase)
== 241 Js
  js   W1 machine-event: machine: event 'danjer'? (danger|lowhp|void|died|chat|'chat:<text>'|hungry|invFull|toolBroke|hurt|night|day|after()|every()|when())
  js0  W1 machine-event: machine: event 'danjer'? (danger|lowhp|void|died|chat|'chat:<text>'|hungry|invFull|toolBroke|hurt|night|day|after()|every()|when())
  lua  -
  lua0 -
== 242 Js
  js   W1 crew-say: say in lane role miner: lanes report through their result (only main's account talks)
  js0  W1 crew-say: say in lane role miner: lanes report through their result (only main's account talks)
  lua  -
  lua0 -
== 243 Js
  js   W1 promise: new Promise: host calls are synchronous (no Promise or await needed)
  js0  W1 promise: new Promise: host calls are synchronous (no Promise or await needed)
  lua  -
  lua0 -
== 244 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 245 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 246 Js
  js   -
  js0  E2 unknown-global: unknown name 'go' (did you mean p or cp?)
  lua  -
  lua0 -
== 247 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 248 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 249 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 250 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 251 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 252 Js
  js   E2 unknown-global: unknown name 'minee' (did you mean mine or mineGen?) | E3 missing-arg: gen() needs at= (args in order: at,count,store,stand) | W3 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  E2 unknown-global: unknown name 'minee' (did you mean mine or mineGen?) | W3 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  E3 missing-arg: gen needs at= (args in order: at,count,store,stand) | W3 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W3 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 253 Js
  js   E1 syntax: syntax: Unexpected identifier 'errors'
  js0  E1 syntax: syntax: Unexpected identifier 'errors'
  lua  -
  lua0 -
== 254 Js
  js   E1 syntax: syntax: Unexpected identifier 'gen'
  js0  E1 syntax: syntax: Unexpected identifier 'gen'
  lua  E1 missing-arg: gen needs at= (args in order: at,count,store,stand) | W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 255 Js
  js   W3 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W3 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 256 Lua
  js   E1 syntax: syntax: Unexpected string
  js0  E1 syntax: syntax: Unexpected string
  lua  -
  lua0 -
== 257 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  W2 unchecked: gen{} result ignored in a loop (local ok,m=gen{..} expect(ok,m)) | W2 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 unchecked: gen{} result ignored in a loop (local ok,m=gen{..} expect(ok,m)) | W2 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 258 Js
  js   W2 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 259 Js
  js   E1 syntax: syntax: Unexpected identifier 'oak'
  js0  E1 syntax: syntax: Unexpected identifier 'oak'
  lua  -
  lua0 -
== 260 Js
  js   E1 syntax: syntax: Unexpected identifier 'crew'
  js0  E1 syntax: syntax: Unexpected identifier 'crew'
  lua  -
  lua0 -
== 261 Js
  js   E1 syntax: syntax: Unexpected identifier 'crew'
  js0  E1 syntax: syntax: Unexpected identifier 'crew'
  lua  -
  lua0 -
== 262 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 263 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 264 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 265 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 266 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 267 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 268 Js
  js   -
  js0  E4 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 269 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 270 Js
  js   W7 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W7 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W7 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W7 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 271 Js
  js   -
  js0  E5 unknown-global: unknown name 'eat' (did you mean pauseAtNight or Map?)
  lua  -
  lua0 -
== 272 Js
  js   W5 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W5 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W5 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W5 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 273 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 274 Js
  js   E1 syntax: syntax: Unexpected token '>'
  js0  E1 syntax: syntax: Unexpected token '>'
  lua  -
  lua0 -
== 275 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 276 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 277 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 278 Js
  js   E1 unknown-global: unknown name 'P' (did you mean cp or Map?)
  js0  E1 unknown-global: unknown name 'P' (did you mean cp or Map?)
  lua  -
  lua0 -
== 279 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 280 Js
  js   E1 syntax: syntax: Unexpected identifier 'P'
  js0  E1 syntax: syntax: Unexpected identifier 'P'
  lua  -
  lua0 -
== 281 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 282 Js
  js   E1 syntax: syntax: Unexpected token '>'
  js0  E1 syntax: syntax: Unexpected token '>'
  lua  -
  lua0 -
== 283 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 284 Js
  js   -
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  -
  lua0 -
== 285 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 286 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 287 Js
  js   W1 unchecked: dig() result ignored in a loop (const r = dig(..); expect(r))
  js0  E1 unknown-global: unknown name 'dig' (did you mean i or log?)
  lua  W1 unchecked: dig{} result ignored in a loop (local ok,m=dig{..} expect(ok,m))
  lua0 -
== 288 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 289 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 290 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 291 Js
  js   E1 unknown-global: unknown name 'noSuchThing'
  js0  E1 unknown-global: unknown name 'noSuchThing'
  lua  -
  lua0 -
== 292 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 293 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 294 Js
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 295 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 296 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 297 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 298 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 299 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 300 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 301 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 302 Lua
  js   E1 syntax: syntax: Invalid or unexpected token
  js0  E1 syntax: syntax: Invalid or unexpected token
  lua  -
  lua0 -
== 303 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 304 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 305 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 306 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 307 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 308 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 309 Js
  js   W4 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W4 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W4 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W4 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 310 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 311 Js
  js   -
  js0  E2 unknown-global: unknown name 'go' (did you mean t or cp?)
  lua  -
  lua0 -
== 312 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 313 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 314 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 315 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 316 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 317 Js
  js   W4 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W4 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 318 Js
  js   W3 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing). | W4 compat: guardArea() is a deprecated compat helper: use patrol({at, r}, fn, {until: () => !night()}) with use(defendIfAttacked()) and on('mob.near', fn).
  js0  W3 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing). | W4 compat: guardArea() is a deprecated compat helper: use patrol({at, r}, fn, {until: () => !night()}) with use(defendIfAttacked()) and on('mob.near', fn).
  lua  -
  lua0 -
== 319 Js
  js   W3 compat: chopTrees() is a deprecated compat helper: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}).
  js0  W3 compat: chopTrees() is a deprecated compat helper: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}).
  lua  -
  lua0 -
== 320 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 321 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 322 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 323 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 324 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 325 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 326 Js
  js   W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 327 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 328 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 329 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 330 Js
  js   W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe'). | W2 compat: waitForDay() is a deprecated compat helper: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)).
  js0  W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe'). | W2 compat: waitForDay() is a deprecated compat helper: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)).
  lua  -
  lua0 -
== 331 Js
  js   E1 syntax: syntax: Unexpected identifier 'is'
  js0  E1 syntax: syntax: Unexpected identifier 'is'
  lua  -
  lua0 -
== 332 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 333 Js
  js   W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing). | W2 compat: chopTrees() is a deprecated compat helper: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}).
  js0  W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing). | W2 compat: chopTrees() is a deprecated compat helper: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}).
  lua  -
  lua0 -
== 334 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 335 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 336 Js
  js   E1 syntax: syntax: Unexpected identifier 'only'
  js0  E1 syntax: syntax: Unexpected identifier 'only'
  lua  -
  lua0 -
== 337 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 338 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 339 Js
  js   E1 syntax: syntax: Unexpected identifier 'cow'
  js0  E1 syntax: syntax: Unexpected identifier 'cow'
  lua  -
  lua0 -
== 340 Js
  js   -
  js0  E3 unknown-global: unknown name 'go' (did you mean a or b?)
  lua  -
  lua0 -
== 341 Js
  js   W9 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W9 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W9 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W9 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 342 Js
  js   W4 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  js0  W4 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  lua  W4 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W4 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 343 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 344 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 345 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 346 Js
  js   E1 syntax: syntax: Unexpected identifier 'gen'
  js0  E1 syntax: syntax: Unexpected identifier 'gen'
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 347 Js
  js   E1 syntax: syntax: Unexpected identifier 'mineGen'
  js0  E1 syntax: syntax: Unexpected identifier 'mineGen'
  lua  -
  lua0 -
== 348 Js
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 349 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 350 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean q or r?)
  lua  -
  lua0 -
== 351 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 352 Js
  js   W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing). | W2 compat: restock() is a deprecated compat helper: use gather(item, n, {from: 'chests', chest}). | W3 compat: deliver() is a deprecated compat helper: use handTo(player, item, n). | W4 compat: chopTrees() is a deprecated compat helper: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}). | W5 compat: harvestFarm() is a deprecated compat helper: use gather('wheat', n, {from: 'harvest', more: true, r, stash: chest}). | W6 compat: waitForDay() is a deprecated compat helper: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)).
  js0  W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing). | W2 compat: restock() is a deprecated compat helper: use gather(item, n, {from: 'chests', chest}). | W3 compat: deliver() is a deprecated compat helper: use handTo(player, item, n). | W4 compat: chopTrees() is a deprecated compat helper: use forEach(spots or find('log'), t => chop(t)) in quota(n, ...), or gather('log', n, {from: 'chop'}). | W5 compat: harvestFarm() is a deprecated compat helper: use gather('wheat', n, {from: 'harvest', more: true, r, stash: chest}). | W6 compat: waitForDay() is a deprecated compat helper: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)).
  lua  -
  lua0 -
== 353 Js
  js   W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing).
  js0  W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing).
  lua  -
  lua0 -
== 354 Js
  js   W2 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W2 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 355 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 356 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 357 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 358 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 359 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 360 Js
  js   -
  js0  E2 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 361 Js
  js   -
  js0  E2 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 362 Js
  js   W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 363 Js
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 364 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 365 Js
  js   W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing).
  js0  W1 compat: store() is a deprecated compat helper: use stash(items, {to: chest}) (returns errors instead of failing).
  lua  -
  lua0 -
== 366 Js
  js   W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W1 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 367 Js
  js   W1 compat: guardArea() is a deprecated compat helper: use patrol({at, r}, fn, {until: () => !night()}) with use(defendIfAttacked()) and on('mob.near', fn).
  js0  W1 compat: guardArea() is a deprecated compat helper: use patrol({at, r}, fn, {until: () => !night()}) with use(defendIfAttacked()) and on('mob.near', fn).
  lua  -
  lua0 -
== 368 Js
  js   E1 syntax: syntax: Unexpected end of input
  js0  E1 syntax: syntax: Unexpected end of input
  lua  -
  lua0 -
== 369 Js
  js   E1 syntax: syntax: Unexpected identifier 'or'
  js0  E1 syntax: syntax: Unexpected identifier 'or'
  lua  -
  lua0 -
== 370 Js
  js   E1 unknown-global: unknown name 'x' (did you mean Proxy or RegExp?)
  js0  E1 unknown-global: unknown name 'x' (did you mean Proxy or RegExp?)
  lua  -
  lua0 -
== 371 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  W12 unchecked: eat{} result ignored in a loop (local ok,m=eat{..} expect(ok,m))
  lua0 -
== 372 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 373 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 374 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 375 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 376 Js
  js   -
  js0  E6 unknown-global: unknown name 'go' (did you mean P or cp?)
  lua  -
  lua0 -
== 377 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  W6 unchecked: go{} result ignored in a loop (local ok,m=go{..} expect(ok,m)) | W17 unchecked: eat{} result ignored in a loop (local ok,m=eat{..} expect(ok,m))
  lua0 -
== 378 Js
  js   -
  js0  E9 unknown-global: unknown name 'attack' (did you mean defendIfAttacked?) | E10 unknown-global: unknown name 'go' (did you mean P or m?)
  lua  -
  lua0 -
== 379 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 380 Js
  js   -
  js0  -
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 381 Lua
  js   E1 syntax: syntax: Unexpected token '.'
  js0  E1 syntax: syntax: Unexpected token '.'
  lua  -
  lua0 -
== 382 Lua
  js   E1 syntax: syntax: Unexpected token '.'
  js0  E1 syntax: syntax: Unexpected token '.'
  lua  -
  lua0 -
== 383 Js
  js   E1 syntax: syntax: Unexpected token '|'
  js0  E1 syntax: syntax: Unexpected token '|'
  lua  -
  lua0 -
== 384 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 385 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 386 Js
  js   E1 syntax: syntax: Unexpected identifier 'path'
  js0  E1 syntax: syntax: Unexpected identifier 'path'
  lua  -
  lua0 -
== 387 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 388 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 389 Js
  js   E1 syntax: syntax: Unexpected identifier 'the'
  js0  E1 syntax: syntax: Unexpected identifier 'the'
  lua  -
  lua0 -
== 390 Js
  js   E1 syntax: syntax: Invalid or unexpected token
  js0  E1 syntax: syntax: Invalid or unexpected token
  lua  -
  lua0 -
== 391 Js
  js   E1 syntax: syntax: Unexpected identifier 'lost'
  js0  E1 syntax: syntax: Unexpected identifier 'lost'
  lua  -
  lua0 -
== 392 Js
  js   E1 syntax: syntax: Invalid or unexpected token
  js0  E1 syntax: syntax: Invalid or unexpected token
  lua  -
  lua0 -
== 393 Js
  js   E1 syntax: syntax: Unexpected identifier 'progress'
  js0  E1 syntax: syntax: Unexpected identifier 'progress'
  lua  -
  lua0 -
== 394 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 395 Lua
  js   E1 syntax: syntax: Unexpected string
  js0  E1 syntax: syntax: Unexpected string
  lua  -
  lua0 -
== 396 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 397 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 398 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 399 Js
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 400 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 401 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 402 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 403 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 404 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 405 Js
  js   E1 syntax: syntax: Unexpected identifier 't'
  js0  E1 syntax: syntax: Unexpected identifier 't'
  lua  -
  lua0 -
== 406 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 407 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  -
  lua0 -
== 408 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 409 Lua
  js   E1 syntax: syntax: Unexpected identifier 'inventory'
  js0  E1 syntax: syntax: Unexpected identifier 'inventory'
  lua  W5 unchecked: go{} result ignored in a loop (local ok,m=go{..} expect(ok,m)) | W7 unchecked: eat{} result ignored in a loop (local ok,m=eat{..} expect(ok,m))
  lua0 -
== 410 Js
  js   -
  js0  E6 unknown-global: unknown name 'go' (did you mean P or m?)
  lua  -
  lua0 -
== 411 Lua
  js   E1 syntax: syntax: Unexpected token 'for'
  js0  E1 syntax: syntax: Unexpected token 'for'
  lua  -
  lua0 -
== 412 Js
  js   -
  js0  E4 unknown-global: unknown name 'chop' (did you mean P or chopTrees?)
  lua  -
  lua0 -
== 413 Js
  js   E1 syntax: syntax: Unexpected token '?'
  js0  E1 syntax: syntax: Unexpected token '?'
  lua  -
  lua0 -
== 414 Js
  js   E2 unknown-global: unknown name 'minee' (did you mean mine or mineGen?)
  js0  E2 unknown-global: unknown name 'minee' (did you mean mine or mineGen?)
  lua  -
  lua0 -
== 415 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 416 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 417 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 418 Js
  js   E1 syntax: syntax: Unexpected identifier 'declared'
  js0  E1 syntax: syntax: Unexpected identifier 'declared'
  lua  -
  lua0 -
== 419 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 420 Js
  js   W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 421 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 422 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 423 Js
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  -
  lua0 -
== 424 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 425 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 426 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 427 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 428 Js
  js   E1 syntax: syntax: Unexpected token ','
  js0  E1 syntax: syntax: Unexpected token ','
  lua  -
  lua0 -
== 429 Js
  js   E1 syntax: syntax: Unexpected identifier 'x12'
  js0  E1 syntax: syntax: Unexpected identifier 'x12'
  lua  -
  lua0 -
== 430 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 431 Js
  js   E1 syntax: syntax: Unexpected token 'var'
  js0  E1 syntax: syntax: Unexpected token 'var'
  lua  -
  lua0 -
== 432 Js
  js   E3 missing-arg: gen() needs at= (args in order: at,count,store,stand)
  js0  -
  lua  E3 missing-arg: gen needs at= (args in order: at,count,store,stand)
  lua0 -
== 433 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  E3 missing-arg: gen needs at= (args in order: at,count,store,stand)
  lua0 -
== 434 Js
  js   E1 syntax: syntax: Unexpected identifier 'at'
  js0  E1 syntax: syntax: Unexpected identifier 'at'
  lua  -
  lua0 -
== 435 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: gen needs at= (args in order: at,count,store,stand) | W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 436 Js
  js   E1 missing-arg: place() needs at= (args in order: block,at)
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  E1 missing-arg: place needs at= (args in order: block,at)
  lua0 -
== 437 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: place needs block= at= (args in order: block,at)
  lua0 -
== 438 Js
  js   E1 missing-arg: place() needs block= (args in order: block,at)
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  E1 missing-arg: place needs at= (args in order: block,at)
  lua0 -
== 439 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: place needs block= at= (args in order: block,at)
  lua0 -
== 440 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: dig needs at= (args in order: at)
  lua0 -
== 441 Js
  js   E1 missing-arg: place() needs at= (args in order: block,at)
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  E1 missing-arg: place needs at= (args in order: block,at)
  lua0 -
== 442 Js
  js   E1 missing-arg: place() needs at= (args in order: block,at)
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  -
  lua0 -
== 443 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: go needs to= (args in order: to,dist)
  lua0 -
== 444 Js
  js   E1 missing-arg: take() needs item= (args in order: item,count,at)
  js0  E1 unknown-global: unknown name 'take' (did you mean wake or Date?)
  lua  -
  lua0 -
== 445 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: take needs item= (args in order: item,count,at)
  lua0 -
== 446 Js
  js   E1 syntax: syntax: Unexpected identifier 'needs'
  js0  E1 syntax: syntax: Unexpected identifier 'needs'
  lua  E1 missing-arg: mine needs block= (args in order: block,count,radius)
  lua0 -
== 447 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 448 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 449 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 450 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 451 Js
  js   -
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  -
  lua0 -
== 452 Js
  js   -
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  -
  lua0 -
== 453 Js
  js   -
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  -
  lua0 -
== 454 Js
  js   -
  js0  E1 unknown-global: unknown name 'place' (did you mean replaceToolWhenBroken or Date?)
  lua  -
  lua0 -
== 455 Js
  js   -
  js0  E1 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 456 Js
  js   -
  js0  E1 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 457 Js
  js   -
  js0  E2 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 458 Js
  js   -
  js0  E2 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 459 Js
  js   -
  js0  E1 unknown-global: unknown name 'go' (did you mean cp or me?)
  lua  -
  lua0 -
== 460 Js
  js   -
  js0  E1 unknown-global: unknown name 'chop' (did you mean chopTrees or cp?) | E4 unknown-global: unknown name 'take' (did you mean wake or Date?) | W1 no-cp: 3 actions and no cp("label") (add one before each long phase, or use machine())
  lua  -
  lua0 W1 no-cp: 3 actions and no cp"label" (add one before each long phase)
== 461 Js
  js   W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W1 no-ensure: gen() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 462 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 463 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  E1 missing-arg: place needs at= (args in order: block,at)
  lua0 -
== 464 Js
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  E1 missing-arg: place needs block= (args in order: block,at)
  lua0 -
== 465 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-cp: 3 actions and no cp"label" (add one before each long phase) | W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 466 Js
  js   E1 syntax: syntax: Unexpected identifier 'errors'
  js0  E1 syntax: syntax: Unexpected identifier 'errors'
  lua  E1 missing-arg: gen needs at= (args in order: at,count,store,stand) | W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W1 no-ensure: gen{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 467 Js
  js   -
  js0  E1 unknown-global: unknown name 'dig' (did you mean log or dist?)
  lua  -
  lua0 -
== 468 Js
  js   E2 forbidden: require: no modules: everything is a global
  js0  E2 forbidden: require: no modules: everything is a global
  lua  E2 removed-global: require is not available in scripts
  lua0 E2 removed-global: require is not available in scripts
== 469 Js
  js   W3 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  js0  W3 compat: mineGen() is a deprecated compat helper: use gather('cobblestone', n, {from: 'gen', at, per, stash: chest}) after ensure('pickaxe').
  lua  -
  lua0 -
== 470 Js
  js   E1 syntax: syntax: Unexpected token ')'
  js0  E1 syntax: syntax: Unexpected token ')'
  lua  -
  lua0 -
== 471 Js
  js   E1 syntax: syntax: Unexpected identifier 'deprecated'
  js0  E1 syntax: syntax: Unexpected identifier 'deprecated'
  lua  -
  lua0 -
== 472 Js
  js   E1 syntax: syntax: Unexpected identifier 'helpers'
  js0  E1 syntax: syntax: Unexpected identifier 'helpers'
  lua  -
  lua0 -
== 473 Js
  js   W1 compat: waitForDay() is a deprecated compat helper: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)).
  js0  W1 compat: waitForDay() is a deprecated compat helper: use waitFor('day', null, maxS) (or waitFor(() => !night(), maxS)).
  lua  -
  lua0 -
== 474 Js
  js   E1 syntax: syntax: Unexpected identifier 'is'
  js0  E1 syntax: syntax: Unexpected identifier 'is'
  lua  -
  lua0 -
== 475 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 476 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 477 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 478 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 479 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 480 Lua
  js   E1 syntax: syntax: Unexpected identifier 'n'
  js0  E1 syntax: syntax: Unexpected identifier 'n'
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 481 Lua
  js   E1 syntax: syntax: Unexpected token '.'
  js0  E1 syntax: syntax: Unexpected token '.'
  lua  -
  lua0 -
== 482 Lua
  js   E1 syntax: syntax: Unexpected string
  js0  E1 syntax: syntax: Unexpected string
  lua  -
  lua0 -
== 483 Lua
  js   E1 syntax: syntax: Unexpected identifier 'and'
  js0  E1 syntax: syntax: Unexpected identifier 'and'
  lua  -
  lua0 -
== 484 Js
  js   E1 syntax: syntax: Unexpected token 'return'
  js0  E1 syntax: syntax: Unexpected token 'return'
  lua  -
  lua0 -
== 485 Js
  js   E1 syntax: syntax: Unexpected token 'return'
  js0  E1 syntax: syntax: Unexpected token 'return'
  lua  -
  lua0 -
== 486 Js
  js   E1 syntax: syntax: Unexpected token 'return'
  js0  E1 syntax: syntax: Unexpected token 'return'
  lua  -
  lua0 -
== 487 Js
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  -
  lua0 -
== 488 Lua
  js   E1 syntax: syntax: Unexpected identifier 'ok'
  js0  E1 syntax: syntax: Unexpected identifier 'ok'
  lua  -
  lua0 -
== 489 Js
  js   E1 syntax: syntax: Unexpected identifier 'axe'
  js0  E1 syntax: syntax: Unexpected identifier 'axe'
  lua  -
  lua0 -
== 490 Lua
  js   E1 syntax: syntax: Unexpected number
  js0  E1 syntax: syntax: Unexpected number
  lua  E1 busy-loop: unbounded while loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait())
  lua0 E1 busy-loop: unbounded while loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait())
== 491 Lua
  js   E1 syntax: syntax: Unexpected identifier 'mine'
  js0  E1 syntax: syntax: Unexpected identifier 'mine'
  lua  E1 removed-global: pcall is not available in scripts (check ok,msg instead)
  lua0 E1 removed-global: pcall is not available in scripts (check ok,msg instead)
== 492 Lua
  js   E1 syntax: syntax: Unexpected identifier 'f'
  js0  E1 syntax: syntax: Unexpected identifier 'f'
  lua  E1 removed-global: loadstring is not available in scripts
  lua0 E1 removed-global: loadstring is not available in scripts
== 493 Js
  js   E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  js0  E1 unknown-global: unknown name 'os' (did you mean ok or on?)
  lua  E1 removed-global: os.execute is not available (os.time/os.clock only)
  lua0 E1 removed-global: os.execute is not available (os.time/os.clock only)
== 494 Js
  js   E1 unknown-global: unknown name 'coroutine'
  js0  E1 unknown-global: unknown name 'coroutine'
  lua  E1 removed-global: coroutine is not available in scripts
  lua0 E1 removed-global: coroutine is not available in scripts
== 495 Lua
  js   E1 syntax: syntax: Unexpected identifier 'i'
  js0  E1 syntax: syntax: Unexpected identifier 'i'
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 496 Lua
  js   E1 syntax: syntax: Unexpected identifier 'dig'
  js0  E1 syntax: syntax: Unexpected identifier 'dig'
  lua  W1 unchecked: dig{} result ignored in a loop (local ok,m=dig{..} expect(ok,m))
  lua0 -
== 497 Lua
  js   E1 syntax: syntax: Unexpected identifier 'i'
  js0  E1 syntax: syntax: Unexpected identifier 'i'
  lua  W1 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W1 say-in-loop: say inside a loop (spams chat; use log or prog)
== 498 Lua
  js   E1 syntax: syntax: Unexpected token '{'
  js0  E1 syntax: syntax: Unexpected token '{'
  lua  W1 no-cp: 7 actions and no cp"label" (add one before each long phase)
  lua0 W1 no-cp: 3 actions and no cp"label" (add one before each long phase)
== 499 Lua
  js   E1 syntax: syntax: Unexpected token 'true'
  js0  E1 syntax: syntax: Unexpected token 'true'
  lua  -
  lua0 -
== 500 Lua
  js   E1 syntax: syntax: Unexpected token 'function'
  js0  E1 syntax: syntax: Unexpected token 'function'
  lua  -
  lua0 -
== 501 Lua
  js   E1 syntax: syntax: Unexpected identifier 'inv'
  js0  E1 syntax: syntax: Unexpected identifier 'inv'
  lua  -
  lua0 -
== 502 Lua
  js   E1 syntax: syntax: Unexpected identifier 'i'
  js0  E1 syntax: syntax: Unexpected identifier 'i'
  lua  -
  lua0 -
== 503 Js
  js   E1 busy-loop: unbounded while loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  js0  E1 busy-loop: unbounded while loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait())
  lua  -
  lua0 -
== 504 Js
  js   E1 syntax: syntax: Unexpected identifier 'errors'
  js0  E1 syntax: syntax: Unexpected identifier 'errors'
  lua  -
  lua0 -
== 505 Js
  js   W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  js0  W1 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r))
  lua  W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 W1 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 506 Js
  js   E1 syntax: syntax: Unexpected identifier 'mine'
  js0  E1 syntax: syntax: Unexpected identifier 'mine'
  lua  E1 missing-arg: mine needs block= (args in order: block,count,radius)
  lua0 -
== 507 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 508 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 509 Js
  js   E1 syntax: syntax: Unexpected identifier 'v1'
  js0  E1 syntax: syntax: Unexpected identifier 'v1'
  lua  -
  lua0 -
== 510 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 511 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 512 Js
  js   E1 syntax: syntax: Unexpected identifier 'cobble_loop'
  js0  E1 syntax: syntax: Unexpected identifier 'cobble_loop'
  lua  -
  lua0 -
== 513 Js
  js   E1 syntax: syntax: Unexpected identifier 'unchanged'
  js0  E1 syntax: syntax: Unexpected identifier 'unchanged'
  lua  -
  lua0 -
== 514 Js
  js   E1 syntax: syntax: Unexpected identifier 'v2'
  js0  E1 syntax: syntax: Unexpected identifier 'v2'
  lua  -
  lua0 -
== 515 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 516 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 517 Js
  js   W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  js0  W2 no-ensure: mine() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does)
  lua  W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
  lua0 W2 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it)
== 518 Js
  js   -
  js0  E1 unknown-global: unknown name 'craft' (did you mean crew or wait?)
  lua  -
  lua0 -
== 519 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 520 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 521 Js
  js   E1 syntax: syntax: Unexpected identifier 'job'
  js0  E1 syntax: syntax: Unexpected identifier 'job'
  lua  -
  lua0 -
== 522 Js
  js   E1 syntax: syntax: Unexpected identifier 'v1'
  js0  E1 syntax: syntax: Unexpected identifier 'v1'
  lua  -
  lua0 -
== 523 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 524 Js
  js   E1 syntax: syntax: Unexpected token 'for'
  js0  E1 syntax: syntax: Unexpected token 'for'
  lua  -
  lua0 -
== 525 Js
  js   E1 syntax: syntax: Unexpected identifier 'hello'
  js0  E1 syntax: syntax: Unexpected identifier 'hello'
  lua  -
  lua0 -
== 526 Js
  js   -
  js0  -
  lua  -
  lua0 -
== 527 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  E3 busy-loop: unbounded repeat loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait()) | E4 removed-global: os.execute is not available (os.time/os.clock only) | E5 missing-arg: place needs at= (args in order: block,at) | E6 missing-arg: craft needs item= (args in order: item,count) | E6 removed-global: pcall is not available in scripts (check ok,msg instead) | W1 no-cp: 17 actions and no cp"label" (add one before each long phase) | W6 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it) | W7 say-in-loop: say inside a loop (spams chat; use log or prog) | W7 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 E3 busy-loop: unbounded repeat loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait()) | E4 removed-global: os.execute is not available (os.time/os.clock only) | E6 removed-global: pcall is not available in scripts (check ok,msg instead) | W1 no-cp: 5 actions and no cp"label" (add one before each long phase) | W6 no-ensure: mine{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it) | W7 say-in-loop: say inside a loop (spams chat; use log or prog) | W7 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 528 Lua
  js   E1 syntax: syntax: Unexpected token ':'
  js0  E1 syntax: syntax: Unexpected token ':'
  lua  E3 missing-arg: dig needs at= (args in order: at) | E7 busy-loop: unbounded while loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait()) | W5 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
  lua0 E7 busy-loop: unbounded while loop never acts or waits (bound it: upto(n,fn) or for i=1,n; add wait()) | W5 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m))
== 529 Js
  js   E1 missing-arg: place() needs at= (args in order: block,at) | E2 missing-arg: dig() needs at= (args in order: at) | E2 unknown-global: unknown name 'cond' (did you mean c or d?) | E3 missing-arg: harvestGenerator() needs at= (args in order: at,count,store,stand) | E4 busy-loop: unbounded for loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait()) | E5 busy-loop: unbounded for loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait()) | E5 forbidden: debugger statement | E5 unknown-global: unknown name 'B' (did you mean A or c?) | E6 readonly-global: gen is read-only (SDK/game function): use your own name or carry | E6 unknown-global: unknown name 'x' (did you mean A or c?) | W1 no-cp: 15 actions and no cp("label") (add one before each long phase, or use machine()) | W3 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W3 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine() | W3 no-ensure: harvestGenerator() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does) | W4 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W4 say-in-loop: say inside a loop (spams chat; use log or prog) | W5 promise: new Promise: host calls are synchronous (no Promise or await needed)
  js0  E1 unknown-global: unknown name 'place' (did you mean A or c?) | E2 unknown-global: unknown name 'dig' (did you mean d or log?) | E4 busy-loop: unbounded for loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait()) | E5 busy-loop: unbounded for loop never acts or waits (bound it: for (let i=0;i<n;i++), upto(n, fn); add wait()) | E5 forbidden: debugger statement | E5 unknown-global: unknown name 'B' (did you mean A or c?) | E6 readonly-global: gen is read-only (SDK/game function): use your own name or carry | E6 unknown-global: unknown name 'x' (did you mean A or c?) | W1 no-cp: 5 actions and no cp("label") (add one before each long phase, or use machine()) | W3 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W3 unbounded: while loop never ends (no break/return): it stops only at a limit; bound it (upto(n, fn)) or use machine() | W3 no-ensure: harvestGenerator() needs a pickaxe: start with ensure('pickaxe') (or mineGen(), which does) | W4 unchecked: mine() result ignored in a loop (const r = mine(..); expect(r)) | W4 say-in-loop: say inside a loop (spams chat; use log or prog) | W5 promise: new Promise: host calls are synchronous (no Promise or await needed)
  lua  E2 missing-arg: dig needs at= (args in order: at) | W1 no-cp: 13 actions and no cp"label" (add one before each long phase) | W3 no-ensure: harvest_generator{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it) | W4 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m)) | W4 say-in-loop: say inside a loop (spams chat; use log or prog)
  lua0 W1 no-cp: 3 actions and no cp"label" (add one before each long phase) | W3 no-ensure: harvest_generator{} needs a pickaxe: start with ensure"pickaxe" (auto-acquires it) | W4 unchecked: mine{} result ignored in a loop (local ok,m=mine{..} expect(ok,m)) | W4 say-in-loop: say inside a loop (spams chat; use log or prog)
facts 03B49D805DC831C2
prefixes FFBAA925E8E4466E
""";
}
