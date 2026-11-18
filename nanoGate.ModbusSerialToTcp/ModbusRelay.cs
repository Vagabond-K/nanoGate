using System;
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

        private readonly ModbusClient _client;
        private readonly SerialPort _port;
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

            lock (BusSync)
            {
                try
                {
                    DiscardPendingInput();
                    int responseLength = _client.ExchangePdu(unitId, pdu, requestLength);
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

                // 방금 실패한 장치의 지연 응답이 다음 transaction의 응답으로 잘못 읽힐 수 있으므로,
                // 버스를 해제하기 전에 라인이 조용해질 때까지 수신 데이터를 버립니다.
                Resynchronize();
            }

            pdu[0] = (byte)(function | 0x80);
            pdu[1] = GatewayTargetFailedToRespond;
            return 2;
        }

        private int DiscardPendingInput()
        {
            int discarded = 0;
            int available = _port.BytesToRead;
            while (available > 0)
            {
                int read = _port.Read(
                    _discardBuffer,
                    0,
                    available < _discardBuffer.Length ? available : _discardBuffer.Length);
                if (read <= 0)
                    break;

                discarded += read;
                available = _port.BytesToRead;
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
