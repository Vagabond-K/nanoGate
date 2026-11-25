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
using nanoGate.Points;
using VagabondK.nF.Web;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;
using VagabondK.nF.MQTT;

namespace nanoGate.ModbusToMqtt
{
    // nanoGate.Core가 호스팅하는 Modbus RTU/ASCII(UART) -> MQTT 게시자(publisher)입니다.
    // 폴링 스레드는 매핑된 Modbus 포인트를 읽어 dirty 표시를 하고,
    // 메인 스레드는 broker 연결을 소유하며 페이로드를 게시합니다.
    // 포인트 변경은 재부팅 후 적용됩니다.
    internal sealed class ModbusToMqttProfile : IGatewayProfile
    {
        private const int SpaceCoil = 0;
        private const int SpaceDiscrete = 1;
        private const int SpaceInput = 2;
        private const int SpaceHolding = 3;

        private const int KindBool = 0;
        private const int KindInt16 = 1;
        private const int KindUInt16 = 2;
        private const int KindInt32 = 3;
        private const int KindUInt32 = 4;
        private const int KindFloat32 = 5;

        // 두 개의 Modbus 레지스터에 걸친 32비트 byte order (A = 최상위 바이트).
        private const int OrderABCD = 0;
        private const int OrderCDAB = 1;
        private const int OrderBADC = 2;
        private const int OrderDCBA = 3;

        // 페이로드 품질: 0 = 양호, 1 = 오류 (부팅 이후 또는 마지막 양호 읽기 이후 읽기가 실패한 경우).
        private const byte QualityGood = 0;
        private const byte QualityBad = 1;

        private const int SocketPollMilliseconds = 50;
        private const int ReconnectBackoffMax = 30000;

        private sealed class MapPoint
        {
            public string Id;
            public int Space;
            public int Address;
            public int Kind;
            public int Order;
            public int Numerator;
            public int Denominator;
            public int Offset;
            // 멀티드롭 폴링용 Modbus unit으로, 포인트의 device 필드에서 파싱합니다.
            public byte Unit;
            public bool Writable;
            // RW 포인트의 명령 topic ("<publish topic>/set"); 쓰기 불가능하면 null입니다.
            public string CmdTopic;
            // 상태 화면을 위해 폴러가 작성하는 마지막 스케일 값과 품질입니다.
            public double Last;
            public byte Flags;
            // 변경 시 게시(publish-on-change) 모드용 변경 감지입니다.
            public bool Dirty;
            public double PublishedValue;
            public byte PublishedFlags;
        }

        private static readonly object _sync = new object();

        private static GatewaySettings _settings;
        private static ModbusClient _client;
        // VagabondK.nF Modbus 문서가 호출자 책임으로 남긴 사후 복구 구현 (DNP3 프로필과 동일):
        // 워치독이 드라이버 행업 시 _port/_client를 통째로 교체합니다.
        private static SerialPort _port;
        private static long _cycleTick;
        private static readonly object PortSync = new object();

        private const int WatchdogPeriodMilliseconds = 5000;
        private static MapPoint[] _map = new MapPoint[0];
        private static int _skipped;
        private static string _skipNote = string.Empty;

        public string Id
        {
            get { return GatewaySettings.GatewayProfile; }
        }

        // MQTT topic은 접두어/ID만 사용하므로 포인트에는 표시 이름이 없습니다(features.name=false).
        public string CapabilitiesJson
        {
            get
            {
                return "{\"byteOrder\":true,\"access\":[\"RO\",\"RW\"],\"name\":false,"
                    + "\"addr\":{\"s\":{\"show\":[\"d\",\"s\",\"a\",\"t\"],\"spaces\":[\"coil\",\"discrete-input\",\"input-register\",\"holding-register\"],"
                    + "\"d\":{\"min\":1,\"max\":247,\"label\":\"Unit\"},\"a\":{\"min\":0,\"max\":65535}},"
                    + "\"t\":{\"show\":[],\"spaces\":[\"topic\"]}},"
                    + "\"cacheCols\":{\"head\":[\"ID\",\"Unit\",\"Modbus\",\"@topic\"]}}";
            }
        }

        public string SettingsKeysJson
        {
            get
            {
                return "[\"protocol\",\"baudRate\",\"dataBits\",\"parity\",\"stopBits\",\"timeout\",\"pollInterval\",\"txPin\",\"rxPin\","
                    + "\"brokerHost\",\"brokerPort\",\"mqttClientId\",\"mqttUsername\",\"mqttPassword\",\"keepAlive\",\"topicPrefix\","
                    + "\"mqttQos\",\"publishMode\",\"publishInterval\",\"payloadFormat\",\"publishBatch\",\"mqttTemplate\",\"mqttBatchTpl\",\"mqttBatchJoin\"]";
            }
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

            // Unit ID는 이제 포인트별이므로, 폴러가 실제로 커버하는 범위를 보고합니다.
            MapPoint[] units = _map;
            int unitMin = 1, unitMax = 1;
            if (units.Length > 0)
            {
                unitMin = units[0].Unit;
                unitMax = unitMin;
                for (int i = 1; i < units.Length; i++)
                {
                    if (units[i].Unit < unitMin) unitMin = units[i].Unit;
                    if (units[i].Unit > unitMax) unitMax = units[i].Unit;
                }
            }

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
                .Prop("unitMin", unitMin)
                .Prop("unitMax", unitMax)
                .Prop("timeout", settings.ResponseTimeout)
                .Prop("pollInterval", settings.PollInterval)
                .Prop("brokerHost", settings.BrokerHost)
                .Prop("brokerPort", settings.BrokerPort)
                .Prop("mqttClientId", settings.ClientId)
                .Prop("mqttUsername", settings.Username)
                .Prop("mqttPassword", settings.Password)
                .Prop("keepAlive", settings.KeepAlive)
                .Prop("topicPrefix", settings.TopicPrefix)
                .Prop("mqttQos", settings.Qos)
                .Prop("publishMode", settings.PublishMode)
                .Prop("publishInterval", settings.PublishInterval)
                .Prop("payloadFormat", settings.PayloadFormat)
                .Prop("publishBatch", settings.Batch)
                .Prop("mqttTemplate", settings.Template)
                .Prop("mqttBatchTpl", settings.BatchTemplate)
                .Prop("mqttBatchJoin", settings.BatchJoin)
                .Prop("pointCount", _map.Length)
                .Prop("skippedPoints", _skipped);

            if (_skipped > 0 && _skipNote.Length > 0)
                w.Prop("skipNote", _skipNote);

            w.Prop("mqttConnected", GatewayStatistics.MqttConnected != 0)
                .Prop("mqttPublishes", GatewayStatistics.MqttPublishes)
                .Prop("mqttReconnects", GatewayStatistics.MqttReconnects)
                .Prop("mqttFailures", GatewayStatistics.MqttFailures)
                .Prop("mqttWrites", GatewayStatistics.MqttWrites);

            w.Prop("statsSince", GatewayStatistics.ResetAtSeconds)
                .Prop("polls", GatewayStatistics.Polls)
                .Prop("requests", GatewayStatistics.Requests)
                .Prop("successes", GatewayStatistics.Successes)
                .Prop("timeouts", GatewayStatistics.Timeouts)
                .Prop("frameErrors", GatewayStatistics.FrameErrors)
                .Prop("deviceExceptions", GatewayStatistics.DeviceExceptions)
                .Prop("lastError", GatewayStatistics.LastError);
        }

