using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;
using RossCarlson.Vatsim.Vpilot.Plugins.Exceptions;
using Fleck;
using Microsoft.FlightSimulator.SimConnect;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using bbv.Common.EventBroker;
using bbv.Common.EventBroker.Handlers;

namespace vPilotTabletMirror
{
    public class TabletPlugin : IPlugin
    {
        private WebSocketServer _server;
        private readonly List<IWebSocketConnection> _sockets = new List<IWebSocketConnection>();
        private readonly object _socketLock = new object();
        private IBroker _broker;
        private IEventBroker _eventBroker;
        private System.Threading.Timer _pollTimer;
        private static readonly HttpClient _httpClient = new HttpClient();

        private readonly Dictionary<string, ControllerInfo> _localControllers = new Dictionary<string, ControllerInfo>();
        private readonly List<object> _messageHistory = new List<object>();

        // Radio & RX/TX State
        private string _com1Freq = "122.800";
        private string _com2Freq = "121.500";
        private bool _com1Tx = true;
        private bool _com2Tx = false;
        private bool _com1Rx = true;
        private bool _com2Rx = true;
        private bool _com1TxActive = false;
        private bool _com2TxActive = false;
        private bool _com1RxActive = false;
        private bool _com2RxActive = false;
        private bool _isRxActive = false;
        private bool _pttActive = false;
        private string _myCallsign = "";

        // Multi-PTT Fields
        private MultiPttManager _multiPttManager;
        private readonly object _pttLock = new object();
        private bool _hardwarePttActive = false;
        private bool _dashboardPttActive = false;
        private bool _lastSentPluginPtt = false;

        // SimConnect Fields
        private SimConnect _simConnect = null;
        private EventWaitHandle _simConnectEvent = new AutoResetEvent(false);
        private DateTime _lastSimConnectAttempt = DateTime.MinValue;

        private enum DEFINITIONS { RadioData }
        private enum REQUESTS { RadioRequest }
        private enum EVENTS { Com1TransmitSelect, Com2TransmitSelect, ComReceiveAllToggle }
        private enum GROUP_ID { DEFAULT }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct RadioDataStruct
        {
            public double Com1;
            public double Com2;
        }

        public string Name => "Tablet Web Mirror";
        public string Author => "Jamie";
        public string Version => "9.8.0";

