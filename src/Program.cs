using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmSessionGuard;

internal static class Program
{
    private const string AppName = "VM Session Guard";

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine($"{AppName} is supported only on Windows.");
            return 2;
        }

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            string configPath = GetConfigPath(args);
            GuardSettings settings = GuardSettings.Load(configPath);
            using var logger = new FileLogger(settings.ResolveLogPath());
            using var stop = new ManualResetEventSlim(false);

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.Set();
            };

            Console.WriteLine($"{AppName} 1.0.0");
            Console.WriteLine($"Config: {Path.GetFullPath(configPath)}");
            Console.WriteLine($"Log:    {settings.ResolveLogPath()}");
            Console.WriteLine("Press Ctrl+C to stop.");
            logger.Info($"Started. threshold={settings.CpuThresholdPercent:F1}%, duration={settings.LowCpuDurationSeconds}s, interval={settings.CheckIntervalSeconds}s, fallback={settings.Fallback}");

            var sampler = new CpuSampler();
            DateTimeOffset? lowSince = null;

            while (!stop.Wait(TimeSpan.FromSeconds(settings.CheckIntervalSeconds)))
            {
                if (!sampler.TrySample(out double cpuPercent))
                {
                    logger.Warn($"Could not sample total CPU usage. Win32 error={Marshal.GetLastWin32Error()}.");
                    lowSince = null;
                    continue;
                }

                // The first successful GetSystemTimes call only establishes a baseline.
                if (double.IsNaN(cpuPercent))
                    continue;

                DateTimeOffset now = DateTimeOffset.Now;
                if (cpuPercent < settings.CpuThresholdPercent)
                {
                    lowSince ??= now;
                    TimeSpan lowFor = now - lowSince.Value;
                    if (lowFor.TotalSeconds >= settings.LowCpuDurationSeconds)
                    {
                        PerformKeepAlive(settings, logger, cpuPercent, lowFor);
                        lowSince = now;
                    }
                }
                else
                {
                    if (lowSince is not null && settings.LogCpuSamples)
                        logger.Info($"CPU recovered to {cpuPercent:F1}%.");
                    lowSince = null;
                }

                if (settings.LogCpuSamples)
                    logger.Info($"CPU={cpuPercent:F1}%, lowFor={(lowSince is null ? 0 : (now - lowSince.Value).TotalSeconds):F0}s");
            }

            logger.Info("Stopped by user.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }

    private static void PerformKeepAlive(GuardSettings settings, FileLogger logger, double cpuPercent, TimeSpan lowFor)
    {
        bool executionStateOk = NativeMethods.SetThreadExecutionState(
            NativeMethods.ExecutionState.SystemRequired |
            NativeMethods.ExecutionState.DisplayRequired) != 0;
        int executionStateError = executionStateOk ? 0 : Marshal.GetLastWin32Error();

        bool fallbackOk = settings.Fallback switch
        {
            FallbackMode.None => true,
            FallbackMode.ScrollLock => NativeMethods.ToggleScrollLockTwice(),
            FallbackMode.MousePixel => NativeMethods.NudgeMouseOnePixel(),
            _ => false
        };
        int fallbackError = fallbackOk ? 0 : Marshal.GetLastWin32Error();

        string executionStateResult = executionStateOk ? "ok" : $"failed (Win32 error={executionStateError})";
        string fallbackResult = settings.Fallback == FallbackMode.None
            ? "disabled"
            : fallbackOk ? "ok" : $"failed (Win32 error={fallbackError})";
        string message = $"Keep-alive attempted after CPU stayed below threshold for {lowFor.TotalSeconds:F0}s (CPU={cpuPercent:F1}%). SetThreadExecutionState={executionStateResult}, fallback={settings.Fallback}:{fallbackResult}.";
        if (executionStateOk && fallbackOk)
            logger.Info(message);
        else
            logger.Error(message);
    }

    private static string GetConfigPath(string[] args)
    {
        if (args.Length == 0)
            return Path.Combine(AppContext.BaseDirectory, "VmSessionGuard.json");
        if (args.Length == 1 && args[0].StartsWith("--config=", StringComparison.Ordinal))
            return args[0]["--config=".Length..];
        if (args.Length == 2 && args[0] == "--config")
            return args[1];
        if (args.Length == 1 && args[0] == "--config")
            throw new ArgumentException("--config requires a file path.");
        throw new ArgumentException($"Invalid arguments: {string.Join(' ', args)}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("VM Session Guard");
        Console.WriteLine("Usage: VmSessionGuard.exe [--config <path>]");
        Console.WriteLine("Stop:  press Ctrl+C or close the console window");
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<FallbackMode>))]
internal enum FallbackMode
{
    None,
    ScrollLock,
    MousePixel
}

