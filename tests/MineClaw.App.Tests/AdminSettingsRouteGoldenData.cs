namespace MineClaw.App.Tests;

/// <summary>The golden dump of AdminSettingsRouteGoldenTests (generated from the pre-refactor code).</summary>
internal static class AdminSettingsRouteGoldenData
{
    public const string Expected = """"""
no token header -> 403 application/json; charset=utf-8 {"error":"X-Supervisor-Token header required"}
wrong token -> 403 application/json; charset=utf-8 {"error":"X-Supervisor-Token header required"}
no role -> 403 application/json; charset=utf-8 {"error":"admin role required"}
no roles header -> 403 application/json; charset=utf-8 {"error":"admin role required"}
no user -> 403 application/json; charset=utf-8 {"error":"admin role required"}
empty user -> 403 application/json; charset=utf-8 {"error":"admin role required"}
ADMIN role -> 200 application/json; charset=utf-8 keys {id,name,version,game_version}
too large -> 413 application/json; charset=utf-8 {"error":"body over 65536 bytes"}
GET /api/admin/settings - -> 200 application/json; charset=utf-8 keys {node,groups,settings,allowed_claude_models,restart_pending,override}
HEAD /api/admin/settings/ - -> 200 application/json; charset=utf-8 keys {node,groups,settings,allowed_claude_models,restart_pending,override}
POST /api/admin/settings {} -> 405 application/json; charset=utf-8 [Allow: GET, PUT] {"error":"use GET, PUT"}
DELETE /api/admin/settings - -> 405 application/json; charset=utf-8 [Allow: GET, PUT] {"error":"use GET, PUT"}
PUT /api/admin/settings - -> 400 application/json; charset=utf-8 {"error":"bad request body"}
PUT /api/admin/settings [1] -> 400 application/json; charset=utf-8 {"error":"bad request body"}
PUT /api/admin/settings {bad -> 400 application/json; charset=utf-8 {"error":"bad request body"}
PUT /api/admin/settings {"changes":5} -> 400 application/json; charset=utf-8 {"error":"changes (a list) required"}
PUT /api/admin/settings {"changes":[],"reason":"ok"} -> 400 application/json; charset=utf-8 {"error":"a reason (at least 3 characters) is required","errors":[{"key":"reason","message":"say why"}]}
PUT /api/admin/settings {"changes":[],"reason":5} -> 400 application/json; charset=utf-8 {"error":"a reason (at least 3 characters) is required","errors":[{"key":"reason","message":"say why"}]}
PUT /api/admin/settings {"changes":[5],"reason":"because"} -> 400 application/json; charset=utf-8 {"error":"every change needs a key"}
PUT /api/admin/settings {"changes":[{"value":1}],"reason":"because"} -> 400 application/json; charset=utf-8 {"error":"every change needs a key"}
PUT /api/admin/settings {"changes":[{"key":"Nope:Nothing","value":1}],"reason":"because"} -> 400 application/json; charset=utf-8 {"error":"Nope:Nothing: not a setting this page manages","errors":[{"key":"Nope:Nothing","message":"not a setting this page manages"}]}
PUT /api/admin/settings {"changes":[{"key":"Nope:A","value":1},{"key":"Nope:B","value":2}],"reason":"bec… -> 400 application/json; charset=utf-8 {"error":"2 settings need attention; nothing was saved","errors":[{"key":"Nope:A","message":"not a setting this page manages"},{"key":"Nope:B","message":"not a setting this page manages"}]}
PUT /api/admin/settings {"changes":[{"key":"Agent:Llm:MainModel","value":"gpt-4o"}],"reason":"because"} -> 400 application/json; charset=utf-8 {"error":"3 settings need attention; nothing was saved","errors":[{"key":"Agent:Llm:MainModel","message":"\u0027gpt-4o\u0027 is not allowed: Claude models must be one of claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5, claude-fable-5-1, claude-mythos-5-1"},{"key":"Agent:Llm:MainModel","message":"Agent:Llm:MainModel \u0027gpt-4o\u0027 is older than the 5.5 generation (use one of claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5, claude-fable-5-1, claude-mythos-5-1)"},{"key":"","message":"no price for model gpt-4o: add Agent:Prices:Models:gpt-4o"}]}
PUT /api/admin/settings {"changes":[{"key":"Agent:Llm:MainModel","value":"claude-sonnet-5-5"}],"reason":… -> 200 application/json; charset=utf-8 keys {applied,restart_required}
PUT /api/admin/settings {"changes":[{"key":"K0"},{"key":"K1"},{"key":"K2"},{"key":"K3"},{"key":"K4"},{"k… -> 400 application/json; charset=utf-8 {"error":"at most 100 changes at once"}
GET /api/admin/settings/node - -> 200 application/json; charset=utf-8 keys {id,name,version,game_version}
POST /api/admin/settings/node - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
GET /api/admin/settings/audit - -> 200 application/json; charset=utf-8 keys {entries}
GET /api/admin/settings/audit?max=0 - -> 200 application/json; charset=utf-8 keys {entries}
PUT /api/admin/settings/audit - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
GET /api/admin/settings/test - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/settings/test - -> 400 application/json; charset=utf-8 {"error":"bad request body"}
POST /api/admin/settings/test {"kind":"x"} -> 400 application/json; charset=utf-8 {"error":"kind must be anthropic or ollama"}
POST /api/admin/settings/test {"kind":"anthropic"} -> 200 application/json; charset=utf-8 {"ok":false,"message":"the key was rejected (401)","latency_ms":9}
POST /api/admin/settings/test {"kind":"anthropic","api_key":"sk-good-1"} -> 200 application/json; charset=utf-8 {"ok":true,"message":"key accepted; claude-haiku-5-5 is available","latency_ms":12,"models":["claude-haiku-5-5"]}
POST /api/admin/settings/test {"kind":"anthropic","api_key":"sk-good-1","model":"gpt-4o"} -> 400 application/json; charset=utf-8 {"error":"test with a 5.5-generation model (claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5, claude-fable-5-1, claude-mythos-5-1)"}
POST /api/admin/settings/test {"kind":"anthropic","api_key":"sk-bad","base_url":"ftp://x"} -> 400 application/json; charset=utf-8 {"error":"base URL must be an absolute http:// or https:// URL"}
POST /api/admin/settings/test {"kind":"anthropic","api_key":"sk-good-1","base_url":null} -> 200 application/json; charset=utf-8 {"ok":true,"message":"key accepted; claude-haiku-5-5 is available","latency_ms":12,"models":["claude-haiku-5-5"]}
POST /api/admin/settings/test {"kind":"ollama"} -> 200 application/json; charset=utf-8 {"ok":true,"message":"reachable: 2 models","latency_ms":4,"models":["qwen3:8b","llava:7b"]}
POST /api/admin/settings/test {"kind":"ollama","endpoint":"ftp://x"} -> 400 application/json; charset=utf-8 {"error":"endpoint must be an absolute http:// or https:// URL"}
POST /api/admin/settings/test {"kind":"ollama","endpoint":"http://down:11434"} -> 200 application/json; charset=utf-8 {"ok":false,"message":"connection refused or host unreachable","latency_ms":3,"models":[]}
POST /api/admin/settings/test {"kind":"ollama","model":"qwen3:8b"} -> 200 application/json; charset=utf-8 {"ok":true,"message":"reachable: 2 models","latency_ms":4,"models":["qwen3:8b","llava:7b"]}
POST /api/admin/settings/test {"kind":"ollama","model":"mistral"} -> 200 application/json; charset=utf-8 {"ok":false,"message":"reachable (2 models), but \u0027mistral\u0027 is not one of them","latency_ms":4,"models":["qwen3:8b","llava:7b"]}
POST /api/admin/settings/test {"kind":"ollama","endpoint":"http://other:11434","ollama_api_key":" k2 "} -> 200 application/json; charset=utf-8 {"ok":true,"message":"reachable: 2 models","latency_ms":4,"models":["qwen3:8b","llava:7b"]}
POST /api/admin/ollama/models - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
GET /api/admin/ollama/models - -> 200 application/json; charset=utf-8 {"endpoint":"http://10.0.0.5:11434","ok":true,"models":[{"name":"qwen3:8b","capabilities":["completion","tools"]},{"name":"llava:7b","capabilities":["vision"]}]}
GET /api/admin/ollama/models?endpoint=ftp://x - -> 400 application/json; charset=utf-8 {"error":"endpoint must be an absolute http:// or https:// URL"}
GET /api/admin/ollama/models?endpoint= http://down:1  - -> 200 application/json; charset=utf-8 {"endpoint":"http://down:1","ok":false,"error":"connection refused or host unreachable","models":[]}
POST /api/admin/restart/safety - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
GET /api/admin/restart/safety - -> 200 application/json; charset=utf-8 keys {safe,checked_at,night,time_of_day,reasons,bodies}
GET /api/admin/restart - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/restart {} -> 400 application/json; charset=utf-8 {"error":"a reason (at least 3 characters) is required"}
POST /api/admin/restart {"reason":"no"} -> 400 application/json; charset=utf-8 {"error":"a reason (at least 3 characters) is required"}
GET /api/admin/ollama - -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/admin/restart/x - -> 404 application/json; charset=utf-8 {"error":"not found"}
restart unsafe -> 409 application/json; charset=utf-8 {"error":"not safe to restart now: maid_chan_101 is outside at night under open sky","safety":{"safe":false,"checked_at":"1970-01-01T00:00:00+00:00","night":true,"time_of_day":18000,"reasons":["maid_chan_101 is outside at night under open sky"],"bodies":[{"name":"maid_chan_101","dim":0,"y":70,"exposed":true,"high_build":false,"drop":0,"reason":"maid_chan_101 is outside at night under open sky"}]}}
restart forced -> 202 application/json; charset=utf-8 keys {accepted,forced,safety,message}
restart again -> 409 application/json; charset=utf-8 {"error":"a restart is already under way"}
restarts: 0
not restartable -> 409 application/json; charset=utf-8 {"error":"this process can\u0027t restart itself"}
write 2 -> 400 application/json; charset=utf-8 {"error":"no changes","errors":[{"key":"","message":"no changes"}]}
write 3 -> 429 application/json; charset=utf-8 [Retry-After: 40] {"error":"too many admin requests; slow down","retry_after_ms":40000}
restart limited -> 429 application/json; charset=utf-8 [Retry-After: 40] {"error":"too many admin requests; slow down","retry_after_ms":40000}
probe 1 -> 200 application/json; charset=utf-8 keys {endpoint,ok,models}
probe 2 -> 429 application/json; charset=utf-8 [Retry-After: 60] {"error":"too many admin requests; slow down","retry_after_ms":60000}
probe 3 -> 429 application/json; charset=utf-8 [Retry-After: 1] {"error":"too many admin requests; slow down","retry_after_ms":1000}
"""""";
}
