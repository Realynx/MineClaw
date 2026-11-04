namespace MineClaw.App.Tests;

/// <summary>The golden dump of AppCliGoldenTests (generated from the pre-refactor code).</summary>
internal static class AppCliGoldenData
{
    public const string Expected = """"""
[]
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--script' 'a.txt']
  script=a.txt minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--script=b.txt']
  script=b.txt minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--script']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --script needs a value
['--script' '--tui']
  script=- minutes=- void=False nokey=False tui=On stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --script needs a value
['--script=']
  script= minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--minutes' '2.5']
  script=- minutes=2.5 void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--minutes=0']
  script=- minutes=0 void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--minutes' '-1']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --minutes: '-1' is not a number
['--minutes' 'abc']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --minutes: 'abc' is not a number
['--minutes']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --minutes needs a value
['--minutes' '1e2']
  script=- minutes=100 void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--void-abort']
  script=- minutes=- void=True nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--record']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=logs/recordings sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--record' 'dir']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=dir sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--record=d2']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=d2 sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--record' '--tui']
  script=- minutes=- void=False nokey=False tui=On stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=logs/recordings sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--no-key-check']
  script=- minutes=- void=False nokey=True tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--no-tui']
  script=- minutes=- void=False nokey=False tui=Off stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--tui']
  script=- minutes=- void=False nokey=False tui=On stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--tui' '--no-tui']
  script=- minutes=- void=False nokey=False tui=Off stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--stdin-commands']
  script=- minutes=- void=False nokey=False tui=Auto stdin=True demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo-bots' '9']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=5 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo-bots' '0']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=1 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo-bots' '2.7']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=2 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo-bots']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --demo-bots needs a value
['--demo-bots' 'x']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --demo-bots: 'x' is not a number
['--demo' '--frame']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=120x40 bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo' '--frame' '80x24']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=80x24 bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo' '--frame=80X24']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=80x24 bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--demo' '--frame' '10x5']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --frame: '10x5' should look like 120x40
['--demo' '--frame' 'wide']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --frame: 'wide' should look like 120x40
['--frame' '80x24']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=80x24 bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --frame needs --demo
['--demo' '--frame' '80x24x2']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=True bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --frame: '80x24x2' should look like 120x40
['--demo' '--frame' '--tui']
  script=- minutes=- void=False nokey=False tui=On stdin=False demo=True bots=3 frame=120x40 bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--bot' '3']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=3 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--bot' '0']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--bot' 'x']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --bot: 'x' is not a number
['--bot']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --bot needs a value
['--view' 'top']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=top data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--view']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --view needs a value
['--data-dir' 'd']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=d log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--log-dir' 'l']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=l config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--config' 'c.json']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=c.json shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--config=x=y']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=x=y shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--shutdown-timeout' '7']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=7 logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--shutdown-timeout' 'z']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --shutdown-timeout: 'z' is not a number
['--log-format' 'JSON']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=json help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--log-format' 'xml']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --log-format: 'xml' (auto|simple|systemd|json)
['--log-format']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --log-format needs a value
['--log-format=systemd']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=systemd help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--clone' 'maid_chan_67']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=maid_chan_67 pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --clone and --parent-pipe go together (clones never run unsupervised)
['--parent-pipe' 'p']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=p record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --clone and --parent-pipe go together (clones never run unsupervised)
['--clone' 'maid_chan_67' '--parent-pipe' 'p']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=maid_chan_67 pipe=p record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--clone']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --clone needs a value
['--server-version' '1.12.2']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=1.12.2 reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--server-version' 'b1.8.1']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=b1.8.1 reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--server-version' 'auto']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=auto reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--server-version' '9.9']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --server-version: '9.9' (auto|b1.8.1|1.12.2)
['--server-version']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --server-version needs a value
['-h']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=True schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--help']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=True schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['-?']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=True schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--schema-version']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=True clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--reset-memory']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=- modes=[] nosdk=False backup=- yes=False
  ! --reset-memory needs --pack <version pack .mcshard>
['--reset-memory' '--pack' 'p.mcshard']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=p.mcshard modes=[] nosdk=False backup=- yes=False
['--reset-memory' '--pack' ' ']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=  modes=[] nosdk=False backup=- yes=False
  ! --reset-memory needs --pack <version pack .mcshard>
['--pack' 'p.mcshard']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=p.mcshard modes=[] nosdk=False backup=- yes=False
  ! --pack/--modes/--no-sdk/--backup-dir go with --reset-memory
['--reset-memory' '--pack' 'p' '--modes' 'none']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=p modes=[] nosdk=False backup=- yes=False
['--reset-memory' '--pack' 'p' '--modes' 'a.mcshard, b.mcshard,,']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=p modes=[a.mcshard|b.mcshard] nosdk=False backup=- yes=False
['--reset-memory' '--pack' 'p' '--modes' 'NONE']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=p modes=[] nosdk=False backup=- yes=False
['--modes' 'a']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[a] nosdk=False backup=- yes=False
  ! --pack/--modes/--no-sdk/--backup-dir go with --reset-memory
['--modes' 'none']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--modes']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! --modes needs a value
['--no-sdk']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=True backup=- yes=False
  ! --pack/--modes/--no-sdk/--backup-dir go with --reset-memory
['--reset-memory' '--pack' 'p' '--no-sdk' '--backup-dir' 'bk' '--yes']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=True pack=p modes=[] nosdk=True backup=bk yes=True
['--backup-dir' 'bk']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=bk yes=False
  ! --pack/--modes/--no-sdk/--backup-dir go with --reset-memory
['-y']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=True
['--yes']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=True
['--reset-memory' '--pack' 'p' '--clone' 'c' '--parent-pipe' 'p']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=c pipe=p record=- sv=- reset=True pack=p modes=[] nosdk=False backup=- yes=False
  ! --reset-memory runs alone, never as a hive clone
['--bogus']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! unknown option '--bogus'
['bare']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! unknown option 'bare'
['-x']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! unknown option '-x'
['--=x']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! unknown option '--=x'
['--a=b']
  script=- minutes=- void=False nokey=False tui=Auto stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
  ! unknown option '--a=b'
['--tui=on']
  script=- minutes=- void=False nokey=False tui=On stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--no-tui=1']
  script=- minutes=- void=False nokey=False tui=Off stdin=False demo=False bots=3 frame=- bot=1 view=- data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
['--minutes' '1' '--script' 's' '--bot' '2' '--view' 'iso' '--demo' '--demo-bots' '4' '--frame' '100x30']
  script=s minutes=1 void=False nokey=False tui=Auto stdin=False demo=True bots=4 frame=100x30 bot=2 view=iso data=- log=- config=- shutdown=- logfmt=- help=False schema=False clone=- pipe=- record=- sv=- reset=False pack=- modes=[] nosdk=False backup=- yes=False
"""""";
}
