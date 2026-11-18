using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using nanoFramework.Hardware.Esp32;
using nanoGate.Core;
using VagabondK.nF.Web;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;
using VagabondK.nF.Modbus.TCP;

namespace nanoGate.ModbusSerialToTcp
{
    // nanoGate.Core가 호스팅하는 Modbus RTU/ASCII(UART) <-> Modbus TCP 변환기입니다.
    internal sealed class ModbusGatewayProfile : IGatewayProfile
    {
        private const int TcpFrameTimeoutMilliseconds = 3000;
        private const int TcpIdleTimeoutSeconds = 60;
        private const int MaxTcpConnections = 4;

        private GatewaySettings _settings;

        public string Id
        {
            get { return GatewaySettings.GatewayProfile; }
        }

        // PDU 릴레이: 포인트 기능이 없어 선언할 능력이 없습니다(탭은 뷰 포함 여부로 결정).
        public string CapabilitiesJson
        {
            get { return "{}"; }
        }

        public string SettingsKeysJson
        {
            get { return "[\"protocol\",\"baudRate\",\"dataBits\",\"parity\",\"stopBits\",\"timeout\",\"tcpPort\",\"txPin\",\"rxPin\"]"; }
        }

        public void Load()
        {
            string source;
            string error;
            _settings = GatewaySettings.Load(out source, out error);
            GatewayHost.SettingsSource = source;
            GatewayHost.SettingsError = error;
        }

        public void WriteStatus(JsonWriter w)
        {
            GatewaySettings settings = _settings;
            w.Prop("protocol", settings.ProtocolName)
                .Prop("schemaVersion", GatewaySettings.ConfigurationSchemaVersion)
                .Prop("baudRate", settings.BaudRate)
                .Prop("dataBits", settings.DataBits)
                .Prop("parity", settings.ParityName)
                .Prop("stopBits", settings.StopBits)
                .Prop("txPin", settings.TxPin)
                .Prop("rxPin", settings.RxPin)
                .Prop("rs485Mode", settings.Rs485Mode)
                .Prop("rs485DePin", settings.Rs485DePin)
                .Prop("timeout", settings.ResponseTimeout)
                .Prop("tcpPort", settings.TcpPort)
                .Prop("statsSince", GatewayStatistics.ResetAtSeconds)
                .Prop("requests", GatewayStatistics.Requests)
                .Prop("successes", GatewayStatistics.Successes)
                .Prop("timeouts", GatewayStatistics.Timeouts)
                .Prop("frameErrors", GatewayStatistics.FrameErrors)
                .Prop("deviceExceptions", GatewayStatistics.DeviceExceptions)
                .Prop("tcpConnections", GatewayStatistics.TcpConnections)
                .Prop("lastError", GatewayStatistics.LastError);

            w.Prop("units");
            ModbusRelay.WriteSeenUnits(w);
        }

        // PDU 릴레이: 포인트를 폴링하지 않으므로 공개할 캐시가 없습니다.
        public bool HasPointCache
        {
            get { return false; }
        }

        public void WriteCache(JsonWriter w)
        {
        }

        public string SaveSettings(string form, out bool storageFailure)
        {
            storageFailure = false;
            GatewaySettings candidate = _settings.Clone();
            string error = candidate.Apply(form);
            if (error == null)
                error = candidate.Validate();
            if (error != null)
                return error;

            error = candidate.Save();
            if (error != null)
            {
                storageFailure = true;
                return error;
            }

            Debug.WriteLine("Settings saved: " + candidate.Serialize());
            return null;
        }

        public string ResetSettings()
        {
            return GatewaySettings.Reset();
        }

        // 포인트 매핑을 사용하지 않으므로 limits에 덧붙일 필드가 없습니다.
        public string ExtraLimitsJson
        {
            get { return ""; }
        }

        public void ResetStatistics()
        {
            GatewayStatistics.Reset();
        }

