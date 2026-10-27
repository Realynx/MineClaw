using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Supervisor;

/// <summary>A launched clone process.</summary>
public interface ICloneProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct = default);
    /// <summary>Hard kill (process tree). Never throws.</summary>
    void Kill();
}

public interface IProcessLauncher
{
    ICloneProcess Start(string clone, string pipeName);
}

/// <summary>
/// Starts clone processes: the same binary as this one (apphost, or <c>dotnet MineClaw.App.dll</c>) with
/// <c>--clone &lt;name&gt; --parent-pipe &lt;pipe&gt;</c> plus the main's pass-through options. Stdout/stderr are redirected
/// (the clone keeps its own log files) so a child never draws over the TUI; stderr lines go to the main log.
/// </summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    private readonly string _exe;
    private readonly IReadOnlyList<string> _prefix;
    private readonly IReadOnlyList<string> _extra;
    private readonly ILogger _log;

    /// <param name="exe">Executable (null = this process's).</param>
    /// <param name="prefixArgs">Arguments before ours (e.g. the .dll when <paramref name="exe"/> is dotnet).</param>
    /// <param name="extraArgs">Appended to every clone's command line (--config, --data-dir, --log-dir...).</param>
    public ProcessLauncher(string? exe = null, IReadOnlyList<string>? prefixArgs = null, IReadOnlyList<string>? extraArgs = null, ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        _extra = extraArgs ?? [];
        if (exe != null) { _exe = exe; _prefix = prefixArgs ?? []; return; }
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("can't find this executable");
        var name = Path.GetFileNameWithoutExtension(self);
        if (name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            _exe = self;
            var entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("no entry assembly");
            _prefix = [entry];
        }
        else { _exe = self; _prefix = prefixArgs ?? []; }
    }

    public ICloneProcess Start(string clone, string pipeName)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in _prefix) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "--clone", clone, "--parent-pipe", pipeName }) psi.ArgumentList.Add(a);
        foreach (var a in _extra) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("clone process didn't start");
        p.OutputDataReceived += (_, _) => { }; // drained: the clone logs to its own files
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _log.LogWarning("[{Clone}] {Line}", clone, e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new OsProcess(p);
    }

    private sealed class OsProcess(Process p) : ICloneProcess
    {
        public int Id => p.Id;
        public bool HasExited { get { try { return p.HasExited; } catch (Exception) { return true; } } }
        public int? ExitCode { get { try { return p.HasExited ? p.ExitCode : null; } catch (Exception) { return null; } } }
        public Task WaitForExitAsync(CancellationToken ct = default) => p.WaitForExitAsync(ct);
        public void Kill() { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { } }
        public void Dispose() { try { p.Dispose(); } catch (Exception) { } }
    }
}
