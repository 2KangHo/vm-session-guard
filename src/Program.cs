using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmSessionGuard;

internal static class Program
{
    private const string AppName = "VM Session Guard";
    private const string AppVersion = "1.4.1";

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
            IReadOnlyList<FallbackMode> fallbackModes = settings.GetFallbackModes();
            DateTimeOffset startedAt = DateTimeOffset.Now;
            string logPath = settings.ResolveLogPath(startedAt);
            using var logger = new FileLogger(logPath);
            using var stop = new ManualResetEventSlim(false);

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop.Set();
            };

            Console.WriteLine($"{AppName} {AppVersion}");
            Console.WriteLine($"Config: {Path.GetFullPath(configPath)}");
            Console.WriteLine($"Log:    {logPath}");
            Console.WriteLine("Press Ctrl+C to stop.");
            logger.Info($"Started. threshold={settings.CpuThresholdPercent:F1}%, duration={settings.LowCpuDurationSeconds}s, interval={settings.CheckIntervalSeconds}s, fallbacks={string.Join(",", fallbackModes)}");
            if (fallbackModes.Contains(FallbackMode.None))
                logger.Warn("Fallback=None only refreshes the local Windows execution state; VM/VDI server-side idle policies may still classify this session as idle. Use an approved input fallback such as MousePixel or ScrollLock.");

            var sampler = new CpuSampler();
            using CpuFloorController? cpuFloor = fallbackModes.Contains(FallbackMode.CpuFloor)
                ? new CpuFloorController(settings.CpuThresholdPercent, logger)
                : null;
            DateTimeOffset? lowSince = null;
            DateTimeOffset? lastKeepAliveAt = null;
            if (cpuFloor is not null && settings.StartCpuFloorImmediately)
            {
                PerformKeepAlive(fallbackModes, logger, cpuFloor, double.NaN, TimeSpan.Zero);
                lastKeepAliveAt = startedAt;
            }

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

                cpuFloor?.Update(cpuPercent);
                DateTimeOffset now = DateTimeOffset.Now;
                if (cpuPercent < settings.CpuThresholdPercent)
                {
                    lowSince ??= now;
                    TimeSpan lowFor = now - lowSince.Value;
                    if (lowFor.TotalSeconds >= settings.LowCpuDurationSeconds)
                    {
                        PerformKeepAlive(fallbackModes, logger, cpuFloor, cpuPercent, lowFor);
                        lowSince = now;
                        lastKeepAliveAt = now;
                    }
                }
                else
                {
                    if (lowSince is not null && settings.LogCpuSamples)
                        logger.Info($"CPU recovered to {cpuPercent:F1}%.");
                    lowSince = null;

                    if (cpuFloor?.IsActive == true &&
                        lastKeepAliveAt is not null &&
                        (now - lastKeepAliveAt.Value).TotalSeconds >= settings.LowCpuDurationSeconds)
                    {
                        PerformKeepAlive(
                            fallbackModes,
                            logger,
                            cpuFloor,
                            cpuPercent,
                            TimeSpan.FromSeconds(settings.LowCpuDurationSeconds));
                        lastKeepAliveAt = now;
                    }
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

    private static void PerformKeepAlive(
        IReadOnlyList<FallbackMode> fallbackModes,
        FileLogger logger,
        CpuFloorController? cpuFloor,
        double cpuPercent,
        TimeSpan lowFor)
    {
        bool executionStateOk = NativeMethods.SetThreadExecutionState(
            NativeMethods.ExecutionState.SystemRequired |
            NativeMethods.ExecutionState.DisplayRequired) != 0;
        int executionStateError = executionStateOk ? 0 : Marshal.GetLastWin32Error();
        bool hasInputFallback = fallbackModes.Any(mode => mode is FallbackMode.ScrollLock or FallbackMode.MousePixel);
        int inputBeforeError = 0;
        TimeSpan? localInputIdleBefore = hasInputFallback
            ? NativeMethods.TryGetLastInputIdle(out inputBeforeError)
            : null;
        var fallbackResults = new List<string>(fallbackModes.Count);
        bool fallbackOk = true;

        foreach (FallbackMode fallback in fallbackModes)
        {
            bool result = fallback switch
            {
                FallbackMode.None => true,
                FallbackMode.ScrollLock => NativeMethods.ToggleScrollLockTwice(),
                FallbackMode.MousePixel => NativeMethods.NudgeMouseOnePixel(),
                FallbackMode.CpuFloor => cpuFloor?.Activate() == true,
                _ => false
            };

            if (result)
            {
                fallbackResults.Add($"{fallback}:ok");
                continue;
            }

            fallbackOk = false;
            fallbackResults.Add(fallback == FallbackMode.CpuFloor
                ? $"{fallback}:failed (see previous log entry)"
                : $"{fallback}:failed (Win32 error={Marshal.GetLastWin32Error()})");
        }

        if (hasInputFallback)
            Thread.Sleep(50);
        int inputAfterError = 0;
        TimeSpan? localInputIdleAfter = hasInputFallback
            ? NativeMethods.TryGetLastInputIdle(out inputAfterError)
            : null;

        string executionStateResult = executionStateOk ? "ok" : $"failed (Win32 error={executionStateError})";
        string fallbackResult = string.Join(",", fallbackResults);
        string localInputResult = !hasInputFallback
            ? "not-requested"
            : localInputIdleBefore.HasValue && localInputIdleAfter.HasValue
            ? $"{localInputIdleBefore.Value.TotalSeconds:F1}s->{localInputIdleAfter.Value.TotalSeconds:F1}s"
            : $"unavailable (Win32 error={(localInputIdleBefore.HasValue ? inputAfterError : inputBeforeError)})";
        string measuredCpu = double.IsNaN(cpuPercent) ? "unavailable" : $"{cpuPercent:F1}%";
        string message = $"Keep-alive attempted after CPU stayed below threshold for {lowFor.TotalSeconds:F0}s (CPU={measuredCpu}). SetThreadExecutionState={executionStateResult}, fallbacks={fallbackResult}, localInputIdle={localInputResult}.";
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
    MousePixel,
    CpuFloor
}

internal sealed class GuardSettings
{
    public double CpuThresholdPercent { get; init; } = 12.5;
    public int LowCpuDurationSeconds { get; init; } = 60;
    public int CheckIntervalSeconds { get; init; } = 5;
    public bool StartCpuFloorImmediately { get; init; } = true;
    public FallbackMode? Fallback { get; init; }
    public FallbackMode[]? Fallbacks { get; init; }
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

    public IReadOnlyList<FallbackMode> GetFallbackModes()
    {
        if (Fallbacks is not null)
            return Fallbacks;
        return Fallback.HasValue ? [Fallback.Value] : [FallbackMode.None];
    }

    public string ResolveLogPath(DateTimeOffset startedAt)
    {
        string path = Environment.ExpandEnvironmentVariables(LogFile);
        string absolutePath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));
        string? directory = Path.GetDirectoryName(absolutePath);
        string fileName = Path.GetFileNameWithoutExtension(absolutePath);
        string extension = Path.GetExtension(absolutePath);
        string timestamp = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return Path.Combine(directory ?? AppContext.BaseDirectory, $"{fileName}-{timestamp}-p{Environment.ProcessId}{extension}");
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
        if (Fallback.HasValue && Fallbacks is not null)
            throw new InvalidDataException("Specify either Fallback or Fallbacks, not both.");

        IReadOnlyList<FallbackMode> fallbackModes = GetFallbackModes();
        if (fallbackModes.Count == 0)
            throw new InvalidDataException("Fallbacks must contain at least one mode.");
        if (fallbackModes.Any(mode => !Enum.IsDefined(mode)))
            throw new InvalidDataException("Fallbacks must contain only None, ScrollLock, MousePixel, or CpuFloor.");
        if (fallbackModes.Count > 1 && fallbackModes.Contains(FallbackMode.None))
            throw new InvalidDataException("Fallbacks cannot combine None with another mode.");
        if (fallbackModes.Contains(FallbackMode.CpuFloor) && CpuThresholdPercent is < 1 or > 25)
            throw new InvalidDataException("CpuThresholdPercent must be between 1 and 25 when Fallbacks includes CpuFloor.");
    }
}

