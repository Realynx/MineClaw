global using DuplexStream = MineClaw.Sim.DuplexStream;

namespace MineClaw.Client.Tests;

// Keep the existing test seam (also linked by Bot/App/Skills/Hive tests); the implementation is shared with the TCP sandbox.
internal sealed class FakeServer(bool release1122 = false) : MineClaw.Sim.FakeServer(release1122) { }
