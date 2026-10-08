using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VanityAgent.Tools;

/// <summary>One agent session's shell: created on first use, closed when the session ends.
/// Stored per async flow so concurrent sessions never share a shell.</summary>
public sealed class ShellHandle
{
    private readonly object _gate = new();
    private PersistentShell? _shell;

    public PersistentShell GetOrCreate(string bashPath, string cwd)
    {
        lock (_gate)
        {
            if (_shell == null || !_shell.IsAlive)
                _shell = new PersistentShell(bashPath, cwd);
            return _shell;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            try { _shell?.Close(); } catch { }
            _shell = null;
        }
    }
}

public sealed record ShellExecResult(string Stdout, string Stderr, int Code, bool Interrupted, bool TimedOut = false);

/// <summary>One long-lived bash process per session.
/// Commands run via eval with stdout/stderr redirected to temp files; completion detected by polling the status file.
/// Shell state (cwd, variables, functions) persists between commands.</summary>
public sealed class PersistentShell
{
    private const int DefaultTimeoutMs = 30 * 60 * 1000;
    private const int SigtermCode      = 143;

    private static readonly ConcurrentDictionary<int, PersistentShell> Live = new();
    private static int _exitHooked;

    private readonly Process      _shell;
    private readonly string       _statusFile, _stdoutFile, _stderrFile, _cwdFile;
    private readonly SemaphoreSlim _queue = new(1, 1);
    private string   _cwd;
    private volatile bool _isAlive = true;
    private volatile bool _commandInterrupted;

    public bool IsAlive => _isAlive && !_shell.HasExited;

    public PersistentShell(string bashPath, string cwd)
    {
        _cwd  = cwd;
        string id     = Guid.NewGuid().ToString("N")[..8];
        string prefix = Path.Combine(Path.GetTempPath(), "vanityagent-" + id);
        _statusFile   = prefix + "-status";
        _stdoutFile   = prefix + "-stdout";
        _stderrFile   = prefix + "-stderr";
        _cwdFile      = prefix + "-cwd";
        foreach (var f in new[] { _statusFile, _stdoutFile, _stderrFile })
            File.WriteAllText(f, "");
        File.WriteAllText(_cwdFile, cwd);

        var psi = new ProcessStartInfo
        {
            FileName               = bashPath,
            WorkingDirectory       = cwd,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        psi.EnvironmentVariables["GIT_EDITOR"]       = "true";
        psi.EnvironmentVariables["CHERE_INVOKING"]   = "1";
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["PYTHONUTF8"]       = "1";

        _shell = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _shell.Exited += (_, _) =>
        {
            _isAlive = false;
            Live.TryRemove(_shell.Id, out _);
            foreach (var f in new[] { _statusFile, _stdoutFile, _stderrFile, _cwdFile })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        };
        if (!_shell.Start()) throw new InvalidOperationException("Failed to start " + bashPath);
        VanityAgent.Infra.ChildJob.Own(_shell);   // the shell and everything it spawns die with the host
        _shell.BeginOutputReadLine();
        _shell.BeginErrorReadLine();
        Live[_shell.Id] = this;
        if (Interlocked.Exchange(ref _exitHooked, 1) == 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                foreach (var s in Live.Values.ToArray())
                    try { s.Close(); } catch { }
            };

        string rc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc");
        if (File.Exists(rc)) SendToShell("source " + Quote(BashPath(rc)));
    }

    public string Pwd()
    {
        try
        {
            string next = File.ReadAllText(_cwdFile).Trim();
            if (next.Length > 0) _cwd = Path.GetFullPath(next);
        }
        catch { }
        return _cwd;
    }

    public async Task SetCwdAsync(string cwd, CancellationToken ct)
    {
        string resolved = Path.GetFullPath(cwd);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Path \"{resolved}\" does not exist");
        await ExecAsync("cd " + Quote(BashPath(resolved)), null, ct).ConfigureAwait(false);
    }