internal sealed class GuardSettings
{
    public double CpuThresholdPercent { get; init; } = 10.0;
    public int LowCpuDurationSeconds { get; init; } = 60;
    public int CheckIntervalSeconds { get; init; } = 5;
    public FallbackMode Fallback { get; init; } = FallbackMode.None;
    public string LogFile { get; init; } = "logs/VmSessionGuard.log";
    public bool LogCpuSamples { get; init; } = false;

    public static GuardSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Configuration file was not found.", path);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter<FallbackMode>());

        GuardSettings settings = JsonSerializer.Deserialize<GuardSettings>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("Configuration is empty.");
        settings.Validate();
        return settings;
    }

    public string ResolveLogPath()
    {
        string path = Environment.ExpandEnvironmentVariables(LogFile);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
    }

    private void Validate()
    {
        if (!double.IsFinite(CpuThresholdPercent) || CpuThresholdPercent is < 0 or > 100)
            throw new InvalidDataException("CpuThresholdPercent must be between 0 and 100.");
        if (LowCpuDurationSeconds is < 1 or > 86400)
            throw new InvalidDataException("LowCpuDurationSeconds must be between 1 and 86400.");
        if (CheckIntervalSeconds is < 1 or > 3600)
            throw new InvalidDataException("CheckIntervalSeconds must be between 1 and 3600.");
        if (string.IsNullOrWhiteSpace(LogFile))
            throw new InvalidDataException("LogFile must not be empty.");
        if (!Enum.IsDefined(Fallback))
            throw new InvalidDataException("Fallback must be None, ScrollLock, or MousePixel.");
    }
}

internal sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _writer;

    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    private void Write(string level, string message) => _writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
    public void Dispose() => _writer.Dispose();
}

internal sealed class CpuSampler
{
    private ulong _previousIdle;
    private ulong _previousTotal;
    private bool _initialized;

    public bool TrySample(out double cpuPercent)
    {
        cpuPercent = 0;
        if (!NativeMethods.GetSystemTimes(out NativeMethods.FileTime idle, out NativeMethods.FileTime kernel, out NativeMethods.FileTime user))
            return false;

        ulong idleTicks = idle.ToUInt64();
        ulong totalTicks = kernel.ToUInt64() + user.ToUInt64();
        if (!_initialized)
        {
            _previousIdle = idleTicks;
            _previousTotal = totalTicks;
            _initialized = true;
            cpuPercent = double.NaN;
            return true;
        }

        ulong idleDelta = idleTicks - _previousIdle;
        ulong totalDelta = totalTicks - _previousTotal;
        _previousIdle = idleTicks;
        _previousTotal = totalTicks;
        if (totalDelta == 0)
            return false;

        cpuPercent = Math.Clamp(100.0 * (totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta, 0.0, 100.0);
        return true;
    }
}

internal static class NativeMethods
{
    [Flags]
    internal enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint KeyUp = 0x0002;
    private const ushort VkScroll = 0x91;

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

    internal static bool ToggleScrollLockTwice()
    {
        Input[] inputs =
        [
            Key(VkScroll, 0), Key(VkScroll, KeyUp),
            Key(VkScroll, 0), Key(VkScroll, KeyUp)
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    internal static bool NudgeMouseOnePixel()
    {
        Input[] inputs = [Mouse(1, 0), Mouse(-1, 0)];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input Key(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } }
    };

    private static Input Mouse(int dx, int dy) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = MouseMove } }
    };
}