internal sealed class CpuFloorController : IDisposable
{
    private const int MaxWorkers = 32;
    private const double MaxWorkerDuty = 0.50;
    private const int WindowMilliseconds = 100;
    private readonly double _targetPercent;
    private readonly FileLogger _logger;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread[] _workers;
    private readonly double _minimumDuty;
    private double _desiredDuty;
    private int _active;
    private DateTimeOffset _nextStatusLog = DateTimeOffset.MinValue;

    public CpuFloorController(double targetPercent, FileLogger logger)
    {
        _targetPercent = targetPercent;
        _logger = logger;
        int workerCount = Math.Min(Environment.ProcessorCount, MaxWorkers);
        _minimumDuty = Math.Clamp(
            (_targetPercent / 100.0) * Environment.ProcessorCount / workerCount,
            0.0,
            MaxWorkerDuty);
        _workers = Enumerable.Range(1, workerCount).Select(index => new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = $"VmSessionGuard.CpuFloor.{index}",
            Priority = ThreadPriority.Normal
        }).ToArray();
    }

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public bool Activate()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return true;

        try
        {
            Volatile.Write(ref _desiredDuty, _minimumDuty);
            foreach (Thread worker in _workers)
                worker.Start();

            double maximumSystemContribution = 100.0 * _workers.Length * MaxWorkerDuty / Environment.ProcessorCount;
            _logger.Warn($"CPU floor activated with {_workers.Length} normal-priority workers. target={_targetPercent:F1}%, minimum worker duty={_minimumDuty * 100:F1}%, safety ceiling contribution={maximumSystemContribution:F1}%.");
            if (maximumSystemContribution < _targetPercent)
                _logger.Warn($"CPU floor target may be unreachable on this {Environment.ProcessorCount}-processor VM because of the safety ceiling.");
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _active, 0);
            _logger.Error($"Could not activate CPU floor: {ex.Message}");
            _stop.Set();
            return false;
        }
    }

    public void Update(double measuredCpuPercent)
    {
        if (Volatile.Read(ref _active) == 0)
            return;

        double currentDuty = Volatile.Read(ref _desiredDuty);
        double processorScale = (double)Environment.ProcessorCount / _workers.Length;
        double adjustment = 0.65 * ((_targetPercent - measuredCpuPercent) / 100.0) * processorScale;
        double nextDuty = Math.Clamp(currentDuty + adjustment, _minimumDuty, MaxWorkerDuty);
        Volatile.Write(ref _desiredDuty, nextDuty);

        DateTimeOffset now = DateTimeOffset.Now;
        if (now >= _nextStatusLog)
        {
            _logger.Info($"CPU floor status: measured={measuredCpuPercent:F1}%, target={_targetPercent:F1}%, minimum worker duty={_minimumDuty * 100:F1}%, worker duty={nextDuty * 100:F1}%.");
            _nextStatusLog = now.AddMinutes(1);
        }
    }

    private void WorkerLoop()
    {
        var stopwatch = new Stopwatch();
        while (!_stop.IsSet)
        {
            double duty = Volatile.Read(ref _desiredDuty);
            if (duty <= 0.0005)
            {
                _stop.Wait(WindowMilliseconds);
                continue;
            }

            double activeMilliseconds = WindowMilliseconds * duty;
            stopwatch.Restart();
            while (stopwatch.Elapsed.TotalMilliseconds < activeMilliseconds && !_stop.IsSet)
                Thread.SpinWait(256);

            int remainingMilliseconds = Math.Max(0, WindowMilliseconds - (int)stopwatch.Elapsed.TotalMilliseconds);
            if (remainingMilliseconds > 0)
                _stop.Wait(remainingMilliseconds);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        foreach (Thread worker in _workers)
        {
            if (worker.IsAlive)
                worker.Join(TimeSpan.FromSeconds(2));
        }
        _stop.Dispose();
    }
}

