using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using nanoGate.Core;
using VagabondK.nF.Web;
using VagabondK.nF.Modbus;
using VagabondK.nF.Modbus.Client;
using VagabondK.nF.Modbus.Server;

namespace nanoGate.ModbusSerialToTcp
{
    // Modbus TCP 요청 PDU마다 데이터를 디코딩하거나 캐시하지 않고 그대로 시리얼 버스로 전달합니다.
    // RS-485 버스는 반이중 방식이고 모든 TCP 연결이 공유하므로 모든 시리얼 교환은
    // BusSync를 거치며, 응답 PDU는 호출자의 버퍼에 바로 써서 반환합니다.
    internal sealed class ModbusRelay : IModbusPduHandler
    {
        private const byte GatewayTargetFailedToRespond = 0x0B;
        private const int ResynchronizeQuietMilliseconds = 50;
        private const int ResynchronizeMaxRounds = 20;

        private static readonly object BusSync = new object();

        // 통계를 초기화한 이후 최소 한 번 응답한 적 있는 unit ID (index = unitId).
        private static readonly bool[] _seenUnits = new bool[248];

        // 드라이버 행업 시 워치독이 포트/클라이언트를 교체하므로 readonly가 아닙니다.
        // 교체는 RecoverySync로, 실제 버스 사용 직렬화는 BusSync로 따로 합니다
        // (행업 스레드가 BusSync를 영구 보유할 수 있어 복구 잠금과 분리).
        private ModbusClient _client;
        private SerialPort _port;
        private readonly object _recoverySync = new object();

        // 요청이 도착했으나 아직 끝나지 않은 상태(req > fin)가 유예시간을 넘으면 행업으로 봅니다.
        // 링크가 유휴(요청 없음)이면 절대 트리거되지 않습니다.
        private long _lastRequestTick;
        private long _lastFinishTick;
        private readonly byte[] _discardBuffer = new byte[64];

        public ModbusRelay(IModbusTransport serialTransport, SerialPort port)
        {
            if (serialTransport == null)
                throw new ArgumentNullException("serialTransport");
            if (port == null)
                throw new ArgumentNullException("port");

            _client = new ModbusClient(serialTransport);
            _port = port;
        }

        public static void ClearSeenUnits()
        {
            for (int i = 0; i < _seenUnits.Length; i++)
                _seenUnits[i] = false;
        }

        // 발견된 unit 목록을 JSON 배열로 덧붙입니다. 예: "units":[1,3,7].
        public static void WriteSeenUnits(JsonWriter w)
        {
            w.BeginArray();
            for (int i = 1; i < _seenUnits.Length; i++)
            {
                if (!_seenUnits[i])
                    continue;

                w.Num(i);
            }

            w.EndArray();
        }

