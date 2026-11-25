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
using VagabondK.nF.DNP3;
using VagabondK.nF.DNP3.TCP;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.ASCII;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.RTU;
using VagabondK.nF.Modbus.Serial;

namespace nanoGate.ModbusToDnp3
{
    // Modbus RTU/ASCII(UART) -> TCP 기반 DNP3 아웃스테이션 변환. nanoGate.Core가 호스트합니다.
    // 폴러 스레드가 매핑된 Modbus 포인트를 읽어 Dnp3Database에 캐싱하고,
    // DNP3 마스터가 그 캐시를 TCP로 읽습니다. 포인트 변경은 재부팅 후 적용됩니다.
    internal sealed class ModbusToDnp3Profile : IGatewayProfile
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

        // 두 Modbus 레지스터에 걸친 32비트 바이트 순서(A = 최상위 바이트).
        private const int OrderABCD = 0;
        private const int OrderCDAB = 1;
        private const int OrderBADC = 2;
        private const int OrderDCBA = 3;

        // nanoFramework의 모든 하드웨어 타깃(ESP32, STM32)은 리틀엔디안이므로, 아래 float 헬퍼는
        // BitConverter 바이트 배열에 LSB부터 값을 채웁니다.

        // DNP3 quality flag: 비트 0은 Online, 비트 4는 Error.
        private const byte FlagOnline = 0x01;
        private const byte FlagErrorAnalog = 0x10;

        private const int Dnp3IdleTimeoutMilliseconds = 120000;
        private const int SocketPollMicroseconds = 20000;

        private sealed class MapPoint
        {
            public string Id;
            public int Space;
            public int Address;
            public int Kind;
            public int Order;
            public int Unit;    // 0 = 게이트웨이 unit id(동적 멀티드롭), 그 외에는 고정 슬레이브
            public Dnp3PointType Type;
            public ushort Index;
            public int Numerator;
            public int Denominator;
            public int Offset;
            public bool Writable;
            // 상태 페이지를 위해 폴러가 작성하는 마지막 스케일 적용 값과 quality.
            public double Last;
            public byte Flags;
        }

        private static GatewaySettings _settings;
        private static MapPoint[] _map = new MapPoint[0];
        private static RangeRule[] _rules = new RangeRule[0];
        private static int _rulePoints;
        private static Dnp3Database _database;
        private static int _skipped;
        private static string _skipNote = string.Empty;

        public string Id
        {
            get { return GatewaySettings.GatewayProfile; }
        }

        public string CapabilitiesJson
        {
            get
            {
                return "{\"byteOrder\":true,"
                    + "\"addr\":{\"s\":{\"show\":[\"s\",\"a\",\"t\"],\"spaces\":[\"coil\",\"discrete-input\",\"input-register\",\"holding-register\"]},"
                    + "\"t\":{\"show\":[\"s\",\"a\"],\"spaces\":[\"binary-input\",\"binary-output\",\"analog-input\",\"analog-output\",\"counter\"]}},"
                    + "\"cacheCols\":{\"head\":[\"@rule\",\"Unit\",\"Modbus\",\"DNP3\"]}}";
            }
        }

        public string SettingsKeysJson
        {
            get { return "[\"protocol\",\"baudRate\",\"dataBits\",\"parity\",\"stopBits\",\"timeout\",\"dnp3Port\",\"localAddress\",\"masterAddress\",\"pollInterval\",\"txPin\",\"rxPin\"]"; }
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
                .Prop("dnp3Port", settings.Dnp3Port)
                .Prop("localAddress", settings.LocalAddress)
                .Prop("masterAddress", settings.MasterAddress)
                .Prop("pollInterval", settings.PollInterval)
                .Prop("ruleCount", _rules.Length)
                .Prop("pointCount", _map.Length)
                .Prop("skippedPoints", _skipped);

            if (_skipped > 0 && _skipNote.Length > 0)
                w.Prop("skipNote", _skipNote);

            if (_database != null)
            {
                w.Prop("dnp3Events", _database.EventCount)
                    .Prop("dnp3Overflow", _database.Overflow);
            }

            w.Prop("statsSince", GatewayStatistics.ResetAtSeconds)
                .Prop("polls", GatewayStatistics.Polls)
                .Prop("requests", GatewayStatistics.Requests)
                .Prop("successes", GatewayStatistics.Successes)
                .Prop("timeouts", GatewayStatistics.Timeouts)
                .Prop("frameErrors", GatewayStatistics.FrameErrors)
                .Prop("deviceExceptions", GatewayStatistics.DeviceExceptions)
                .Prop("dnp3Connections", GatewayStatistics.Dnp3Connections)
                .Prop("lastError", GatewayStatistics.LastError);
        }

