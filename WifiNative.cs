using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;

namespace TopBar
{
    /// <summary>One visible Wi-Fi network.</summary>
    public sealed record WifiNetwork(
        string Ssid,
        uint Signal,
        bool Secured,
        bool Connected,
        bool Connectable,
        int AuthAlgo,
        int CipherAlgo);

    /// <summary>
    /// Thin wlanapi.dll wrapper: enumerate adapter, scan networks, connect
    /// (open / known profile / new secured with password), disconnect.
    /// All blocking calls run on background threads by the callers.
    /// </summary>
    internal static class WifiNative
    {
        // ── P/Invoke ────────────────────────────────────────────────────

        private const uint WLAN_CLIENT_VERSION = 2;

        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(IntPtr client, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid guid, uint opcode, IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr pOpcodeType);
        [DllImport("wlanapi.dll")] private static extern uint WlanConnect(IntPtr client, ref Guid guid, ref WLAN_CONNECTION_PARAMETERS param, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr client, ref Guid guid, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern uint WlanSetProfile(IntPtr client, ref Guid guid, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string profileXml, [MarshalAs(UnmanagedType.LPWStr)] string? security, bool overwrite, IntPtr reserved, out uint reasonCode);
        [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr client, ref Guid guid, IntPtr ssid, IntPtr ieData, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr mem);

        private const uint OPCODE_CURRENT_CONNECTION = 7;

        private enum WLAN_INTERFACE_STATE : uint
        {
            NotReady = 0, Connected = 1, AdHocFormed = 2, Disconnecting = 3,
            Disconnected = 4, Associating = 5, Discovering = 6, Authenticating = 7
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO
        {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
            public WLAN_INTERFACE_STATE State;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DOT11_SSID
        {
            public uint Length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_AVAILABLE_NETWORK
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
            public DOT11_SSID Ssid;
            public uint BssType;
            public uint NumberOfBssids;
            [MarshalAs(UnmanagedType.Bool)] public bool NetworkConnectable;
            public uint NotConnectableReason;
            public uint NumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] PhyTypes;
            [MarshalAs(UnmanagedType.Bool)] public bool MorePhyTypes;
            public uint SignalQuality;
            [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
            public uint AuthAlgo;
            public uint CipherAlgo;
            public uint Flags;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_ATTRIBUTES
        {
            public WLAN_INTERFACE_STATE InterfaceState;
            public uint ConnectionMode;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
            public DOT11_SSID Ssid;
            public uint BssType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
            public uint PhyType;
            public uint PhyIndex;
            public uint SignalQuality;
            public uint ReceiveRate;
            public uint TransmitRate;
            [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
            [MarshalAs(UnmanagedType.Bool)] public bool OneXEnabled;
            public uint AuthAlgo;
            public uint CipherAlgo;
        }

        private enum WLAN_CONNECTION_MODE : uint { Profile = 0, TemporaryProfile = 1, DiscoverySecure = 2, DiscoveryUnsecure = 3, Auto = 4 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_PARAMETERS
        {
            public WLAN_CONNECTION_MODE Mode;
            [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
            public IntPtr Ssid;
            public IntPtr BssidList;
            public uint BssType;
            public uint Flags;
        }

        // ── Public API ──────────────────────────────────────────────────

        public static bool TryGetInterface(out Guid guid, out string desc)
        {
            guid = Guid.Empty; desc = "";
            IntPtr client = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out client) != 0) return false;
                return TryGetBestInterface(client, out guid, out desc);
            }
            catch { return false; }
            finally { if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); }
        }

        public static Task<List<WifiNetwork>> ScanAsync() => Task.Run(() =>
        {
            var result = new List<WifiNetwork>();
            IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out client) != 0) return result;
                if (!TryGetInterfaceGuid(client, out Guid guid)) return result;

                string connectedSsid = GetConnectedSsid(client, guid);

                // Trigger a fresh active scan — without this the list below is
                // just the stale cache (often a single network).
                try { WlanScan(client, ref guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } catch { }
                System.Threading.Thread.Sleep(2500);

                if (WlanGetAvailableNetworkList(client, ref guid, 0, IntPtr.Zero, out list) != 0) return result;
                uint count = (uint)Marshal.ReadInt32(list);
                int entrySize = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = list + 8 + i * entrySize;
                    WLAN_AVAILABLE_NETWORK n;
                    try { n = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(entry); }
                    catch { continue; }
                    string ssid = SsidToString(n.Ssid);
                    if (string.IsNullOrEmpty(ssid)) continue;
                    if (result.Any(r => r.Ssid == ssid)) continue; // dedupe multi-band
                    result.Add(new WifiNetwork(
                        ssid, n.SignalQuality, n.SecurityEnabled,
                        string.Equals(ssid, connectedSsid, StringComparison.Ordinal),
                        n.NetworkConnectable, (int)n.AuthAlgo, (int)n.CipherAlgo));
                }
                // Connected first, then strongest signal
                return result.OrderByDescending(r => r.Connected).ThenByDescending(r => r.Signal).ToList();
            }
            catch { return result; }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
        });

