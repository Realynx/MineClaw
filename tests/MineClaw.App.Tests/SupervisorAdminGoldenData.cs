namespace MineClaw.App.Tests;

/// <summary>The golden dump of SupervisorAdminGoldenTests (generated from the pre-refactor code).</summary>
internal static class SupervisorAdminGoldenData
{
    public const string Expected = """"""
off -> 403 application/json; charset=utf-8 {"error":"admin writes are off (Supervisor:AllowAdminWrites)"}
no token -> 403 application/json; charset=utf-8 {"error":"admin writes need Supervisor:Token"}
wrong token -> 403 application/json; charset=utf-8 {"error":"X-Supervisor-Token header required"}
no header -> 403 application/json; charset=utf-8 {"error":"X-Supervisor-Token header required"}
no admin -> 403 application/json; charset=utf-8 {"error":"admin writes are not available in this process"}
too large -> 413 application/json; charset=utf-8 {"error":"body over 262144 bytes"}
too large shard -> 413 application/json; charset=utf-8 {"error":"body over 67108864 bytes"}
no shards -> 403 application/json; charset=utf-8 {"error":"memory shards are not available in this process"}
no compaction -> 500 application/json; charset=utf-8 {"error":"the activity compaction is not available in this process"}
busy -> 409 application/json; charset=utf-8 {"error":"a rebuild is already running (job j0)"}
GET /api/admin - -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/admin/nothing - -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/admin/memory - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/memory - -> 400 application/json; charset=utf-8 {"error":"bad request body: JSON body required"}
POST /api/admin/memory [1] -> 400 application/json; charset=utf-8 {"error":"bad request body: JSON object required"}
POST /api/admin/memory {bad -> 400 application/json; charset=utf-8 {"error":"bad request body: \u0027b\u0027 is an invalid start of a property name. Expected a \u0027\u0022\u0027. LineNumber: 0 | BytePositionInLine: 1."}
POST /api/admin/memory {"op":"update","kind":"fact","id":1,"fields":{"text":"b","n":5,"t":true,"f":false,"z":null},"who":"Fox"} -> 200 application/json; charset=utf-8 {"ok":true,"message":"updated fact #1","id":"1","before":"{\u0022text\u0022:\u0022a\u0022}","after":"{\u0022text\u0022:\u0022b\u0022}","reembedded":true}
POST /api/admin/memory {"op":"update","kind":"note"} -> 400 application/json; charset=utf-8 {"ok":false,"message":"unknown kind","reembedded":false}
POST /api/admin/memory {"kind":"fact","fields":{"x":[1]}} -> 400 application/json; charset=utf-8 {"error":"bad request body: fields must be strings, numbers or booleans"}
POST /api/admin/MEMORY/ {"kind":"fact"} -> 200 application/json; charset=utf-8 {"ok":true,"message":"updated fact #1","id":"1","before":"{\u0022text\u0022:\u0022a\u0022}","after":"{\u0022text\u0022:\u0022b\u0022}","reembedded":true}
GET /api/admin/rebuild/estimate - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/rebuild/estimate {"scope":"node","node":7} -> 200 application/json; charset=utf-8 {"summaries":3,"input_tokens":900,"output_tokens":750,"embedding_tokens":750,"usd":0.0042,"model":"claude-haiku-5-5","note":"ok"}
POST /api/admin/rebuild/estimate {"scope":"kind","kind":"fact","max_usd":"1.5","who":"Fox"} -> 200 application/json; charset=utf-8 {"summaries":3,"input_tokens":900,"output_tokens":750,"embedding_tokens":750,"usd":0.0042,"model":"claude-haiku-5-5","note":"ok"}
POST /api/admin/rebuild/estimate {"scope":"node","node":"x"} -> 400 application/json; charset=utf-8 {"error":"bad request body: The input string \u0027x\u0027 was not in a correct format."}
GET /api/admin/rebuild - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/rebuild {"scope":"node","node":7} -> 202 application/json; charset=utf-8 {"job_id":"j1"}
GET /api/admin/backup - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/backup - -> 200 application/json; charset=utf-8 {"path":"/data/backups/memory-backup-x.db"}
POST /api/admin/backup {"who":"Fox"} -> 200 application/json; charset=utf-8 {"path":"/data/backups/memory-backup-x.db"}
POST /api/admin/backup {bad -> 200 application/json; charset=utf-8 {"path":"/data/backups/memory-backup-x.db"}
POST /api/admin/backup {"who":""} -> 200 application/json; charset=utf-8 {"path":"/data/backups/memory-backup-x.db"}
POST /api/admin/audit - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
GET /api/admin/audit - -> 200 application/json; charset=utf-8 {"target":"live:test","entries":[{"at":"1970-01-01T00:00:00+00:00","who":"Fox","target":"live:test","op":"update","kind":"fact","id":"1","before":"a","after":"b","ok":true,"message":"ok"}]}
HEAD /api/admin/audit?max=9999 - -> 200 application/json; charset=utf-8 {"target":"live:test","entries":[{"at":"1970-01-01T00:00:00+00:00","who":"Fox","target":"live:test","op":"update","kind":"fact","id":"1","before":"a","after":"b","ok":true,"message":"ok"}]}
GET /api/admin/audit?max=0 - -> 200 application/json; charset=utf-8 {"target":"live:test","entries":[{"at":"1970-01-01T00:00:00+00:00","who":"Fox","target":"live:test","op":"update","kind":"fact","id":"1","before":"a","after":"b","ok":true,"message":"ok"}]}
GET /api/admin/audit?max=x - -> 200 application/json; charset=utf-8 {"target":"live:test","entries":[{"at":"1970-01-01T00:00:00+00:00","who":"Fox","target":"live:test","op":"update","kind":"fact","id":"1","before":"a","after":"b","ok":true,"message":"ok"}]}
GET /api/admin/shard/import - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/shard/import/preview - -> 400 application/json; charset=utf-8 {"error":"bad request body: JSON body required"}
POST /api/admin/shard/import/preview {"mode":"merge"} -> 400 application/json; charset=utf-8 {"error":"bad request body: shard (base64 .mcshard) required"}
POST /api/admin/shard/import/preview {"shard":"!!"} -> 400 application/json; charset=utf-8 {"error":"bad request body: shard is not valid base64"}
POST /api/admin/shard/import/preview {"shard":"YWJj","mode":"replace","conflict":"mine","include_rules":true,"include_world":"true","who":"Fox","max_usd":2} -> 400 application/json; charset=utf-8 {"error":"shard refused: bad magic"}
POST /api/admin/shard/import/preview {"shard":"Ym9vbQ=="} -> 500 application/json; charset=utf-8 {"error":"crash"}
POST /api/admin/shard/import/preview {"shard":"YWJj","mode":"sideways"} -> 400 application/json; charset=utf-8 {"error":"shard refused: bad magic"}
POST /api/admin/shard/import {"shard":"YWJj"} -> 400 application/json; charset=utf-8 {"error":"shard refused: not a shard"}
GET /api/admin/activity/compact - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
POST /api/admin/activity/compact - -> 200 application/json; charset=utf-8 {"ok":true,"dry_run":false,"docs_before":10,"docs_after":8,"activity_before":5,"activity_after":3,"processed":5,"removed":2,"rewritten":1,"episodes":1,"dirty_summaries":0,"message":"done","samples":["a"]}
POST /api/admin/activity/compact {"dry_run":true,"who":"Fox"} -> 200 application/json; charset=utf-8 {"ok":true,"dry_run":true,"docs_before":10,"docs_after":8,"activity_before":5,"activity_after":3,"processed":5,"removed":2,"rewritten":1,"episodes":1,"dirty_summaries":0,"message":"done","samples":["a"]}
POST /api/admin/activity/compact {"dry_run":"yes","who":"fail"} -> 400 application/json; charset=utf-8 {"ok":false,"dry_run":false,"docs_before":10,"docs_after":8,"activity_before":5,"activity_after":3,"processed":5,"removed":2,"rewritten":1,"episodes":1,"dirty_summaries":0,"message":"done","samples":["a"]}
POST /api/admin/activity/compact [1] -> 400 application/json; charset=utf-8 {"error":"bad request body: JSON object required"}
POST /api/admin/activity/compact {"who":"cancel"} -> 503 application/json; charset=utf-8 {"error":"cancelled"}
POST /api/admin/schema - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
GET /api/admin/schema - -> 200 application/json; charset=utf-8 keys {target,kinds}
GET /api/admin/rebuild/j1 - -> 200 application/json; charset=utf-8 {"job_id":"j1","state":"done","started":"1970-01-01T00:00:00+00:00","finished":"1970-01-01T00:00:00+00:00","request":{"scope":"node","node":7,"max_usd":0.50,"who":"admin"},"progress":{"stage":"done","done":1,"total":1},"result":{"ok":true,"message":"rebuilt","diffs":[{"index":7,"layer":2,"old":"old","new":"new"}],"spent_usd":0.001,"elapsed":"00:00:02"}}
GET /api/admin/rebuild/j2 - -> 404 application/json; charset=utf-8 {"error":"no such job"}
POST /api/admin/rebuild/j1 - -> 405 application/json; charset=utf-8 [Allow: GET] {"error":"use GET"}
POST /api/admin/rebuild/j1/cancel - -> 200 application/json; charset=utf-8 {"job_id":"j1","cancelled":true}
POST /api/admin/rebuild/j2/CANCEL - -> 404 application/json; charset=utf-8 {"error":"no running job j2"}
GET /api/admin/rebuild/j1/cancel - -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
PUT /api/admin/memory {} -> 405 application/json; charset=utf-8 [Allow: POST] {"error":"use POST"}
calls: estimate RebuildRequest { Scope = node, Node = 7, Kind = , MaxUsd = 0.50, Who = admin } | estimate RebuildRequest { Scope = kind, Node = , Kind = fact, MaxUsd = 1.5, Who = Fox } | start RebuildRequest { Scope = node, Node = 7, Kind = , MaxUsd = 0.50, Who = admin } | backup admin | backup Fox | backup admin | backup admin | audit 200 | audit 5000 | audit 1 | audit 200 | preview abc ReplaceSection KeepMine True True Fox 2 | preview boom Merge KeepNewer False False admin 0.25 | preview abc Merge KeepNewer False False admin 0.25 | import abc | compact False admin | compact True Fox | compact False fail | compact False cancel
"""""";
}
