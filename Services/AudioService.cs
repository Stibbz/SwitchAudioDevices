using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using AudioPilot.Models;
using System.Runtime.InteropServices;

namespace AudioPilot.Services
{
    /// <summary>Rich description of a single audio render endpoint.</summary>
    public record AudioEndpointInfo(
        string Id,
        string Name,
        bool   IsDefault,
        bool   IsBluetooth,
        bool   IsBluetoothConnected,
        ulong  BluetoothAddress,
        float  Volume);

    public class AudioService : IDisposable, IMMNotificationClient
    {
        private readonly MMDeviceEnumerator _enumerator = new();
        private readonly BluetoothService   _bt         = new();

        public AudioService() => _enumerator.RegisterEndpointNotificationCallback(this);

        /// <summary>Raised on the thread-pool when any render endpoint is added, removed,
        /// or changes state (e.g. BT headset disconnects).</summary>
        public event Action? DeviceStateChanged;

        // BT paired-device list changes rarely; cache it for a few seconds so that
        // back-and-forth settings navigation doesn't re-enumerate on every trip.
        private IReadOnlyList<BluetoothDeviceInfo>? _btCache;
        private DateTime _btCacheAt = DateTime.MinValue;
        private static readonly TimeSpan BtCacheTtl = TimeSpan.FromSeconds(5);

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all audio render endpoints the app knows about:
        /// active (wired / BT connected) + paired-but-disconnected BT devices.
        /// Non-BT unplugged devices are excluded.
        /// </summary>
        public IReadOnlyList<AudioEndpointInfo> GetAllEndpoints()
        {
            var defaultId = GetDefaultId();
            var btDevices = GetCachedBtDevices();
            var result    = new List<AudioEndpointInfo>();

            foreach (var ep in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                try
                {
                    var isActive       = ep.State == DeviceState.Active;
                    var isDisconnected = ep.State is DeviceState.Unplugged or DeviceState.NotPresent;

                    if (!isActive && !isDisconnected) continue;

                    var btMatch    = MatchBluetooth(ep.FriendlyName, btDevices);
                    var isBtByProp = btMatch == null && IsBluetoothByProperty(ep);

                    float volume = 0f;
                    if (isActive)
                    {
                        try { volume = ep.AudioEndpointVolume.MasterVolumeLevelScalar; }
                        catch { }
                    }

                    if (isActive)
                    {
                        bool isBt = btMatch != null || isBtByProp;
                        result.Add(new AudioEndpointInfo(
                            ep.ID, ep.FriendlyName, ep.ID == defaultId,
                            IsBluetooth:          isBt,
                            IsBluetoothConnected: isBt,
                            BluetoothAddress:     btMatch?.Address ?? 0,
                            Volume:               volume));
                    }
                    else if (btMatch != null || isBtByProp)
                    {
                        result.Add(new AudioEndpointInfo(
                            ep.ID, ep.FriendlyName, ep.ID == defaultId,
                            IsBluetooth:          true,
                            IsBluetoothConnected: false,
                            BluetoothAddress:     btMatch?.Address ?? 0,
                            Volume:               0f));
                    }
                }
                catch (COMException)
                {
                    // Device is in a transient/invalid state (e.g. 0x8889000A, 0xE000020B).
                    // Skip it — it will re-appear on the next poll once Windows
                    // has finished updating the endpoint's property store.
                }
            }

            return result;
        }

        /// <summary>Filtered view used by the main switcher window.</summary>
        public IReadOnlyList<AudioEndpointInfo> GetPlaybackEndpoints(AppSettings settings)
            => GetAllEndpoints()
                .Where(d => !settings.DisabledDeviceIds.Contains(d.Id))
                .ToList();

        public string? GetDefaultDeviceName()
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.FriendlyName; }
            catch { return null; }
        }

        public void SetDefaultDevice(string deviceId)
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            policy.SetDefaultEndpoint(deviceId, Role.Console);
            policy.SetDefaultEndpoint(deviceId, Role.Multimedia);
            policy.SetDefaultEndpoint(deviceId, Role.Communications);
        }

        public void SetDeviceVolume(string deviceId, float volume)
        {
            try
            {
                var ep = _enumerator.GetDevice(deviceId);
                if (ep != null)
                    ep.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
            }
            catch { }
        }

        public void UnmuteDevice(string deviceId)
        {
            try
            {
                var ep = _enumerator.GetDevice(deviceId);
                if (ep == null) return;
                var vol = ep.AudioEndpointVolume;
                if (vol.Mute)
                    vol.Mute = false;
                if (vol.MasterVolumeLevelScalar < 0.25f)
                    vol.MasterVolumeLevelScalar = 0.25f;
            }
            catch { }
        }

        public async Task<bool> ConnectBluetoothDeviceAsync(ulong bluetoothAddress, string deviceName)
        {
            // Invalidate cache so the next poll after connecting sees fresh BT state.
            _btCache = null;
            return await _bt.ConnectDeviceAsync(bluetoothAddress, deviceName);
        }

        /// <summary>Discards the cached BT paired-device list so the next call to
        /// <see cref="GetAllEndpoints"/> fetches a fresh snapshot from the radio.</summary>
        public void InvalidateBtCache() => _btCache = null;

        /// <summary>Releases the ACL keep-alive socket held since the last connect attempt.</summary>
        public void ReleaseAclSocket() => _bt.ReleaseAclSocket();

        /// <summary>Returns true if the BT radio currently reports the device as connected.</summary>
        public bool IsBluetoothRadioConnected(ulong address)
        {
            InvalidateBtCache();
            return GetCachedBtDevices().Any(d => d.Address == address && d.IsConnected);
        }

        public void Dispose()
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }

        // ── IMMNotificationClient ───────────────────────────────────────────────

        void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState)
            => DeviceStateChanged?.Invoke();

        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId)
            => DeviceStateChanged?.Invoke();

        void IMMNotificationClient.OnDeviceRemoved(string deviceId)
            => DeviceStateChanged?.Invoke();

        void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        { }

        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        { }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private IReadOnlyList<BluetoothDeviceInfo> GetCachedBtDevices()
        {
            if (_btCache != null && DateTime.UtcNow - _btCacheAt < BtCacheTtl)
                return _btCache;
            _btCache  = _bt.GetPairedDevices();
            _btCacheAt = DateTime.UtcNow;
            return _btCache;
        }

        private string? GetDefaultId()
        {
            try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.ID; }
            catch { return null; }
        }

        private static BluetoothDeviceInfo? MatchBluetooth(
            string endpointName, IReadOnlyList<BluetoothDeviceInfo> btDevices)
            => btDevices
                .Where(bt =>
                    endpointName.Contains(bt.Name, StringComparison.OrdinalIgnoreCase) ||
                    bt.Name.Contains(endpointName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(bt => bt.Name.Length)
                .FirstOrDefault();

        private static readonly PropertyKey InstanceIdKey =
            new(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);

        private static bool IsBluetoothByProperty(MMDevice ep)
        {
            try
            {
                var val = ep.Properties[InstanceIdKey]?.Value?.ToString() ?? string.Empty;
                return val.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                    || val.Contains("BTHLE",   StringComparison.OrdinalIgnoreCase)
                    || val.Contains("BTH\\",   StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    // ── COM interop for setting default audio endpoint ──────────────────────────

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr pKey, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint(string pszDeviceName, Role role);
        [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }
}