        public void Run()
        {
            GatewaySettings settings = _settings;
            SerialPort port;
            try
            {
                Configuration.SetPinFunction(settings.TxPin, DeviceFunction.COM2_TX);
                Configuration.SetPinFunction(settings.RxPin, DeviceFunction.COM2_RX);
                port = new SerialPort(
                    "COM2",
                    settings.BaudRate,
                    settings.Parity,
                    settings.DataBits,
                    settings.StopBits == 2 ? StopBits.Two : StopBits.One);
                if (settings.Rs485Mode == "rts")
                {
                    // 네이티브 RTS가 송신 중(마지막 정지 비트까지) DE//RE를 제어합니다.
                    // 포트가 닫힌 상태에서 열기 전에 설정해야 합니다.
                    Configuration.SetPinFunction(settings.Rs485DePin, DeviceFunction.COM2_RTS);
                    port.Mode = SerialMode.RS485;
                }
                port.Open();
            }
            catch (Exception error)
            {
                GatewayHost.Error = "UART 초기화 실패: " + ErrorText.Describe(error);
                Debug.WriteLine(GatewayHost.Error);
                return;
            }

            var channel = new SerialPortChannel(port);
            IModbusTransport serialTransport;
            if (settings.Ascii)
            {
                serialTransport = new AsciiTransport(channel, settings.ResponseTimeout);
            }
            else
            {
                serialTransport = new RtuTransport(
                    channel,
                    settings.BaudRate,
                    RtuReceiveMode.ResponseLength,
                    settings.BitsPerCharacter,
                    settings.ResponseTimeout);
            }

            var relay = new ModbusRelay(serialTransport, port);

            Socket listener;
            try
            {
                listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Any, settings.TcpPort));
                listener.Listen(1);
            }
            catch (SocketException error)
            {
                GatewayHost.Error = "Modbus TCP 포트 " + settings.TcpPort + " 열기 실패: " + error.ErrorCode;
                Debug.WriteLine(GatewayHost.Error);
                return;
            }

            GatewayHost.Ready = true;
            Debug.WriteLine(
                "nanoGate ready: Modbus TCP " + settings.TcpPort + " -> " + settings.ProtocolName + " " +
                settings.BaudRate + " (TX " + settings.TxPin + ", RX " + settings.RxPin + "), multi-drop, all unit IDs.");

            while (true)
            {
                Socket connection;
                try
                {
                    connection = listener.Accept();
                }
                catch (SocketException error)
                {
                    Debug.WriteLine("Modbus TCP accept failed: " + error.ErrorCode);
                    Thread.Sleep(1000);
                    continue;
                }

                if (GatewayStatistics.TcpConnections >= MaxTcpConnections)
                {
                    Debug.WriteLine("Modbus TCP connection refused: " + MaxTcpConnections + " already active.");
                    try { connection.Close(); } catch (Exception) { }
                    continue;
                }

                var worker = new Thread(() => Serve(connection, relay));
                worker.Start();
            }
        }

        // Modbus TCP 클라이언트 하나를 담당하며, 유휴 상태나 연결 해제, 잘못된 프레임 수신 시까지 처리합니다.
        // 각 연결은 별도의 스레드에서 실행되고, 공유 RS-485 버스는 ModbusRelay 내부에서 직렬화됩니다.
        private static void Serve(Socket connection, ModbusRelay relay)
        {
            Interlocked.Increment(ref GatewayStatistics.TcpConnections);
            var pdu = new byte[Protocol.MaxPduLength];
            try
            {
                using (connection)
                {
                    var tcpTransport = new TcpTransport(new SocketChannel(connection), TcpFrameTimeoutMilliseconds);
                    int idleRounds = 0;
                    while (idleRounds < TcpIdleTimeoutSeconds * 1000 / TcpFrameTimeoutMilliseconds)
                    {
                        FrameContext context;
                        int requestLength;
                        try
                        {
                            requestLength = tcpTransport.Receive(pdu, out context);
                        }
                        catch (TimeoutException)
                        {
                            idleRounds++;
                            continue;
                        }

                        idleRounds = 0;
                        byte unit = context.UnitId;
                        if (unit < 1 || unit > 247)
                            continue; // 릴레이는 브로드캐스트 및 예약 주소에 응답하지 않습니다.

                        // Multi-drop: 모든 유효한 unit 주소를 그대로 시리얼 버스로 전달합니다.
                        int responseLength = relay.Process(context, pdu, requestLength);
                        tcpTransport.Send(context, pdu, responseLength);
                    }

                    Debug.WriteLine("Closing idle Modbus TCP connection.");
                }
            }
            catch (ModbusProtocolException error)
            {
                Debug.WriteLine("Closing Modbus TCP connection after protocol error: " + error.Message);
            }
            catch (SocketException error)
            {
                Debug.WriteLine("Closing Modbus TCP connection after socket error: " + error.ErrorCode);
            }
            catch (IOException error)
            {
                Debug.WriteLine("Closing Modbus TCP connection after I/O error: " + error.Message);
            }
            finally
            {
                Interlocked.Decrement(ref GatewayStatistics.TcpConnections);
            }
        }
    }
}