        public TabletPlugin()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string folderPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string assemblyPath = Path.Combine(folderPath, new AssemblyName(args.Name).Name + ".dll");
                if (File.Exists(assemblyPath)) return Assembly.LoadFrom(assemblyPath);
                return null;
            };
        }

        // Built-in Dashboard HTTP server fields
        private TcpListener _httpServer = null;
        private CancellationTokenSource _httpCts = null;

        public void Initialize(IBroker broker)
        {
            _broker = broker;

            try
            {
                _broker.SessionEnded += (s, e) => Shutdown();
            }
            catch (Exception) { }

            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
            }
            catch (Exception) { }

            _broker.ControllerAdded += OnControllerAdded;
            _broker.ControllerDeleted += OnControllerDeleted;
            _broker.RadioMessageReceived += OnRadioMessageReceived;
            _broker.PrivateMessageReceived += OnPrivateMessageReceived;
            _broker.BroadcastMessageReceived += OnBroadcastMessageReceived;
            _broker.MetarReceived += OnMetarReceived;
            _broker.AtisReceived += OnAtisReceived;
            _broker.NetworkConnected += OnNetworkConnected;
            _broker.NetworkDisconnected += OnNetworkDisconnected;

            _myCallsign = GetEffectiveCallsign();

            HookEventBroker();

            // Auto-launch the dashboard web server silently in the background
            StartDashboardServer();

            // Initialize Multi-Device & Multi-Keybind PTT Manager
            try
            {
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vPilot", "Plugins");
                string pttConfigPath = Path.Combine(pluginDir, "ptt_bindings.json");
                _multiPttManager = new MultiPttManager(pttConfigPath);
                _multiPttManager.HardwarePttChanged += (pressed, activeIds) =>
                {
                    ApplyPttState(hardwarePressed: pressed, dashboardPressed: null);
                    BroadcastPttBindings();
                };
                _multiPttManager.BindingsUpdated += () => BroadcastPttBindings();
                _multiPttManager.CaptureStatusChanged += (status, binding, msg) => BroadcastPttCaptureStatus(status, binding, msg);
                _multiPttManager.Start();
            }
            catch (Exception) { }

            FleckLog.Level = LogLevel.Info;
            // Using port 9090 to avoid potential local conflicts with MobiFlight
            _server = new WebSocketServer("ws://0.0.0.0:9090");
            _server.RestartAfterListenError = true;

            _server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    lock (_socketLock)
                    {
                        _sockets.Add(socket);
                    }
                    _ = Task.Run(async () => await SendInitialData(socket));
                };

                socket.OnClose = () =>
                {
                    lock (_socketLock)
                    {
                        _sockets.Remove(socket);
                    }
                };

                socket.OnMessage = message =>
                {
                    HandleWebSocketMessage(message, socket);
                };
            });

            _pollTimer = new System.Threading.Timer(async _ =>
            {
                PollSimConnect();
                await BroadcastStates();
            }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }

        private void HookEventBroker()
        {
            try
            {
                if (_broker == null) return;
                var brokerType = _broker.GetType();
                var fields = brokerType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    if (f.FieldType.FullName.Contains("IEventBroker") || typeof(IEventBroker).IsAssignableFrom(f.FieldType))
                    {
                        _eventBroker = f.GetValue(_broker) as IEventBroker;
                        break;
                    }
                }

                if (_eventBroker != null)
                {
                    _eventBroker.Register(this);
                }
            }
            catch (Exception) { }
        }

        #region vPilot EventBroker Subscriptions

        [EventSubscription("RadioStackStateChanged", typeof(Publisher))]
        public void OnRadioStackStateChanged(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                var state = GetPropOrFieldValue<object>(e, "RadioStackState");
                if (state != null)
                {
                    bool c1Tx = GetPropOrFieldValue<bool>(state, "Com1TransmitEnabled", false);
                    bool c2Tx = GetPropOrFieldValue<bool>(state, "Com2TransmitEnabled", false);

                    if (c1Tx && !c2Tx)
                    {
                        _com1Tx = true;
                        _com2Tx = false;
                        _com2TxActive = false;
                    }
                    else if (c2Tx && !c1Tx)
                    {
                        _com1Tx = false;
                        _com2Tx = true;
                        _com1TxActive = false;
                    }
                    else if (c1Tx && c2Tx)
                    {
                        // Prioritize COM1 unless COM2 was explicitly selected
                        if (!_com2Tx)
                        {
                            _com1Tx = true;
                            _com2Tx = false;
                            _com2TxActive = false;
                        }
                    }

                    _com1Rx = GetPropOrFieldValue<bool>(state, "Com1ReceiveEnabled", _com1Rx);
                    _com2Rx = GetPropOrFieldValue<bool>(state, "Com2ReceiveEnabled", _com2Rx);

                    var f1 = GetPropOrFieldValue<uint>(state, "Com1ActiveFrequency", 0);
                    if (f1 > 0) _com1Freq = FormatFrequencyUint(f1);

                    var f2 = GetPropOrFieldValue<uint>(state, "Com2ActiveFrequency", 0);
                    if (f2 > 0) _com2Freq = FormatFrequencyUint(f2);

                    BroadcastRadioStateQuick();
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("CommandSubmitted", typeof(Publisher))]
        public void OnCommandSubmitted(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                var cmd = GetPropOrFieldValue<object>(e, "Command");
                if (cmd != null && cmd.GetType().Name == "SetActiveComRadioCommand")
                {
                    byte index = GetPropOrFieldValue<byte>(cmd, "Index", (byte)0);
                    if (index == 1)
                    {
                        _com1Tx = true;
                        _com2Tx = false;
                        _com2TxActive = false;
                        BroadcastRadioStateQuick();
                    }
                    else if (index == 2)
                    {
                        _com1Tx = false;
                        _com2Tx = true;
                        _com1TxActive = false;
                        BroadcastRadioStateQuick();
                    }
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("ComRadioTransmittingChanged", typeof(Publisher))]
        public void OnComRadioTransmittingChanged(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                int radio = GetPropOrFieldValue<int>(e, "Radio", 0);
                bool transmitting = GetPropOrFieldValue<bool>(e, "TxRx", false);

                if (radio == 1)
                {
                    _com1TxActive = transmitting;
                    if (transmitting)
                    {
                        _com2TxActive = false;
                        _com1Tx = true;
                        _com2Tx = false;
                    }
                }
                else if (radio == 2)
                {
                    _com2TxActive = transmitting;
                    if (transmitting)
                    {
                        _com1TxActive = false;
                        _com2Tx = true;
                        _com1Tx = false;
                    }
                }

                BroadcastRadioStateQuick();
            }
            catch (Exception) { }
        }

        [EventSubscription("ComRadioReceivingChanged", typeof(Publisher))]
        public void OnComRadioReceivingChanged(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                int radio = GetPropOrFieldValue<int>(e, "Radio", 0);
                bool receiving = GetPropOrFieldValue<bool>(e, "TxRx", false);

                if (radio == 1) _com1RxActive = receiving;
                else if (radio == 2) _com2RxActive = receiving;

                BroadcastRadioStateQuick();
            }
            catch (Exception) { }
        }

        [EventSubscription("PushToTalkStateChanged", typeof(Publisher))]
        public void OnPushToTalkStateChanged(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                _pttActive = GetPropOrFieldValue<bool>(e, "Down", false);
                if (!_pttActive)
                {
                    _com1TxActive = false;
                    _com2TxActive = false;
                }
                else
                {
                    // Activate ONLY the selected transmit radio, never both
                    if (_com2Tx)
                    {
                        _com2TxActive = true;
                        _com1TxActive = false;
                    }
                    else
                    {
                        _com1TxActive = true;
                        _com2TxActive = false;
                    }
                }
                BroadcastRadioStateQuick();
            }
            catch (Exception) { }
        }

        [EventSubscription("NotificationPosted", typeof(Publisher))]
        public void OnNotificationPosted(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string message = GetPropOrFieldValue<string>(e, "Message");
                if (string.IsNullOrWhiteSpace(message)) return;

                string typeName = GetPropOrFieldValue<object>(e, "Type")?.ToString() ?? "Info";
                string severity = "Info";
                if (string.Equals(typeName, "Error", StringComparison.OrdinalIgnoreCase)) severity = "Error";
                else if (string.Equals(typeName, "Warning", StringComparison.OrdinalIgnoreCase)) severity = "Warning";

                BroadcastSystemNotification(severity, message);
            }
            catch (Exception) { }
        }

        [EventSubscription("CommandValidationFailed", typeof(Publisher))]
        public void OnCommandValidationFailed(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string err = GetPropOrFieldValue<string>(e, "ErrorMessage");
                if (!string.IsNullOrWhiteSpace(err))
                {
                    BroadcastSystemNotification("Error", err);
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("WallopSent", typeof(Publisher))]
        public void OnWallopSent(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string callsign = GetPropOrFieldValue<string>(e, "OurCallsign");
                string msg = GetPropOrFieldValue<string>(e, "Message");
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    string prefix = string.IsNullOrEmpty(callsign) ? "[WALLOP]" : $"[WALLOP {callsign}]";
                    BroadcastSystemNotification("Info", $"{prefix} {msg}");
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("MetarReceived", typeof(Publisher))]
        public void OnEventBrokerMetarReceived(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string metar = GetPropOrFieldValue<string>(e, "Data");
                if (string.IsNullOrWhiteSpace(metar)) metar = GetPropOrFieldValue<string>(e, "Metar");
                if (string.IsNullOrWhiteSpace(metar)) metar = GetPropOrFieldValue<string>(e, "Message");
                if (!string.IsNullOrWhiteSpace(metar))
                {
                    BroadcastSystemNotification("Success", metar.Trim());
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("RequestedAtisReceived", typeof(Publisher))]
        public void OnEventBrokerRequestedAtisReceived(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string from = GetPropOrFieldValue<string>(e, "From");
                var lines = GetPropOrFieldValue<IEnumerable>(e, "Lines");
                PublishAtisLines(from, lines);
            }
            catch (Exception) { }
        }

        [EventSubscription("ServerMessageReceived", typeof(Publisher))]
        public void OnEventBrokerServerMessageReceived(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string from = GetPropOrFieldValue<string>(e, "From");
                string msg = GetPropOrFieldValue<string>(e, "Data");
                if (string.IsNullOrWhiteSpace(msg)) msg = GetPropOrFieldValue<string>(e, "Message");
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    string tag = string.IsNullOrWhiteSpace(from) ? "SERVER" : from.Trim().ToUpperInvariant();
                    BroadcastSystemNotification("Info", $"{tag}: {msg.Trim()}");
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("BroadcastMessageReceived", typeof(Publisher))]
        public void OnEventBrokerBroadcastMessageReceived(object sender, EventArgs e)
        {
            try
            {
                if (e == null) return;
                string from = GetPropOrFieldValue<string>(e, "From");
                string msg = GetPropOrFieldValue<string>(e, "Data");
                if (string.IsNullOrWhiteSpace(msg)) msg = GetPropOrFieldValue<string>(e, "Message");
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    string tag = string.IsNullOrWhiteSpace(from) ? "SERVER" : from.Trim().ToUpperInvariant();
                    BroadcastSystemNotification("Warning", $"[BROADCAST {tag}] {msg.Trim()}");
                }
            }
            catch (Exception) { }
        }

        #endregion

        private static string FormatFrequencyUint(uint freq)
        {
            if (freq >= 100000000)
            {
                return (freq / 1000000.0m).ToString("0.000");
            }
            if (freq >= 100000)
            {
                return (freq / 1000.0m).ToString("0.000");
            }
            if (freq >= 100)
            {
                return freq.ToString("0.000");
            }
            return freq.ToString();
        }

        private static T GetPropOrFieldValue<T>(object target, string name, T defaultValue = default)
        {
            if (target == null) return defaultValue;
            try
            {
                var type = target.GetType();
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null)
                {
                    var val = prop.GetValue(target);
                    if (val != null)
                    {
                        if (typeof(T).IsAssignableFrom(val.GetType())) return (T)val;
                        return (T)Convert.ChangeType(val, typeof(T));
                    }
                }
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    var val = field.GetValue(target);
                    if (val != null)
                    {
                        if (typeof(T).IsAssignableFrom(val.GetType())) return (T)val;
                        return (T)Convert.ChangeType(val, typeof(T));
                    }
                }
            }
            catch { }
            return defaultValue;
        }

        private void HandleWebSocketMessage(string message, IWebSocketConnection socket = null)
        {
            try
            {
                var command = JsonConvert.DeserializeObject<Dictionary<string, object>>(message);
                if (command == null || !command.TryGetValue("Action", out var actionObj)) return;
                string action = actionObj?.ToString();

                if (action == "set_ptt")
                {
                    if (command.TryGetValue("Pressed", out var pressedObj) && bool.TryParse(pressedObj?.ToString(), out bool pressed))
                    {
                        ApplyPttState(hardwarePressed: null, dashboardPressed: pressed);
                    }
                }
                else if (action == "get_ptt_bindings")
                {
                    SendPttBindingsToSocket(socket);
                }
                else if (action == "start_ptt_capture")
                {
                    _multiPttManager?.StartCapture(15000);
                }
                else if (action == "cancel_ptt_capture")
                {
                    _multiPttManager?.CancelCapture();
                }
                else if (action == "remove_ptt_binding")
                {
                    if (command.TryGetValue("Id", out var idObj))
                    {
                        _multiPttManager?.RemoveBinding(idObj?.ToString());
                    }
                }
                else if (action == "toggle_ptt_binding")
                {
                    if (command.TryGetValue("Id", out var idObj) &&
                        command.TryGetValue("Enabled", out var enObj) &&
                        bool.TryParse(enObj?.ToString(), out bool enabled))
                    {
                        _multiPttManager?.SetBindingEnabled(idObj?.ToString(), enabled);
                    }
                }
                else if (action == "get_state")
                {
                    string effectiveCs = GetEffectiveCallsign();
                    if (!string.IsNullOrEmpty(effectiveCs)) _myCallsign = effectiveCs;
                    BroadcastRadioStateQuick();
                    BroadcastCallsign();
                }
                else if (action == "set_active_com")
                {
                    if (command.TryGetValue("Radio", out var radioObj) && int.TryParse(radioObj?.ToString(), out int radio))
                    {
                        SetActiveCom(radio);
                    }
                }
                else if (action == "toggle_rx")
                {
                    if (command.TryGetValue("Radio", out var radioObj) && int.TryParse(radioObj?.ToString(), out int radio))
                    {
                        ToggleRx(radio);
                    }
                }
                else if (action == "send_radio_message")
                {
                    if (command.TryGetValue("Message", out var msgObj))
                    {
                        string text = msgObj?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            SendRadioMessageFromDashboard(text, socket);
                        }
                    }
                }
                else if (action == "send_command")
                {
                    if (command.TryGetValue("Command", out var cmdObj))
                    {
                        string text = cmdObj?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(text))
                        {
                            ExecuteDotCommandFromDashboard(text, socket);
                        }
                    }
                }
                else if (action == "send_private_message")
                {
                    if (command.TryGetValue("To", out var toObj) && command.TryGetValue("Message", out var msgObj))
                    {
                        string to = toObj?.ToString()?.Trim().ToUpper();
                        string text = msgObj?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(to) && !string.IsNullOrEmpty(text))
                        {
                            SendPrivateMessageFromDashboard(to, text, socket);
                        }
                    }
                }
                else if (action == "clear_history")
                {
                    lock (_messageHistory)
                    {
                        _messageHistory.Clear();
                    }
                    var clearData = new { Type = "ClearHistory" };
                    BroadcastJson(JsonConvert.SerializeObject(clearData));
                }
            }
            catch (Exception) { }
        }

        private void ApplyPttState(bool? hardwarePressed, bool? dashboardPressed)
        {
            bool combined;
            bool shouldNotifyBroker;

            lock (_pttLock)
            {
                if (hardwarePressed.HasValue) _hardwarePttActive = hardwarePressed.Value;
                if (dashboardPressed.HasValue) _dashboardPttActive = dashboardPressed.Value;

                combined = _hardwarePttActive || _dashboardPttActive;
                shouldNotifyBroker = combined != _lastSentPluginPtt;
                if (shouldNotifyBroker)
                {
                    _lastSentPluginPtt = combined;
                    _pttActive = combined;
                    if (combined)
                    {
                        if (_com2Tx)
                        {
                            _com2TxActive = true;
                            _com1TxActive = false;
                        }
                        else
                        {
                            _com1TxActive = true;
                            _com2TxActive = false;
                        }
                    }
                    else
                    {
                        _com1TxActive = false;
                        _com2TxActive = false;
                    }
                }
            }

            if (shouldNotifyBroker)
            {
                try { _broker?.SetPtt(combined); } catch { }
                BroadcastRadioStateQuick();
            }
        }

        private object GetPttBindingsPayload()
        {
            var manager = _multiPttManager;
            var bindings = manager != null
                ? manager.GetBindingsSnapshot().Select(b => new
                {
                    b.Id,
                    b.Kind,
                    b.DeviceName,
                    b.InstanceGuid,
                    b.ProductGuid,
                    b.ButtonIndex,
                    b.VirtualKeyCode,
                    b.Enabled,
                    b.DisplayLabel
                }).ToList()
                : new object[0].Select(_ => new
                {
                    Id = "",
                    Kind = "",
                    DeviceName = "",
                    InstanceGuid = "",
                    ProductGuid = "",
                    ButtonIndex = 0,
                    VirtualKeyCode = 0,
                    Enabled = true,
                    DisplayLabel = ""
                }).ToList();

            var devices = manager?.GetConnectedDevicesSnapshot() ?? new List<ConnectedInputDevice>();
            var activeIds = manager?.GetActiveBindingIdsSnapshot() ?? new List<string>();

            return new
            {
                Type = "PttBindings",
                Bindings = bindings,
                Devices = devices,
                ActiveIds = activeIds,
                IsCapturing = manager?.IsCapturing ?? false
            };
        }

        private void SendPttBindingsToSocket(IWebSocketConnection socket)
        {
            try
            {
                string json = JsonConvert.SerializeObject(GetPttBindingsPayload());
                if (socket != null && socket.IsAvailable)
                {
                    socket.Send(json);
                }
            }
            catch { }
        }

        private void BroadcastPttBindings()
        {
            try
            {
                BroadcastJson(JsonConvert.SerializeObject(GetPttBindingsPayload()));
            }
            catch { }
        }

        private void BroadcastPttCaptureStatus(string status, PttBinding binding, string message)
        {
            try
            {
                var payload = new
                {
                    Type = "PttCaptureStatus",
                    Status = status,
                    Message = message,
                    Binding = binding != null ? new
                    {
                        binding.Id,
                        binding.Kind,
                        binding.DeviceName,
                        binding.ButtonIndex,
                        binding.VirtualKeyCode,
                        binding.Enabled,
                        binding.DisplayLabel
                    } : null
                };
                BroadcastJson(JsonConvert.SerializeObject(payload));
            }
            catch { }
        }

        private MethodInfo _cachedCommandParserMethod;
        private bool _searchedCommandParser;

        private void SendRadioMessageFromDashboard(string text, IWebSocketConnection socket)
        {
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith("."))
            {
                ExecuteDotCommandFromDashboard(text.Trim(), socket);
                return;
            }

            try
            {
                _broker.SendRadioMessage(text);

                if (string.IsNullOrEmpty(_myCallsign)) _myCallsign = GetEffectiveCallsign();
                string sender = string.IsNullOrEmpty(_myCallsign) ? "You" : _myCallsign;
                string freq = _com2Tx ? _com2Freq : _com1Freq;

                var msgData = new
                {
                    Type = "RadioTextMessage",
                    Sender = sender,
                    Frequency = freq,
                    Message = text,
                    IsOutgoing = true,
                    TimeStr = $"[{DateTime.Now:HH:mm:ss}]"
                };

                RecordAndBroadcastMessage(msgData);
            }
            catch (NotConnectedException)
            {
                SendErrorToClient(socket, "General", "Cannot send radio message: vPilot is not connected to VATSIM.");
            }
            catch (NotTransmittingException)
            {
                SendErrorToClient(socket, "General", "Cannot send radio message: Neither COM1 nor COM2 is set to TX (or avionics off).");
            }
            catch (Exception ex)
            {
                SendErrorToClient(socket, "General", $"Failed to send radio message: {ex.Message}");
            }
        }

        private void ExecuteDotCommandFromDashboard(string rawCommand, IWebSocketConnection socket)
        {
            string cmdText = (rawCommand ?? "").Trim();
            if (string.IsNullOrEmpty(cmdText)) return;

            if (string.Equals(cmdText, ".clear", StringComparison.OrdinalIgnoreCase))
            {
                lock (_messageHistory)
                {
                    _messageHistory.Clear();
                }
                BroadcastJson(JsonConvert.SerializeObject(new { Type = "ClearHistory" }));
                return;
            }

            // Handle .msg / .chat / .message consistently across dashboard tabs and vPilot
            var pmMatch = System.Text.RegularExpressions.Regex.Match(
                cmdText,
                @"^\.(?:msg|message|chat)(?:\s+(\S+)(?:\s+(.+))?)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (pmMatch.Success)
            {
                string targetCallsign = pmMatch.Groups[1].Value.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(targetCallsign))
                {
                    SendErrorToClient(socket, "General", "Usage: .msg CALLSIGN [message]");
                    return;
                }

                string pmBody = pmMatch.Groups[2].Value.Trim();
                BroadcastJson(JsonConvert.SerializeObject(new { Type = "OpenChatTab", Callsign = targetCallsign }));

                if (!string.IsNullOrEmpty(pmBody))
                {
                    SendPrivateMessageFromDashboard(targetCallsign, pmBody, socket);
                }
                else
                {
                    TryFireVpilotCommand(BuildKnownCommandFallback(GetVpilotAssembly(), $".chat {targetCallsign}"), socket);
                }
                return;
            }

            try
            {
                var vpilotAssembly = GetVpilotAssembly();
                if (vpilotAssembly == null || _eventBroker == null)
                {
                    SendErrorToClient(socket, "General", "vPilot command dispatcher is not ready.");
                    return;
                }

                object parsedCmd = null;
                var parserMethod = ResolveVpilotCommandParser(vpilotAssembly);
                if (parserMethod != null)
                {
                    try
                    {
                        parsedCmd = parserMethod.Invoke(null, new object[] { cmdText });
                    }
                    catch (TargetInvocationException tie) when (tie.InnerException != null)
                    {
                        SendErrorToClient(socket, "General", tie.InnerException.Message);
                        return;
                    }
                }
                else
                {
                    parsedCmd = BuildKnownCommandFallback(vpilotAssembly, cmdText);
                    if (parsedCmd == null)
                    {
                        SendErrorToClient(socket, "General", $"Unsupported or unknown command: {cmdText.Split(' ')[0]}");
                        return;
                    }
                }

                if (parsedCmd == null)
                {
                    SendErrorToClient(socket, "General", $"Unknown command: {cmdText}");
                    return;
                }

                BroadcastSystemNotification("Command", $"> {cmdText}");
                TryFireVpilotCommand(parsedCmd, socket);
            }
            catch (Exception ex)
            {
                SendErrorToClient(socket, "General", $"Command error: {ex.Message}");
            }
        }

        private Assembly GetVpilotAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "vPilot");
        }

        private MethodInfo ResolveVpilotCommandParser(Assembly vpilotAssembly)
        {
            if (_searchedCommandParser) return _cachedCommandParserMethod;
            _searchedCommandParser = true;

            try
            {
                var commandBaseType = vpilotAssembly?.GetType("RossCarlson.Vatsim.Vpilot.Commands.Commands.Command");
                if (commandBaseType == null) return null;

                var gaMethod = vpilotAssembly.GetType("ga")?.GetMethod(
                    "b",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string) },
                    null);

                if (gaMethod != null && commandBaseType.IsAssignableFrom(gaMethod.ReturnType))
                {
                    _cachedCommandParserMethod = gaMethod;
                    return _cachedCommandParserMethod;
                }

                foreach (var type in vpilotAssembly.GetTypes())
                {
                    foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        var parameters = m.GetParameters();
                        if (parameters.Length == 1 &&
                            parameters[0].ParameterType == typeof(string) &&
                            commandBaseType.IsAssignableFrom(m.ReturnType))
                        {
                            _cachedCommandParserMethod = m;
                            return _cachedCommandParserMethod;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        private object BuildKnownCommandFallback(Assembly vpilotAssembly, string cmdText)
        {
            try
            {
                if (vpilotAssembly == null || string.IsNullOrWhiteSpace(cmdText)) return null;
                var parts = cmdText.Trim().Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                string verb = parts[0].ToLowerInvariant();
                string arg = parts.Length > 1 ? parts[1].Trim() : "";

                if (verb == ".ctaf" && !string.IsNullOrEmpty(arg))
                    return CreateVpilotCommand(vpilotAssembly, "GetCtafFrequencyCommand", arg.ToUpperInvariant());
                if ((verb == ".wx" || verb == ".metar") && !string.IsNullOrEmpty(arg))
                    return CreateVpilotCommand(vpilotAssembly, "RequestMetarCommand", arg.ToUpperInvariant());
                if (verb == ".atis" && !string.IsNullOrEmpty(arg))
                    return CreateVpilotCommand(vpilotAssembly, "RequestAtisCommand", arg.ToUpperInvariant());
                if (verb == ".chat" && !string.IsNullOrEmpty(arg))
                    return CreateVpilotCommand(vpilotAssembly, "StartChatSessionCommand", arg.ToUpperInvariant());
                if (verb == ".wallop" && !string.IsNullOrEmpty(arg))
                    return CreateVpilotCommand(vpilotAssembly, "SendWallopCommand", arg);
                if (verb == ".tune" && !string.IsNullOrEmpty(arg))
                    return CreateVpilotCommand(vpilotAssembly, "TuneControllerFrequencyCommand", arg.ToUpperInvariant());
            }
            catch { }

            return null;
        }

        private object CreateVpilotCommand(Assembly vpilotAssembly, string shortTypeName, params object[] ctorArgs)
        {
            var t = vpilotAssembly?.GetType($"RossCarlson.Vatsim.Vpilot.Commands.Commands.{shortTypeName}");
            return t != null ? Activator.CreateInstance(t, ctorArgs) : null;
        }

        private void TryFireVpilotCommand(object commandInstance, IWebSocketConnection socket)
        {
            if (commandInstance == null || _eventBroker == null) return;
            try
            {
                var vpilotAssembly = GetVpilotAssembly();
                var cmdArgsType = vpilotAssembly?.GetType("RossCarlson.Vatsim.Vpilot.Events.Arguments.CommandSubmittedEventArgs");
                if (cmdArgsType == null) return;
                var args = Activator.CreateInstance(cmdArgsType, new[] { commandInstance });
                _eventBroker.Fire("CommandSubmitted", this, HandlerRestriction.None, this, (EventArgs)args);
            }
            catch (Exception ex)
            {
                SendErrorToClient(socket, "General", $"Failed to execute command: {ex.Message}");
            }
        }

        private readonly object _recentSysLock = new object();
        private readonly Dictionary<string, DateTime> _recentSystemMsgs = new Dictionary<string, DateTime>();

        private void BroadcastSystemNotification(string severity, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string cleanSeverity = string.IsNullOrEmpty(severity) ? "Info" : severity;
            string cleanMessage = message.Trim();

            if (!string.Equals(cleanSeverity, "Command", StringComparison.OrdinalIgnoreCase))
            {
                string key = $"{cleanSeverity}:{cleanMessage}";
                DateTime now = DateTime.UtcNow;
                lock (_recentSysLock)
                {
                    if (_recentSystemMsgs.TryGetValue(key, out var lastSeen) && (now - lastSeen).TotalSeconds < 2.0)
                    {
                        return;
                    }
                    _recentSystemMsgs[key] = now;
                    if (_recentSystemMsgs.Count > 50)
                    {
                        var stale = _recentSystemMsgs.Where(kv => (now - kv.Value).TotalSeconds > 5.0).Select(kv => kv.Key).ToList();
                        foreach (var k in stale) _recentSystemMsgs.Remove(k);
                    }
                }
            }

            var msgData = new
            {
                Type = "SystemNotification",
                Severity = cleanSeverity,
                Message = cleanMessage,
                TimeStr = $"[{DateTime.Now:HH:mm:ss}]"
            };
            RecordAndBroadcastMessage(msgData);
        }

        private void SendPrivateMessageFromDashboard(string to, string text, IWebSocketConnection socket)
        {
            try
            {
                _broker.SendPrivateMessage(to, text);

                if (string.IsNullOrEmpty(_myCallsign)) _myCallsign = GetEffectiveCallsign();
                string sender = string.IsNullOrEmpty(_myCallsign) ? "You" : _myCallsign;

                var pmData = new
                {
                    Type = "PrivateMessage",
                    Tab = to,
                    Sender = sender,
                    Recipient = to,
                    Message = text,
                    IsOutgoing = true,
                    TimeStr = $"[{DateTime.Now:HH:mm:ss}]"
                };

                RecordAndBroadcastMessage(pmData);
            }
            catch (NotConnectedException)
            {
                SendErrorToClient(socket, to, "Cannot send private message: vPilot is not connected to VATSIM.");
            }
            catch (Exception ex)
            {
                SendErrorToClient(socket, to, $"Failed to send private message: {ex.Message}");
            }
        }

        private void RecordAndBroadcastMessage(object msgData)
        {
            lock (_messageHistory)
            {
                _messageHistory.Add(msgData);
                if (_messageHistory.Count > 100) _messageHistory.RemoveAt(0);
            }
            BroadcastJson(JsonConvert.SerializeObject(msgData));
        }

        private void SendErrorToClient(IWebSocketConnection socket, string tab, string errorMessage)
        {
            string json = JsonConvert.SerializeObject(new
            {
                Type = "SystemError",
                Tab = string.IsNullOrEmpty(tab) ? "General" : tab,
                Message = errorMessage,
                TimeStr = $"[{DateTime.Now:HH:mm:ss}]"
            });

            if (socket != null && socket.IsAvailable)
            {
                try { socket.Send(json); } catch { }
            }
            else
            {
                BroadcastJson(json);
            }
        }

        private void SetActiveCom(int radio)
        {
            if (radio == 1)
            {
                _com1Tx = true;
                _com2Tx = false;
                _com2TxActive = false;
            }
            else if (radio == 2)
            {
                _com1Tx = false;
                _com2Tx = true;
                _com1TxActive = false;
            }

            // Submit vPilot command if possible
            try
            {
                if (_eventBroker != null)
                {
                    var vpilotAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "vPilot");
                    var cmdType = vpilotAssembly?.GetType("RossCarlson.Vatsim.Vpilot.Commands.Commands.SetActiveComRadioCommand");
                    var cmdArgsType = vpilotAssembly?.GetType("RossCarlson.Vatsim.Vpilot.Events.Arguments.CommandSubmittedEventArgs");
                    if (cmdType != null && cmdArgsType != null)
                    {
                        var cmd = Activator.CreateInstance(cmdType, new object[] { (byte)radio });
                        var args = Activator.CreateInstance(cmdArgsType, new object[] { cmd });
                        _eventBroker.Fire("CommandSubmitted", this, HandlerRestriction.None, this, (EventArgs)args);
                    }
                }
            }
            catch (Exception) { }

            // Transmit SimConnect event as fallback / sim synchronization
            try
            {
                if (_simConnect != null)
                {
                    var ev = radio == 1 ? EVENTS.Com1TransmitSelect : EVENTS.Com2TransmitSelect;
                    _simConnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, ev, 0, GROUP_ID.DEFAULT, SIMCONNECT_EVENT_FLAG.DEFAULT);
                }
            }
            catch (Exception) { }

            BroadcastRadioStateQuick();
        }

        private void ToggleRx(int radio)
        {
            if (radio == 1) _com1Rx = !_com1Rx;
            else if (radio == 2) _com2Rx = !_com2Rx;

            try
            {
                if (_simConnect != null)
                {
                    _simConnect.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, EVENTS.ComReceiveAllToggle, 0, GROUP_ID.DEFAULT, SIMCONNECT_EVENT_FLAG.DEFAULT);
                }
            }
            catch (Exception) { }

            BroadcastRadioStateQuick();
        }

        private object GetCurrentRadioData()
        {
            return new
            {
                Type = "Radios",
                Com1 = _com1Freq,
                Com2 = _com2Freq,
                Com1Tx = _com1Tx,
                Com1Rx = _com1Rx,
                Com1TxActive = _com1TxActive,
                Com1RxActive = _com1RxActive,
                Com2Tx = _com2Tx,
                Com2Rx = _com2Rx,
                Com2TxActive = _com2TxActive,
                Com2RxActive = _com2RxActive,
                RxActive = _com1RxActive || _com2RxActive || _isRxActive,
                TxActive = _com1TxActive || _com2TxActive || _pttActive,
                Callsign = _myCallsign
            };
        }

        private void BroadcastRadioStateQuick()
        {
            try
            {
                string json = JsonConvert.SerializeObject(GetCurrentRadioData());
                BroadcastJson(json);
            }
            catch (Exception) { }
        }

        private void BroadcastJson(string json)
        {
            lock (_socketLock)
            {
                foreach (var socket in _sockets)
                {
                    if (socket.IsAvailable) socket.Send(json);
                }
            }
        }

        private void InitSimConnect()
        {
            if ((DateTime.Now - _lastSimConnectAttempt).TotalSeconds < 5) return;
            _lastSimConnectAttempt = DateTime.Now;

            try
            {
                _simConnect = new SimConnect("vPilotTabletMirror", IntPtr.Zero, 0, _simConnectEvent, 0);

                _simConnect.AddToDataDefinition(DEFINITIONS.RadioData, "COM ACTIVE FREQUENCY:1", "Megahertz", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);
                _simConnect.AddToDataDefinition(DEFINITIONS.RadioData, "COM ACTIVE FREQUENCY:2", "Megahertz", SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

                _simConnect.RegisterDataDefineStruct<RadioDataStruct>(DEFINITIONS.RadioData);
                _simConnect.OnRecvSimobjectData += OnRecvSimobjectData;

                _simConnect.MapClientEventToSimEvent(EVENTS.Com1TransmitSelect, "COM1_TRANSMIT_SELECT");
                _simConnect.MapClientEventToSimEvent(EVENTS.Com2TransmitSelect, "COM2_TRANSMIT_SELECT");
                _simConnect.MapClientEventToSimEvent(EVENTS.ComReceiveAllToggle, "COM_RECEIVE_ALL_TOGGLE");
            }
            catch (Exception)
            {
                _simConnect = null;
            }
        }

        private void PollSimConnect()
        {
            if (_simConnect == null)
            {
                InitSimConnect();
                return;
            }

            try
            {
                if (_simConnectEvent.WaitOne(0))
                {
                    _simConnect.ReceiveMessage();
                }

                _simConnect.RequestDataOnSimObject(
                    REQUESTS.RadioRequest,
                    DEFINITIONS.RadioData,
                    SimConnect.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_PERIOD.ONCE,
                    SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                    0, 0, 0
                );
            }
            catch (Exception)
            {
                _simConnect = null;
            }
        }

        private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
        {
            if (data.dwRequestID == (uint)REQUESTS.RadioRequest && data.dwData.Length > 0)
            {
                var radios = (RadioDataStruct)data.dwData[0];
                bool changed = false;
                if (radios.Com1 > 100)
                {
                    string f1 = radios.Com1.ToString("0.000");
                    if (_com1Freq != f1) { _com1Freq = f1; changed = true; }
                }
                if (radios.Com2 > 100)
                {
                    string f2 = radios.Com2.ToString("0.000");
                    if (_com2Freq != f2) { _com2Freq = f2; changed = true; }
                }

                if (changed)
                {
                    BroadcastRadioStateQuick();
                }
            }
        }

        private void OnRadioMessageReceived(object sender, RadioMessageReceivedEventArgs e)
        {
            if (e != null)
            {
                _isRxActive = true;
                _com1RxActive = true;
                BroadcastRadioStateQuick();

                _ = Task.Delay(2500).ContinueWith(_ =>
                {
                    _isRxActive = false;
                    _com1RxActive = false;
                    BroadcastRadioStateQuick();
                });

                string senderCallsign = "";
                string messageText = "";
                string frequency = "";

                try
                {
                    var type = e.GetType();
                    foreach (var prop in type.GetProperties())
                    {
                        string propName = prop.Name.ToLower();
                        var rawVal = prop.GetValue(e);

                        if (rawVal != null)
                        {
                            string val = "";
                            if (rawVal is IEnumerable list && !(rawVal is string))
                            {
                                foreach (var item in list)
                                {
                                    val = item?.ToString();
                                    break;
                                }
                            }
                            else
                            {
                                val = rawVal.ToString();
                            }

                            if (!string.IsNullOrEmpty(val))
                            {
                                if (propName.Contains("callsign") || propName.Contains("from") || propName.Contains("sender"))
                                {
                                    if (string.IsNullOrEmpty(senderCallsign)) senderCallsign = val;
                                }
                                else if (propName.Contains("message") || propName.Contains("text") || propName.Contains("body"))
                                {
                                    if (string.IsNullOrEmpty(messageText)) messageText = val;
                                }
                                else if (propName.Contains("freq"))
                                {
                                    if (string.IsNullOrEmpty(frequency)) frequency = val;
                                }
                            }
                        }
                    }
                }
                catch (Exception) { }

                if (!string.IsNullOrEmpty(messageText))
                {
                    if (int.TryParse(frequency, out int freqInt))
                    {
                        long correctedFreqInt = freqInt;
                        if (correctedFreqInt < 100000) correctedFreqInt += 100000;
                        frequency = (correctedFreqInt / 1000.0m).ToString("0.000");
                    }

                    var radioMsgData = new
                    {
                        Type = "RadioTextMessage",
                        Sender = string.IsNullOrEmpty(senderCallsign) ? "Unknown" : senderCallsign,
                        Frequency = frequency,
                        Message = messageText,
                        IsOutgoing = false,
                        TimeStr = $"[{DateTime.Now.ToString("HH:mm:ss")}]"
                    };

                    RecordAndBroadcastMessage(radioMsgData);
                }
            }
        }

        private void OnPrivateMessageReceived(object sender, PrivateMessageReceivedEventArgs e)
        {
            if (e != null)
            {
                string senderCallsign = "";
                string messageText = "";

                try
                {
                    var type = e.GetType();
                    foreach (var prop in type.GetProperties())
                    {
                        string propName = prop.Name.ToLower();
                        var val = prop.GetValue(e)?.ToString();

                        if (!string.IsNullOrEmpty(val))
                        {
                            if (propName.Contains("callsign") || propName.Contains("sender") || propName.Contains("from"))
                            {
                                if (senderCallsign == "") senderCallsign = val;
                            }
                            else if (propName.Contains("message") || propName.Contains("text") || propName.Contains("body"))
                            {
                                messageText = val;
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(senderCallsign)) senderCallsign = "Controller";
                    senderCallsign = senderCallsign.Trim().ToUpper();
                }
                catch (Exception) { }

                if (!string.IsNullOrEmpty(messageText))
                {
                    var pmData = new
                    {
                        Type = "PrivateMessage",
                        Tab = senderCallsign,
                        Sender = senderCallsign,
                        Recipient = _myCallsign,
                        Message = messageText,
                        IsOutgoing = false,
                        TimeStr = $"[{DateTime.Now.ToString("HH:mm:ss")}]"
                    };

                    RecordAndBroadcastMessage(pmData);
                }
            }
        }

        private void OnBroadcastMessageReceived(object sender, BroadcastMessageReceivedEventArgs e)
        {
            if (e != null && !string.IsNullOrWhiteSpace(e.Message))
            {
                string from = string.IsNullOrWhiteSpace(e.From) ? "SERVER" : e.From.Trim().ToUpperInvariant();
                BroadcastSystemNotification("Warning", $"[BROADCAST {from}] {e.Message}");
            }
        }

        private void OnMetarReceived(object sender, MetarReceivedEventArgs e)
        {
            if (e != null && !string.IsNullOrWhiteSpace(e.Metar))
            {
                BroadcastSystemNotification("Success", e.Metar.Trim());
            }
        }

        private void OnAtisReceived(object sender, AtisReceivedEventArgs e)
        {
            if (e == null) return;
            PublishAtisLines(e.From, e.Lines);
        }

        private void PublishAtisLines(string fromCallsign, IEnumerable lines)
        {
            if (lines == null) return;
            var lineList = lines.Cast<object>()
                .Select(x => x?.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            if (lineList.Count == 0) return;

            string from = string.IsNullOrWhiteSpace(fromCallsign) ? "Controller" : fromCallsign.Trim().ToUpperInvariant();
            BroadcastSystemNotification("Success", $"{from} ATIS:");
            foreach (string line in lineList)
            {
                BroadcastSystemNotification("Success", line);
            }
        }

        private void OnControllerAdded(object sender, ControllerAddedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Callsign))
            {
                long correctedFreqInt = e.Frequency;
                if (correctedFreqInt < 100000) correctedFreqInt += 100000;
                string formattedFreq = (correctedFreqInt / 1000.0m).ToString("0.000");

                var info = new ControllerInfo
                {
                    Callsign = e.Callsign,
                    Frequency = formattedFreq,
                    Atis = new List<string> { "Fetching live data from VATSIM..." }
                };

                lock (_localControllers)
                {
                    _localControllers[e.Callsign] = info;
                }

                BroadcastControllers();
            }
        }

        private void OnControllerDeleted(object sender, ControllerDeletedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Callsign))
            {
                lock (_localControllers)
                {
                    _localControllers.Remove(e.Callsign);
                }

                BroadcastControllers();
            }
        }

        private void BroadcastControllers()
        {
            List<ControllerInfo> list;
            lock (_localControllers)
            {
                list = _localControllers.Values.ToList();
            }

            var data = new { Type = "Controllers", List = list };
            BroadcastJson(JsonConvert.SerializeObject(data));
        }

        private void OnNetworkConnected(object sender, NetworkConnectedEventArgs e)
        {
            if (e != null && !string.IsNullOrEmpty(e.Callsign))
            {
                _myCallsign = e.Callsign.Trim().ToUpper();
                BroadcastCallsign();
            }
            else
            {
                string cs = GetEffectiveCallsign();
                if (!string.IsNullOrEmpty(cs))
                {
                    _myCallsign = cs;
                    BroadcastCallsign();
                }
            }
        }

        private void OnNetworkDisconnected(object sender, EventArgs e)
        {
            string cs = GetEffectiveCallsign();
            if (!string.IsNullOrEmpty(cs))
            {
                _myCallsign = cs;
                BroadcastCallsign();
            }
        }

        [EventSubscription("NetworkConnected", typeof(Publisher))]
        public void OnEventBrokerNetworkConnected(object sender, EventArgs e)
        {
            try
            {
                string cs = "";
                if (e != null)
                {
                    var info = GetPropOrFieldValue<object>(e, "ConnectInfo");
                    if (info != null)
                    {
                        cs = GetPropOrFieldValue<string>(info, "Callsign");
                    }
                    if (string.IsNullOrEmpty(cs))
                    {
                        cs = GetPropOrFieldValue<string>(e, "Callsign");
                    }
                }
                if (string.IsNullOrEmpty(cs))
                {
                    cs = GetEffectiveCallsign();
                }
                if (!string.IsNullOrEmpty(cs))
                {
                    _myCallsign = cs.Trim().ToUpper();
                    BroadcastCallsign();
                    BroadcastRadioStateQuick();
                }
            }
            catch (Exception) { }
        }

        [EventSubscription("NetworkDisconnected", typeof(Publisher))]
        public void OnEventBrokerNetworkDisconnected(object sender, EventArgs e)
        {
            try
            {
                string cs = GetEffectiveCallsign();
                if (!string.IsNullOrEmpty(cs))
                {
                    _myCallsign = cs.Trim().ToUpper();
                    BroadcastCallsign();
                    BroadcastRadioStateQuick();
                }
            }
            catch (Exception) { }
        }

        private void BroadcastCallsign()
        {
            try
            {
                if (!string.IsNullOrEmpty(_myCallsign))
                {
                    var data = new { Type = "Callsign", Callsign = _myCallsign };
                    BroadcastJson(JsonConvert.SerializeObject(data));
                }
            }
            catch (Exception) { }
        }

        private string GetEffectiveCallsign()
        {
            try
            {
                string memCs = GetCallsignFromMemory();
                if (!string.IsNullOrEmpty(memCs)) return memCs;
            }
            catch { }

            return ReadCallsignFromConfig();
        }

        private string GetCallsignFromMemory()
        {
            try
            {
                if (_broker != null)
                {
                    var brokerType = _broker.GetType();
                    var aaField = brokerType.GetField("aa", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (aaField != null)
                    {
                        var dzObj = aaField.GetValue(_broker);
                        if (dzObj != null)
                        {
                            var dzType = dzObj.GetType();
                            var cgMethod = dzType.GetMethod("cg", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (cgMethod != null)
                            {
                                var connectInfo = cgMethod.Invoke(dzObj, null);
                                if (connectInfo != null)
                                {
                                    var cs = GetPropOrFieldValue<string>(connectInfo, "Callsign");
                                    if (!string.IsNullOrEmpty(cs)) return cs.Trim().ToUpper();
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private string ReadCallsignFromConfig()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string configFile = Path.Combine(appData, "vPilot", "vPilotConfig.xml");
                if (File.Exists(configFile))
                {
                    using (var stream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var doc = new System.Xml.XmlDocument();
                        doc.Load(stream);
                        var node = doc.SelectSingleNode("//LastConnectInfo/@Callsign");
                        if (node != null && !string.IsNullOrEmpty(node.Value))
                        {
                            return node.Value.Trim().ToUpper();
                        }
                    }
                }
            }
            catch (Exception) { }
            return "";
        }

        private async Task BroadcastStates()
        {
            try
            {
                string cs = GetEffectiveCallsign();
                if (!string.IsNullOrEmpty(cs) && cs != _myCallsign)
                {
                    _myCallsign = cs;
                    BroadcastCallsign();
                }

                var radioData = GetCurrentRadioData();
                BroadcastJson(JsonConvert.SerializeObject(radioData));

                await RefreshVatsimAtis();
            }
            catch (Exception) { }
        }

        private async Task SendInitialData(IWebSocketConnection socket)
        {
            try
            {
                if (string.IsNullOrEmpty(_myCallsign))
                {
                    _myCallsign = GetEffectiveCallsign();
                }

                // Send radio status immediately!
                var radioData = GetCurrentRadioData();
                if (socket.IsAvailable) socket.Send(JsonConvert.SerializeObject(radioData));

                if (!string.IsNullOrEmpty(_myCallsign) && socket.IsAvailable)
                {
                    var csData = new { Type = "Callsign", Callsign = _myCallsign };
                    socket.Send(JsonConvert.SerializeObject(csData));
                }

                await RefreshVatsimAtis();

                List<ControllerInfo> list;
                lock (_localControllers)
                {
                    list = _localControllers.Values.ToList();
                }
                var controllerData = new { Type = "Controllers", List = list };
                if (socket.IsAvailable) socket.Send(JsonConvert.SerializeObject(controllerData));

                List<object> history;
                lock (_messageHistory)
                {
                    history = _messageHistory.ToList();
                }
                var historyData = new { Type = "MessageHistory", Messages = history };
                if (socket.IsAvailable) socket.Send(JsonConvert.SerializeObject(historyData));

                SendPttBindingsToSocket(socket);
            }
            catch (Exception) { }
        }

        private async Task RefreshVatsimAtis()
        {
            try
            {
                string url = "https://data.vatsim.net/v3/vatsim-data.json";
                string response = await _httpClient.GetStringAsync(url);
                var vatsimData = JObject.Parse(response);
                var controllersToken = vatsimData["controllers"] as JArray;
                var atisToken = vatsimData["atis"] as JArray;

                if (_localControllers.Count > 0)
                {
                    lock (_localControllers)
                    {
                        foreach (var kvp in _localControllers)
                        {
                            JToken match = controllersToken?.FirstOrDefault(c => c["callsign"]?.ToString() == kvp.Key);
                            if (match == null) match = atisToken?.FirstOrDefault(a => a["callsign"]?.ToString() == kvp.Key);

                            if (match != null)
                            {
                                var textAtis = match["text_atis"] as JArray;
                                if (textAtis != null && textAtis.Count > 0)
                                {
                                    kvp.Value.Atis = textAtis.Select(t => t.ToString()).ToList();
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        public void Shutdown()
        {
            try
            {
                _multiPttManager?.Dispose();
                _multiPttManager = null;
            }
            catch { }

            lock (_pttLock)
            {
                if (_lastSentPluginPtt)
                {
                    _lastSentPluginPtt = false;
                    _hardwarePttActive = false;
                    _dashboardPttActive = false;
                    try { _broker?.SetPtt(false); } catch { }
                }
            }

            StopDashboardServer();

            _pollTimer?.Dispose();
            if (_simConnect != null)
            {
                try { _simConnect.Dispose(); } catch { }
                _simConnect = null;
            }
            if (_eventBroker != null)
            {
                try { _eventBroker.Unregister(this); } catch { }
                _eventBroker = null;
            }
            lock (_socketLock)
            {
                foreach (var socket in _sockets)
                {
                    try { socket.Close(); } catch { }
                }
                _sockets.Clear();
            }
            try { _server?.Dispose(); } catch { }
        }

        #region Dashboard Web Server Management

        private void StartDashboardServer()
        {
            try
            {
                if (IsPortInUse(8085))
                {
                    // Web server is already running on port 8085
                    return;
                }

                _httpCts = new CancellationTokenSource();
                _httpServer = new TcpListener(IPAddress.Any, 8085);
                _httpServer.Start();

                var token = _httpCts.Token;
                Task.Run(() => RunHttpServerLoop(token), token);
            }
            catch (Exception) { }
        }

        private void RunHttpServerLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_httpServer == null) break;
                    TcpClient client = _httpServer.AcceptTcpClient();
                    Task.Run(() => HandleHttpClient(client));
                }
                catch (SocketException)
                {
                    if (token.IsCancellationRequested) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception) { }
            }
        }

        private void HandleHttpClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;

                    using (NetworkStream stream = client.GetStream())
                    {
                        byte[] buffer = new byte[4096];
                        int bytesRead = stream.Read(buffer, 0, buffer.Length);
                        if (bytesRead <= 0) return;

                        string request = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        string firstLine = request.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];

                        if (firstLine.IndexOf("favicon.ico", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            byte[] notFound = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            stream.Write(notFound, 0, notFound.Length);
                            return;
                        }

                        byte[] body = GetDashboardHtmlBytes();
                        if (body == null || body.Length == 0)
                        {
                            byte[] serverError = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            stream.Write(serverError, 0, serverError.Length);
                            return;
                        }

                        string headers =
                            "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: text/html; charset=utf-8\r\n" +
                            $"Content-Length: {body.Length}\r\n" +
                            "Cache-Control: no-cache, no-store, must-revalidate\r\n" +
                            "Connection: close\r\n\r\n";

                        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        stream.Write(body, 0, body.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception) { }
        }

        private byte[] GetDashboardHtmlBytes()
        {
            // 1. Prefer external Dashboard/index.html on disk if available
            try
            {
                string dashboardDir = GetDashboardDirectory();
                if (!string.IsNullOrEmpty(dashboardDir))
                {
                    string htmlPath = Path.Combine(dashboardDir, "index.html");
                    if (File.Exists(htmlPath))
                    {
                        return File.ReadAllBytes(htmlPath);
                    }
                }
            }
            catch (Exception) { }

            // 2. Fallback to embedded index.html inside plugin assembly (zero-dependency fallback)
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string resourceName = Array.Find(
                    asm.GetManifestResourceNames(),
                    n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(resourceName))
                {
                    using (Stream resStream = asm.GetManifestResourceStream(resourceName))
                    {
                        if (resStream != null)
                        {
                            using (MemoryStream ms = new MemoryStream())
                            {
                                resStream.CopyTo(ms);
                                return ms.ToArray();
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            return null;
        }

        private void StopDashboardServer()
        {
            try { _httpCts?.Cancel(); } catch { }
            try { _httpServer?.Stop(); } catch { }
            _httpServer = null;
        }

        private string GetDashboardDirectory()
        {
            try
            {
                // 1. Check if dashboard_path.txt exists next to plugin dll
                string pluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(pluginFolder))
                {
                    string configPath = Path.Combine(pluginFolder, "dashboard_path.txt");
                    if (File.Exists(configPath))
                    {
                        string customPath = File.ReadAllText(configPath).Trim();
                        if (Directory.Exists(customPath)) return customPath;
                    }

                    // 2. Check for Dashboard subfolder next to plugin
                    string localDash = Path.Combine(pluginFolder, "Dashboard");
                    if (Directory.Exists(localDash)) return localDash;
                }

                // 3. Check Documents folder (including Flightsim.to extracted folders with random suffixes)
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
                {
                    string[] searchDirs = new[]
                    {
                        Path.Combine(docs, "vPilot Dashboard v2"),
                        Path.Combine(docs, "vPilot-Tablet-Dashboard", "Dashboard"),
                        Path.Combine(docs, "vPilot Dashboard"),
                        Path.Combine(docs, "vPilotDashboard")
                    };

                    foreach (var dir in searchDirs)
                    {
                        if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "index.html"))) return dir;
                    }

                    // Scan any vPilot-Tablet-Dashboard* folder (e.g. vPilot-Tablet-Dashboard-v1.0.0_bHvpm)
                    try
                    {
                        foreach (var dir in Directory.GetDirectories(docs, "vPilot-Tablet-Dashboard*"))
                        {
                            string subDash = Path.Combine(dir, "Dashboard");
                            if (Directory.Exists(subDash) && File.Exists(Path.Combine(subDash, "index.html"))) return subDash;
                            if (File.Exists(Path.Combine(dir, "index.html"))) return dir;
                        }
                    }
                    catch { }
                }

                // 4. Check user profile directory
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    string p1 = Path.Combine(userProfile, "Documents", "vPilot-Tablet-Dashboard", "Dashboard");
                    if (Directory.Exists(p1) && File.Exists(Path.Combine(p1, "index.html"))) return p1;
                    string p2 = Path.Combine(userProfile, "Documents", "vPilot Dashboard v2");
                    if (Directory.Exists(p2) && File.Exists(Path.Combine(p2, "index.html"))) return p2;
                }
            }
            catch { }

            return null;
        }

        private bool IsPortInUse(int port)
        {
            try
            {
                var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
                var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();
                foreach (var endpoint in tcpConnInfoArray)
                {
                    if (endpoint.Port == port) return true;
                }
            }
            catch { }
            return false;
        }

        #endregion
    }

    public class ControllerInfo
    {
        public string Callsign { get; set; }
        public string Frequency { get; set; }
        public List<string> Atis { get; set; } = new List<string>();
    }
}
