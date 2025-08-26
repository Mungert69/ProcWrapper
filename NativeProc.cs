using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class NativeProc
{
    // ========= P/Invoke =========
    [DllImport("procwrapper", EntryPoint = "start_process", CallingConvention = CallingConvention.Cdecl)]
    private static extern int start_process([MarshalAs(UnmanagedType.LPStr)] string path, IntPtr argv);

    [DllImport("procwrapper", EntryPoint = "read_stdout", CallingConvention = CallingConvention.Cdecl)]
    private static extern int read_stdout(int handle, IntPtr buffer, int buflen);

    [DllImport("procwrapper", EntryPoint = "read_stderr", CallingConvention = CallingConvention.Cdecl)]
    private static extern int read_stderr(int handle, IntPtr buffer, int buflen);

    [DllImport("procwrapper", EntryPoint = "is_running", CallingConvention = CallingConvention.Cdecl)]
    private static extern int is_running(int handle);

    [DllImport("procwrapper", EntryPoint = "get_exit_code", CallingConvention = CallingConvention.Cdecl)]
    private static extern int get_exit_code(int handle);

    [DllImport("procwrapper", EntryPoint = "stop_process", CallingConvention = CallingConvention.Cdecl)]
    private static extern int stop_process(int handle);

    // ========= argv helpers =========
    private static IntPtr BuildArgv(string[] parts) {
        IntPtr[] ptrs = new IntPtr[parts.Length + 1];
        for (int i = 0; i < parts.Length; i++) {
            byte[] bytes = Encoding.UTF8.GetBytes(parts[i] + "\0");
            IntPtr mem = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            ptrs[i] = mem;
        }
        ptrs[parts.Length] = IntPtr.Zero;
        IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
        for (int i = 0; i < ptrs.Length; i++)
            Marshal.WriteIntPtr(argv, i * IntPtr.Size, ptrs[i]);
        return argv;
    }

    private static void FreeArgv(IntPtr argv, int count) {
        for (int i = 0; i < count; i++) {
            IntPtr p = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
        Marshal.FreeHGlobal(argv);
    }

  public class ProcessStream : IDisposable
{
    public event Action<string>? OnStdoutLine;
    public event Action<string>? OnStderrLine;
    public event Action<int>?    OnExited;

    public bool Debug { get; set; } = true;

    private int _handle = -1;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    // NEW: signal when we've drained both stdout/stderr
    private TaskCompletionSource<bool>? _drainedTcs;

    private const int BUF_SIZE = 4096;

    public bool Start(string exePath, string[] args)
    {
        string[] argv = new string[args.Length + 1];
        argv[0] = exePath;
        Array.Copy(args, 0, argv, 1, args.Length);

        if (Debug) Console.WriteLine($"[proc] start: {exePath} {string.Join(" ", args)}");

        IntPtr nativeArgv = BuildArgv(argv);
        _handle = start_process(exePath, nativeArgv);
        FreeArgv(nativeArgv, argv.Length);

        if (_handle < 0)
        {
            if (Debug) Console.WriteLine("[proc] start failed (handle < 0)");
            return false;
        }

        if (Debug) Console.WriteLine($"[proc] started handle={_handle}");

        _cts = new CancellationTokenSource();
        _drainedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _readerTask = Task.Run(() => ReaderLoop(_cts.Token));
        return true;
    }

    // NEW: await this after WaitForExitAsync to ensure tail has flushed
    public Task WaitForDrainAsync() => _drainedTcs?.Task ?? Task.CompletedTask;

    private async Task ReaderLoop(CancellationToken ct)
    {
        var stdoutBuf = Marshal.AllocHGlobal(BUF_SIZE);
        var stderrBuf = Marshal.AllocHGlobal(BUF_SIZE);
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        bool sawExit = false;
        bool outEof = false, errEof = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int nOut = read_stdout(_handle, stdoutBuf, BUF_SIZE - 1);
                if (nOut > 0)
                {
                    if (Debug) Console.WriteLine($"[proc] read stdout {nOut} bytes");
                    byte[] tmp = new byte[nOut];
                    Marshal.Copy(stdoutBuf, tmp, 0, nOut);
                    sbOut.Append(Encoding.UTF8.GetString(tmp));
                    EmitLines(sbOut, OnStdoutLine);
                }
                else if (nOut == 0 && GetExitCode() >= 0)
                {
                    // EOF on stdout (after exit observed)
                    outEof = true;
                }

                int nErr = read_stderr(_handle, stderrBuf, BUF_SIZE - 1);
                if (nErr > 0)
                {
                    if (Debug) Console.WriteLine($"[proc] read stderr {nErr} bytes");
                    byte[] tmp = new byte[nErr];
                    Marshal.Copy(stderrBuf, tmp, 0, nErr);
                    sbErr.Append(Encoding.UTF8.GetString(tmp));
                    EmitLines(sbErr, OnStderrLine);
                }
                else if (nErr == 0 && GetExitCode() >= 0)
                {
                    // EOF on stderr (after exit observed)
                    errEof = true;
                }

                int exitStatus = GetExitCode();
                if (exitStatus == -1)
                {
                    if (Debug) Console.WriteLine("[proc] GetExitCode returned -1 (error/invalid)");
                    OnExited?.Invoke(-1);
                    break;
                }

                if (exitStatus >= 0)
                {
                    if (!sawExit)
                    {
                        sawExit = true;
                        if (Debug) Console.WriteLine("[proc] exit observed, draining...");
                    }

                    // Keep looping until both streams report EOF
                    if (outEof && errEof)
                    {
                        // flush any trailing partial lines
                        if (sbOut.Length > 0) OnStdoutLine?.Invoke(sbOut.ToString());
                        if (sbErr.Length > 0) OnStderrLine?.Invoke(sbErr.ToString());

                        if (Debug) Console.WriteLine($"[proc] exited with code {exitStatus}");
                        OnExited?.Invoke(exitStatus);
                        break;
                    }
                }

                await Task.Delay(20, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (Debug) Console.WriteLine("[proc] reader cancelled");
        }
        finally
        {
            Marshal.FreeHGlobal(stdoutBuf);
            Marshal.FreeHGlobal(stderrBuf);
            _drainedTcs?.TrySetResult(true);
        }
    }

    private static void EmitLines(StringBuilder sb, Action<string>? callback)
    {
        if (callback == null) return;
        string all = sb.ToString();
        int start = 0, idx;
        while ((idx = all.IndexOf('\n', start)) >= 0)
        {
            string line = all.Substring(start, idx - start).TrimEnd('\r');
            callback(line);
            start = idx + 1;
        }
        if (start > 0)
        {
            string rem = all.Substring(start);
            sb.Clear();
            sb.Append(rem);
        }
    }

    public int GetExitCode()
    {
        if (_handle < 0) return -1;
        try { return get_exit_code(_handle); }
        catch { return -1; }
    }

    public Task<int> WaitForExitAsync(int pollMs = 50, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCreationSource<int>();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task.Run(async () =>
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    int ec = GetExitCode();
                    if (ec >= 0 || ec == -1) { tcs.TrySetResult(ec); return; }
                    await Task.Delay(pollMs, linked.Token).ConfigureAwait(false);
                }
                tcs.TrySetCanceled(linked.Token);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(linked.Token);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, linked.Token);
        return tcs.Task;
    }

    public void Stop()
    {
        if (Debug) Console.WriteLine("[proc] stop requested");
        if (_handle >= 0)
        {
            try { stop_process(_handle); } catch { /* ignore */ }
        }
        _cts?.Cancel();
        try { _readerTask?.Wait(500); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    // tiny helper so we can use TaskCompletionSource in netstandard-friendly way
    private sealed class TaskCreationSource<T> : TaskCompletionSource<T>
    {
        public TaskCreationSource() : base(TaskCreationOptions.RunContinuationsAsynchronously) { }
    }
}

}

