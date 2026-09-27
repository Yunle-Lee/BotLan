using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace IslandUI.Services;

/// <summary>WiFi status via WlanAPI: SSID, signal quality, link state.</summary>
public sealed class WifiService : INotifyPropertyChanged
{
    public static WifiService Instance { get; } = new();

    private readonly Timer _timer;
    private bool _isConnected;
    private string _ssid = "";
    private int _signalPercent;

    private WifiService()
    {
        Sample(null);
        _timer = new Timer(Sample, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsConnected
    {
        get => _isConnected;
        private set => SetField(ref _isConnected, value);
    }

    public string Ssid
    {
        get => _ssid;
        private set => SetField(ref _ssid, value);
    }

    /// <summary>0-100 signal quality; 0 when disconnected or no WiFi hardware.</summary>
    public int SignalPercent
    {
        get => _signalPercent;
        private set => SetField(ref _signalPercent, value);
    }

    private void Sample(object? state)
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var client) != 0)
            {
                IsConnected = false;
                SignalPercent = 0;
                Ssid = "";
                return;
            }
            try
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out var list) != 0) return;
                try
                {
                    var header = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(list);
                    for (int i = 0; i < header.dwNumberOfItems; i++)
                    {
                        var infoPtr = list + Marshal.SizeOf<WLAN_INTERFACE_INFO_LIST>()
                            + i * Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                        var info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(infoPtr);
                        if (info.isState != WLAN_INTERFACE_STATE.wlan_interface_state_connected) continue;

                        if (WlanQueryInterface(client, ref info.InterfaceGuid,
                                wlan_intf_opcode_current_connection, IntPtr.Zero,
                                out int dataSize, out var data, out _) == 0)
                        {
                            try
                            {
                                var conn = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                                Ssid = Encoding.UTF8.GetString(conn.dot11Ssid.ucSSID)
                                    .TrimEnd('\0');
                                SignalPercent = (int)conn.wlanSignalQuality;
                                IsConnected = true;
                            }
                            finally
                            {
                                WlanFreeMemory(data);
                            }
                        }
                    }
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            }
            finally
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
        }
        catch
        {
            IsConnected = false;
            SignalPercent = 0;
        }
    }

    private const int wlan_intf_opcode_current_connection = 7;

    private enum WLAN_INTERFACE_STATE
    {
        wlan_interface_state_not_ready = 0,
        wlan_interface_state_connected = 1,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public WLAN_INTERFACE_STATE isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        // WLAN_ASSOCIATION_ATTRIBUTES:
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        // (remaining fields not needed)
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        int OpCode, IntPtr pReserved, out int pdwDataSize, out IntPtr ppData, out IntPtr pwlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
}