        public bool HasPointCache
        {
            get { return true; }
        }

        // /api/cache 엔드포인트용 캐시 전체 실시간 값; DNP3 quality 비트 0은 Online, 비트 4는 Error.
        public void WriteCache(JsonWriter w)
        {
            MapPoint[] map = _map;
            for (int i = 0; i < map.Length; i++)
            {
                byte flags = map[i].Flags;
                bool online = (flags & 1) != 0;
                string quality = online ? "Online" : ((flags & 16) != 0 ? "오류" : "Offline");
                w.BeginObject()
                    .Prop("id", map[i].Id)
                    .Prop("v", NumberText(map[i].Last))
                    .Prop("q", quality)
                    .Prop("ok", online)
                    .EndObject();
            }
        }

        // nanoFramework의 double.ToString은 잡음이 많은 17자리(55.5 -> "55.499999999999998")를 출력할 수 있으므로 대신 고정소수점으로 표시합니다.
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
                    // 네이티브 RTS가 송신 중(마지막 정지 비트까지) DE/RE를 제어합니다.
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
                serialTransport = new AsciiTransport(channel, settings.ResponseTimeout);
            else
                serialTransport = new RtuTransport(
                    channel,
                    settings.BaudRate,
                    RtuReceiveMode.ResponseLength,
                    settings.BitsPerCharacter,
                    settings.ResponseTimeout);

            var client = new ModbusClient(serialTransport);

            BuildMap();
            var definitions = new Dnp3PointDefinition[_map.Length];
            for (int i = 0; i < _map.Length; i++)
            {
                MapPoint p = _map[i];
                Dnp3Value initial = p.Type == Dnp3PointType.Analog || p.Type == Dnp3PointType.AnalogOutputStatus
                    ? Dnp3Value.Analog(0.0)
                    : Dnp3Value.Bits(0);
                definitions[i] = new Dnp3PointDefinition(p.Type, p.Index, initial);
            }

            _database = new Dnp3Database(definitions, _map.Length > 128 ? 256 : 128);
            var station = new Dnp3Station(
                _database,
                (ushort)settings.LocalAddress,
                (ushort)settings.MasterAddress,
                new ControlHandler(client),
                null,
                256,
                30000,
                2,
                5000,
                3000);

