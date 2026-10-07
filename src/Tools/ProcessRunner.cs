using System.Diagnostics;
using System.Text;

namespace VanityAgent.Tools;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        int timeoutMs, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            WorkingDirectory       = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUTF8"]       = "1";
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        VanityAgent.Infra.ChildJob.Own(process);   // dies with the host

        process.StandardInput.Close();
        var stdOutTask = ReadCappedAsync(process.StandardOutput, cancellationToken);
        var stdErrTask = ReadCappedAsync(process.StandardError,  cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }   // an exited process makes Kill throw (first-chance noise in the debugger)
            if (!timedOut) throw;
        }

        var stdOut   = await SafeRead(stdOutTask, DrainMs).ConfigureAwait(false);
        var stdErr   = await SafeRead(stdErrTask, DrainMs).ConfigureAwait(false);
        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ProcessResult(exitCode, stdOut, stdErr, timedOut);
    }

    private const int MaxCapturedChars = 2_000_000;
    private const int DrainMs          = 10_000;

    private static async Task<string> ReadCappedAsync(TextReader reader, CancellationToken ct)
    {
        var sb     = new StringBuilder();
        var buffer = new char[16384];
        var capped = false;
        int read;
        while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (capped) continue;
            if (sb.Length + read > MaxCapturedChars)
            {
                sb.Append(buffer, 0, MaxCapturedChars - sb.Length);
                capped = true;
            }
            else sb.Append(buffer, 0, read);
        }
        return sb.ToString();
    }

    private static async Task<string> SafeRead(Task<string> task, int drainMs = -1)
    {
        try
        {
            if (drainMs < 0) return await task.ConfigureAwait(false);
            var done = await Task.WhenAny(task, Task.Delay(drainMs)).ConfigureAwait(false);
            return done == task ? await task.ConfigureAwait(false) : string.Empty;
        }
        catch { return string.Empty; }
    }

    /// <summary>Head 60% + tail 40% truncation so long build logs keep both start and final error.</summary>
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var head = (int)(maxChars * 0.6);
        var tail = maxChars - head;
        return text[..head]
            + $"\n[…{text.Length - maxChars} chars elided…]\n"
            + text[^tail..];
    }
}
