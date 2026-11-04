namespace MineClaw.App.Tests;

/// <summary>The golden dump of SupervisorServerGoldenTests (generated from the pre-refactor code).</summary>
internal static class SupervisorServerGoldenData
{
    public const string Expected = """"""
== token -
GET /api/admin/settings -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/admin/x -> 403 application/json; charset=utf-8 {"error":"admin writes are off (Supervisor:AllowAdminWrites)"}
POST /api/admin/x [t] -> 403 application/json; charset=utf-8 {"error":"admin writes are off (Supervisor:AllowAdminWrites)"}
GET /api/viewer/now -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/designer/sessions -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/story -> 404 application/json; charset=utf-8 {"error":"not found"}
POST /api/bots -> 405 application/json; charset=utf-8 [Allow: GET, HEAD] {"error":"read-only: GET only"}
DELETE / -> 405 application/json; charset=utf-8 [Allow: GET, HEAD] {"error":"read-only: GET only"}
HEAD / -> 200 text/html; charset=utf-8 keys <27685 chars>
GET / -> 200 text/html; charset=utf-8 keys <27685 chars>
GET /index.html -> 200 text/html; charset=utf-8 keys <27685 chars>
GET /favicon.ico -> 204 image/x-icon <0 bytes E3B0C44298FC>
GET /api/bots -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/bots [t] -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/bots/?token=t -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/bots?token=wrong -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/bots [tt] -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/hive/tree [t] -> 200 application/json; charset=utf-8 {"id":"main","name":"main","kind":"main","state":"running","children":[]}
GET /api/status?bot=67 [t] -> 200 application/json; charset=utf-8 {"main":"maid_chan","selected":"67"}
GET /api/events [t] -> 200 application/json; charset=utf-8 {"events":[{"id":1,"at":"2026-11-04T12:00:00+00:00","kind":"chat","level":"info","text":"a"},{"id":2,"at":"2026-11-04T12:00:00+00:00","kind":"danger","level":"warn","text":"b"}],"last":2}
GET /api/events?since=3 [t] -> 200 application/json; charset=utf-8 {"events":[{"id":4,"at":"2026-11-04T12:00:00+00:00","kind":"chat","level":"info","text":"a"},{"id":5,"at":"2026-11-04T12:00:00+00:00","kind":"danger","level":"warn","text":"b"}],"last":5}
GET /api/events?since=9 [t] -> 200 application/json; charset=utf-8 {"events":[],"last":9}
GET /api/events?since=x [t] -> 200 application/json; charset=utf-8 {"events":[{"id":1,"at":"2026-11-04T12:00:00+00:00","kind":"chat","level":"info","text":"a"},{"id":2,"at":"2026-11-04T12:00:00+00:00","kind":"danger","level":"warn","text":"b"}],"last":2}
GET /api/locate?q=mob chamber [t] -> 200 application/json; charset=utf-8 {"name":"mob chamber","dim":0,"x":20.5,"y":80,"z":-4.5}
GET /api/locate?q= [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/locate?q=nowhere [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/locate [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view/hive [t] -> 200 image/png [X-Height: 3] [X-Tokens: 1] [X-Width: 4] <12 bytes 1F8A8E0E11A1>
GET /view/hive?meta=1 [t] -> 200 application/json; charset=utf-8 {"width":4,"height":3,"tokens":1,"bytes":12,"legend":"hive","facts":["fact: hive"],"note":"note"}
GET /camera [t] -> 400 application/json; charset=utf-8 {"error":"camera needs x, y, z (yaw, pitch, dim, w, h optional)"}
GET /camera?x=1&y=2 [t] -> 400 application/json; charset=utf-8 {"error":"camera needs x, y, z (yaw, pitch, dim, w, h optional)"}
GET /camera?x=1&y=500&z=3 [t] -> 200 image/png [X-Height: 360] [X-Tokens: 1] [X-Width: 480] <26 bytes 240918FFE2E9>
GET /camera?x=1&y=64&z=3&meta=true [t] -> 200 application/json; charset=utf-8 {"width":480,"height":360,"tokens":1,"bytes":25,"legend":"camera 1,64,3","facts":["fact: camera 1,64,3"],"note":"note"}
GET /camera?x=1.25&y=64&z=-3&yaw=90&pitch=-120&dim=-1&w=200&h=100 [t] -> 200 image/png [X-Height: 100] [X-Tokens: 1] [X-Width: 200] <29 bytes 2F82EC0E7296>
GET /camera?x=NaN&y=1&z=1 [t] -> 400 application/json; charset=utf-8 {"error":"camera needs x, y, z (yaw, pitch, dim, w, h optional)"}
GET /camera?x=1&y=1&z=1&dim=7 [t] -> 200 image/png [X-Height: 360] [X-Tokens: 1] [X-Width: 480] <24 bytes 4D13E3423355>
GET /api/blueprints [t] -> 200 application/json; charset=utf-8 {"designs":[{"id":7,"name":"hut"}]}
GET /api/blueprints/7 [t] -> 200 application/json; charset=utf-8 {"id":7,"blocks":12}
GET /api/blueprints/8 [t] -> 404 application/json; charset=utf-8 {"error":"no such design"}
GET /api/blueprints/x [t] -> 404 application/json; charset=utf-8 {"error":"no such design"}
GET /view/buildplan/7 [t] -> 200 text/html; charset=utf-8 <html>plan 7</html>
GET /view/buildplan/7?meta=1 [t] -> 200 application/json; charset=utf-8 {"id":7,"steps":3}
GET /view/buildplan/8 [t] -> 404 application/json; charset=utf-8 {"error":"no build plan for that design"}
GET /view/buildplan/x [t] -> 400 application/json; charset=utf-8 {"error":"design id?"}
GET /api/buildplans/7 [t] -> 200 application/json; charset=utf-8 {"id":7,"steps":3}
GET /api/buildplans/8 [t] -> 404 application/json; charset=utf-8 {"error":"no build plan for that design"}
GET /api/buildplans/ [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view/blueprint/7 [t] -> 200 image/png [X-Height: 3] [X-Tokens: 1] [X-Width: 4] <16 bytes F648D65CE188>
GET /view/blueprint/7?meta=true [t] -> 200 application/json; charset=utf-8 {"id":7,"blocks":12}
GET /view/blueprint/8?meta=1 [t] -> 404 application/json; charset=utf-8 {"error":"no such design"}
GET /view/blueprint/8 [t] -> 404 application/json; charset=utf-8 {"error":"no preview for that design"}
GET /view/blueprint/x [t] -> 400 application/json; charset=utf-8 {"error":"design id?"}
GET /view/maid_chan [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <31 bytes ECF4168B291B>
GET /view/67?mode=EYE&radius=20 [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <23 bytes F75F46977F7A>
GET /view/67?mode=brain&radius=1 [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <24 bytes 7B40D6ED40F2>
GET /view/67?mode=iso&xray=on [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <20 bytes 1D5E7C9B56B7>
GET /view/67?mode=iso&xray=0 [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <21 bytes 5B0F82300888>
GET /view/67?mode=iso&xray=maybe&radius=x [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <22 bytes 46A788A92EE6>
GET /view/67?mode=sheet&meta=1 [t] -> 200 application/json; charset=utf-8 {"width":4,"height":3,"tokens":1,"bytes":24,"legend":"67 sheet r6 Auto","facts":["fact: 67 sheet r6 Auto"],"seen":"s\u00E9en\nline","note":"note"}
GET /view/67?mode=dance [t] -> 400 application/json; charset=utf-8 {"error":"unknown mode \u0027dance\u0027 (sheet|brain|eye|iso|shoulder|orbit|high|top|side|layers)"}
GET /view/nobody [t] -> 404 application/json; charset=utf-8 {"error":"no bot \u0027nobody\u0027 online"}
GET /view/maid%5Fchan [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <31 bytes ECF4168B291B>
GET /view/busy [t] -> 503 application/json; charset=utf-8 [Retry-After: 1] {"error":"renderer busy, retry"}
GET /view/gone [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view/cancel [t] -> 503 application/json; charset=utf-8 {"error":"cancelled"}
GET /view/boom [t] -> 500 application/json; charset=utf-8 {"error":"kaboom"}
GET /api/nothing [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
calls: hive | camera CameraQuery { X = 1, Y = 200, Z = 3, Yaw = 0, Pitch = 20, Dim = 0, W = 480, H = 360 } | camera CameraQuery { X = 1, Y = 64, Z = 3, Yaw = 0, Pitch = 20, Dim = 0, W = 480, H = 360 } | camera CameraQuery { X = 1.25, Y = 64, Z = -3, Yaw = 90, Pitch = -90, Dim = -1, W = 200, H = 100 } | camera CameraQuery { X = 1, Y = 1, Z = 1, Yaw = 0, Pitch = 20, Dim = 0, W = 480, H = 360 } | view maid_chan sheet 6 Auto | view 67 eye 12 Auto | view 67 brain 3 Auto | view 67 iso 6 On | view 67 iso 6 Off | view 67 iso 6 Auto | view 67 sheet 6 Auto | view busy sheet 6 Auto | view gone sheet 6 Auto | view cancel sheet 6 Auto | view boom sheet 6 Auto
== token t
GET /api/admin/settings -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/admin/x -> 403 application/json; charset=utf-8 {"error":"admin writes are off (Supervisor:AllowAdminWrites)"}
POST /api/admin/x [t] -> 403 application/json; charset=utf-8 {"error":"admin writes are off (Supervisor:AllowAdminWrites)"}
GET /api/viewer/now -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/designer/sessions -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/story -> 404 application/json; charset=utf-8 {"error":"not found"}
POST /api/bots -> 405 application/json; charset=utf-8 [Allow: GET, HEAD] {"error":"read-only: GET only"}
DELETE / -> 405 application/json; charset=utf-8 [Allow: GET, HEAD] {"error":"read-only: GET only"}
HEAD / -> 200 text/html; charset=utf-8 keys <27685 chars>
GET / -> 200 text/html; charset=utf-8 keys <27685 chars>
GET /index.html -> 200 text/html; charset=utf-8 keys <27685 chars>
GET /favicon.ico -> 204 image/x-icon <0 bytes E3B0C44298FC>
GET /api/bots -> 401 application/json; charset=utf-8 {"error":"token required (X-Supervisor-Token header or ?token=)"}
GET /api/bots [t] -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/bots/?token=t -> 200 application/json; charset=utf-8 keys {at,bots}
GET /api/bots?token=wrong -> 401 application/json; charset=utf-8 {"error":"token required (X-Supervisor-Token header or ?token=)"}
GET /api/bots [tt] -> 401 application/json; charset=utf-8 {"error":"token required (X-Supervisor-Token header or ?token=)"}
GET /api/hive/tree [t] -> 200 application/json; charset=utf-8 {"id":"main","name":"main","kind":"main","state":"running","children":[]}
GET /api/status?bot=67 [t] -> 200 application/json; charset=utf-8 {"main":"maid_chan","selected":"67"}
GET /api/events [t] -> 200 application/json; charset=utf-8 {"events":[{"id":1,"at":"2026-11-04T12:00:00+00:00","kind":"chat","level":"info","text":"a"},{"id":2,"at":"2026-11-04T12:00:00+00:00","kind":"danger","level":"warn","text":"b"}],"last":2}
GET /api/events?since=3 [t] -> 200 application/json; charset=utf-8 {"events":[{"id":4,"at":"2026-11-04T12:00:00+00:00","kind":"chat","level":"info","text":"a"},{"id":5,"at":"2026-11-04T12:00:00+00:00","kind":"danger","level":"warn","text":"b"}],"last":5}
GET /api/events?since=9 [t] -> 200 application/json; charset=utf-8 {"events":[],"last":9}
GET /api/events?since=x [t] -> 200 application/json; charset=utf-8 {"events":[{"id":1,"at":"2026-11-04T12:00:00+00:00","kind":"chat","level":"info","text":"a"},{"id":2,"at":"2026-11-04T12:00:00+00:00","kind":"danger","level":"warn","text":"b"}],"last":2}
GET /api/locate?q=mob chamber [t] -> 200 application/json; charset=utf-8 {"name":"mob chamber","dim":0,"x":20.5,"y":80,"z":-4.5}
GET /api/locate?q= [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/locate?q=nowhere [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /api/locate [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view/hive [t] -> 200 image/png [X-Height: 3] [X-Tokens: 1] [X-Width: 4] <12 bytes 1F8A8E0E11A1>
GET /view/hive?meta=1 [t] -> 200 application/json; charset=utf-8 {"width":4,"height":3,"tokens":1,"bytes":12,"legend":"hive","facts":["fact: hive"],"note":"note"}
GET /camera [t] -> 400 application/json; charset=utf-8 {"error":"camera needs x, y, z (yaw, pitch, dim, w, h optional)"}
GET /camera?x=1&y=2 [t] -> 400 application/json; charset=utf-8 {"error":"camera needs x, y, z (yaw, pitch, dim, w, h optional)"}
GET /camera?x=1&y=500&z=3 [t] -> 200 image/png [X-Height: 360] [X-Tokens: 1] [X-Width: 480] <26 bytes 240918FFE2E9>
GET /camera?x=1&y=64&z=3&meta=true [t] -> 200 application/json; charset=utf-8 {"width":480,"height":360,"tokens":1,"bytes":25,"legend":"camera 1,64,3","facts":["fact: camera 1,64,3"],"note":"note"}
GET /camera?x=1.25&y=64&z=-3&yaw=90&pitch=-120&dim=-1&w=200&h=100 [t] -> 200 image/png [X-Height: 100] [X-Tokens: 1] [X-Width: 200] <29 bytes 2F82EC0E7296>
GET /camera?x=NaN&y=1&z=1 [t] -> 400 application/json; charset=utf-8 {"error":"camera needs x, y, z (yaw, pitch, dim, w, h optional)"}
GET /camera?x=1&y=1&z=1&dim=7 [t] -> 200 image/png [X-Height: 360] [X-Tokens: 1] [X-Width: 480] <24 bytes 4D13E3423355>
GET /api/blueprints [t] -> 200 application/json; charset=utf-8 {"designs":[{"id":7,"name":"hut"}]}
GET /api/blueprints/7 [t] -> 200 application/json; charset=utf-8 {"id":7,"blocks":12}
GET /api/blueprints/8 [t] -> 404 application/json; charset=utf-8 {"error":"no such design"}
GET /api/blueprints/x [t] -> 404 application/json; charset=utf-8 {"error":"no such design"}
GET /view/buildplan/7 [t] -> 200 text/html; charset=utf-8 <html>plan 7</html>
GET /view/buildplan/7?meta=1 [t] -> 200 application/json; charset=utf-8 {"id":7,"steps":3}
GET /view/buildplan/8 [t] -> 404 application/json; charset=utf-8 {"error":"no build plan for that design"}
GET /view/buildplan/x [t] -> 400 application/json; charset=utf-8 {"error":"design id?"}
GET /api/buildplans/7 [t] -> 200 application/json; charset=utf-8 {"id":7,"steps":3}
GET /api/buildplans/8 [t] -> 404 application/json; charset=utf-8 {"error":"no build plan for that design"}
GET /api/buildplans/ [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view/blueprint/7 [t] -> 200 image/png [X-Height: 3] [X-Tokens: 1] [X-Width: 4] <16 bytes F648D65CE188>
GET /view/blueprint/7?meta=true [t] -> 200 application/json; charset=utf-8 {"id":7,"blocks":12}
GET /view/blueprint/8?meta=1 [t] -> 404 application/json; charset=utf-8 {"error":"no such design"}
GET /view/blueprint/8 [t] -> 404 application/json; charset=utf-8 {"error":"no preview for that design"}
GET /view/blueprint/x [t] -> 400 application/json; charset=utf-8 {"error":"design id?"}
GET /view/maid_chan [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <31 bytes ECF4168B291B>
GET /view/67?mode=EYE&radius=20 [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <23 bytes F75F46977F7A>
GET /view/67?mode=brain&radius=1 [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <24 bytes 7B40D6ED40F2>
GET /view/67?mode=iso&xray=on [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <20 bytes 1D5E7C9B56B7>
GET /view/67?mode=iso&xray=0 [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <21 bytes 5B0F82300888>
GET /view/67?mode=iso&xray=maybe&radius=x [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <22 bytes 46A788A92EE6>
GET /view/67?mode=sheet&meta=1 [t] -> 200 application/json; charset=utf-8 {"width":4,"height":3,"tokens":1,"bytes":24,"legend":"67 sheet r6 Auto","facts":["fact: 67 sheet r6 Auto"],"seen":"s\u00E9en\nline","note":"note"}
GET /view/67?mode=dance [t] -> 400 application/json; charset=utf-8 {"error":"unknown mode \u0027dance\u0027 (sheet|brain|eye|iso|shoulder|orbit|high|top|side|layers)"}
GET /view/nobody [t] -> 404 application/json; charset=utf-8 {"error":"no bot \u0027nobody\u0027 online"}
GET /view/maid%5Fchan [t] -> 200 image/png [X-Height: 3] [X-Seen: s?en?line] [X-Tokens: 1] [X-Width: 4] <31 bytes ECF4168B291B>
GET /view/busy [t] -> 503 application/json; charset=utf-8 [Retry-After: 1] {"error":"renderer busy, retry"}
GET /view/gone [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view/cancel [t] -> 503 application/json; charset=utf-8 {"error":"cancelled"}
GET /view/boom [t] -> 500 application/json; charset=utf-8 {"error":"kaboom"}
GET /api/nothing [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
GET /view [t] -> 404 application/json; charset=utf-8 {"error":"not found"}
calls: hive | camera CameraQuery { X = 1, Y = 200, Z = 3, Yaw = 0, Pitch = 20, Dim = 0, W = 480, H = 360 } | camera CameraQuery { X = 1, Y = 64, Z = 3, Yaw = 0, Pitch = 20, Dim = 0, W = 480, H = 360 } | camera CameraQuery { X = 1.25, Y = 64, Z = -3, Yaw = 90, Pitch = -90, Dim = -1, W = 200, H = 100 } | camera CameraQuery { X = 1, Y = 1, Z = 1, Yaw = 0, Pitch = 20, Dim = 0, W = 480, H = 360 } | view maid_chan sheet 6 Auto | view 67 eye 12 Auto | view 67 brain 3 Auto | view 67 iso 6 On | view 67 iso 6 Off | view 67 iso 6 Auto | view 67 sheet 6 Auto | view busy sheet 6 Auto | view gone sheet 6 Auto | view cancel sheet 6 Auto | view boom sheet 6 Auto
"""""";
}