internal sealed class FileLogger : IDisposable
{
    private readonly StreamWriter _writer;

    public FileLogger(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
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
    private const uint MouseMoveNoCoalesce = 0x2000;
    private const uint KeyScanCode = 0x0008;
    private const uint KeyUp = 0x0002;
    private const ushort ScrollScanCode = 0x46;
    private const int InputPulseDelayMilliseconds = 75;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

    internal static TimeSpan? TryGetLastInputIdle(out int error)
    {
        var lastInputInfo = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref lastInputInfo))
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        error = 0;
        uint currentTick = unchecked((uint)GetTickCount64());
        uint idleMilliseconds = unchecked(currentTick - lastInputInfo.Time);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    internal static bool ToggleScrollLockTwice()
    {
        for (int cycle = 0; cycle < 2; cycle++)
        {
            if (!SendKey(ScrollScanCode, 0) || !SendKey(ScrollScanCode, KeyUp))
                return false;
            Thread.Sleep(InputPulseDelayMilliseconds);
        }

        return true;
    }

    internal static bool NudgeMouseOnePixel()
    {
        Input[] forward = [Mouse(4, 0)];
        if (SendInput(1, forward, Marshal.SizeOf<Input>()) != 1)
            return false;

        Thread.Sleep(InputPulseDelayMilliseconds);

        Input[] backward = [Mouse(-4, 0)];
        return SendInput(1, backward, Marshal.SizeOf<Input>()) == 1;
    }

    private static bool SendKey(ushort scanCode, uint flags)
    {
        Input[] inputs = [Key(scanCode, flags)];
        return SendInput(1, inputs, Marshal.SizeOf<Input>()) == 1;
    }

    private static Input Key(ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { Scan = scanCode, Flags = KeyScanCode | flags } }
    };

    private static Input Mouse(int dx, int dy) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Flags = MouseMove | MouseMoveNoCoalesce } }
    };

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
