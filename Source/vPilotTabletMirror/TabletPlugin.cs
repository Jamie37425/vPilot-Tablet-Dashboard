using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RossCarlson.Vatsim.Vpilot.Plugins;
using RossCarlson.Vatsim.Vpilot.Plugins.Events;
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

        // Dashboard web server process
        private Process _dashboardProcess = null;

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
            _broker.NetworkConnected += OnNetworkConnected;
            _broker.NetworkDisconnected += OnNetworkDisconnected;

            _myCallsign = GetEffectiveCallsign();

            HookEventBroker();

            // Auto-launch the dashboard web server silently in the background
            StartDashboardServer();

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
                    HandleWebSocketMessage(message);
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
                    if (val != null) return (T)Convert.ChangeType(val, typeof(T));
                }
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    var val = field.GetValue(target);
                    if (val != null) return (T)Convert.ChangeType(val, typeof(T));
                }
            }
            catch { }
            return defaultValue;
        }

        private void HandleWebSocketMessage(string message)
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
                        _broker.SetPtt(pressed);
                        _pttActive = pressed;
                        if (_pttActive)
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
                        BroadcastRadioStateQuick();
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
                        TimeStr = $"[{DateTime.Now.ToString("HH:mm:ss")}]"
                    };

                    lock (_messageHistory)
                    {
                        _messageHistory.Add(radioMsgData);
                        if (_messageHistory.Count > 100) _messageHistory.RemoveAt(0);
                    }

                    BroadcastJson(JsonConvert.SerializeObject(radioMsgData));
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
                }
                catch (Exception) { }

                if (!string.IsNullOrEmpty(messageText))
                {
                    var pmData = new
                    {
                        Type = "PrivateMessage",
                        Sender = senderCallsign,
                        Message = messageText,
                        TimeStr = $"[{DateTime.Now.ToString("HH:mm:ss")}]"
                    };

                    lock (_messageHistory)
                    {
                        _messageHistory.Add(pmData);
                        if (_messageHistory.Count > 100) _messageHistory.RemoveAt(0);
                    }

                    BroadcastJson(JsonConvert.SerializeObject(pmData));
                }
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

                string dashboardDir = GetDashboardDirectory();
                if (string.IsNullOrEmpty(dashboardDir) || !Directory.Exists(dashboardDir))
                {
                    return;
                }

                string batPath = Path.Combine(dashboardDir, "Launch_Dashboard.bat");
                string pythonExe = FindPythonExecutable();

                ProcessStartInfo psi = null;

                if (File.Exists(batPath))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + batPath + "\"",
                        WorkingDirectory = dashboardDir,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false
                    };
                }
                else if (!string.IsNullOrEmpty(pythonExe))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = pythonExe,
                        Arguments = "-m http.server 8085",
                        WorkingDirectory = dashboardDir,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false
                    };
                }

                if (psi != null)
                {
                    _dashboardProcess = Process.Start(psi);
                }
            }
            catch (Exception) { }
        }

        private void StopDashboardServer()
        {
            try
            {
                if (_dashboardProcess != null && !_dashboardProcess.HasExited)
                {
                    _dashboardProcess.Kill();
                    _dashboardProcess.Dispose();
                    _dashboardProcess = null;
                }
            }
            catch { }

            // Ensure any python process listening on port 8085 is closed
            KillProcessOnPort(8085);
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

                // 3. Check Documents folder
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string[] searchDirs = new[]
                {
                    Path.Combine(docs, "vPilot Dashboard v2"),
                    Path.Combine(docs, "vPilot-Tablet-Dashboard", "Dashboard"),
                    Path.Combine(docs, "vPilot Dashboard"),
                    Path.Combine(docs, "vPilotDashboard")
                };

                foreach (var dir in searchDirs)
                {
                    if (Directory.Exists(dir)) return dir;
                }

                // 4. Check user profile directory
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile))
                {
                    string p1 = Path.Combine(userProfile, "Documents", "vPilot-Tablet-Dashboard", "Dashboard");
                    if (Directory.Exists(p1)) return p1;
                    string p2 = Path.Combine(userProfile, "Documents", "vPilot Dashboard v2");
                    if (Directory.Exists(p2)) return p2;
                }
            }
            catch { }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "vPilot-Tablet-Dashboard", "Dashboard");
        }

        private string FindPythonExecutable()
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] candidates = new[]
                {
                    Path.Combine(localApp, @"Microsoft\WindowsApps\py.exe"),
                    Path.Combine(localApp, @"Programs\Python\Python311\python.exe"),
                    Path.Combine(localApp, @"Programs\Python\Python312\python.exe"),
                    Path.Combine(localApp, @"Programs\Python\Python310\python.exe")
                };

                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            catch { }

            return "py";
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

        private static void KillProcessOnPort(int port)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c for /f \"tokens=5\" %a in ('netstat -aon ^| findstr :{port} ^| findstr LISTENING') do if %a neq 0 taskkill /f /pid %a",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(2000);
                }
            }
            catch { }
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