        public bool HasPointCache
        {
            get { return true; }
        }

        // /api/cache 엔드포인트용 전체 라이브 캐시입니다. 품질 비트 0 = 오류(QualityBad 참조).
        public void WriteCache(JsonWriter w)
        {
            MapPoint[] map = _map;
            for (int i = 0; i < map.Length; i++)
            {
                bool good = (map[i].Flags & 1) == 0;
                w.BeginObject()
                    .Prop("id", map[i].Id)
                    .Prop("v", NumberText(map[i].Last))
                    .Prop("q", good ? "양호" : "오류")
                    .Prop("ok", good)
                    .EndObject();
            }
        }

        public string SaveSettings(string form, out bool storageFailure)
        {
            storageFailure = false;
            GatewaySettings candidate = _settings.Clone();
            string error = candidate.Apply(form, true);
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

        // /api/status limits에 덧붙이는 포인트 표 한계값(nanoGate.Points 소유).
        public string ExtraLimitsJson
        {
            get
            {
                return "\"pointsSchema\":" + PointMappingTable.SchemaVersion +
                    ",\"nameMax\":" + PointMappingTable.NameMaxLength +
                    ",\"idMax\":" + ProtocolAddress.MaximumIdLength +
                    ",\"tokMax\":" + ProtocolAddress.MaximumTokenLength +
                    ",\"addrMax\":" + ProtocolAddress.MaximumAddressLength +
                    ",\"points\":" + PointMappingTable.MaximumPointCount +
                    ",\"bytes\":" + PointMappingTable.MaximumFileLength;
            }
        }

        public void ResetStatistics()
        {
            GatewayStatistics.Reset();
        }

        public void Run()
        {
            GatewaySettings settings = _settings;
            try
            {
                lock (PortSync)
                {
                    _port = OpenPort(settings);
                    _client = new ModbusClient(CreateTransport(settings, _port));
                }
            }
            catch (Exception error)
            {
                GatewayHost.Error = "UART 초기화 실패: " + ErrorText.Describe(error);
                Debug.WriteLine(GatewayHost.Error);
                return;
            }

            BuildMap();

            _cycleTick = Environment.TickCount64;
            new Thread(PollLoop).Start();
            new Thread(WatchdogLoop).Start();

            GatewayHost.Ready = true;
            Debug.WriteLine(
                "nanoGate ready: " + settings.ProtocolName + " " + settings.BaudRate + " (TX " + settings.TxPin +
                ", RX " + settings.RxPin + ") -> MQTT " + settings.BrokerHost + ":" +
                settings.BrokerPort + " (" + settings.PayloadFormat + ", " + settings.PublishMode + "), " +
                _map.Length + " points, poll " + settings.PollInterval + " ms.");

            MqttLoop();
        }

        // RTU 링크가 무음 상태(슬레이브 전원 미공급)면 모든 읽기가 TimeoutException을 던지고
        // nanoFramework은 예외가 발생할 때마다 콘솔 트레이스를 출력하여
        // 웹 스레드를 기아 상태로 만듭니다. 한 주기가 완전히 실패하면 지수적으로 백오프하고,
        // 다음 양호 주기가 끝나면 즉시 재개합니다.
        private const int PollBackoffMaxMilliseconds = 15000;
        private static int _pollFailStreak;

        // 한 주기에 매핑된 모든 Modbus 포인트를 한 번씩 읽고 변경된 값에 dirty 표시를 합니다.
        private static void PollLoop()
        {
            while (true)
            {
                long started = Environment.TickCount64;
                long successesBefore = GatewayStatistics.Successes;
                ModbusClient client = _client;
                if (client == null)
                {
                    // 복구 워치독이 포트를 다시 여는 중입니다.
                    Thread.Sleep(500);
                    continue;
                }

                PollFile(client);

                GatewayStatistics.IncPolls();
                _cycleTick = Environment.TickCount64;
                if (_map.Length == 0 || GatewayStatistics.Successes != successesBefore)
                    _pollFailStreak = 0;
                else
                    _pollFailStreak++;

                int interval = _settings.PollInterval;
                if (_pollFailStreak > 0)
                {
                    int shift = _pollFailStreak < 5 ? _pollFailStreak : 5;
                    long backoff = (long)interval << shift;
                    interval = backoff > PollBackoffMaxMilliseconds ? PollBackoffMaxMilliseconds : (int)backoff;
                }
                long elapsed = Environment.TickCount64 - started;
                int wait = interval - (int)elapsed;
                Thread.Sleep(wait < 50 ? 50 : wait);
            }
        }

        // COM2를 설정에 맞게 엽니다. 호출자 잠금(PortSync) 하에서만 실행됩니다.
        private static SerialPort OpenPort(GatewaySettings settings)
        {
            Configuration.SetPinFunction(settings.TxPin, DeviceFunction.COM2_TX);
            Configuration.SetPinFunction(settings.RxPin, DeviceFunction.COM2_RX);
            var port = new SerialPort(
                "COM2",
                settings.BaudRate,
                settings.Parity,
                settings.DataBits,
                settings.StopBits == 2 ? StopBits.Two : StopBits.One);
            if (settings.Rs485Mode == "rts")
            {
                // 네이티브 RTS가 송신 중(마지막 정지 비트까지) DE/RE를 제어합니다.
                // 포트가 닫힌 상태에서 열기 전에 설정해야 합니다.
                Configuration.SetPinFunction(settings.Rs485DePin, DeviceFunction.COM2_RTS);
                port.Mode = SerialMode.RS485;
            }
            port.Open();
            return port;
        }

        private static IModbusTransport CreateTransport(GatewaySettings settings, SerialPort port)
        {
            var channel = new SerialPortChannel(port);
            if (settings.Ascii)
                return new AsciiTransport(channel, settings.ResponseTimeout);
            return new RtuTransport(
                channel,
                settings.BaudRate,
                RtuReceiveMode.ResponseLength,
                settings.BitsPerCharacter,
                settings.ResponseTimeout);
        }

        // 지연 도착한 응답이나 에코가 포트에 남아 다음 프레임을 망가뜨립니다.
        // nanoFramework SerialPort에는 DiscardInBuffer가 없어 읽어서 버립니다.
        private static readonly byte[] _sink = new byte[256];

        private static void Drain()
        {
            SerialPort port = _port;
            if (port == null)
                return;
            try
            {
                for (int i = 0; i < 8; i++)
                {
                    int available = port.BytesToRead;
                    if (available == 0)
                        return;
                    port.Read(_sink, 0, available < _sink.Length ? available : _sink.Length);
                }
            }
            catch
            {
                // 포트가 막 교체된 상태라면 무시. 복구 워치독이 수습합니다.
            }
        }

        // 폴링 사이클이 유예시간을 넘기면 드라이버 행업으로 판단하고 포트를 다시 엽니다.
        private static void WatchdogLoop()
        {
            while (true)
            {
                Thread.Sleep(WatchdogPeriodMilliseconds);
                if (!GatewayHost.Ready)
                    continue;

                long grace = StallGraceMilliseconds();
                if (Environment.TickCount64 - _cycleTick < grace)
                    continue;

                // 복구에 성공하든 실패하든 다음 시도까지 동일 유예를 줍니다.
                _cycleTick = Environment.TickCount64;
                RecoverPort();
            }
        }

        private static void RecoverPort()
        {
            lock (PortSync)
            {
                Debug.WriteLine("Poll stall detected; reopening COM2.");
                SerialPort old = _port;
                _port = null;
                _client = null;
                try
                {
                    if (old != null)
                        old.Dispose();
                }
                catch
                {
                    // 행업된 읽기 스레드가 깨지며 예외를 던지는 것이 정상입니다.
                }

                try
                {
                    GatewaySettings settings = _settings;
                    _port = OpenPort(settings);
                    _client = new ModbusClient(CreateTransport(settings, _port));
                    GatewayStatistics.SetLastError("폴링 정지 감지: COM2를 다시 열었습니다.");
                }
                catch (Exception error)
                {
                    GatewayHost.Error = "UART 복구 실패: " + ErrorText.Describe(error);
                    Debug.WriteLine(GatewayHost.Error);
                }
            }
        }

        // 한 주기가 정상적으로 걸릴 수 있는 최악 시간 + 여유.
        private static long StallGraceMilliseconds()
        {
            long grace = (long)(_map.Length + 8) * (_settings.ResponseTimeout + 50)
                + _settings.PollInterval + 20000;
            if (grace < 45000)
                grace = 45000;
            if (grace > 300000)
                grace = 300000;
            return grace;
        }

        private static void PollFile(ModbusClient client)
        {
            MapPoint[] map = _map;
            var bits = new bool[1];
            var registers = new ushort[2];
            var bytes = new byte[4];

            for (int i = 0; i < map.Length; i++)
            {
                MapPoint p = map[i];
                GatewayStatistics.IncRequests();
                try
                {
                    double value;
                    lock (GatewayHost.ConfigurationSync)
                    {
                        Drain();
                        value = ReadValue(client, p, bits, registers, bytes);
                    }

                    value = value * p.Numerator / p.Denominator + p.Offset;
                    Publish(p, value, QualityGood);
                    GatewayStatistics.IncSuccesses();
                }
                catch (ModbusException error)
                {
                    GatewayStatistics.IncDeviceExceptions();
                    MarkFailed(p, "포인트 " + p.Id + " 장치 예외: " + error.Message);
                }
                catch (TimeoutException)
                {
                    GatewayStatistics.IncTimeouts();
                    MarkFailed(p, "포인트 " + p.Id + " 응답 없음");
                }
                catch (Exception error)
                {
                    GatewayStatistics.IncFrameErrors();
                    MarkFailed(p, "포인트 " + p.Id + " 오류: " + ErrorText.Describe(error));
                }
            }
        }

        private static double ReadValue(ModbusClient client, MapPoint p, bool[] bits, ushort[] registers, byte[] bytes)
        {
            ushort address = (ushort)p.Address;
            byte unit = p.Unit;
            if (p.Space == SpaceCoil)
            {
                client.ReadCoils(unit, address, 1, bits, 0);
                return bits[0] ? 1 : 0;
            }

            if (p.Space == SpaceDiscrete)
            {
                client.ReadDiscreteInputs(unit, address, 1, bits, 0);
                return bits[0] ? 1 : 0;
            }

            int count = p.Kind >= KindInt32 ? 2 : 1;
            if (p.Space == SpaceInput)
                client.ReadInputRegisters(unit, address, (ushort)count, registers, 0);
            else
                client.ReadHoldingRegisters(unit, address, (ushort)count, registers, 0);

            return DecodeRegisters(registers, 0, p.Kind, bytes, p.Order);
        }

        private static double DecodeRegisters(ushort[] registers, int at, int kind, byte[] bytes, int order)
        {
            switch (kind)
            {
                case KindInt16:
                    return (short)registers[at];
                case KindInt32:
                    return unchecked((int)Combine32(registers, at, order));
                case KindUInt32:
                    return Combine32(registers, at, order);
                case KindFloat32:
                    return BitsToSingle(Combine32(registers, at, order), bytes);
                default:
                    return registers[at];
            }
        }

        // 레지스터들은 Modbus 방식대로 레지스터마다 big-endian 순서로 첫 레지스터에 A,B, 둘째 레지스터에 C,D 바이트를 담습니다.
        private static uint Combine32(ushort[] registers, int at, int order)
        {
            ushort w0 = registers[at];
            ushort w1 = registers[at + 1];
            byte A = (byte)(w0 >> 8);
            byte B = (byte)w0;
            byte C = (byte)(w1 >> 8);
            byte D = (byte)w1;

            switch (order)
            {
                case OrderCDAB:
                    return ((uint)C << 24) | ((uint)D << 16) | ((uint)A << 8) | B;
                case OrderBADC:
                    return ((uint)B << 24) | ((uint)A << 16) | ((uint)D << 8) | C;
                case OrderDCBA:
                    return ((uint)D << 24) | ((uint)C << 16) | ((uint)B << 8) | A;
                default:
                    return ((uint)A << 24) | ((uint)B << 16) | ((uint)C << 8) | D;
            }
        }

        private static double BitsToSingle(uint bits, byte[] bytes)
        {
            // BitConverter.ToSingle를 위한 리틀엔디안 채움입니다 (모든 nanoFramework 타깃은 리틀엔디안).
            bytes[0] = (byte)bits;
            bytes[1] = (byte)(bits >> 8);
            bytes[2] = (byte)(bits >> 16);
            bytes[3] = (byte)(bits >> 24);
            return BitConverter.ToSingle(bytes, 0);
        }

        // double 표현으로부터 계산한 IEEE-754 단정밀도 비트입니다. 이 CLR에서는
        // 사용할 수 없는 BitConverter.GetBytes(float) 호출을 피합니다.
        private static uint SingleToBits(double value)
        {
            long d = BitConverter.DoubleToInt64Bits(value);
            uint sign = (uint)((d >> 63) & 1) << 31;
            int exp = (int)((d >> 52) & 0x7FF) - 1023 + 127;
            uint mant = (uint)((d & 0xFFFFFFFFFFFFFL) >> 29);

            if (exp >= 255)
                return sign | 0x7F800000;
            if (exp <= 0)
            {
                mant |= 0x800000;
                if (exp < -23)
                    return sign;
                return sign | (mant >> (1 - exp));
            }

            return sign | ((uint)exp << 23) | mant;
        }

        private static int OrderOf(string order)
        {
            if (order == "CDAB")
                return OrderCDAB;
            if (order == "BADC")
                return OrderBADC;
            if (order == "DCBA")
                return OrderDCBA;
            return OrderABCD;
        }

        // 최신 스케일 값과 품질을 저장하고 MQTT 스레드에 변경을 표시합니다.
        private static void Publish(MapPoint p, double value, byte flags)
        {
            lock (_sync)
            {
                p.Last = value;
                p.Flags = flags;
                if (flags != p.PublishedFlags || value != p.PublishedValue)
                {
                    p.Dirty = true;
                    p.PublishedValue = value;
                    p.PublishedFlags = flags;
                }
            }
        }

        private static void MarkFailed(MapPoint p, string message)
        {
            Publish(p, p.Last, QualityBad);
            GatewayStatistics.SetLastError(message);
        }

        // 현재 맵에 있는 RW 포인트의 명령 topic 목록입니다.
        private static string[] CommandTopics()
        {
            MapPoint[] map = _map;
            int n = 0;
            for (int i = 0; i < map.Length; i++)
                if (map[i].CmdTopic != null)
                    n++;

            var topics = new string[n];
            int at = 0;
            for (int i = 0; i < map.Length; i++)
                if (map[i].CmdTopic != null)
                    topics[at++] = map[i].CmdTopic;
            return topics;
        }

        // RW 포인트의 "<topic>/set" 명령을 처리합니다. connection.Poll 안에서
        // MQTT 스레드로 실행되며, 버스 락이 폴러에 대해 쓰기를 직렬화합니다.
        // 예외가 발생하면 연결이 폴트되므로 절대 던져선 안 되어 전부 여기서 잡습니다.
        private static void OnCommand(string topic, byte[] payload, bool retained, bool duplicate)
        {
            try
            {
                if (retained)
                    return; // 재전송된 retained 명령은 장치에 써서는 안 됩니다

                MapPoint[] map = _map;
                MapPoint p = null;
                for (int i = 0; i < map.Length; i++)
                {
                    if (map[i].CmdTopic == topic)
                    {
                        p = map[i];
                        break;
                    }
                }

                if (p == null)
                    return;

                string text = payload.Length == 0 ? "" : Encoding.UTF8.GetString(payload, 0, payload.Length).Trim();
                double value;
                if (!TryParseCommand(text, p.Kind == KindBool, out value))
                {
                    GatewayStatistics.SetLastError("쓰기 명령 값 오류 (" + p.Id + "): " + text);
                    return;
                }

                try
                {
                    lock (GatewayHost.ConfigurationSync)
                    {
                        Drain();
                        WriteValue(p, value);
                    }
                }
                catch (Exception error)
                {
                    GatewayStatistics.SetLastError("쓰기 실패 " + p.Id + ": " + ErrorText.Describe(error));
                    return;
                }

                GatewayStatistics.IncMqttWrites();
                Publish(p, value, QualityGood);
                Debug.WriteLine("MQTT write: " + topic + " = " + text + " -> " + p.Id);
            }
            catch (Exception)
            {
                // 넘어갑니다: 여기서 예외를 던지면 MQTT 연결이 폴트되기 때문입니다.
            }
        }

        // Coil 포인트는 0/1/true/false/on/off를 받고, register 포인트는 숫자를 받습니다.
        private static bool TryParseCommand(string text, bool isBool, out double value)
        {
            value = 0;
            if (text.Length == 0)
                return false;

            if (isBool)
            {
                if (text == "true" || text == "on")
                {
                    value = 1;
                    return true;
                }
                if (text == "false" || text == "off")
                    return true;
            }

            if (!double.TryParse(text, out value))
                return false;

            if (isBool)
                value = value != 0 ? 1 : 0;
            return true;
        }

        // 실값(engineering value) 하나를 매핑된 Modbus 포인트에 씁니다(역스케일 적용).
        private static void WriteValue(MapPoint p, double value)
        {
            ModbusClient client = _client;
            ushort address = (ushort)p.Address;

            if (p.Space == SpaceCoil)
            {
                client.WriteSingleCoil(p.Unit, address, value != 0);
                return;
            }

            double raw = (value - p.Offset) * p.Denominator / p.Numerator;
            if (!Fits(p.Kind, raw))
                throw new ArgumentOutOfRangeException("value");

            if (p.Kind == KindInt16 || p.Kind == KindUInt16)
            {
                client.WriteSingleRegister(p.Unit, address, unchecked((ushort)RoundToInt(raw)));
                return;
            }

            uint bits;
            if (p.Kind == KindFloat32)
                bits = SingleToBits(raw);
            else if (p.Kind == KindUInt32)
                bits = unchecked((uint)(long)raw);
            else
                bits = unchecked((uint)RoundToInt(raw));

            var registers = new ushort[2];
            Split32(bits, p.Order, registers);
            client.WriteMultipleRegisters(p.Unit, address, 2, registers, 0);
        }

        // Combine32의 역연산: 값의 바이트들을 포인트의 레지스터 순서에 따라 배치합니다.
        private static void Split32(uint bits, int order, ushort[] registers)
        {
            byte A = (byte)(bits >> 24);
            byte B = (byte)(bits >> 16);
            byte C = (byte)(bits >> 8);
            byte D = (byte)bits;

            switch (order)
            {
                case OrderCDAB:
                    registers[0] = (ushort)((C << 8) | D);
                    registers[1] = (ushort)((A << 8) | B);
                    break;
                case OrderBADC:
                    registers[0] = (ushort)((B << 8) | A);
                    registers[1] = (ushort)((D << 8) | C);
                    break;
                case OrderDCBA:
                    registers[0] = (ushort)((D << 8) | C);
                    registers[1] = (ushort)((B << 8) | A);
                    break;
                default:
                    registers[0] = (ushort)((A << 8) | B);
                    registers[1] = (ushort)((C << 8) | D);
                    break;
            }
        }

        private static bool IsWholeNumber(double value)
        {
            return value == (double)(long)value;
        }

        private static bool Fits(int kind, double raw)
        {
            if (raw != raw || raw > 1e300 || raw < -1e300)
                return false;

            if (kind == KindFloat32)
                return raw >= -3.40282301e+38 && raw <= 3.40282301e+38;

            if (!IsWholeNumber(raw))
                return false;

            if (kind == KindInt16)
                return raw >= -32768 && raw <= 32767;
            if (kind == KindUInt16)
                return raw >= 0 && raw <= 65535;
            if (kind == KindInt32)
                return raw >= -2147483648.0 && raw <= 2147483647.0;
            return raw >= 0 && raw <= 4294967295.0;
        }

        // broker 연결을 소유합니다: 접속, 게시, 백오프를 동반한 재접속을 수행합니다.
        private static void MqttLoop()
        {
            int backoff = 1000;

            while (true)
            {
                Socket socket = null;
                MqttConnection connection = null;
                try
                {
                    GatewaySettings settings = _settings;
                    IPAddress[] addresses = Dns.GetHostEntry(settings.BrokerHost).AddressList;
                    if (addresses.Length == 0)
                        throw new SocketException(SocketError.HostNotFound);

                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Connect(new IPEndPoint(addresses[0], settings.BrokerPort));

                    connection = new MqttConnection(new SocketMqttChannel(socket));
                    var options = new MqttConnectionOptions();
                    options.ClientId = settings.ClientId;
                    options.KeepAliveSeconds = (ushort)settings.KeepAlive;
                    options.CleanSession = true;
                    if (settings.Username.Length > 0)
                    {
                        options.UserName = settings.Username;
                        if (settings.Password.Length > 0)
                            options.Password = Encoding.UTF8.GetBytes(settings.Password);
                    }

                    string statusTopic = StatusTopic(settings);
                    options.WillTopic = statusTopic;
                    options.WillPayload = Encoding.UTF8.GetBytes("offline");
                    options.WillQos = MqttQos.AtLeastOnce;
                    options.WillRetain = true;

                    connection.Connect(options);
                    connection.Publish(statusTopic, Encoding.UTF8.GetBytes("online"), MqttQos.AtLeastOnce, true);

                    // RW 포인트: broker가 장치를 제어할 수 있도록 명령을 수신합니다.
                    string[] cmdTopics = CommandTopics();
                    if (cmdTopics.Length > 0)
                    {
                        connection.Received = OnCommand;
                        bool subscribed = true;
                        for (int i = 0; i < cmdTopics.Length; i++)
                            if (!connection.Subscribe(cmdTopics[i], MqttQos.AtLeastOnce))
                                subscribed = false;
                        if (!subscribed)
                            GatewayStatistics.SetLastError("MQTT 명령 토픽 구독을 브로커가 거부했습니다.");
                    }

                    GatewayStatistics.MqttConnected = 1;
                    backoff = 1000;
                    Debug.WriteLine("MQTT connected: " + settings.BrokerHost + ":" + settings.BrokerPort +
                        " (prefix " + settings.TopicPrefix + ", " + settings.PayloadFormat + "/" + settings.PublishMode + ")");

                    long lastSweep = 0;
                    while (true)
                    {
                        Pump(connection, ref lastSweep);
                        connection.Poll(SocketPollMilliseconds);
                    }
                }
                catch (Exception error)
                {
                    GatewayStatistics.MqttConnected = 0;
                    GatewayStatistics.IncMqttFailures();
                    GatewayStatistics.IncMqttReconnects();
                    GatewayStatistics.SetLastError("MQTT 연결 오류: " + ErrorText.Describe(error));
                    Debug.WriteLine(GatewayStatistics.LastError);

                    try
                    {
                        if (connection != null)
                            connection.Abort();
                    }
                    catch (Exception)
                    {
                    }

                    try
                    {
                        if (socket != null)
                            socket.Close();
                    }
                    catch (Exception)
                    {
                    }

                    Thread.Sleep(backoff);
                    backoff = backoff > ReconnectBackoffMax / 2 ? ReconnectBackoffMax : backoff * 2;
                }
            }
        }

        // 연결 스레드에서 발송 시점이 된 메시지를 게시합니다.
        private static void Pump(MqttConnection connection, ref long lastSweep)
        {
            GatewaySettings settings = _settings;
            MapPoint[] map = _map;
            long now = Environment.TickCount64;

            bool intervalMode = settings.PublishMode == "interval";
            bool due;
            if (intervalMode)
            {
                due = now - lastSweep >= settings.PublishInterval;
                if (!due)
                    return;
            }
            else
            {
                due = false;
                lock (_sync)
                {
                    for (int i = 0; i < map.Length; i++)
                    {
                        if (map[i].Dirty)
                        {
                            due = true;
                            break;
                        }
                    }
                }

                if (!due)
                    return;
            }

            lastSweep = now;
            MqttQos qos = QosOf(settings.Qos);

            if (settings.Batch == 1)
            {
                byte[] payload;
                lock (_sync)
                {
                    payload = BuildBatchPayload(map, settings, now);
                    for (int i = 0; i < map.Length; i++)
                        map[i].Dirty = false;
                }

                connection.Publish(settings.TopicPrefix + "/data", payload, qos, false);
                GatewayStatistics.IncMqttPublishes();
                return;
            }

            for (int i = 0; i < map.Length; i++)
            {
                MapPoint p = map[i];
                byte[] payload;
                lock (_sync)
                {
                    if (!intervalMode && !p.Dirty)
                        continue;

                    payload = BuildPayload(p, settings, now);
                    p.Dirty = false;
                }

                connection.Publish(TopicOf(p, settings), payload, qos, false);
                GatewayStatistics.IncMqttPublishes();
            }
        }

        private static MqttQos QosOf(int value)
        {
            if (value == 1)
                return MqttQos.AtLeastOnce;
            if (value == 2)
                return MqttQos.ExactlyOnce;
            return MqttQos.AtMostOnce;
        }

        private static string StatusTopic(GatewaySettings settings)
        {
            return settings.TopicPrefix + "/" + settings.ClientId + "/status";
        }

        private static string TopicOf(MapPoint p, GatewaySettings settings)
        {
            // topic은 항상 "prefix/point-ID"입니다: 신원은 topic이 담고, 페이로드에는 값/메타데이터만 담습니다.
            return settings.TopicPrefix + "/" + p.Id;
        }

        private static byte[] BuildPayload(MapPoint p, GatewaySettings settings, long now)
        {
            if (settings.PayloadFormat == "binary")
                return BinaryPayload(p);

            var text = new StringBuilder(96);
            if (settings.PayloadFormat == "plain")
            {
                text.Append(FormatValue(p));
                return Encoding.UTF8.GetBytes(text.ToString());
            }

            if (settings.PayloadFormat == "template")
            {
                ApplyTemplate(settings.Template, p, settings, text, now);
                return Encoding.UTF8.GetBytes(text.ToString());
            }

            var w = new JsonWriter(64);
            w.BeginObject().PropRaw("v", FormatValue(p)).Prop("q", p.Flags).Prop("t", now).EndObject();
            return Encoding.UTF8.GetBytes(w.ToString());
        }

        // 전체 포인트를 하나의 메시지로 만듭니다. 배치 템플릿이 행들을 감싸고, {points}는
        // 포인트당 한 행씩 설정된 구분자(쉼표 또는 개행)로 연결한 것으로 확장됩니다.
        private static byte[] BuildBatchPayload(MapPoint[] map, GatewaySettings settings, long now)
        {
            var rows = new StringBuilder(160);
            bool useTemplate = settings.PayloadFormat == "template";
            for (int i = 0; i < map.Length; i++)
            {
                if (i > 0)
                    rows.Append(settings.RowSeparator);

                if (useTemplate)
                {
                    ApplyTemplate(settings.Template, map[i], settings, rows, now);
                }
                else
                {
                    rows.Append('"').Append(JsonEscapeString(map[i].Id)).Append("\":");
                    rows.Append(map[i].Flags == QualityGood ? FormatValue(map[i]) : "null");
                }
            }

            var text = new StringBuilder(rows.Length + 64);
            string rowsText = rows.ToString();
            if (!useTemplate)
                rowsText = "{" + rowsText + "}"; // json 행은 객체 멤버이므로, 기본 템플릿이 유효한 JSON으로 유지되게 감쌉니다
            ApplyBatchTemplate(settings.BatchTemplate, rowsText, text, now);
            return Encoding.UTF8.GetBytes(text.ToString());
        }

        // 배치 플레이스홀더는 {points}와 {t}입니다. 어떤 알려진 토큰도 열지 않는 '{'는 리터럴로 남습니다.
        private static void ApplyBatchTemplate(string template, string rows, StringBuilder text, long now)
        {
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '\\' && TryUnescape(template, ref i, text))
                    continue;

                if (c == '{')
                {
                    if (i + 1 < template.Length && template[i + 1] == '{')
                    {
                        text.Append('{');
                        i++;
                        continue;
                    }

                    int end = template.IndexOf('}', i);
                    if (end < 0)
                    {
                        text.Append(c);
                        continue;
                    }

                    string token = template.Substring(i + 1, end - i - 1);
                    if (token != "points" && token != "t")
                    {
                        text.Append('{');
                        continue;
                    }

                    if (token == "points")
                        text.Append(rows);
                    else
                        text.Append(now);

                    i = end;
                }
                else if (c == '}')
                {
                    if (i + 1 < template.Length && template[i + 1] == '}')
                        i++;
                    text.Append('}');
                }
                else
                {
                    text.Append(c);
                }
            }
        }

