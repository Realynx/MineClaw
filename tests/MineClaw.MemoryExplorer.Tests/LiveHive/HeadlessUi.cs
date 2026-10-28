using Avalonia;
using Avalonia.Headless;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>One Avalonia headless session for the whole test assembly (its dispatcher thread is "the UI thread").</summary>
public static class HeadlessUi
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessUi)));

    public static Task Run(Action a) => Session.Value.Dispatch(a, CancellationToken.None);
    public static Task<T> Run<T>(Func<T> f) => Session.Value.Dispatch(f, CancellationToken.None);
}

/// <summary>Tests that touch Avalonia's dispatcher run one at a time.</summary>
[CollectionDefinition("avalonia", DisableParallelization = true)]
public sealed class AvaloniaCollection;
