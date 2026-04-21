using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace JavaScriptPlayground;

internal sealed class PlaygroundShellBridge
{
    private readonly object _gate = new();
    private readonly ShellSpec _shellSpec;
    private readonly string? _scriptPath;
    private string _workingDirectory;
    private string? _previousWorkingDirectory;
    private PlaygroundShellSession? _activeSession;

    public PlaygroundShellBridge(string? workingDirectory = null)
    {
        _shellSpec = ResolveShellSpec();
        _scriptPath = ResolveScriptPath();
        _workingDirectory = ResolveStartingDirectory(workingDirectory);
    }

    public string shell => _shellSpec.DisplayName;

    public string cwd
    {
        get
        {
            lock (_gate)
            {
                return _workingDirectory;
            }
        }
    }

    public bool supportsTty => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !string.IsNullOrWhiteSpace(_scriptPath);

    public PlaygroundShellResult execute(string? command) => execute(command, 15000);

    public PlaygroundShellResult execute(string? command, int timeoutMs) => execute(command, timeoutMs, 0, 0);

    public PlaygroundShellResult execute(string? command, int timeoutMs, int columns, int rows)
    {
        var trimmed = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return PlaygroundShellResult.Create(0, string.Empty, string.Empty, string.Empty, false, timeoutMs, shell, cwd);
        }