            Socket listener;
            try
            {
                listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Any, settings.Dnp3Port));
                listener.Listen(1);
            }
            catch (SocketException error)
            {
                GatewayHost.Error = "DNP3 포트 " + settings.Dnp3Port + " 열기 실패: " + error.ErrorCode;
                Debug.WriteLine(GatewayHost.Error);
                return;
            }

            var poller = new Thread(() => PollLoop(client));
            poller.Start();

            GatewayHost.Ready = true;
            Debug.WriteLine(
                "nanoGate ready: " + settings.ProtocolName + " " + settings.BaudRate + " (TX " + settings.TxPin +
                ", RX " + settings.RxPin + ") -> DNP3 Outstation TCP " + settings.Dnp3Port +
                " (local " + settings.LocalAddress + ", master " + settings.MasterAddress + "), " +
                _map.Length + " points, poll " + settings.PollInterval + " ms.");

            while (true)
            {
                Socket connection;
                try
                {
                    connection = listener.Accept();
                }
                catch (SocketException error)
                {
                    Debug.WriteLine("DNP3 accept failed: " + error.ErrorCode);
                    Thread.Sleep(1000);
                    continue;
                }

                using (connection)
                    Serve(connection, station);
            }
        }

        // DNP3 마스터는 한 번에 하나만 처리하며, 유휴 또는 반열림(half-open) 연결은 120초 후에 끊습니다.
        private static void Serve(Socket connection, Dnp3Station station)
        {
            Interlocked.Increment(ref GatewayStatistics.Dnp3Connections);
            station.ResetConnection();
            var session = new Dnp3TcpSession(connection, station, 3000, 3000);

            try
            {
                long lastRx = Environment.TickCount64;
                while (Environment.TickCount64 - lastRx < Dnp3IdleTimeoutMilliseconds)
                {
                    if (connection.Poll(SocketPollMicroseconds, SelectMode.SelectRead))
                    {
                        if (connection.Available == 0)
                            return;

                        lastRx = Environment.TickCount64;
                    }

                    session.RunOnce(0);
                }

                Debug.WriteLine("Closing idle DNP3 connection.");
            }
            catch (Dnp3ProtocolException error)
            {
                Debug.WriteLine("Closing DNP3 connection after protocol error: " + error.Message);
            }
            catch (SocketException error)
            {
                Debug.WriteLine("Closing DNP3 connection after socket error: " + error.ErrorCode);
            }
            catch (IOException error)
            {
                Debug.WriteLine("Closing DNP3 connection after I/O error: " + error.Message);
            }
            catch (TimeoutException error)
            {
                Debug.WriteLine("Closing DNP3 connection after timeout: " + error.Message);
            }
            finally
            {
                Interlocked.Decrement(ref GatewayStatistics.Dnp3Connections);
            }
        }

        // RTU 링크가 무음이면(슬레이브에 전원이 없음) 모든 읽기가 TimeoutException을 던지고,
        // nanoFramework는 예외가 발생할 때마다 콘솔 트레이스를 출력해 웹 스레드를
        // 기아 상태로 만듭니다. 한 주기 전체가 실패하면 지수 백오프로 전환하고,
        // 다음 주기가 성공하면 즉시 정상 주기로 복귀합니다.
        private const int PollBackoffMaxMilliseconds = 15000;
        private static int _pollFailStreak;

        // 매 주기마다 매핑된 Modbus 포인트를 한 번씩 읽어 DNP3 데이터베이스를 갱신합니다.
        private static void PollLoop(object clientObject)
        {
            var client = (ModbusClient)clientObject;

            while (true)
            {
                long started = Environment.TickCount64;
                long successesBefore = GatewayStatistics.Successes;
                // 주기 예외가 절대 루프를 종료시키면 안 되며, 대신 상태 페이지에 표시합니다.
                try
                {
                    PollRules(client);
                    PollFile(client);
                }
                catch (Exception error)
                {
                    GatewayHost.Error = "폴링 사이클 오류: " + ErrorText.Describe(error);
                    Debug.WriteLine(GatewayHost.Error);
                }

                GatewayStatistics.IncPolls();
                bool hasWork = _rules.Length > 0 || _map.Length > _rulePoints;
                if (!hasWork || GatewayStatistics.Successes != successesBefore)
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

        // unit이 0인 포인트는 레거시 "동적" 행입니다. 게이트웨이 unit-id 설정이 폐기되었으므로,
        // 이제 0은 Modbus 기본 unit인 1을 단순히 의미합니다.
        private static byte UnitOf(MapPoint p)
        {
            return p.Unit != 0 ? (byte)p.Unit : (byte)1;
        }

        private static void PollFile(ModbusClient client)
        {
            MapPoint[] map = _map;
            var bits = new bool[1];
            var registers = new ushort[2];
            var bytes = new byte[4];

            for (int i = _rulePoints; i < map.Length; i++)
            {
                MapPoint p = map[i];
                GatewayStatistics.IncRequests();
                try
                {
                    double value;
                    lock (GatewayHost.ConfigurationSync)
                        value = ReadValue(client, UnitOf(p), p, bits, registers, bytes);

                    value = value * p.Numerator / p.Denominator + p.Offset;
                    p.Last = value;
                    p.Flags = FlagOnline;
                    Publish(p, value, FlagOnline);
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

        // 범위 규칙은 각 영역을 블록 요청으로 읽어 값을 포인트들에게 분배합니다.
        // 규칙 포인트들은 _map의 앞 부분을 규칙 순서대로 차지합니다.
        private static void PollRules(ModbusClient client)
        {
            RangeRule[] rules = _rules;
            MapPoint[] map = _map;
            int offset = 0;
            for (int r = 0; r < rules.Length; r++)
            {
                RangeRule rule = rules[r];
                int count = map.Length - offset < rule.Count ? map.Length - offset : rule.Count;
                // 폴링 헬퍼는 증가분이 아니라 map 안의 절대 끝 위치를 반환합니다.
                if (rule.Space == RangeStore.SpaceCoil || rule.Space == RangeStore.SpaceDiscrete)
                    offset = PollRuleBits(client, rule, map, offset, count);
                else
                    offset = PollRuleRegisters(client, rule, map, offset, count);
            }
        }

        private static int PollRuleBits(ModbusClient client, RangeRule rule, MapPoint[] map, int start, int count)
        {
            if (count == 0)
                return start;

            byte unit = rule.Unit != 0 ? (byte)rule.Unit : (byte)1;
            bool coils = rule.Space == RangeStore.SpaceCoil;
            var values = new bool[count];
            try
            {
                int at = 0;
                while (at < count)
                {
                    int chunk = count - at < 2000 ? count - at : 2000;
                    GatewayStatistics.IncRequests();
                    lock (GatewayHost.ConfigurationSync)
                    {
                        if (coils)
                            client.ReadCoils(unit, (ushort)(rule.From + at), (ushort)chunk, values, at);
                        else
                            client.ReadDiscreteInputs(unit, (ushort)(rule.From + at), (ushort)chunk, values, at);
                    }
                    GatewayStatistics.IncSuccesses();
                    at += chunk;
                }

                for (int i = 0; i < count; i++)
                {
                    MapPoint p = map[start + i];
                    p.Last = values[i] ? 1 : 0;
                    p.Flags = FlagOnline;
                    Publish(p, p.Last, FlagOnline);
                }
            }
            catch (Exception error)
            {
                FailArea(map, start, count, coils ? "coil" : "discrete-input", error);
            }

            return start + count;
        }

        private static int PollRuleRegisters(ModbusClient client, RangeRule rule, MapPoint[] map, int start, int count)
        {
            if (count == 0)
                return start;

            byte unit = rule.Unit != 0 ? (byte)rule.Unit : (byte)1;
            bool inputs = rule.Space == RangeStore.SpaceInput;
            int width = RangeStore.WidthOf(rule.Kind);
            int total = count * width;
            var registers = new ushort[total];
            var bytes = new byte[4];
            try
            {
                int at = 0;
                while (at < total)
                {
                    int chunk = total - at < 125 ? total - at : 125;
                    GatewayStatistics.IncRequests();
                    lock (GatewayHost.ConfigurationSync)
                    {
                        if (inputs)
                            client.ReadInputRegisters(unit, (ushort)(rule.From + at), (ushort)chunk, registers, at);
                        else
                            client.ReadHoldingRegisters(unit, (ushort)(rule.From + at), (ushort)chunk, registers, at);
                    }
                    GatewayStatistics.IncSuccesses();
                    at += chunk;
                }

                for (int i = 0; i < count; i++)
                {
                    MapPoint p = map[start + i];
                    double value = DecodeRegisters(registers, i * width, p.Kind, bytes, p.Order);
                    value = value * p.Numerator / p.Denominator + p.Offset;
                    p.Last = value;
                    p.Flags = FlagOnline;
                    Publish(p, value, FlagOnline);
                }
            }
            catch (Exception error)
            {
                FailArea(map, start, count, inputs ? "input-register" : "holding-register", error);
            }

            return start + count;
        }

        private static void FailArea(MapPoint[] map, int start, int count, string area, Exception error)
        {
            if (error is ModbusException)
                GatewayStatistics.IncDeviceExceptions();
            else if (error is TimeoutException)
                GatewayStatistics.IncTimeouts();
            else
                GatewayStatistics.IncFrameErrors();

            for (int i = 0; i < count; i++)
                MarkFailed(map[start + i], "범위 규칙 " + area + " 영역 읽기 실패: " + ErrorText.Describe(error));
        }

        private static double ReadValue(ModbusClient client, byte unitId, MapPoint p, bool[] bits, ushort[] registers, byte[] bytes)
        {
            ushort address = (ushort)p.Address;
            if (p.Space == SpaceCoil)
            {
                client.ReadCoils(unitId, address, 1, bits, 0);
                return bits[0] ? 1 : 0;
            }

            if (p.Space == SpaceDiscrete)
            {
                client.ReadDiscreteInputs(unitId, address, 1, bits, 0);
                return bits[0] ? 1 : 0;
            }

            int count = p.Kind >= KindInt32 ? 2 : 1;
            if (p.Space == SpaceInput)
                client.ReadInputRegisters(unitId, address, (ushort)count, registers, 0);
            else
                client.ReadHoldingRegisters(unitId, address, (ushort)count, registers, 0);

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

        // 각 레지스터는 Modbus의 레지스터별 빅엔디안 순서에 따라 첫 번째에 바이트 A,B, 두 번째에 C,D를 담습니다.
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
            // BitConverter.ToSingle용 리틀엔디안 채움(nanoFramework의 모든 타깃은 리틀엔디안입니다).
            bytes[0] = (byte)bits;
            bytes[1] = (byte)(bits >> 8);
            bytes[2] = (byte)(bits >> 16);
            bytes[3] = (byte)(bits >> 24);
            return BitConverter.ToSingle(bytes, 0);
        }

        // double 표현으로부터 계산한 IEEE-754 단정도 부동소수점 비트; 이 CLR에서는 사용할 수 없는
        // BitConverter.GetBytes(float)를 피합니다.
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

        // 32비트 값(A = 최상위 바이트)을 지정된 순서로 두 레지스터에 나눕니다.
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

        private static void Publish(MapPoint p, double value, byte flags)
        {
            Dnp3Value pointValue;
            if (p.Type == Dnp3PointType.Analog || p.Type == Dnp3PointType.AnalogOutputStatus)
                pointValue = Dnp3Value.Analog(value, flags);
            else if (p.Type == Dnp3PointType.Binary || p.Type == Dnp3PointType.BinaryOutputStatus)
                pointValue = Dnp3Value.Bits(value != 0 ? 1u : 0u, flags);
            else
                pointValue = Dnp3Value.Bits(value > 0 ? unchecked((ulong)value) : 0u, flags);

            _database.Update(p.Type, p.Index, pointValue);
        }

        private static void MarkFailed(MapPoint p, string message)
        {
            p.Flags = p.Type == Dnp3PointType.Analog || p.Type == Dnp3PointType.AnalogOutputStatus
                ? FlagErrorAnalog : (byte)0;
            Publish(p, p.Last, p.Flags);
            GatewayStatistics.SetLastError(message);
        }

        // 런타임 맵을 구성합니다: 범위 규칙이 먼저(블록 폴링), 그 다음 포인트 파일 행입니다.
        private static void BuildMap()
        {
            MigrateAutoRules();

            _skipNote = string.Empty;
            string rulesError;
            string rulesText = RangeStore.ReadAll(out rulesError);
            RangeRule[] rules = new RangeRule[0];
            string parseError = rulesError;
            if (parseError == null)
                parseError = RangeStore.Parse(rulesText, out rules);
            if (parseError != null)
            {
                rules = new RangeRule[0];
                _skipNote = "범위 규칙 파일을 사용하지 못합니다: " + parseError;
                Debug.WriteLine(_skipNote);
            }

            // unitId 설정은 폐기되었습니다: 레거시 동적(unit=0) 규칙에 오래된 설정 unit 값을
            // 한 번만 입혀, 해당 설정 없이도 멀티드롭 동작이 유지되게 합니다.
            if (_settings.UnitId > 1 && rules.Length > 0)
            {
                bool stamped = false;
                for (int r = 0; r < rules.Length; r++)
                    if (rules[r].Unit == 0)
                    {
                        rules[r].Unit = _settings.UnitId;
                        stamped = true;
                    }
                if (stamped && RangeStore.Save(RangeStore.Serialize(rules)) == null)
                {
                    _settings.UnitId = 1;
                    _settings.Save();
                }
            }

            _rules = rules;

            // 규칙 포인트는 32행 포인트 파일 상한의 적용을 받지 않으므로, 두 소스 모두에 맞는 크기로 맵을 잡습니다.
            int ruleTotal = 0;
            for (int r = 0; r < rules.Length; r++)
                ruleTotal += rules[r].Count;

            var map = new MapPoint[PointMappingTable.MaximumPointCount + ruleTotal];
            int count = 0;
            _skipped = 0;

            for (int r = 0; r < rules.Length; r++)
            {
                RangeRule rule = rules[r];
                int width = RangeStore.WidthOf(rule.Kind);
                for (int i = 0; i < rule.Count && count < map.Length; i++)
                {
                    var p = new MapPoint();
                    p.Id = "규칙" + (r + 1) + "-" + i;
                    p.Space = rule.Space;
                    p.Address = rule.From + i * width;
                    p.Kind = rule.Kind;
                    p.Order = rule.Order;
                    p.Unit = rule.Unit;
                    p.Type = RuleTargetType(rule.To);
                    p.Index = (ushort)(rule.Index + i);
                    p.Numerator = rule.Num;
                    p.Denominator = rule.Den;
                    p.Offset = rule.Off;
                    p.Writable = rule.Writable;
                    p.Flags = 0;
                    map[count++] = p;
                }
            }

            _rulePoints = count;

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
                                    MapPoint point = ParseLine(line, length, scratch, map, count);
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
                        MapPoint point = ParseLine(line, length, scratch, map, count);
                        if (point != null && count < map.Length)
                            map[count++] = point;
                    }
                }
            }

            _map = new MapPoint[count];
            Array.Copy(map, _map, count);
        }

        // 일회성 마이그레이션: 레거시 자동 모드 설정을 동등한 범위 규칙으로 바꾸고,
        // 그 다음 auto 키를 제외한 설정 파일을 다시 저장합니다.
        private static void MigrateAutoRules()
        {
            GatewaySettings settings = _settings;
            if (settings.MappingMode != "auto")
                return;
            if (settings.AutoCoils == 0 && settings.AutoDiscretes == 0 &&
                settings.AutoInputCount == 0 && settings.AutoHoldingCount == 0)
                return;

            string readError;
            string existing = RangeStore.ReadAll(out readError);
            RangeRule[] probe;
            if (readError == null && RangeStore.Parse(existing, out probe) == null && probe.Length > 0)
                return;

            string text = RangeStore.BuildAutoRules(settings.AutoCoils, settings.AutoDiscretes,
                settings.AutoInputType, settings.AutoInputCount,
                settings.AutoHoldingType, settings.AutoHoldingCount, settings.AutoByteOrder);
            string saveError = RangeStore.Save(text);
            if (saveError != null)
            {
                Debug.WriteLine("자동 매핑 마이그레이션 실패: " + saveError);
                return;
            }

            settings.MappingMode = "file";
            string rewriteError = settings.Save();
            Debug.WriteLine(rewriteError == null
                ? "자동 매핑 설정을 범위 규칙으로 옮겼습니다."
                : "마이그레이션 후 설정 저장 실패: " + rewriteError);
        }

        private static Dnp3PointType RuleTargetType(int to)
        {
            if (to == RangeStore.ToBinary)
                return Dnp3PointType.Binary;
            if (to == RangeStore.ToBinaryOutput)
                return Dnp3PointType.BinaryOutputStatus;
            if (to == RangeStore.ToAnalogOutput)
                return Dnp3PointType.AnalogOutputStatus;
            if (to == RangeStore.ToCounter)
                return Dnp3PointType.Counter;
            return Dnp3PointType.Analog;
        }

        // DNP3 Select/Operate와 Direct Operate 명령을 Modbus 쓰기로 실행합니다.
        // DNP3 연결 스레드에서 동작하며, 버스 락이 폴러와의 직렬화를 보장합니다.
        private sealed class ControlHandler : IDnp3ControlHandler
        {
            private readonly ModbusClient _client;

            public ControlHandler(ModbusClient client)
            {
                _client = client;
            }

            public Dnp3CommandStatus Validate(Dnp3Command command)
            {
                MapPoint point = Find(command);
                if (point == null)
                    return Dnp3CommandStatus.NotSupported;
                if (!point.Writable)
                    return Dnp3CommandStatus.NotAuthorized;

                if (command.Group == 41)
                {
                    double raw = (command.AnalogValue - point.Offset) * point.Denominator / point.Numerator;
                    if (!Fits(point.Kind, raw))
                        return Dnp3CommandStatus.OutOfRange;
                }

                return Dnp3CommandStatus.Success;
            }

            public Dnp3CommandStatus Operate(Dnp3Command command, bool selected)
            {
                MapPoint point = Find(command);
                if (point == null)
                    return Dnp3CommandStatus.NotSupported;
                if (!point.Writable)
                    return Dnp3CommandStatus.NotAuthorized;

                double value;
                try
                {
                    lock (GatewayHost.ConfigurationSync)
                    {
                        if (command.Group == 12)
                        {
                            // 펄스 코드는 레벨을 한 번만 쓰며, 시간 기반 재펄스는 에뮬레이션하지 않습니다.
                            int action = command.ControlCode & 15;
                            bool on = action == 1 || action == 3;
                            _client.WriteSingleCoil(UnitOf(point), (ushort)point.Address, on);
                            value = on ? 1 : 0;
                        }
                        else
                        {
                            double raw = (command.AnalogValue - point.Offset) * point.Denominator / point.Numerator;
                            if (!Fits(point.Kind, raw))
                                return Dnp3CommandStatus.OutOfRange;
                            WriteRegisters(point, raw);
                            value = command.AnalogValue;
                        }
                    }
                }
                catch (Exception error)
                {
                    GatewayStatistics.SetLastError("쓰기 실패 " + point.Id + ": " + ErrorText.Describe(error));
                    return error is ModbusException ? Dnp3CommandStatus.DownstreamFailure : Dnp3CommandStatus.HardwareError;
                }

                point.Last = value;
                point.Flags = FlagOnline;
                Publish(point, value, FlagOnline);
                return Dnp3CommandStatus.Success;
            }

            private static MapPoint Find(Dnp3Command command)
            {
                Dnp3PointType type = command.Group == 12
                    ? Dnp3PointType.BinaryOutputStatus : Dnp3PointType.AnalogOutputStatus;
                MapPoint[] map = _map;
                for (int i = 0; i < map.Length; i++)
                    if (map[i].Type == type && map[i].Index == command.Index)
                        return map[i];
                return null;
            }

            private void WriteRegisters(MapPoint point, double raw)
            {
                var registers = new ushort[2];
                if (point.Kind == KindInt16 || point.Kind == KindUInt16)
                {
                    registers[0] = unchecked((ushort)RoundToInt(raw));
                    _client.WriteSingleRegister(UnitOf(point), (ushort)point.Address, registers[0]);
                    return;
                }

                uint bits;
                if (point.Kind == KindFloat32)
                    bits = SingleToBits(raw);
                else if (point.Kind == KindUInt32)
                    bits = unchecked((uint)(long)raw);
                else
                    bits = unchecked((uint)RoundToInt(raw));
                Split32(bits, point.Order, registers);

                _client.WriteMultipleRegisters(UnitOf(point), (ushort)point.Address, 2, registers, 0);
            }

            // nanoFramework에는 System.Math가 없으므로, 0에서 먼 방향으로 반올림합니다.
            private static int RoundToInt(double value)
            {
                return value >= 0 ? (int)(value + 0.5) : (int)(value - 0.5);
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
                if (kind == KindUInt32)
                    return raw >= 0 && raw <= 4294967295;
                return raw >= -2147483648 && raw <= 2147483647;
            }
        }

        private static MapPoint ParseLine(byte[] line, int length, byte[] scratch, MapPoint[] map, int count)
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

            return ToMapPoint(point, map, count);
        }

        private static MapPoint ToMapPoint(PointMapping point, MapPoint[] map, int count)
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

            if (point.Target.Protocol != "dnp3")
            {
                NoteSkipped(point.Id + ": 목적지 프로토콜은 dnp3여야 합니다.");
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

            // 주소는 VagabondK.nF.Modbus API에 맞게 철저히 0 기준 원시 인덱스(0~65535)입니다.
            if (address < 0 || address > 65535)
            {
                NoteSkipped(point.Id + ": 원본 주소가 범위를 넘습니다: " + point.Source.Address);
                return null;
            }

            Dnp3PointType type;
            string targetSpace = point.Target.AddressSpace;
            if (targetSpace == "analog-input")
                type = Dnp3PointType.Analog;
            else if (targetSpace == "binary-input")
                type = Dnp3PointType.Binary;
            else if (targetSpace == "counter")
                type = Dnp3PointType.Counter;
            else if (targetSpace == "binary-output")
                type = Dnp3PointType.BinaryOutputStatus;
            else if (targetSpace == "analog-output")
                type = Dnp3PointType.AnalogOutputStatus;
            else
            {
                NoteSkipped(point.Id + ": 지원하지 않는 목적지 주소 영역입니다: " + targetSpace);
                return null;
            }

            // Modbus로 되쓰려면 ReadWrite 접근과 일치하는 원본 영역이 필요합니다.
            bool writable = false;
            if (type == Dnp3PointType.BinaryOutputStatus || type == Dnp3PointType.AnalogOutputStatus)
            {
                if (point.Access != PointAccess.ReadWrite)
                {
                    NoteSkipped(point.Id + ": 출력 포인트는 ReadWrite 접근이어야 합니다.");
                    return null;
                }

                if (type == Dnp3PointType.BinaryOutputStatus && space != SpaceCoil)
                {
                    NoteSkipped(point.Id + ": Binary Output은 Coil 원본만 쓸 수 있습니다.");
                    return null;
                }

                if (type == Dnp3PointType.AnalogOutputStatus && space != SpaceHolding)
                {
                    NoteSkipped(point.Id + ": Analog Output은 Holding Register 원본만 쓸 수 있습니다.");
                    return null;
                }

                writable = true;
            }

            int index;
            if (!TryParseAddress(point.Target.Address, out index) || index < 0 || index > 65535)
            {
                NoteSkipped(point.Id + ": DNP3 인덱스 형식이 올바르지 않습니다: " + point.Target.Address);
                return null;
            }

            for (int i = 0; i < count; i++)
            {
                if (map[i].Type == type && map[i].Index == (ushort)index)
                {
                    NoteSkipped(point.Id + ": DNP3 주소가 포인트 " + map[i].Id + "와(와) 겹칩니다.");
                    return null;
                }
            }

            var result = new MapPoint();
            result.Id = point.Id;
            result.Space = space;
            result.Address = address;
            result.Kind = kind;
            result.Order = OrderOf(ProtocolAddress.ByteOrderOf(dataType));
            result.Type = type;
            result.Index = (ushort)index;
            result.Numerator = point.ScaleNumerator;
            result.Denominator = point.ScaleDenominator;
            result.Offset = point.Offset;
            result.Writable = writable;
            result.Flags = 0;
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
                Debug.WriteLine("ModbusToDnp3 skipped point: " + message);
            }
        }
    }
}
