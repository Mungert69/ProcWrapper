using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

class Program
{
    // Default for your current repo; can be overridden with --libdir
    private const string DefaultLinuxLibDir = "/home/mahadeva/code/ProcWrapper/exes";
    private const string DefaultBinary      = "openssl";
    private static readonly string[] DefaultArgs = new[] { "version", "-v" }; // or "-a" if you prefer

    static async Task<int> Main()
    {
        // Parse CLI: [--libdir <dir>] [--android-exe <path>] <binary> [args...]
        var cli = Environment.GetCommandLineArgs().Skip(1).ToList();
        string? linuxLibDir = null;
        string? androidExePath = null;

        for (int i = 0; i < cli.Count;)
        {
            if (cli[i] == "--libdir" && i + 1 < cli.Count) { linuxLibDir = cli[i + 1]; cli.RemoveAt(i); cli.RemoveAt(i); continue; }
            if (cli[i] == "--android-exe" && i + 1 < cli.Count) { androidExePath = cli[i + 1]; cli.RemoveAt(i); cli.RemoveAt(i); continue; }
            i++;
        }

        string binary;
        string[] subArgs;

        if (cli.Count == 0)
        {
            // No args passed → default to your OpenSSL demo
            binary  = DefaultBinary;
            subArgs = DefaultArgs;
            linuxLibDir ??= DefaultLinuxLibDir;
            Console.WriteLine($"[info] No args provided. Defaulting to: {binary} {string.Join(" ", subArgs)}");
        }
        else
        {
            binary  = cli[0];
            subArgs = cli.Skip(1).ToArray();

            // If user didn’t pass --libdir, keep your default for convenience
            if (OperatingSystem.IsLinux()) linuxLibDir ??= DefaultLinuxLibDir;
        }

        // Build platform-specific command
        (string exePath, string[] args) = BuildPlatformCommand(
            binaryPathOrName: binary,
            subCommand: subArgs,
            linuxLibDir: linuxLibDir,
            androidExePath: androidExePath
        );

        var ps = new NativeProc.ProcessStream { Debug = true };
        ps.OnStdoutLine += l => Console.WriteLine("[OUT] " + l);
        ps.OnStderrLine += l => Console.WriteLine("[ERR] " + l);
        ps.OnExited     += ec => Console.WriteLine($">>> Exited with code {ec}");

        if (!ps.Start(exePath, args))
        {
            Console.WriteLine("Failed to start process");
            PrintUsage();
            return 1;
        }

        int exit = await ps.WaitForExitAsync();
        Console.WriteLine("WaitForExitAsync -> " + exit);
        return exit == -1 ? 1 : exit;
    }

    /// <summary>
    /// Build a platform-appropriate command:
    /// - Linux desktop: execute the glibc loader with --library-path <linuxLibDir> <binary> <args...>
    /// - Android: execute the binary directly (typically copied to Context.FilesDir and chmod +x)
    /// - Windows/macOS: execute directly (you manage PATH/DYLD outside if needed)
    /// </summary>
    private static (string exePath, string[] args) BuildPlatformCommand(
        string binaryPathOrName,
        string[] subCommand,
        string? linuxLibDir = null,
        string? androidExePath = null)
    {
        if (OperatingSystem.IsAndroid())
        {
            string exe = !string.IsNullOrWhiteSpace(androidExePath)
                ? androidExePath
                : binaryPathOrName; // assume caller passes full path (e.g., FilesDir/openssl)
            return (exe, subCommand);
        }

        if (OperatingSystem.IsLinux())
        {
            // If user passed a full path, use it; otherwise assume it lives under linuxLibDir
            string exeFull = Path.IsPathRooted(binaryPathOrName)
                ? binaryPathOrName
                : Path.Combine(linuxLibDir ?? ".", binaryPathOrName);

            // Choose the correct ELF loader for current arch
            string loader = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "/lib64/ld-linux-x86-64.so.2",
                Architecture.X86   => "/lib/ld-linux.so.2",
                Architecture.Arm64 => "/lib/ld-linux-aarch64.so.1",
                Architecture.Arm   => "/lib/ld-linux-armhf.so.3",
                _ => "/lib64/ld-linux-x86-64.so.2"
            };

            string libDir = linuxLibDir ?? Path.GetDirectoryName(exeFull) ?? ".";
            string[] args = new[] { "--library-path", libDir, exeFull }
                            .Concat(subCommand)
                            .ToArray();

            return (loader, args);
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            // Run directly; caller manages PATH / DYLD_LIBRARY_PATH / @rpath outside
            string exeFull = Path.IsPathRooted(binaryPathOrName)
                ? binaryPathOrName
                : Path.Combine(Directory.GetCurrentDirectory(), binaryPathOrName);
            return (exeFull, subCommand);
        }

        throw new PlatformNotSupportedException("Unknown platform.");
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [--libdir <dir>] [--android-exe <path>] <binary> [args...]");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- --libdir /home/mahadeva/code/ProcWrapper/exes openssl version -v");
        Console.WriteLine("  dotnet run -- ls -l /etc");
        Console.WriteLine("  dotnet run -- --android-exe /data/data/<pkg>/files/openssl openssl version -a   # Android");
        Console.WriteLine();
    }
}

