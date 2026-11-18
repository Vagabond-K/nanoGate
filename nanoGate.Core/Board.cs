using nanoFramework.Runtime.Native;

namespace nanoGate.Core
{
    // 모든 컨버터 프로필이 공유하는 보드별 GPIO 정보.
    // 보드 감지: 장치마다 허용 핀이 달라 설정 검증이 참조합니다. 게이트웨이에서도 읽습니다.
    public static class Board
    {
        internal const int Other = 0;
        public const int Esp32C3 = 1;
        public const int Esp32S3 = 2;

        private static string _targetName;
        private static int _kind = -1;

        public static string TargetName
        {
            get
            {
                if (_targetName == null)
                {
                    string name = SystemInfo.TargetName;
                    _targetName = name == null ? string.Empty : name;
                }

                return _targetName;
            }
        }

        public static int Kind
        {
            get
            {
                if (_kind < 0)
                {
                    string name = TargetName;
                    if (name.IndexOf("ESP32_C3") == 0)
                        _kind = Esp32C3;
                    else if (name.IndexOf("ESP32_S3") == 0)
                        _kind = Esp32S3;
                    else
                        _kind = Other;
                }

                return _kind;
            }
        }

        public static string AllowedPinsText
        {
            get
            {
                switch (Kind)
                {
                    case Esp32C3:
                        return "0, 1, 3, 4, 5, 6, 7";
                    case Esp32S3:
                        return "1, 2, 4-18, 21, 38-42, 47, 48";
                    default:
                        return "1-48";
                }
            }
        }

        // BOOT 버튼: ESP32-C3에서는 GPIO9, 그 밖의 보드에서는 GPIO0. 설정 모드와 공장 초기화 전용으로 예약됩니다.
        public static int ButtonPin
        {
            get { return Kind == Esp32C3 ? 9 : 0; }
        }

        // Geekble Mini(ESP32-C3)의 상태 LED. 다른 보드에는 일반 GPIO LED가 없습니다.
        public static int LedPin
        {
            get { return Kind == Esp32C3 ? 10 : -1; }
        }

        public static bool IsPinAllowed(int pin)
        {
            switch (Kind)
            {
                case Esp32C3:
                    // GPIO2/8/9는 strapping, GPIO10은 LED, GPIO18/19는 USB, GPIO20/21은 콘솔, GPIO11-17은 flash용.
                    return pin == 0 || pin == 1 || (pin >= 3 && pin <= 7);
                case Esp32S3:
                    return pin == 1 || pin == 2 || (pin >= 4 && pin <= 18) || pin == 21 ||
                        (pin >= 38 && pin <= 42) || pin == 47 || pin == 48;
                default:
                    return pin >= 1 && pin <= 48;
            }
        }
    }
}