    public async Task<ShellExecResult> ExecAsync(string command, int? timeoutMs, CancellationToken ct)
    {
        await _queue.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var reg = ct.Register(KillChildren);
            return await ExecCoreAsync(command, timeoutMs, ct).ConfigureAwait(false);
        }
        finally { _queue.Release(); }
    }

    private async Task<ShellExecResult> ExecCoreAsync(string command, int? timeoutMs, CancellationToken ct)
    {
        // bash -n: syntax check without executing
        try
        {
            var check = await ProcessRunner.RunAsync(
                _shell.StartInfo.FileName, ["-n", "-c", command], _cwd, 1000, ct).ConfigureAwait(false);
            if (check.ExitCode != 0 && !check.TimedOut)
                return new ShellExecResult("", check.StdErr, 128, false);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        int commandTimeout  = timeoutMs ?? DefaultTimeoutMs;
        _commandInterrupted = false;
        await TruncateWithRetryAsync(_stdoutFile, ct).ConfigureAwait(false);
        await TruncateWithRetryAsync(_stderrFile, ct).ConfigureAwait(false);
        await TruncateWithRetryAsync(_statusFile, ct).ConfigureAwait(false);

        var parts = new[]
        {
            "eval " + Quote(command) + " < /dev/null > " + Quote(BashPath(_stdoutFile)) + " 2> " + Quote(BashPath(_stderrFile)),
            "EXEC_EXIT_CODE=$?",
            "pwd -W > " + Quote(BashPath(_cwdFile)),
            "echo $EXEC_EXIT_CODE > " + Quote(BashPath(_statusFile)),
        };
        SendToShell(string.Join("\n", parts));

        var started = Stopwatch.StartNew();
        while (true)
        {
            long statusSize = 0;
            try { if (File.Exists(_statusFile)) statusSize = new FileInfo(_statusFile).Length; } catch { }

            bool timedOut = started.ElapsedMilliseconds > commandTimeout;
            if (statusSize > 0 || timedOut || _commandInterrupted || !IsAlive)
            {
                string stdout = SafeRead(_stdoutFile);
                string stderr = SafeRead(_stderrFile);
                int code;
                if (statusSize > 0)
                    int.TryParse(SafeRead(_statusFile).Trim(), out code);
                else
                {
                    KillChildren();
                    code = SigtermCode;
                    if (!_commandInterrupted || timedOut)
                        stderr += (stderr.Length > 0 ? "\n" : "") + TimedOutNote(commandTimeout);
                }
                // A command stopped at its limit is not one the operator stopped. KillChildren raises the interrupted
                // flag for both, so a timeout used to come back marked "[aborted]", with no word on what the limit was
                // or that the call can ask for a longer one: in one project build 21 commands ran into the 120 s
                // default, none ever passed timeout_ms, and seven were followed by the same kind of command again.
                return new ShellExecResult(stdout, stderr, code, _commandInterrupted && !timedOut, timedOut);
            }
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The longest one command may ask for.</summary>
    public const int MaxTimeoutMs = 600_000;

    /// <summary>What a command stopped at its time limit is told: the limit it hit, that its output so far is kept,
    /// and the two ways on - a longer limit for this call, or less work in one command.</summary>
    internal static string TimedOutNote(int limitMs) =>
        $"Command stopped: it was still running at this call's {limitMs / 1000} s limit. What it printed until then is above. " +
        (limitMs < MaxTimeoutMs
            ? $"A command that needs longer takes timeout_ms, up to {MaxTimeoutMs}; otherwise run less in one command."
            : "That is the longest one command may run: run less in one command.");

    private static string SafeRead(string file)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (!File.Exists(file)) return "";
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 4) { Thread.Sleep(30); }
            catch { return ""; }
        }
        return "";
    }

    private static async Task TruncateWithRetryAsync(string file, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                return;
            }
            catch (IOException) when (i < 9) { await Task.Delay(30, ct).ConfigureAwait(false); }
            catch { return; }
        }
    }

    private void SendToShell(string command)
    {
        try
        {
            _shell.StandardInput.Write(command + "\n");
            _shell.StandardInput.Flush();
        }
        catch (Exception e)
        {
            _isAlive = false;
            throw new InvalidOperationException("Error in SendToShell: " + e.Message, e);
        }
    }

    public void KillChildren()
    {
        try
        {
            foreach (int pid in ChildProcessIds(_shell.Id))
                try { using var p = Process.GetProcessById(pid); if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
        catch { }
        finally { _commandInterrupted = true; }
    }

    public void Close()
    {
        _isAlive = false;
        try { _shell.StandardInput.Close(); } catch { }
        try { if (!_shell.HasExited) _shell.Kill(entireProcessTree: true); } catch { }
        Live.TryRemove(_shell.Id, out _);
    }

    public static string Quote(string s)    => "'" + (s ?? "").Replace("'", "'\\''") + "'";
    public static string BashPath(string p) => (p ?? "").Replace('\\', '/');

    // ── Toolhelp32 process-tree enumeration ──────────────────────────────────────
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint   dwSize; public uint cntUsage; public uint th32ProcessID;
        public IntPtr th32DefaultHeapID; public uint th32ModuleID; public uint cntThreads;
        public uint   th32ParentProcessID; public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]  private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap,  ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll", SetLastError = true)]  private static extern bool CloseHandle(IntPtr handle);

    private static List<int> ChildProcessIds(int parentPid)
    {
        var children = new List<int>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return children;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
            if (!Process32FirstW(snap, ref entry)) return children;
            do { if (entry.th32ParentProcessID == (uint)parentPid) children.Add((int)entry.th32ProcessID); }
            while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return children;
    }
}
