using System;
using System.Threading;

namespace nanoGate.ModbusToMqtt
{
    internal static class GatewayStatistics
    {
        // 요청 1건은 폴러가 시도한 포인트 읽기 1회를 의미합니다.
        // 카운터는 64비트이므로 빠른 폴링(100ms 간격 256개 포인트)이라도
        // 장치 수명 내에 오버플로될 수 없습니다. nanoFramework의 Interlocked에는
        // 64비트 오버로드가 없으므로, 증분은 대신 Inc* 헬퍼와 락을 거쳐 처리합니다.
        public static long Requests;
        public static long Successes;
        public static long Timeouts;
        public static long FrameErrors;
        public static long DeviceExceptions;
        // 전체 포인트에 대해 완료된 폴링 주기 수.
        public static long Polls;
        // MQTT 카운터: 게시한 메시지, 게시 실패 시도, 재접속,
        // 그리고 "<topic>/set" 명령으로 실행된 Modbus 쓰기 수.
        public static long MqttPublishes;
        public static long MqttFailures;
        public static long MqttReconnects;
        public static long MqttWrites;
        // 현재 broker 연결 상태 게이지(1 = 연결됨); Reset()으로도 초기화되지 않습니다.
        public static int MqttConnected;

        private static readonly object _sync = new object();

        private static string _lastError = string.Empty;
        private static int _resetAtSeconds;

        public static string LastError
        {
            get { return _lastError; }
        }

        // 카운터가 마지막으로 초기화된 시점의 부팅 후 초 단위 시간; 0은 부팅 이후 누적임을 의미합니다.
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

        public static void IncPolls()
        {
            lock (_sync) { Polls++; }
        }

        public static void IncMqttPublishes()
        {
            lock (_sync) { MqttPublishes++; }
        }

        public static void IncMqttFailures()
        {
            lock (_sync) { MqttFailures++; }
        }

        public static void IncMqttReconnects()
        {
            lock (_sync) { MqttReconnects++; }
        }

        public static void IncMqttWrites()
        {
            lock (_sync) { MqttWrites++; }
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
                Polls = 0;
                MqttPublishes = 0;
                MqttFailures = 0;
                MqttReconnects = 0;
                MqttWrites = 0;
            }

            _lastError = string.Empty;
            _resetAtSeconds = (int)(Environment.TickCount64 / 1000);
        }
    }
}
