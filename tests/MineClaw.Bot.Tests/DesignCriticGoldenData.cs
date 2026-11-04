namespace MineClaw.Bot.Tests;

/// <summary>The golden dump of <see cref="DesignCriticGoldenTests"/> (generated from the pre-refactor code).</summary>
internal static class DesignCriticGoldenData
{
    public const string Expected = """"""
== ok
  ok True skipped False cached False usd 0 why - text [ok] issues []
  ok True skipped False cached True usd 0 why - text [ok] issues []
== Looks good.
  ok True skipped False cached False usd 0 why - text [ok] issues []
  ok True skipped False cached True usd 0 why - text [ok] issues []
== empty
  ok True skipped False cached False usd 0 why - text [ok] issues []
  ok True skipped False cached True usd 0 why - text [ok] issues []
== null answer
  ok True skipped False cached False usd 0 why - text [ok] issues []
  ok True skipped False cached True usd 0 why - text [ok] issues []
== problem
  ok False skipped False cached False usd 0 why - text [the roof is flat] issues []
  ok False skipped False cached True usd 0 why - text [the roof is flat] issues []
== ok with facts
  ok False skipped False cached False usd 0 why - text [door faces a wall @2,0,2 3,1,3; odd glass] issues [door faces a wall @2,0,2 3,1,3 | odd glass]
  ok False skipped False cached True usd 0 why - text [door faces a wall @2,0,2 3,1,3; odd glass] issues [door faces a wall @2,0,2 3,1,3 | odd glass]
== problem with facts
  ok False skipped False cached False usd 0 why - text [roof too flat; door faces a wall @0,0,0 2,1,2; windows uneven] issues [door faces a wall @0,0,0 2,1,2 | windows uneven]
  ok False skipped False cached True usd 0 why - text [roof too flat; door faces a wall @0,0,0 2,1,2; windows uneven] issues [door faces a wall @0,0,0 2,1,2 | windows uneven]
== empty answer with facts
  ok False skipped False cached False usd 0 why - text [no roof @2,3,2] issues [no roof @2,3,2]
  ok False skipped False cached True usd 0 why - text [no roof @2,3,2] issues [no roof @2,3,2]
== long
  ok False skipped False cached False usd 0 why - text [the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls a…] issues []
  ok False skipped False cached True usd 0 why - text [the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls are much too tall for a hut the walls a…] issues []
== error
  ok True skipped True cached False usd 0 why http 529 text [] issues []
  ok True skipped True cached False usd 0 why http 529 text [] issues []
== throws
  ok True skipped True cached False usd 0 why perception failed: InvalidOperationException text [] issues []
  ok True skipped True cached False usd 0 why perception failed: InvalidOperationException text [] issues []
== no service
  ok True skipped True cached False usd 0 why no perception model configured text [] issues []
  ok True skipped True cached False usd 0 why no perception model configured text [] issues []
== direct
  ok True skipped True cached False usd 0 why no perception model configured text [] issues []
  ok True skipped True cached False usd 0 why no perception model configured text [] issues []
== no budget
  ok True skipped True cached False usd 0 why perception budget used (0/h) text [] issues []
  ok True skipped True cached False usd 0 why perception budget used (0/h) text [] issues []
"""""";
}
