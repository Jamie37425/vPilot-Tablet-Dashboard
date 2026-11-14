using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;
using SharpDX.DirectInput;

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

    public class MultiPttManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private sealed class DeviceHandle : IDisposable
        {
            public DeviceInstance Info { get; set; }
            public Joystick Joystick { get; set; }
            public string InstanceGuidStr { get; set; }
            public string ProductGuidStr { get; set; }

            public void Dispose()
            {
                try { Joystick?.Unacquire(); } catch { }
                try { Joystick?.Dispose(); } catch { }
                Joystick = null;
            }
        }

        private readonly string _configPath;
        private readonly object _stateLock = new object();
        private readonly List<PttBinding> _bindings = new List<PttBinding>();
        private readonly List<ConnectedInputDevice> _connectedDevices = new List<ConnectedInputDevice>();
        private readonly HashSet<string> _activeBindingIds = new HashSet<string>();

        private CancellationTokenSource _cts;
        private Thread _pollThread;
        private bool _hardwarePttActive;

        // Capture mode fields (managed under _stateLock)
        private bool _captureRequested;
        private bool _capturing;
        private DateTime _captureDeadlineUtc;
        private readonly Dictionary<string, bool[]> _baselineButtons = new Dictionary<string, bool[]>();
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
            if (_pollThread != null) return;
            _cts = new CancellationTokenSource();
            _pollThread = new Thread(() => PollLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "vPilotMultiPttPollThread"
            };
            _pollThread.Start();
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
            if (removed) BindingsUpdated?.Invoke();
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
            if (changed) BindingsUpdated?.Invoke();
            return changed;
        }

        private void PollLoop(CancellationToken token)
        {
            DirectInput directInput = null;
            var devices = new Dictionary<string, DeviceHandle>(StringComparer.OrdinalIgnoreCase);
            DateTime lastScanUtc = DateTime.MinValue;

            try
            {
                directInput = new DirectInput();

                while (!token.IsCancellationRequested)
                {
                    bool capturePending;
                    bool captureActive;
                    DateTime captureDeadline;
                    List<PttBinding> enabledBindings;

                    lock (_stateLock)
                    {
                        capturePending = _captureRequested;
                        captureActive = _capturing;
                        captureDeadline = _captureDeadlineUtc;
                        enabledBindings = _bindings.Where(b => b.Enabled).Select(CloneBinding).ToList();
                    }

                    bool rescanDue = capturePending || (DateTime.UtcNow - lastScanUtc).TotalSeconds >= 3.0;
                    if (rescanDue)
                    {
                        RefreshAttachedDevices(directInput, devices);
                        lastScanUtc = DateTime.UtcNow;
                    }

                    if (capturePending)
                    {
                        BeginCaptureSnapshot(devices);
                        captureActive = true;
                    }

                    if (captureActive)
                    {
                        if (DateTime.UtcNow > captureDeadline)
                        {
                            EndCaptureTimeout();
                        }
                        else if (CheckForCapturedInput(devices))
                        {
                            // Reload bindings snapshot after capture completed
                            lock (_stateLock)
                            {
                                enabledBindings = _bindings.Where(b => b.Enabled).Select(CloneBinding).ToList();
                            }
                        }
                    }

                    EvaluateActiveBindings(devices, enabledBindings);

                    Thread.Sleep(25);
                }
            }
            catch (Exception)
            {
                // Ignore loop termination errors during vPilot exit
            }
            finally
            {
                foreach (var handle in devices.Values)
                {
                    handle.Dispose();
                }
                devices.Clear();
                try { directInput?.Dispose(); } catch { }
            }
        }

        private void RefreshAttachedDevices(DirectInput directInput, Dictionary<string, DeviceHandle> devices)
        {
            try
            {
                var attached = directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly);
                var currentGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var snapshotList = new List<ConnectedInputDevice>();

                foreach (var devInstance in attached)
                {
                    string instGuid = devInstance.InstanceGuid.ToString();
                    string prodGuid = devInstance.ProductGuid.ToString();
                    string name = (devInstance.InstanceName ?? "Game Controller").Trim();

                    currentGuids.Add(instGuid);
                    snapshotList.Add(new ConnectedInputDevice
                    {
                        Name = name,
                        InstanceGuid = instGuid,
                        ProductGuid = prodGuid
                    });

                    if (!devices.ContainsKey(instGuid))
                    {
                        try
                        {
                            var joystick = new Joystick(directInput, devInstance.InstanceGuid);
                            try { joystick.Acquire(); } catch { }
                            devices[instGuid] = new DeviceHandle
                            {
                                Info = devInstance,
                                Joystick = joystick,
                                InstanceGuidStr = instGuid,
                                ProductGuidStr = prodGuid
                            };
                        }
                        catch { }
                    }
                }

                var removedKeys = devices.Keys.Where(k => !currentGuids.Contains(k)).ToList();
                foreach (var key in removedKeys)
                {
                    devices[key].Dispose();
                    devices.Remove(key);
                }

                lock (_stateLock)
                {
                    _connectedDevices.Clear();
                    _connectedDevices.AddRange(snapshotList);
                }
            }
            catch { }
        }

        private bool[] ReadJoystickButtons(DeviceHandle handle)
        {
            if (handle?.Joystick == null) return null;
            try
            {
                handle.Joystick.Poll();
                return handle.Joystick.GetCurrentState()?.Buttons;
            }
            catch
            {
                try
                {
                    handle.Joystick.Acquire();
                    handle.Joystick.Poll();
                    return handle.Joystick.GetCurrentState()?.Buttons;
                }
                catch
                {
                    return null;
                }
            }
        }

        private void BeginCaptureSnapshot(Dictionary<string, DeviceHandle> devices)
        {
            var joyBaseline = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in devices)
            {
                var buttons = ReadJoystickButtons(kvp.Value);
                if (buttons != null)
                {
                    joyBaseline[kvp.Key] = (bool[])buttons.Clone();
                }
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

        private void EndCaptureTimeout()
        {
            lock (_stateLock)
            {
                _capturing = false;
                _captureRequested = false;
                _baselineButtons.Clear();
                _baselineKeys.Clear();
            }
            CaptureStatusChanged?.Invoke("Timeout", null, "Timed out waiting for a stick, yoke, or keyboard input.");
        }

        private bool CheckForCapturedInput(Dictionary<string, DeviceHandle> devices)
        {
            // Esc cancels capture cleanly
            if (IsVirtualKeyDown(0x1B))
            {
                CancelCapture("Capture cancelled (Esc pressed).");
                return false;
            }

            // 1. Check all attached DirectInput controllers for a fresh false -> true button press
            foreach (var kvp in devices)
            {
                var buttons = ReadJoystickButtons(kvp.Value);
                if (buttons == null) continue;

                _baselineButtons.TryGetValue(kvp.Key, out var baseBtns);

                for (int i = 0; i < buttons.Length; i++)
                {
                    bool nowDown = buttons[i];
                    bool wasDownAtStart = baseBtns != null && i < baseBtns.Length && baseBtns[i];

                    if (nowDown && !wasDownAtStart)
                    {
                        var binding = new PttBinding
                        {
                            Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                            Kind = "JoystickButton",
                            DeviceName = (kvp.Value.Info.InstanceName ?? "Controller").Trim(),
                            InstanceGuid = kvp.Value.InstanceGuidStr,
                            ProductGuid = kvp.Value.ProductGuidStr,
                            ButtonIndex = i,
                            VirtualKeyCode = 0,
                            Enabled = true
                        };

                        FinishCapturedBinding(binding);
                        return true;
                    }
                    else if (!nowDown && wasDownAtStart && baseBtns != null && i < baseBtns.Length)
                    {
                        // Once a previously held switch is released, allow re-pressing it
                        baseBtns[i] = false;
                    }
                }
            }

            // 2. Check keyboard / extra mouse keys for a fresh false -> true transition
            foreach (int vk in EnumerateCapturableVirtualKeys())
            {
                bool nowDown = IsVirtualKeyDown(vk);
                _baselineKeys.TryGetValue(vk, out bool wasDownAtStart);

                if (nowDown && !wasDownAtStart)
                {
                    var binding = new PttBinding
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

                    FinishCapturedBinding(binding);
                    return true;
                }
                else if (!nowDown && wasDownAtStart)
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

            string msg = duplicate
                ? $"Updated existing PTT bind: {finalBinding.DisplayLabel}"
                : $"Bound PTT: {finalBinding.DisplayLabel}";

            CaptureStatusChanged?.Invoke("Captured", finalBinding, msg);
            BindingsUpdated?.Invoke();
        }

        private void EvaluateActiveBindings(Dictionary<string, DeviceHandle> devices, List<PttBinding> enabledBindings)
        {
            if (enabledBindings.Count == 0)
            {
                UpdatePressedState(new HashSet<string>());
                return;
            }

            // Poll only devices that match an enabled binding (unless capture mode already polled them)
            var cachedButtons = new Dictionary<string, bool[]>(StringComparer.OrdinalIgnoreCase);
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
                    DeviceHandle handle = FindMatchingDevice(devices, bind);
                    if (handle == null) continue;

                    if (!cachedButtons.TryGetValue(handle.InstanceGuidStr, out var buttons))
                    {
                        buttons = ReadJoystickButtons(handle);
                        cachedButtons[handle.InstanceGuidStr] = buttons;
                    }

                    if (buttons != null && bind.ButtonIndex < buttons.Length && buttons[bind.ButtonIndex])
                    {
                        pressedIds.Add(bind.Id);
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

        private static DeviceHandle FindMatchingDevice(Dictionary<string, DeviceHandle> devices, PttBinding bind)
        {
            if (!string.IsNullOrEmpty(bind.InstanceGuid) && devices.TryGetValue(bind.InstanceGuid, out var exact))
            {
                return exact;
            }

            if (!string.IsNullOrEmpty(bind.ProductGuid))
            {
                return devices.Values.FirstOrDefault(d =>
                    string.Equals(d.ProductGuidStr, bind.ProductGuid, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        private static bool IsVirtualKeyDown(int vKey)
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }

        private static IEnumerable<int> EnumerateCapturableVirtualKeys()
        {
            // Skip mouse left (0x01), mouse right (0x02), middle (0x04), generic Shift/Ctrl/Alt (0x10-0x12) so L/R keys capture specifically
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
                string json = File.ReadAllText(_configPath);
                var loaded = JsonConvert.DeserializeObject<List<PttBinding>>(json);
                if (loaded != null)
                {
                    lock (_stateLock)
                    {
                        _bindings.Clear();
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
                string json = JsonConvert.SerializeObject(_bindings, Formatting.Indented);
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
            try { _cts?.Cancel(); } catch { }
            try
            {
                if (_pollThread != null && _pollThread.IsAlive)
                {
                    _pollThread.Join(500);
                }
            }
            catch { }
            _pollThread = null;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
    }
}