        public static Task<(bool Ok, string CurrentSsid, uint Signal)> GetStatusAsync() => Task.Run(() =>
        {
            IntPtr client = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out client) != 0)
                    return (false, "", 0u);
                if (!TryGetInterfaceGuid(client, out Guid guid))
                    return (false, "", 0u);
                string ssid = GetConnectedSsid(client, guid);
                uint sig = GetConnectedSignal(client, guid);
                return (true, ssid, sig);
            }
            catch { return (false, "", 0u); }
            finally { if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); }
        });

        /// <summary>Connect. Tries the stored profile first for secured nets;
        /// falls back to false so the UI can prompt for a password.</summary>
        public static Task<bool> TryKnownProfileAsync(string ssid) => Task.Run(() =>
            ConnectWithProfile(ssid, ssid));

        public static Task<bool> ConnectOpenAsync(string ssid) => Task.Run(() =>
        {
            if (!TryGetInterface(out Guid guid, out _)) return false;
            string xml = BuildProfileXml(ssid, null, 1, 0);
            if (!SetProfile(ssid, xml)) return false;
            return ConnectWithProfile(ssid, ssid);
        });

        public static Task<bool> ConnectSecuredAsync(string ssid, int authAlgo, int cipherAlgo, string password) => Task.Run(() =>
        {
            if (!TryGetInterface(out Guid guid, out _)) return false;
            string xml = BuildProfileXml(ssid, password, authAlgo, cipherAlgo);
            if (!SetProfile(ssid, xml)) return false;
            return ConnectWithProfile(ssid, ssid);
        });

        public static Task DisconnectAsync() => Task.Run(() =>
        {
            IntPtr client = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out client) != 0) return;
                if (!TryGetInterfaceGuid(client, out Guid guid)) return;
                WlanDisconnect(client, ref guid, IntPtr.Zero);
            }
            catch { }
            finally { if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); }
        });

        // ── Internals ───────────────────────────────────────────────────

        private static bool TryGetInterfaceGuid(IntPtr client, out Guid guid)
            => TryGetBestInterface(client, out guid, out _);

        /// <summary>
        /// Picks the real adapter: the connected one wins, otherwise the first
        /// non-virtual ready adapter (skips Wi-Fi Direct / hosted-network
        /// virtual NICs that would report stale scans + "not connected").
        /// </summary>
        private static bool TryGetBestInterface(IntPtr client, out Guid guid, out string desc)
        {
            guid = Guid.Empty; desc = "";
            IntPtr list = IntPtr.Zero;
            try
            {
                if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return false;
                int count = Marshal.ReadInt32(list);
                if (count == 0) return false;
                int entrySize = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
                int bestScore = -1;
                for (int i = 0; i < count; i++)
                {
                    WLAN_INTERFACE_INFO info;
                    try { info = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * entrySize); }
                    catch { continue; }
                    string d = info.Description ?? "";
                    int score;
                    if (info.State == WLAN_INTERFACE_STATE.Connected) score = 3;
                    else if (d.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0
                        || d.IndexOf("direct", StringComparison.OrdinalIgnoreCase) >= 0
                        || d.IndexOf("hosted", StringComparison.OrdinalIgnoreCase) >= 0) score = 0;
                    else if (info.State == WLAN_INTERFACE_STATE.NotReady) score = 0;
                    else score = 2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        guid = info.InterfaceGuid;
                        desc = d;
                    }
                }
                return bestScore >= 0;
            }
            catch { return false; }
            finally { if (list != IntPtr.Zero) WlanFreeMemory(list); }
        }

        private static string SsidToString(DOT11_SSID s)
        {
            try
            {
                if (s.Length == 0 || s.Bytes == null) return "";
                int len = (int)Math.Min(s.Length, 32);
                return System.Text.Encoding.UTF8.GetString(s.Bytes, 0, len);
            }
            catch { return ""; }
        }

        private static string GetConnectedSsid(IntPtr client, Guid guid)
        {
            IntPtr data = IntPtr.Zero;
            try
            {
                if (WlanQueryInterface(client, ref guid, OPCODE_CURRENT_CONNECTION, IntPtr.Zero, out _, out data, IntPtr.Zero) != 0)
                    return "";
                var attr = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                if (attr.InterfaceState != WLAN_INTERFACE_STATE.Connected) return "";
                return SsidToString(attr.Ssid);
            }
            catch { return ""; }
            finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
        }

        private static uint GetConnectedSignal(IntPtr client, Guid guid)
        {
            IntPtr data = IntPtr.Zero;
            try
            {
                if (WlanQueryInterface(client, ref guid, OPCODE_CURRENT_CONNECTION, IntPtr.Zero, out _, out data, IntPtr.Zero) != 0)
                    return 0;
                var attr = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(data);
                return attr.InterfaceState == WLAN_INTERFACE_STATE.Connected ? attr.SignalQuality : 0;
            }
            catch { return 0; }
            finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
        }

        private static bool SetProfile(string name, string xml)
        {
            IntPtr client = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out client) != 0) return false;
                if (!TryGetInterfaceGuid(client, out Guid guid)) return false;
                // 0 = profile user scope (all users)
                return WlanSetProfile(client, ref guid, 0, xml, null, true, IntPtr.Zero, out _) == 0;
            }
            catch { return false; }
            finally { if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); }
        }

        private static bool ConnectWithProfile(string ssid, string profileName)
        {
            IntPtr client = IntPtr.Zero;
            try
            {
                if (WlanOpenHandle(WLAN_CLIENT_VERSION, IntPtr.Zero, out _, out client) != 0) return false;
                if (!TryGetInterfaceGuid(client, out Guid guid)) return false;
                var p = new WLAN_CONNECTION_PARAMETERS
                {
                    Mode = WLAN_CONNECTION_MODE.Profile,
                    Profile = profileName,
                    Ssid = IntPtr.Zero,
                    BssidList = IntPtr.Zero,
                    BssType = 1, // infrastructure
                    Flags = 0
                };
                if (WlanConnect(client, ref guid, ref p, IntPtr.Zero) != 0) return false;
                // Verify the connection actually lands (async operation)
                for (int i = 0; i < 10; i++)
                {
                    System.Threading.Thread.Sleep(800);
                    if (string.Equals(GetConnectedSsid(client, guid), ssid, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch { return false; }
            finally { if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero); }
        }

        private static string BuildProfileXml(string ssid, string? password, int authAlgo, int cipherAlgo)
        {
            string eSsid = SecurityElement.Escape(ssid) ?? ssid;
            string security;
            if (password == null)
            {
                security = @"<MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security></MSM>";
            }
            else
            {
                // DOT11_AUTH: 1=open 4=WPA-PSK 6=RSNA 7=RSNA-PSK 9=WPA3-SAE
                string auth = authAlgo == 9 ? "WPA3SAE" : "WPA2PSK";
                string enc = cipherAlgo == 4 ? "TKIP" : "AES"; // DOT11_CIPHER TKIP=2? map defensively
                if (cipherAlgo == 1 || cipherAlgo == 5) enc = "WEP"; // shared-key fallback
                string eKey = SecurityElement.Escape(password) ?? "";
                security = $@"<MSM><security><authEncryption><authentication>{auth}</authentication><encryption>{enc}</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{eKey}</keyMaterial></sharedKey></security></MSM>";
            }
            return $@"<?xml version=""1.0""?><WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1""><name>{eSsid}</name><SSIDConfig><SSID><name>{eSsid}</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode><autoSwitch>false</autoSwitch>{security}</WLANProfile>";
        }
    }
}
