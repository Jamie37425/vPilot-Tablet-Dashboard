using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace vPilotTabletMirror
{
    public class PttBinding
    {
        public string Id { get; set; }
        public string Kind { get; set; } // "JoystickButton" or "KeyboardKey"
        public string DeviceName { get; set; }
        public string InstanceGuid { get; set; }
        public string ProductGuid { get; set; }
        public int ButtonIndex { get; set; }
        public int VirtualKeyCode { get; set; }
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public string DisplayLabel => Kind == "JoystickButton"
            ? $"{DeviceName} — Button {ButtonIndex + 1}"
            : $"Keyboard — {DeviceName}";
    }

    public class ConnectedInputDevice
    {
        public string Name { get; set; }
        public string InstanceGuid { get; set; }
        public string ProductGuid { get; set; }
    }

    /// <summary>
    /// Monitors multiple flight controllers and keyboard/mouse binds simultaneously using
    /// passive Windows Raw Input (WM_INPUT) so MSFS 2024 DirectInput devices are never acquired or polled.
    /// </summary>
    public class MultiPttManager : IDisposable
    {
        private const int WM_INPUT = 0x00FF;
        private const int WM_INPUT_DEVICE_CHANGE = 0x00FE;
        private const int WM_APP = 0x8000;
        private const int WM_APP_SYNC = WM_APP + 1;
        private const int WM_APP_REFRESH_DEVICES = WM_APP + 2;
        private const int WM_APP_EXIT = WM_APP + 3;

        private const uint RID_INPUT = 0x10000003;
        private const uint RIM_TYPEHID = 2;
        private const uint RIDEV_REMOVE = 0x00000001;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_DEVNOTIFY = 0x00002000;
        private const uint RIDI_PREPARSEDDATA = 0x20000005;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIDI_DEVICEINFO = 0x2000000B;

        private const int HIDP_INPUT = 0;
        private const int HIDP_STATUS_SUCCESS = 0x00110000;
        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_JOYSTICK = 0x04;
        private const ushort HID_USAGE_GENERIC_GAMEPAD = 0x05;
        private const ushort HID_USAGE_PAGE_BUTTON = 0x09;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(
            IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetRawInputDeviceInfoW(
            IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetProductString(
            IntPtr HidDeviceObject, [Out] byte[] Buffer, uint BufferLength);

        [DllImport("hid.dll")]
        private static extern uint HidP_MaxUsageListLength(
            int ReportType, ushort UsagePage, IntPtr PreparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsages(
            int ReportType, ushort UsagePage, ushort LinkCollection,
            [Out] ushort[] UsageList, ref uint UsageLength,
            IntPtr PreparsedData, IntPtr Report, uint ReportLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        private struct RID_DEVICE_INFO
        {
            [FieldOffset(0)] public uint cbSize;
            [FieldOffset(4)] public uint dwType;
            [FieldOffset(8)] public uint hidVendorId;
            [FieldOffset(12)] public uint hidProductId;
            [FieldOffset(16)] public uint hidVersionNumber;
            [FieldOffset(20)] public ushort hidUsagePage;
            [FieldOffset(22)] public ushort hidUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        private sealed class PttConfigFile
        {
            public bool HardwarePttEnabled { get; set; } = true;
            public List<PttBinding> Bindings { get; set; } = new List<PttBinding>();
        }

        private sealed class RawDeviceRecord : IDisposable
        {
            public IntPtr Handle { get; set; }
            public string DevicePath { get; set; }
            public string Name { get; set; }
            public string ProductGuidStr { get; set; }
            public IntPtr PreparsedData { get; set; }
            public uint MaxButtonUsages { get; set; }
            public HashSet<int> PressedButtons { get; } = new HashSet<int>();

            public void FreePreparsed()
            {
                if (PreparsedData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(PreparsedData);
                    PreparsedData = IntPtr.Zero;
                }
                MaxButtonUsages = 0;
                PressedButtons.Clear();
            }

            public void Dispose()
            {
                FreePreparsed();
            }
        }

        private sealed class RawInputMessageWindow : NativeWindow
        {
            private readonly MultiPttManager _owner;

            public RawInputMessageWindow(MultiPttManager owner)
            {
                _owner = owner;
                var cp = new CreateParams
                {
                    Caption = "vPilotTabletMirrorRawInputSink",
                    Parent = new IntPtr(-3) // HWND_MESSAGE
                };
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_INPUT:
                        _owner.OnRawInputMessage(m.LParam);
                        break;
                    case WM_INPUT_DEVICE_CHANGE:
                        _owner.OnRawDeviceChange();
                        break;
                    case WM_APP_SYNC:
                        _owner.SyncRegistrationAndDevices(rescanDevices: false);
                        break;
                    case WM_APP_REFRESH_DEVICES:
                        _owner.SyncRegistrationAndDevices(rescanDevices: true);
                        break;
                    case WM_APP_EXIT:
                        _owner.OnThreadExitRequested();
                        break;
                }
                base.WndProc(ref m);
            }
        }

        private readonly string _configPath;
        private readonly object _stateLock = new object();
        private readonly List<PttBinding> _bindings = new List<PttBinding>();
        private readonly List<ConnectedInputDevice> _connectedDevices = new List<ConnectedInputDevice>();
        private readonly HashSet<string> _activeBindingIds = new HashSet<string>();
        private readonly Dictionary<IntPtr, RawDeviceRecord> _rawDevices = new Dictionary<IntPtr, RawDeviceRecord>();

        private Thread _uiThread;
        private RawInputMessageWindow _messageWindow;
        private ApplicationContext _appContext;
        private System.Windows.Forms.Timer _keyboardTimer;
        private readonly ManualResetEventSlim _threadReady = new ManualResetEventSlim(false);
        private bool _rawInputRegistered = false;
        private bool _hardwarePttEnabled = true;
        private bool _hardwarePttActive = false;

        // Capture mode fields (managed under _stateLock)
        private bool _captureRequested = false;
        private bool _capturing = false;
        private DateTime _captureDeadlineUtc;
        private readonly Dictionary<IntPtr, HashSet<int>> _baselineButtons = new Dictionary<IntPtr, HashSet<int>>();
        private readonly Dictionary<int, bool> _baselineKeys = new Dictionary<int, bool>();

        public event Action<bool, List<string>> HardwarePttChanged;
        public event Action<string, PttBinding, string> CaptureStatusChanged;
        public event Action BindingsUpdated;

        public MultiPttManager(string configPath)
        {
            _configPath = configPath;
            LoadBindingsFromDisk();
        }

        public void Start()
        {
            if (_uiThread != null) return;
            _threadReady.Reset();
            _uiThread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "vPilotMultiPttRawInputThread"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            _threadReady.Wait(2000);
        }

        public List<PttBinding> GetBindingsSnapshot()
        {
            lock (_stateLock)
            {
                return _bindings.Select(CloneBinding).ToList();
            }
        }

        public List<ConnectedInputDevice> GetConnectedDevicesSnapshot()
        {
            lock (_stateLock)
            {
                return _connectedDevices.Select(d => new ConnectedInputDevice
                {
                    Name = d.Name,
                    InstanceGuid = d.InstanceGuid,
                    ProductGuid = d.ProductGuid
                }).ToList();
            }
        }

        public List<string> GetActiveBindingIdsSnapshot()
        {
            lock (_stateLock)
            {
                return _activeBindingIds.ToList();
            }
        }

        public bool IsCapturing
        {
            get { lock (_stateLock) { return _capturing || _captureRequested; } }
        }

        public bool HardwarePttEnabled
        {
            get { lock (_stateLock) { return _hardwarePttEnabled; } }
        }

        public void SetHardwarePttEnabled(bool enabled)
        {
            bool changed = false;
            lock (_stateLock)
            {
                if (_hardwarePttEnabled != enabled)
                {
                    _hardwarePttEnabled = enabled;
                    changed = true;
                    if (!enabled && (_capturing || _captureRequested))
                    {
                        _capturing = false;
                        _captureRequested = false;
                        _baselineButtons.Clear();
                        _baselineKeys.Clear();
                    }
                    SaveBindingsToDiskLocked();
                }
            }

            if (changed)
            {
                if (!enabled)
                {
                    UpdatePressedState(new HashSet<string>());
                }
                PostThreadMessage(WM_APP_SYNC);
                BindingsUpdated?.Invoke();
            }
        }

        public void RequestDeviceRefresh()
        {
            PostThreadMessage(WM_APP_REFRESH_DEVICES);
        }

        public void StartCapture(int timeoutMs = 15000)
        {
            lock (_stateLock)
            {
                _captureRequested = true;
                _capturing = false;
                _captureDeadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                _baselineButtons.Clear();
                _baselineKeys.Clear();
            }
            PostThreadMessage(WM_APP_REFRESH_DEVICES);
            CaptureStatusChanged?.Invoke("Listening", null, "Press any button on your stick, yoke, throttle, or keyboard (Esc to cancel)...");
        }

        public void CancelCapture(string reason = "Capture cancelled.")
        {
            bool wasActive;
            lock (_stateLock)
            {
                wasActive = _capturing || _captureRequested;
                _captureRequested = false;
                _capturing = false;
                _baselineButtons.Clear();
                _baselineKeys.Clear();
            }
            if (wasActive)
            {
                PostThreadMessage(WM_APP_SYNC);
                CaptureStatusChanged?.Invoke("Cancelled", null, reason);
            }
        }

        public bool RemoveBinding(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            bool removed;
            lock (_stateLock)
            {
                int countBefore = _bindings.Count;
                _bindings.RemoveAll(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
                removed = _bindings.Count < countBefore;
                if (removed) SaveBindingsToDiskLocked();
            }
            if (removed)
            {
                PostThreadMessage(WM_APP_SYNC);
                BindingsUpdated?.Invoke();
            }
            return removed;
        }

        public bool SetBindingEnabled(string id, bool enabled)
        {
            if (string.IsNullOrEmpty(id)) return false;
            bool changed = false;
            lock (_stateLock)
            {
                var bind = _bindings.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
                if (bind != null && bind.Enabled != enabled)
                {
                    bind.Enabled = enabled;
                    changed = true;
                    SaveBindingsToDiskLocked();
                }
            }
            if (changed)
            {
                PostThreadMessage(WM_APP_SYNC);
                BindingsUpdated?.Invoke();
            }
            return changed;
        }

        private void PostThreadMessage(int msg)
        {
            try
            {
                var hwnd = _messageWindow?.Handle ?? IntPtr.Zero;
                if (hwnd != IntPtr.Zero)
                {
                    PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        }

        private void RunMessageLoop()
        {
            try
            {
                _messageWindow = new RawInputMessageWindow(this);
                _appContext = new ApplicationContext();
                _keyboardTimer = new System.Windows.Forms.Timer { Interval = 50 };
                _keyboardTimer.Tick += (s, e) => OnKeyboardOrCaptureTick();

                SyncRegistrationAndDevices(rescanDevices: true);
                _threadReady.Set();

                Application.Run(_appContext);
            }
            catch { }
            finally
            {
                _threadReady.Set();
                try
                {
                    _keyboardTimer?.Stop();
                    _keyboardTimer?.Dispose();
                    _keyboardTimer = null;
                }
                catch { }
                RegisterRawInputSink(enable: false);
                ReleaseAllRawDevices();
                try
                {
                    _messageWindow?.DestroyHandle();
                    _messageWindow = null;
                }
                catch { }
                try
                {
                    _appContext?.Dispose();
                    _appContext = null;
                }
                catch { }
            }
        }

        private void OnThreadExitRequested()
        {
            try
            {
                _keyboardTimer?.Stop();
                RegisterRawInputSink(enable: false);
                _appContext?.ExitThread();
            }
            catch { }
        }

        private void RegisterRawInputSink(bool enable)
        {
            if (_messageWindow == null || _messageWindow.Handle == IntPtr.Zero) return;
            if (enable == _rawInputRegistered) return;

            uint flags = enable ? (RIDEV_INPUTSINK | RIDEV_DEVNOTIFY) : RIDEV_REMOVE;
            IntPtr target = enable ? _messageWindow.Handle : IntPtr.Zero;

            var rid = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_JOYSTICK,
                    dwFlags = flags,
                    hwndTarget = target
                },
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_GAMEPAD,
                    dwFlags = flags,
                    hwndTarget = target
                }
            };

            if (RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
            {
                _rawInputRegistered = enable;
            }
        }

        private void SyncRegistrationAndDevices(bool rescanDevices)
        {
            bool pttEnabled;
            bool capturePending;
            bool captureActive;
            List<PttBinding> enabledBindings;

            lock (_stateLock)
            {
                pttEnabled = _hardwarePttEnabled;
                capturePending = _captureRequested;
                captureActive = _capturing;
                enabledBindings = _bindings.Where(b => b.Enabled).Select(CloneBinding).ToList();
            }

            bool captureMode = capturePending || captureActive;
            bool needJoystickSink = captureMode || (pttEnabled && enabledBindings.Any(b => b.Kind == "JoystickButton"));
            bool needKeyTimer = captureMode || (pttEnabled && enabledBindings.Any(b => b.Kind == "KeyboardKey"));

            if (rescanDevices || _rawDevices.Count == 0)
            {
                EnumerateRawHidControllers();
            }

            // Prepare PreparsedData ONLY for bound joysticks (or all joysticks during capture); free unused ones
            foreach (var rec in _rawDevices.Values)
            {
                bool needed = captureMode || (pttEnabled && IsDeviceNeededForBindings(rec, enabledBindings));
                if (needed)
                {
                    if (rec.PreparsedData == IntPtr.Zero)
                    {
                        LoadPreparsedData(rec);
                    }
                }
                else if (rec.PreparsedData != IntPtr.Zero)
                {
                    rec.FreePreparsed();
                }
            }

            RegisterRawInputSink(needJoystickSink);

            if (needKeyTimer)
            {
                if (_keyboardTimer != null && !_keyboardTimer.Enabled) _keyboardTimer.Start();
            }
            else
            {
                if (_keyboardTimer != null && _keyboardTimer.Enabled) _keyboardTimer.Stop();
            }

            if (capturePending)
            {
                BeginCaptureSnapshot();
            }
            else
            {
                EvaluateActiveBindingsSnapshot();
            }
        }

        private void OnRawDeviceChange()
        {
            SyncRegistrationAndDevices(rescanDevices: true);
        }

        private void EnumerateRawHidControllers()
        {
            try
            {
                uint count = 0;
                uint structSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
                GetRawInputDeviceList(IntPtr.Zero, ref count, structSize);

                var currentHandles = new HashSet<IntPtr>();
                var snapshotList = new List<ConnectedInputDevice>();

                if (count > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)(count * structSize));
                    try
                    {
                        uint actual = GetRawInputDeviceList(buf, ref count, structSize);
                        if (actual != uint.MaxValue)
                        {
                            for (int i = 0; i < actual; i++)
                            {
                                var item = (RAWINPUTDEVICELIST)Marshal.PtrToStructure(
                                    IntPtr.Add(buf, (int)(i * structSize)), typeof(RAWINPUTDEVICELIST));
                                if (item.dwType != RIM_TYPEHID) continue;

                                if (!TryReadHidDeviceInfo(item.hDevice, out var info)) continue;
                                if (info.hidUsagePage != HID_USAGE_PAGE_GENERIC ||
                                    (info.hidUsage != HID_USAGE_GENERIC_JOYSTICK && info.hidUsage != HID_USAGE_GENERIC_GAMEPAD))
                                {
                                    continue;
                                }

                                currentHandles.Add(item.hDevice);

                                if (!_rawDevices.TryGetValue(item.hDevice, out var rec))
                                {
                                    string devPath = ReadDeviceNamePath(item.hDevice);
                                    string prodName = ReadHidProductName(devPath, info.hidVendorId, info.hidProductId);
                                    string productGuid = FormatDirectInputProductGuid(info.hidVendorId, info.hidProductId);

                                    rec = new RawDeviceRecord
                                    {
                                        Handle = item.hDevice,
                                        DevicePath = devPath,
                                        Name = prodName,
                                        ProductGuidStr = productGuid
                                    };
                                    _rawDevices[item.hDevice] = rec;
                                }

                                snapshotList.Add(new ConnectedInputDevice
                                {
                                    Name = rec.Name,
                                    InstanceGuid = rec.DevicePath,
                                    ProductGuid = rec.ProductGuidStr
                                });
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }

                var removed = _rawDevices.Keys.Where(h => !currentHandles.Contains(h)).ToList();
                foreach (var h in removed)
                {
                    _rawDevices[h].Dispose();
                    _rawDevices.Remove(h);
                }

                bool listChanged;
                lock (_stateLock)
                {
                    listChanged = _connectedDevices.Count != snapshotList.Count ||
                        _connectedDevices.Select(d => d.InstanceGuid)
                            .Zip(snapshotList.Select(d => d.InstanceGuid), string.Equals)
                            .Any(eq => !eq);
                    _connectedDevices.Clear();
                    _connectedDevices.AddRange(snapshotList);
                }

                if (listChanged)
                {
                    BindingsUpdated?.Invoke();
                }
            }
            catch { }
        }

        private static bool TryReadHidDeviceInfo(IntPtr hDevice, out RID_DEVICE_INFO info)
        {
            info = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf(typeof(RID_DEVICE_INFO)) };
            uint sz = info.cbSize;
            IntPtr ptr = Marshal.AllocHGlobal((int)sz);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICEINFO, ptr, ref sz) == uint.MaxValue)
                {
                    return false;
                }
                info = (RID_DEVICE_INFO)Marshal.PtrToStructure(ptr, typeof(RID_DEVICE_INFO));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static string ReadDeviceNamePath(IntPtr hDevice)
        {
            uint chars = 0;
            GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            if (chars == 0) return string.Empty;

            IntPtr ptr = Marshal.AllocHGlobal((int)(chars * 2));
            try
            {
                if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, ptr, ref chars) != uint.MaxValue)
                {
                    return (Marshal.PtrToStringUni(ptr) ?? string.Empty).Trim();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return string.Empty;
        }

        private static string ReadHidProductName(string devPath, uint vid, uint pid)
        {
            if (!string.IsNullOrEmpty(devPath))
            {
                IntPtr hf = CreateFileW(devPath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (hf != IntPtr.Zero && hf != new IntPtr(-1))
                {
                    try
                    {
                        byte[] buf = new byte[256];
                        if (HidD_GetProductString(hf, buf, (uint)buf.Length))
                        {
                            string name = Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
                            if (!string.IsNullOrEmpty(name)) return name;
                        }
                    }
                    finally
                    {
                        CloseHandle(hf);
                    }
                }
            }
            return $"HID Controller ({vid:X4}:{pid:X4})";
        }

        private static string FormatDirectInputProductGuid(uint vendorId, uint productId)
        {
            return string.Format("{0:x4}{1:x4}-0000-0000-0000-504944564944", productId & 0xFFFF, vendorId & 0xFFFF);
        }

        private static void LoadPreparsedData(RawDeviceRecord rec)
        {
            try
            {
                uint sz = 0;
                GetRawInputDeviceInfoW(rec.Handle, RIDI_PREPARSEDDATA, IntPtr.Zero, ref sz);
                if (sz == 0) return;

                IntPtr ptr = Marshal.AllocHGlobal((int)sz);
                if (GetRawInputDeviceInfoW(rec.Handle, RIDI_PREPARSEDDATA, ptr, ref sz) == uint.MaxValue)
                {
                    Marshal.FreeHGlobal(ptr);
                    return;
                }

                uint maxButtons = HidP_MaxUsageListLength(HIDP_INPUT, HID_USAGE_PAGE_BUTTON, ptr);
                if (maxButtons == 0) maxButtons = 64;
                rec.PreparsedData = ptr;
                rec.MaxButtonUsages = maxButtons;
            }
            catch { }
        }

        private void ReleaseAllRawDevices()
        {
            foreach (var rec in _rawDevices.Values)
            {
                rec.Dispose();
            }
            _rawDevices.Clear();
        }

        private static bool IsDeviceNeededForBindings(RawDeviceRecord rec, List<PttBinding> enabledBindings)
        {
            if (enabledBindings == null || enabledBindings.Count == 0) return false;
            return enabledBindings.Any(b => b.Kind == "JoystickButton" && BindingMatchesDevice(rec, b));
        }

        private static bool BindingMatchesDevice(RawDeviceRecord rec, PttBinding bind)
        {
            if (!string.IsNullOrEmpty(bind.InstanceGuid) &&
                string.Equals(bind.InstanceGuid, rec.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(bind.ProductGuid) &&
                string.Equals(bind.ProductGuid, rec.ProductGuidStr, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(bind.DeviceName) &&
                string.Equals(bind.DeviceName, rec.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private void OnRawInputMessage(IntPtr hRawInput)
        {
            try
            {
                uint dwSize = 0;
                uint hdrSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dwSize, hdrSize);
                if (dwSize < hdrSize + 8) return;

                IntPtr buffer = Marshal.AllocHGlobal((int)dwSize);
                try
                {
                    if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref dwSize, hdrSize) == uint.MaxValue) return;

                    var hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buffer, typeof(RAWINPUTHEADER));
                    if (hdr.dwType != RIM_TYPEHID) return;
                    if (!_rawDevices.TryGetValue(hdr.hDevice, out var rec) || rec.PreparsedData == IntPtr.Zero) return;

                    int hidOffset = (int)hdrSize;
                    uint dwSizeHid = (uint)Marshal.ReadInt32(buffer, hidOffset);
                    uint dwCount = (uint)Marshal.ReadInt32(buffer, hidOffset + 4);
                    if (dwSizeHid == 0 || dwCount == 0) return;

                    int rawDataStart = hidOffset + 8;
                    if (rawDataStart + (dwSizeHid * dwCount) > dwSize) return;

                    uint cap = rec.MaxButtonUsages > 0 ? rec.MaxButtonUsages : 64;
                    ushort[] usages = new ushort[cap];

                    for (uint r = 0; r < dwCount; r++)
                    {
                        uint usageLen = cap;
                        IntPtr reportPtr = IntPtr.Add(buffer, rawDataStart + (int)(r * dwSizeHid));
                        int status = HidP_GetUsages(
                            HIDP_INPUT,
                            HID_USAGE_PAGE_BUTTON,
                            0,
                            usages,
                            ref usageLen,
                            rec.PreparsedData,
                            reportPtr,
                            dwSizeHid);

                        if (status == HIDP_STATUS_SUCCESS)
                        {
                            rec.PressedButtons.Clear();
                            for (int u = 0; u < usageLen; u++)
                            {
                                if (usages[u] >= 1)
                                {
                                    rec.PressedButtons.Add(usages[u] - 1);
                                }
                            }
                        }
                    }

                    bool captureActive;
                    lock (_stateLock)
                    {
                        captureActive = _capturing;
                    }

                    if (captureActive)
                    {
                        CheckCapturedJoystickButton(rec);
                    }

                    EvaluateActiveBindingsSnapshot();
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { }
        }

        private void BeginCaptureSnapshot()
        {
            var joyBaseline = new Dictionary<IntPtr, HashSet<int>>();
            foreach (var rec in _rawDevices.Values)
            {
                joyBaseline[rec.Handle] = new HashSet<int>(rec.PressedButtons);
            }

            var keyBaseline = new Dictionary<int, bool>();
            foreach (int vk in EnumerateCapturableVirtualKeys())
            {
                keyBaseline[vk] = IsVirtualKeyDown(vk);
            }

            lock (_stateLock)
            {
                _baselineButtons.Clear();
                foreach (var kvp in joyBaseline) _baselineButtons[kvp.Key] = kvp.Value;

                _baselineKeys.Clear();
                foreach (var kvp in keyBaseline) _baselineKeys[kvp.Key] = kvp.Value;

                _captureRequested = false;
                _capturing = true;
            }
        }

        private void OnKeyboardOrCaptureTick()
        {
            bool pttEnabled;
            bool captureActive;
            DateTime captureDeadline;
            bool hasKeyBinds;

            lock (_stateLock)
            {
                pttEnabled = _hardwarePttEnabled;
                captureActive = _capturing;
                captureDeadline = _captureDeadlineUtc;
                hasKeyBinds = _bindings.Any(b => b.Enabled && b.Kind == "KeyboardKey");
            }

            if (captureActive)
            {
                if (DateTime.UtcNow > captureDeadline)
                {
                    EndCaptureTimeout();
                    return;
                }

                if (IsVirtualKeyDown(0x1B))
                {
                    CancelCapture("Capture cancelled (Esc pressed).");
                    return;
                }

                if (CheckCapturedVirtualKey())
                {
                    return;
                }
            }

            if (pttEnabled && hasKeyBinds)
            {
                EvaluateActiveBindingsSnapshot();
            }
        }

        private void EndCaptureTimeout()
        {
            lock (_stateLock)
            {
                _capturing = false;
                _captureRequested = false;
                _baselineButtons.Clear();
                _baselineKeys.Clear();
            }
            SyncRegistrationAndDevices(rescanDevices: false);
            CaptureStatusChanged?.Invoke("Timeout", null, "Timed out waiting for a stick, yoke, or keyboard input.");
        }

        private void CheckCapturedJoystickButton(RawDeviceRecord rec)
        {
            _baselineButtons.TryGetValue(rec.Handle, out var baseline);

            // Release latched baseline buttons once user lifts held switches
            if (baseline != null)
            {
                baseline.RemoveWhere(idx => !rec.PressedButtons.Contains(idx));
            }

            foreach (int btnIndex in rec.PressedButtons)
            {
                if (baseline != null && baseline.Contains(btnIndex)) continue;

                var candidate = new PttBinding
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Kind = "JoystickButton",
                    DeviceName = rec.Name,
                    InstanceGuid = rec.DevicePath,
                    ProductGuid = rec.ProductGuidStr,
                    ButtonIndex = btnIndex,
                    VirtualKeyCode = 0,
                    Enabled = true
                };
                FinishCapturedBinding(candidate);
                return;
            }
        }

        private bool CheckCapturedVirtualKey()
        {
            foreach (int vk in EnumerateCapturableVirtualKeys())
            {
                bool nowDown = IsVirtualKeyDown(vk);
                _baselineKeys.TryGetValue(vk, out bool wasDown);

                if (nowDown && !wasDown)
                {
                    var candidate = new PttBinding
                    {
                        Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                        Kind = "KeyboardKey",
                        DeviceName = GetFriendlyVirtualKeyName(vk),
                        InstanceGuid = string.Empty,
                        ProductGuid = string.Empty,
                        ButtonIndex = -1,
                        VirtualKeyCode = vk,
                        Enabled = true
                    };
                    FinishCapturedBinding(candidate);
                    return true;
                }
                else if (!nowDown && wasDown)
                {
                    _baselineKeys[vk] = false;
                }
            }
            return false;
        }

        private void FinishCapturedBinding(PttBinding candidate)
        {
            PttBinding finalBinding;
            bool duplicate = false;

            lock (_stateLock)
            {
                _capturing = false;
                _captureRequested = false;
                _baselineButtons.Clear();
                _baselineKeys.Clear();

                PttBinding existing = null;
                if (candidate.Kind == "JoystickButton")
                {
                    existing = _bindings.FirstOrDefault(b =>
                        b.Kind == "JoystickButton" &&
                        b.ButtonIndex == candidate.ButtonIndex &&
                        (string.Equals(b.InstanceGuid, candidate.InstanceGuid, StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(b.ProductGuid) && string.Equals(b.ProductGuid, candidate.ProductGuid, StringComparison.OrdinalIgnoreCase))));
                }
                else if (candidate.Kind == "KeyboardKey")
                {
                    existing = _bindings.FirstOrDefault(b =>
                        b.Kind == "KeyboardKey" &&
                        b.VirtualKeyCode == candidate.VirtualKeyCode);
                }

                if (existing != null)
                {
                    existing.Enabled = true;
                    existing.DeviceName = candidate.DeviceName;
                    if (!string.IsNullOrEmpty(candidate.InstanceGuid)) existing.InstanceGuid = candidate.InstanceGuid;
                    finalBinding = CloneBinding(existing);
                    duplicate = true;
                }
                else
                {
                    _bindings.Add(candidate);
                    finalBinding = CloneBinding(candidate);
                }

                SaveBindingsToDiskLocked();
            }

            SyncRegistrationAndDevices(rescanDevices: false);

            string msg = duplicate
                ? $"Updated existing PTT bind: {finalBinding.DisplayLabel}"
                : $"Bound PTT: {finalBinding.DisplayLabel}";

            CaptureStatusChanged?.Invoke("Captured", finalBinding, msg);
            BindingsUpdated?.Invoke();
        }

        private void EvaluateActiveBindingsSnapshot()
        {
            bool pttEnabled;
            List<PttBinding> enabledBindings;
            lock (_stateLock)
            {
                pttEnabled = _hardwarePttEnabled;
                enabledBindings = _bindings.Where(b => b.Enabled).Select(CloneBinding).ToList();
            }

            if (!pttEnabled || enabledBindings.Count == 0)
            {
                UpdatePressedState(new HashSet<string>());
                return;
            }

            var pressedIds = new HashSet<string>();
            foreach (var bind in enabledBindings)
            {
                if (bind.Kind == "KeyboardKey")
                {
                    if (bind.VirtualKeyCode > 0 && IsVirtualKeyDown(bind.VirtualKeyCode))
                    {
                        pressedIds.Add(bind.Id);
                    }
                    continue;
                }

                if (bind.Kind == "JoystickButton" && bind.ButtonIndex >= 0)
                {
                    foreach (var rec in _rawDevices.Values)
                    {
                        if (BindingMatchesDevice(rec, bind) && rec.PressedButtons.Contains(bind.ButtonIndex))
                        {
                            pressedIds.Add(bind.Id);
                            break;
                        }
                    }
                }
            }

            UpdatePressedState(pressedIds);
        }

        private void UpdatePressedState(HashSet<string> pressedIds)
        {
            bool anyPressed = pressedIds.Count > 0;
            bool stateChanged = false;
            bool activeIdsChanged = false;
            List<string> activeList = null;

            lock (_stateLock)
            {
                if (anyPressed != _hardwarePttActive)
                {
                    _hardwarePttActive = anyPressed;
                    stateChanged = true;
                }

                if (!_activeBindingIds.SetEquals(pressedIds))
                {
                    _activeBindingIds.Clear();
                    foreach (var id in pressedIds) _activeBindingIds.Add(id);
                    activeIdsChanged = true;
                }

                if (stateChanged || activeIdsChanged)
                {
                    activeList = _activeBindingIds.ToList();
                }
            }

            if (stateChanged || activeIdsChanged)
            {
                HardwarePttChanged?.Invoke(anyPressed, activeList);
            }
        }

        private static bool IsVirtualKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        private static IEnumerable<int> EnumerateCapturableVirtualKeys()
        {
            for (int vk = 0x05; vk <= 0xFE; vk++)
            {
                if (vk == 0x10 || vk == 0x11 || vk == 0x12 || vk == 0x1B) continue;
                yield return vk;
            }
        }

        private static string GetFriendlyVirtualKeyName(int vk)
        {
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x70 && vk <= 0x87) return $"F{vk - 0x6F}";
            if (vk >= 0x60 && vk <= 0x69) return $"Numpad {vk - 0x60}";

            switch (vk)
            {
                case 0x05: return "Mouse Button 4";
                case 0x06: return "Mouse Button 5";
                case 0x08: return "Backspace";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x13: return "Pause";
                case 0x14: return "Caps Lock";
                case 0x20: return "Space";
                case 0x21: return "Page Up";
                case 0x22: return "Page Down";
                case 0x23: return "End";
                case 0x24: return "Home";
                case 0x25: return "Left Arrow";
                case 0x26: return "Up Arrow";
                case 0x27: return "Right Arrow";
                case 0x28: return "Down Arrow";
                case 0x2D: return "Insert";
                case 0x2E: return "Delete";
                case 0x6A: return "Numpad *";
                case 0x6B: return "Numpad +";
                case 0x6D: return "Numpad -";
                case 0x6E: return "Numpad .";
                case 0x6F: return "Numpad /";
                case 0x90: return "Num Lock";
                case 0x91: return "Scroll Lock";
                case 0xA0: return "Left Shift";
                case 0xA1: return "Right Shift";
                case 0xA2: return "Left Ctrl";
                case 0xA3: return "Right Ctrl";
                case 0xA4: return "Left Alt";
                case 0xA5: return "Right Alt";
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
                default: return $"Key 0x{vk:X2}";
            }
        }

        private void LoadBindingsFromDisk()
        {
            try
            {
                if (!File.Exists(_configPath)) return;
                string json = File.ReadAllText(_configPath)?.Trim();
                if (string.IsNullOrEmpty(json)) return;

                List<PttBinding> loaded = null;
                bool loadedEnabled = true;

                if (json.StartsWith("["))
                {
                    loaded = JsonConvert.DeserializeObject<List<PttBinding>>(json);
                }
                else
                {
                    var cfg = JsonConvert.DeserializeObject<PttConfigFile>(json);
                    if (cfg != null)
                    {
                        loadedEnabled = cfg.HardwarePttEnabled;
                        loaded = cfg.Bindings;
                    }
                }

                lock (_stateLock)
                {
                    _hardwarePttEnabled = loadedEnabled;
                    _bindings.Clear();
                    if (loaded != null)
                    {
                        foreach (var b in loaded)
                        {
                            if (b == null) continue;
                            if (string.IsNullOrEmpty(b.Id)) b.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                            _bindings.Add(b);
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveBindingsToDiskLocked()
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var payload = new PttConfigFile
                {
                    HardwarePttEnabled = _hardwarePttEnabled,
                    Bindings = _bindings
                };
                string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch { }
        }

        private static PttBinding CloneBinding(PttBinding b)
        {
            return new PttBinding
            {
                Id = b.Id,
                Kind = b.Kind,
                DeviceName = b.DeviceName,
                InstanceGuid = b.InstanceGuid,
                ProductGuid = b.ProductGuid,
                ButtonIndex = b.ButtonIndex,
                VirtualKeyCode = b.VirtualKeyCode,
                Enabled = b.Enabled
            };
        }

        public void Dispose()
        {
            PostThreadMessage(WM_APP_EXIT);
            try
            {
                if (_uiThread != null && _uiThread.IsAlive)
                {
                    _uiThread.Join(1000);
                }
            }
            catch { }
            _uiThread = null;
            try { _threadReady.Dispose(); } catch { }
        }
    }
}