        lock (_gate)
        {
            if (TryChangeDirectory(trimmed, out var cdResult))
            {
                return cdResult;
            }

            return RunCommand(trimmed, timeoutMs, columns, rows);
        }
    }

    public PlaygroundShellSession? start(string? command, int columns, int rows)
    {
        var trimmed = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) || !supportsTty || string.IsNullOrWhiteSpace(_scriptPath))
        {
            return null;
        }

        lock (_gate)
        {
            _activeSession?.kill();

            var session = PlaygroundShellSession.Start(
                _scriptPath!,
                _shellSpec,
                trimmed,
                _workingDirectory,
                SanitizeTerminalColumns(columns),
                SanitizeTerminalRows(rows));

            _activeSession = session;
            return session;
        }
    }

    private PlaygroundShellResult RunCommand(string command, int timeoutMs, int columns, int rows)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(command, _workingDirectory, columns, rows)
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return PlaygroundShellResult.Create(
                1,
                string.Empty,
                ex.Message,
                ex.Message,
                false,
                timeoutMs,
                shell,
                _workingDirectory);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var clampedTimeout = Math.Max(timeoutMs, 1);
        var timedOut = !process.WaitForExit(clampedTimeout);
        if (timedOut)
        {
            TryKill(process);
        }

        process.WaitForExit();

        var stdout = AwaitOutput(stdoutTask);
        var stderr = AwaitOutput(stderrTask);
        var output = CombineOutput(stdout, stderr);

        if (timedOut && string.IsNullOrWhiteSpace(output))
        {
            output = $"Command timed out after {clampedTimeout} ms.";
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        return PlaygroundShellResult.Create(
            exitCode,
            stdout,
            stderr,
            output,
            timedOut,
            clampedTimeout,
            shell,
            _workingDirectory);
    }

    private bool TryChangeDirectory(string command, out PlaygroundShellResult result)
    {
        result = null!;

        if (!IsChangeDirectoryCommand(command))
        {
            return false;
        }

        var target = command.Length <= 2 ? "~" : command.Substring(2).Trim();
        var resolvedTarget = ResolveDirectoryTarget(target);
        if (!Directory.Exists(resolvedTarget))
        {
            var error = $"Directory not found: {target}";
            result = PlaygroundShellResult.Create(
                1,
                string.Empty,
                error,
                error,
                false,
                0,
                shell,
                _workingDirectory);
            return true;
        }

        _previousWorkingDirectory = _workingDirectory;
        _workingDirectory = resolvedTarget;
        var output = _workingDirectory + Environment.NewLine;
        result = PlaygroundShellResult.Create(
            0,
            output,
            string.Empty,
            output,
            false,
            0,
            shell,
            _workingDirectory);
        return true;
    }

    private string ResolveDirectoryTarget(string? target)
    {
        var candidate = (target ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(candidate) || candidate == "~")
        {
            return GetHomeDirectory();
        }

        if (candidate == "-" && !string.IsNullOrWhiteSpace(_previousWorkingDirectory))
        {
            return _previousWorkingDirectory!;
        }

        candidate = Unquote(candidate);
        if (candidate.StartsWith("~/", StringComparison.Ordinal) || candidate.StartsWith("~\\", StringComparison.Ordinal))
        {
            candidate = Path.Combine(GetHomeDirectory(), candidate.Substring(2));
        }

        return Path.GetFullPath(Path.IsPathRooted(candidate)
            ? candidate
            : Path.Combine(_workingDirectory, candidate));
    }

    private static bool IsChangeDirectoryCommand(string command)
    {
        if (!command.StartsWith("cd", StringComparison.Ordinal))
        {
            return false;
        }

        return command.Length == 2 || char.IsWhiteSpace(command[2]);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2)
        {
            var quote = value[0];
            if ((quote == '"' || quote == '\'') && value[^1] == quote)
            {
                return value.Substring(1, value.Length - 2);
            }
        }

        return value;
    }

    private ProcessStartInfo CreateStartInfo(string command, string workingDirectory, int columns, int rows)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _shellSpec.FileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in _shellSpec.ArgumentPrefix)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(command);
        ConfigureTerminalEnvironment(startInfo, columns, rows);
        return startInfo;
    }

    private static void ConfigureTerminalEnvironment(ProcessStartInfo startInfo, int columns, int rows)
    {
        var normalizedColumns = SanitizeTerminalColumns(columns);
        var normalizedRows = SanitizeTerminalRows(rows);

        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";
        startInfo.Environment["COLUMNS"] = normalizedColumns.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["LINES"] = normalizedRows.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string AwaitOutput(System.Threading.Tasks.Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string CombineOutput(string stdout, string stderr)
    {
        if (string.IsNullOrEmpty(stdout))
        {
            return stderr ?? string.Empty;
        }

        if (string.IsNullOrEmpty(stderr))
        {
            return stdout;
        }

        return stdout.EndsWith('\n') ? stdout + stderr : stdout + Environment.NewLine + stderr;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string ResolveStartingDirectory(string? workingDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory!;

        return Directory.Exists(candidate)
            ? Path.GetFullPath(candidate)
            : GetHomeDirectory();
    }

    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Environment.CurrentDirectory : home;
    }

    private static ShellSpec ResolveShellSpec()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pwsh = FindCommandOnPath("pwsh");
            if (!string.IsNullOrWhiteSpace(pwsh))
            {
                return new ShellSpec(pwsh, new[] { "-NoLogo", "-NoProfile", "-Command" }, "pwsh");
            }

            var powershell = FindCommandOnPath("powershell");
            if (!string.IsNullOrWhiteSpace(powershell))
            {
                return new ShellSpec(powershell, new[] { "-NoLogo", "-NoProfile", "-Command" }, "powershell");
            }

            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            return new ShellSpec(
                string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec,
                new[] { "/d", "/c" },
                "cmd");
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
        {
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        }

        return new ShellSpec(shell, new[] { "-lc" }, Path.GetFileName(shell));
    }

    private static string? ResolveScriptPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        var script = FindCommandOnPath("script");
        if (!string.IsNullOrWhiteSpace(script))
        {
            return script;
        }

        return File.Exists("/usr/bin/script") ? "/usr/bin/script" : null;
    }

    private static int SanitizeTerminalColumns(int columns) => Math.Clamp(columns, 40, 240);

    private static int SanitizeTerminalRows(int rows) => Math.Clamp(rows, 12, 120);

    private static string? FindCommandOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetWindowsExecutableNames(command)
            : new[] { command };

        foreach (var directory in path.Split(Path.PathSeparator).Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetWindowsExecutableNames(string command)
    {
        yield return command;
        yield return command + ".exe";
        yield return command + ".cmd";
        yield return command + ".bat";
    }

    internal sealed record ShellSpec(string FileName, string[] ArgumentPrefix, string DisplayName);
}

internal sealed class PlaygroundShellSession
{
    private readonly object _gate = new();
    private readonly Process _process;
    private readonly StringBuilder _pendingOutput = new();
    private readonly Task _stdoutReader;
    private readonly Task _stderrReader;
    private bool _killed;

    private PlaygroundShellSession(Process process)
    {
        _process = process;
        _stdoutReader = Task.Run(() => ReadStream(_process.StandardOutput.BaseStream));
        _stderrReader = Task.Run(() => ReadStream(_process.StandardError.BaseStream));
    }