        // 템플릿 안의 \n, \r, \t, \\는 렌더링 시 실제 문자가 되므로,
        // 줄 단위 형식도 한 줄짜리 설정 파일에 저장할 수 있습니다.
        private static bool TryUnescape(string template, ref int i, StringBuilder text)
        {
            if (i + 1 >= template.Length)
                return false;

            char next = template[i + 1];
            if (next == 'n')
                text.Append('\n');
            else if (next == 'r')
                text.Append('\r');
            else if (next == 't')
                text.Append('\t');
            else if (next == '\\')
                text.Append('\\');
            else
                return false;

            i++;
            return true;
        }

        // 템플릿 플레이스홀더: {id} {v} {q} {t} {topic} {addr} {space} {unit} {type}.
        // {{와 }}는 중괄호를 이스케이프하고 \n \t \\는 실제 문자로 렌더링되므로,
        // {"id":"{id}"} 같은 JSON 템플릿도 바깥 중괄호를 중복하지 않고 동작합니다.
        private static void ApplyTemplate(string template, MapPoint p, GatewaySettings settings, StringBuilder text, long now)
        {
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '\\' && TryUnescape(template, ref i, text))
                    continue;

                if (c == '{')
                {
                    if (i + 1 < template.Length && template[i + 1] == '{')
                    {
                        text.Append('{');
                        i++;
                        continue;
                    }

                    int end = template.IndexOf('}', i);
                    if (end < 0)
                    {
                        text.Append(c);
                        continue;
                    }

                    string token = template.Substring(i + 1, end - i - 1);
                    if (token == "id")
                        text.Append(JsonEscapeString(p.Id));
                    else if (token == "v")
                        text.Append(FormatValue(p));
                    else if (token == "q")
                        text.Append(p.Flags);
                    else if (token == "t")
                        text.Append(now);
                    else if (token == "topic")
                        text.Append(JsonEscapeString(TopicOf(p, settings)));
                    else if (token == "addr")
                        text.Append(p.Address);
                    else if (token == "space")
                        text.Append(SpaceName(p));
                    else if (token == "unit")
                        text.Append(p.Unit);
                    else if (token == "type")
                        text.Append(KindName(p.Kind));
                    else
                    {
                        text.Append('{');
                        continue;
                    }

                    i = end;
                }
                else if (c == '}')
                {
                    if (i + 1 < template.Length && template[i + 1] == '}')
                        i++;
                    text.Append('}');
                }
                else
                {
                    text.Append(c);
                }
            }
        }

        private static string SpaceName(MapPoint p)
        {
            if (p.Space == SpaceCoil)
                return "coil";
            if (p.Space == SpaceDiscrete)
                return "discrete";
            if (p.Space == SpaceInput)
                return "input";
            return "holding";
        }

        private static string KindName(int kind)
        {
            if (kind == KindBool)
                return "Bool";
            if (kind == KindInt16)
                return "Int16";
            if (kind == KindUInt16)
                return "UInt16";
            if (kind == KindInt32)
                return "Int32";
            if (kind == KindUInt32)
                return "UInt32";
            return "Float32";
        }

        private static byte[] BinaryPayload(MapPoint p)
        {
            if (p.Kind == KindBool)
                return new byte[] { (byte)(p.Last != 0 ? 1 : 0) };

            if (p.Kind == KindInt16 || p.Kind == KindUInt16)
            {
                int raw = RoundToInt(p.Last);
                return new byte[] { (byte)(raw >> 8), (byte)raw };
            }

            uint bits;
            if (p.Kind == KindFloat32)
                bits = SingleToBits(p.Last);
            else if (p.Kind == KindUInt32)
                bits = unchecked((uint)(long)p.Last);
            else
                bits = unchecked((uint)RoundToInt(p.Last));

            byte A = (byte)(bits >> 24);
            byte B = (byte)(bits >> 16);
            byte C = (byte)(bits >> 8);
            byte D = (byte)bits;
            switch (p.Order)
            {
                case OrderCDAB:
                    return new byte[] { C, D, A, B };
                case OrderBADC:
                    return new byte[] { B, A, D, C };
                case OrderDCBA:
                    return new byte[] { D, C, B, A };
                default:
                    return new byte[] { A, B, C, D };
            }
        }

        private static string FormatValue(MapPoint p)
        {
            if (p.Kind == KindBool)
                return p.Last != 0 ? "true" : "false";
            return NumberText(p.Last);
        }

        // nanoFramework의 double.ToString은 잡음이 많은 17자리(55.5 -> "55.499999999999998")를
        // 출력할 수 있고, 스케일된 포인트를 정수로 반올림하면 실제 소수부가 사라지므로 대신 고정소수점으로 렌더링합니다.
        private static string NumberText(double value)
        {
            if (value >= 1e15 || value <= -1e15)
                return value.ToString();

            bool negative = value < 0;
            if (negative)
                value = -value;

            long scaled = (long)(value * 1e6 + 0.5);
            long whole = scaled / 1000000;
            long frac = scaled % 1000000;

            var text = new StringBuilder(24);
            if (negative && (whole != 0 || frac != 0))
                text.Append('-');
            text.Append(whole);
            if (frac != 0)
            {
                string f = frac.ToString();
                while (f.Length < 6)
                    f = "0" + f;
                int end = 6;
                while (f[end - 1] == '0')
                    end--;
                text.Append('.').Append(f.Substring(0, end));
            }

            return text.ToString();
        }

        // nanoFramework에는 System.Math가 없으므로, 0에서 먼 방향으로 반올림합니다.
        private static int RoundToInt(double value)
        {
            return value >= 0 ? (int)(value + 0.5) : (int)(value - 0.5);
        }

        // 이스케이프 단일 출처를 위해 패키지의 JsonWriter.Escape에 위임합니다(따옴표는 호출자가 추가).
        private static string JsonEscapeString(string value)
        {
            return JsonWriter.Escape(value);
        }

        // 공유 포인트 파일에서 런타임 맵을 만듭니다. 잘못되거나 지원되지 않는 행은 건너뜁니다.
        private static void BuildMap()
        {
            var map = new MapPoint[PointMappingTable.MaximumPointCount];
            int count = 0;
            _skipped = 0;
            _skipNote = string.Empty;

            string fileError;
            Stream file = PointMappingStore.OpenRead(out fileError);
            if (file == null)
            {
                if (fileError != null)
                {
                    _skipNote = "포인트 파일을 읽지 못했습니다: " + fileError;
                    Debug.WriteLine(_skipNote);
                }
            }
            else
            {
                using (file)
                {
                    var line = new byte[PointMappingTable.MaximumLineLength];
                    var scratch = new byte[PointMappingTable.MaximumFieldBytes];
                    var chunk = new byte[512];
                    int length = 0;
                    bool overflowing = false;
                    int read;
                    while ((read = file.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            byte value = chunk[i];
                            if (value == (byte)'\n')
                            {
                                if (!overflowing)
                                {
                                    MapPoint point = ParseLine(line, length, scratch);
                                    if (point != null && count < map.Length)
                                        map[count++] = point;
                                }

                                length = 0;
                                overflowing = false;
                            }
                            else if (value != (byte)'\r')
                            {
                                if (length < line.Length)
                                    line[length++] = value;
                                else
                                    overflowing = true;
                            }
                        }
                    }

                    if (!overflowing && length > 0)
                    {
                        MapPoint point = ParseLine(line, length, scratch);
                        if (point != null && count < map.Length)
                            map[count++] = point;
                    }
                }
            }

            _map = new MapPoint[count];
            Array.Copy(map, _map, count);

            // RW 포인트는 "<publish topic>/set"에서 명령을 수신합니다.
            for (int i = 0; i < _map.Length; i++)
                if (_map[i].Writable)
                    _map[i].CmdTopic = TopicOf(_map[i], _settings) + "/set";
        }

        private static MapPoint ParseLine(byte[] line, int length, byte[] scratch)
        {
            if (length < 6 ||
                line[0] != (byte)'p' || line[1] != (byte)'o' || line[2] != (byte)'i' ||
                line[3] != (byte)'n' || line[4] != (byte)'t' || line[5] != (byte)'=')
                return null;

            PointMapping point;
            bool canonical;
            string error;
            if (!PointMappingTable.TryParseRecord(line, length, scratch, out point, out canonical, out error))
            {
                NoteSkipped(error);
                return null;
            }

            return ToMapPoint(point);
        }

        private static MapPoint ToMapPoint(PointMapping point)
        {
            string error = point.Validate();
            if (error != null)
            {
                NoteSkipped(error);
                return null;
            }

            if (point.Access == PointAccess.WriteOnly)
            {
                NoteSkipped(point.Id + ": 쓰기 전용 포인트는 폴링할 수 없습니다.");
                return null;
            }

            string sourceProtocol = point.Source.Protocol;
            if (sourceProtocol != "modbus-rtu" && sourceProtocol != "modbus-ascii" && sourceProtocol != "modbus-tcp")
            {
                NoteSkipped(point.Id + ": 원본 프로토콜은 modbus-rtu/ascii/tcp여야 합니다.");
                return null;
            }

            if (point.Target.Protocol != "mqtt")
            {
                NoteSkipped(point.Id + ": 목적지 프로토콜은 mqtt여야 합니다.");
                return null;
            }

            // 멀티드롭: 각 포인트는 device 필드에 자신의 Modbus unit을 담습니다.
            // 일반적인 "1".."247" 표기와 옛 UI가 작성한 레거시 "unit-N" 형식을 모두 받습니다.
            int unit;
            string device = point.Source.DeviceId;
            if (device.Length > 5 && device.Substring(0, 5) == "unit-")
                device = device.Substring(5);
            if (device.Length == 0)
                unit = 1;
            else if (!TryParseAddress(device, out unit) || unit < 1 || unit > 247)
            {
                NoteSkipped(point.Id + ": 원본 Unit ID 형식이 올바르지 않습니다: " + point.Source.DeviceId);
                return null;
            }

            int space;
            string sourceSpace = point.Source.AddressSpace;
            if (sourceSpace == "coil")
                space = SpaceCoil;
            else if (sourceSpace == "discrete-input")
                space = SpaceDiscrete;
            else if (sourceSpace == "input-register")
                space = SpaceInput;
            else if (sourceSpace == "holding-register")
                space = SpaceHolding;
            else
            {
                NoteSkipped(point.Id + ": 지원하지 않는 원본 주소 영역입니다: " + sourceSpace);
                return null;
            }

            int kind;
            string dataType = point.Source.DataType;
            string baseType = ProtocolAddress.BaseTypeOf(dataType);
            if (baseType == "Bool")
                kind = KindBool;
            else if (baseType == "Int16")
                kind = KindInt16;
            else if (baseType == "UInt16")
                kind = KindUInt16;
            else if (baseType == "Int32")
                kind = KindInt32;
            else if (baseType == "UInt32")
                kind = KindUInt32;
            else if (baseType == "Float32")
                kind = KindFloat32;
            else
            {
                NoteSkipped(point.Id + ": 지원하지 않는 원본 자료형입니다: " + dataType);
                return null;
            }

            if ((kind == KindBool) != (space == SpaceCoil || space == SpaceDiscrete))
            {
                NoteSkipped(point.Id + ": Bool은 Coil/Discrete Input 영역에서만, Register 자료형은 Register 영역에서만 사용할 수 있습니다.");
                return null;
            }

            int address;
            if (!TryParseAddress(point.Source.Address, out address))
            {
                NoteSkipped(point.Id + ": 원본 주소 형식이 올바르지 않습니다: " + point.Source.Address);
                return null;
            }

            // 주소는 VagabondK.nF.Modbus API와 일치하도록 0기준 원시 인덱스(0~65535)만 엄격하게 사용합니다.
            if (address < 0 || address > 65535)
            {
                NoteSkipped(point.Id + ": 원본 주소가 범위를 넘습니다: " + point.Source.Address);
                return null;
            }

            if (point.Target.AddressSpace != "topic")
            {
                NoteSkipped(point.Id + ": 지원하지 않는 목적지 주소 영역입니다: " + point.Target.AddressSpace);
                return null;
            }

            // 목적지 주소 필드는 사용하지 않습니다: 게시 topic은 항상 "prefix/point-ID"입니다(TopicOf 참조).

            // RW 포인트는 "<publish topic>/set"에서의 MQTT 쓰기도 받습니다(OnCommand 참조).
            bool writable = point.Access == PointAccess.ReadWrite &&
                (space == SpaceCoil || space == SpaceHolding);

            var result = new MapPoint();
            result.Id = point.Id;
            result.Space = space;
            result.Address = address;
            result.Kind = kind;
            result.Order = OrderOf(ProtocolAddress.ByteOrderOf(dataType));
            result.Numerator = point.ScaleNumerator;
            result.Denominator = point.ScaleDenominator;
            result.Offset = point.Offset;
            result.Unit = (byte)unit;
            result.Writable = writable;
            result.Flags = QualityBad;
            result.Dirty = true;
            return result;
        }

        private static bool TryParseAddress(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 10)
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
                int next = value * 10 + (text[i] - '0');
                if (next < value)
                    return false;
                value = next;
            }

            return true;
        }

        private static void NoteSkipped(string message)
        {
            _skipped++;
            if (_skipNote.Length == 0)
            {
                _skipNote = message;
                Debug.WriteLine("ModbusToMqtt skipped point: " + message);
            }
        }
    }
}
