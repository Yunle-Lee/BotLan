using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IslandUI.Services;

/// <summary>
/// Lightweight Windows CPU / memory sampler. Uses a PerformanceCounter for
/// total CPU utilisation and GlobalMemoryStatusEx for memory.
/// </summary>
public sealed class SystemStatsService : INotifyPropertyChanged, IDisposable
{
    public static SystemStatsService Instance { get; } = new();

    private readonly PerformanceCounter _cpuCounter;
    private readonly Timer _timer;
    private double _cpuPercent;
    private double _memoryPercent;
    private double _memoryUsedGb;
    private double _memoryTotalGb;
    private double _diskPercent;
    private double _diskUsedGb;
    private double _diskTotalGb;

    private SystemStatsService()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
        _ = _cpuCounter.NextValue(); // first sample is always 0 — discard

        _timer = new Timer(Sample, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double CpuPercent
    {
        get => _cpuPercent;
        private set => SetField(ref _cpuPercent, value, nameof(CpuPercent));
    }

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set => SetField(ref _memoryPercent, value, nameof(MemoryPercent));
    }

    public double MemoryUsedGb
    {
        get => _memoryUsedGb;
        private set => SetField(ref _memoryUsedGb, value, nameof(MemoryUsedGb));
    }

    public double MemoryTotalGb
    {
        get => _memoryTotalGb;
        private set => SetField(ref _memoryTotalGb, value, nameof(MemoryTotalGb));
    }

    public double DiskPercent
    {
        get => _diskPercent;
        private set => SetField(ref _diskPercent, value, nameof(DiskPercent));
    }

    public double DiskUsedGb
    {
        get => _diskUsedGb;
        private set => SetField(ref _diskUsedGb, value, nameof(DiskUsedGb));
    }

    public double DiskTotalGb
    {
        get => _diskTotalGb;
        private set => SetField(ref _diskTotalGb, value, nameof(DiskTotalGb));
    }

    private int _diskSampleCounter;

    private void Sample(object? state)
    {
        try
        {
            CpuPercent = Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch
        {
            // Counter instance can briefly disappear; keep last value.
        }

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem))
        {
            MemoryPercent = mem.dwMemoryLoad;
            MemoryTotalGb = Math.Round(mem.ullTotalPhys / 1073741824.0, 1);
            MemoryUsedGb = Math.Round((mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0, 1);
        }

        // Disk usage is nearly static; sample it every 15 ticks.
        if (++_diskSampleCounter % 15 == 1)
        {
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                var drive = new DriveInfo(root);
                DiskTotalGb = Math.Round(drive.TotalSize / 1073741824.0, 1);
                DiskUsedGb = Math.Round((drive.TotalSize - drive.AvailableFreeSpace) / 1073741824.0, 1);
                DiskPercent = Math.Round(100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize, 1);
            }
            catch
            {
                // Drive may not be ready; keep last value.
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _cpuCounter.Dispose();
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