    public bool isRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public int exitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : 0;
            }
            catch
            {
                return -1;
            }
        }
    }

    public bool wasKilled => _killed;

    internal static PlaygroundShellSession Start(
        string scriptPath,
        PlaygroundShellBridge.ShellSpec shellSpec,
        string command,
        string workingDirectory,
        int columns,
        int rows)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(scriptPath, shellSpec, command, workingDirectory, columns, rows)
        };

        process.Start();
        return new PlaygroundShellSession(process);
    }

    public string read()
    {
        lock (_gate)
        {
            if (_pendingOutput.Length == 0)
            {
                return string.Empty;
            }

            var text = _pendingOutput.ToString();
            _pendingOutput.Clear();
            return StripScriptEofMarker(text);
        }
    }

    public bool write(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return false;
        }

        try
        {
            if (_process.HasExited)
            {
                return false;
            }

            _process.StandardInput.Write(data);
            _process.StandardInput.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool resize(int columns, int rows)
    {
        // The portable script-backed path cannot issue TIOCSWINSZ after startup.
        // The initial rows/columns are applied before exec; report success so the
        // JavaScript side can keep one code path for future richer PTY backends.
        return columns > 0 && rows > 0;
    }

    public void kill()
    {
        _killed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    public void close() => kill();

    private async Task ReadStream(Stream stream)
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var charCount = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (charCount <= 0)
                {
                    continue;
                }

                lock (_gate)
                {
                    _pendingOutput.Append(chars, 0, charCount);
                }
            }
        }
        catch
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string scriptPath,
        PlaygroundShellBridge.ShellSpec shellSpec,
        string command,
        string workingDirectory,
        int columns,
        int rows)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = scriptPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var wrappedCommand = CreateWrappedCommand(command, columns, rows);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("/dev/null");
            startInfo.ArgumentList.Add(shellSpec.FileName);
            foreach (var argument in shellSpec.ArgumentPrefix)
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.ArgumentList.Add(wrappedCommand);
        }
        else
        {
            startInfo.ArgumentList.Add("-q");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(BuildShellCommand(shellSpec, wrappedCommand));
            startInfo.ArgumentList.Add("/dev/null");
        }

        startInfo.Environment["TERM"] = "xterm-256color";
        startInfo.Environment["COLORTERM"] = "truecolor";
        startInfo.Environment["COLUMNS"] = columns.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["LINES"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return startInfo;
    }

    private static string CreateWrappedCommand(string command, int columns, int rows)
    {
        return $"stty rows {rows} cols {columns} 2>/dev/null; export TERM=xterm-256color COLORTERM=truecolor COLUMNS={columns} LINES={rows}; exec {command}";
    }

    private static string BuildShellCommand(PlaygroundShellBridge.ShellSpec shellSpec, string command)
    {
        var builder = new StringBuilder();
        builder.Append(ShellQuote(shellSpec.FileName));
        foreach (var argument in shellSpec.ArgumentPrefix)
        {
            builder.Append(' ');
            builder.Append(ShellQuote(argument));
        }

        builder.Append(' ');
        builder.Append(ShellQuote(command));
        return builder.ToString();
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private static string StripScriptEofMarker(string value)
    {
        return value.Replace("\u0004\b\b", string.Empty, StringComparison.Ordinal);
    }
}

internal sealed class PlaygroundShellResult
{
    private PlaygroundShellResult()
    {
    }

    public int exitCode { get; init; }

    public string stdout { get; init; } = string.Empty;

    public string stderr { get; init; } = string.Empty;

    public string output { get; init; } = string.Empty;

    public bool timedOut { get; init; }

    public int timeoutMs { get; init; }

    public string shell { get; init; } = string.Empty;

    public string cwd { get; init; } = string.Empty;

    public bool success { get; init; }

    public static PlaygroundShellResult Create(
        int exitCode,
        string stdout,
        string stderr,
        string output,
        bool timedOut,
        int timeoutMs,
        string shell,
        string cwd)
    {
        return new PlaygroundShellResult
        {
            exitCode = exitCode,
            stdout = stdout ?? string.Empty,
            stderr = stderr ?? string.Empty,
            output = output ?? string.Empty,
            timedOut = timedOut,
            timeoutMs = timeoutMs,
            shell = shell ?? string.Empty,
            cwd = cwd ?? string.Empty,
            success = !timedOut && exitCode == 0
        };
    }
}
