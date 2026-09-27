using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IslandUI.Services;

/// <summary>
/// Windows battery status via GetSystemPowerStatus.
/// </summary>
public sealed class BatteryService : INotifyPropertyChanged
{
    public static BatteryService Instance { get; } = new();

    private readonly Timer _timer;
    private bool _hasBattery;
    private int _percent;
    private bool _isCharging;
    private bool _isPluggedIn;

    private BatteryService()
    {
        Sample(null);
        _timer = new Timer(Sample, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool HasBattery
    {
        get => _hasBattery;
        private set => SetField(ref _hasBattery, value);
    }

    /// <summary>0-100, or -1 when unknown.</summary>
    public int Percent
    {
        get => _percent;
        private set => SetField(ref _percent, value);
    }

    public bool IsCharging
    {
        get => _isCharging;
        private set => SetField(ref _isCharging, value);
    }

    public bool IsPluggedIn
    {
        get => _isPluggedIn;
        private set => SetField(ref _isPluggedIn, value);
    }

    private void Sample(object? state)
    {
        if (!GetSystemPowerStatus(out var s)) return;
        // BatteryFlag bit 7 (128) = no system battery; 255 = unknown status
        HasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
        Percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : -1;
        IsCharging = (s.BatteryFlag & 8) != 0;
        IsPluggedIn = s.ACLineStatus == 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
