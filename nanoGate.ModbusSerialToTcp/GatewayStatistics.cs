using System;
using System.Threading;

namespace nanoGate.ModbusSerialToTcp
{
    internal static class GatewayStatistics
    {
        // 카운터는 릴레이가 오래 동작해도 오버플로가 나지 않도록 64비트입니다.
        // nanoFramework의 Interlocked에는 64비트 오버로드가 없으므로 증가는
        // 대신 Inc* 도우미 메서드와 lock을 통해 수행합니다.
        public static long Requests;
        public static long Successes;
        public static long Timeouts;
        public static long FrameErrors;
        public static long DeviceExceptions;
        // 현재 활성 Modbus TCP 연결 수 게이지; Reset()에서도 의도적으로 초기화하지 않습니다.
        public static int TcpConnections;

        private static readonly object _sync = new object();

        private static string _lastError = string.Empty;
        private static int _resetAtSeconds;

        public static string LastError
        {
            get { return _lastError; }
        }

        // 카운터를 마지막으로 지운 시점의 가동 시간(초); 0이면 부팅 이후 누적이라는 뜻입니다.
        public static int ResetAtSeconds
        {
            get { return _resetAtSeconds; }
        }

        public static void IncRequests()
        {
            lock (_sync) { Requests++; }
        }

        public static void IncSuccesses()
        {
            lock (_sync) { Successes++; }
        }

        public static void IncTimeouts()
        {
            lock (_sync) { Timeouts++; }
        }

        public static void IncFrameErrors()
        {
            lock (_sync) { FrameErrors++; }
        }

        public static void IncDeviceExceptions()
        {
            lock (_sync) { DeviceExceptions++; }
        }

        public static void SetLastError(string message)
        {
            _lastError = "[" + (Environment.TickCount64 / 1000) + "s] " + message;
        }

        public static void Reset()
        {
            lock (_sync)
            {
                Requests = 0;
                Successes = 0;
                Timeouts = 0;
                FrameErrors = 0;
                DeviceExceptions = 0;
            }

            ModbusRelay.ClearSeenUnits();
            _lastError = string.Empty;
            _resetAtSeconds = (int)(Environment.TickCount64 / 1000);
        }
    }
}