        // 호출자가 라우팅해 넘긴 요청 하나를 처리합니다. 응답 PDU 길이를 반환합니다.
        public int Process(FrameContext context, byte[] pdu, int requestLength)
        {
            byte function = pdu[0];
            byte unitId = context.UnitId;
            GatewayStatistics.IncRequests();
            _lastRequestTick = Environment.TickCount64;

            ModbusClient client;
            lock (_recoverySync)
                client = _client;

            if (client == null)
            {
                // 복구 워치독이 포트를 다시 여는 중입니다.
                GatewayStatistics.SetLastError("UART 복구 진행 중 — 요청을 건너뛰었습니다");
                _lastFinishTick = Environment.TickCount64;
                pdu[0] = (byte)(function | 0x80);
                pdu[1] = GatewayTargetFailedToRespond;
                return 2;
            }

            lock (BusSync)
            {
                try
                {
                    DiscardPendingInput();
                    int responseLength = client.ExchangePdu(unitId, pdu, requestLength);
                    if (pdu[0] == function)
                    {
                        if (unitId >= 1 && unitId < _seenUnits.Length)
                            _seenUnits[unitId] = true;

                        GatewayStatistics.IncSuccesses();
                    }
                    else
                    {
                        GatewayStatistics.IncDeviceExceptions();
                        GatewayStatistics.SetLastError("하위 장치(Unit " + unitId + ") 예외 코드 0x" + pdu[1].ToString("X2"));
                    }

                    return responseLength;
                }
                catch (TimeoutException)
                {
                    GatewayStatistics.IncTimeouts();
                    GatewayStatistics.SetLastError("하위 장치 응답 없음 (Unit " + unitId + ")");
                }
                catch (ModbusProtocolException error)
                {
                    GatewayStatistics.IncFrameErrors();
                    GatewayStatistics.SetLastError("시리얼 프레임 오류: " + ErrorText.Describe(error));
                }
                catch (IOException error)
                {
                    GatewayStatistics.IncFrameErrors();
                    GatewayStatistics.SetLastError("시리얼 I/O 오류: " + ErrorText.Describe(error));
                }
                catch (ObjectDisposedException)
                {
                    // 복구 워치독이 행업된 포트를 강제로 닫은 것입니다.
                    GatewayStatistics.IncFrameErrors();
                    GatewayStatistics.SetLastError("정지 복구로 포트가 교체되었습니다");
                }
                finally
                {
                    _lastFinishTick = Environment.TickCount64;
                }

                // 방금 실패한 장치의 지연 응답이 다음 transaction의 응답으로 잘못 읽힐 수 있으므로,
                // 버스를 해제하기 전에 라인이 조용해질 때까지 수신 데이터를 버립니다.
                Resynchronize();
            }

            pdu[0] = (byte)(function | 0x80);
            pdu[1] = GatewayTargetFailedToRespond;
            return 2;
        }

        // 진행 중이던 교환이 유예시간보다 오래 끝나지 않았다면 true입니다.
        public bool PendingExchangeStalled(long graceMilliseconds)
        {
            long req = _lastRequestTick;
            return req != 0 && req > _lastFinishTick
                && Environment.TickCount64 - req > graceMilliseconds;
        }

        // 행업 의심 시 COM2를 다시 열고 클라이언트를 교체합니다. BusSync를 기다리지
        // 않아야 하므로 짧은 전용 잠금만 사용합니다. 행업 스레드는 Dispose로 깨어납니다.
        public void Recover(GatewaySettings settings)
        {
            lock (_recoverySync)
            {
                Debug.WriteLine("Serial exchange stall detected; reopening COM2.");
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
                    // 행업 스레드가 깨지며 예외를 던지는 것이 정상입니다.
                }

                try
                {
                    SerialPort port = ModbusGatewayProfile.OpenPort(settings);
                    _port = port;
                    _client = new ModbusClient(ModbusGatewayProfile.CreateTransport(settings, port));
                    GatewayStatistics.SetLastError("시리얼 정지 감지: COM2를 다시 열었습니다.");
                }
                catch (Exception error)
                {
                    GatewayHost.Error = "UART 복구 실패: " + ErrorText.Describe(error);
                    Debug.WriteLine(GatewayHost.Error);
                }

                // 다음 시도까지 같은 유예를 줍니다.
                _lastFinishTick = Environment.TickCount64;
            }
        }

        private int DiscardPendingInput()
        {
            SerialPort port = _port;
            if (port == null)
                return 0; // 복구 중 — 포트가 곧 다시 열립니다.

            int discarded = 0;
            int available = port.BytesToRead;
            while (available > 0)
            {
                int read = port.Read(
                    _discardBuffer,
                    0,
                    available < _discardBuffer.Length ? available : _discardBuffer.Length);
                if (read <= 0)
                    break;

                discarded += read;
                available = port.BytesToRead;
            }

            return discarded;
        }

        // 지연되거나 부분적인 응답은 UART FIFO에 묶여서 오므로 라인이 조용해질 때까지 기다립니다.
        private void Resynchronize()
        {
            for (int round = 0; round < ResynchronizeMaxRounds; round++)
            {
                Thread.Sleep(ResynchronizeQuietMilliseconds);
                if (DiscardPendingInput() == 0)
                    return;
            }
        }
    }
}
