using System;
using System.IO;
using System.IO.Ports;
using nanoGate.Core;

namespace nanoGate.ModbusSerialToTcp
{
    internal sealed class GatewaySettings
    {
        internal const int ConfigurationSchemaVersion = 2;
        internal const string GatewayProfile = "modbus-serial-tcp";

        private const string FilePath = "I:\\nanogate.cfg";
        private const string TempFilePath = "I:\\nanogate.tmp";
        private const int KeyProtocol = 1;
        private const int KeyBaudRate = 2;
        private const int KeyDataBits = 4;
        private const int KeyParity = 8;
        private const int KeyStopBits = 16;
        private const int KeyTxPin = 32;
        private const int KeyRxPin = 64;
        // 128비트는 KeyUnitId였는데 설정이 제거되었습니다 (릴레이는 모든 unit ID를 전달합니다).
        private const int KeyTimeout = 256;
        private const int KeyTcpPort = 512;
        private const int AllKeys = 895;
        private const int KeySchemaVersion = 1024;
        private const int KeyGatewayProfile = 2048;
        // RS485 방향 제어: AllKeys에 포함하지 않습니다 — 옛 파일은 두 키가 없어도
        // 기본값(auto/15)으로 정상 로드되고, 저장하면 기록됩니다.
        private const int KeyRs485Mode = 4096;
        private const int KeyRs485DePin = 8192;

        private static readonly int[] BaudRates = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

        public bool Ascii;
        public int BaudRate;
        public int DataBits;
        public Parity Parity;
        public int StopBits;
        public int TxPin;
        public int RxPin;
        public int ResponseTimeout;
        public int TcpPort;
        // "auto" = 송수신 자동 제어 모듈(핀 불필요), "rts" = 네이티브 RTS로 DE/RE 제어.
        public string Rs485Mode;
        public int Rs485DePin;

        public string ProtocolName
        {
            get { return Ascii ? "ASCII" : "RTU"; }
        }

        public string ParityName
        {
            get
            {
                if (Parity == Parity.Even)
                    return "Even";
                if (Parity == Parity.Odd)
                    return "Odd";
                return "None";
            }
        }

        public int BitsPerCharacter
        {
            get { return 1 + DataBits + (Parity == Parity.None ? 0 : 1) + StopBits; }
        }

        public static GatewaySettings CreateDefault()
        {
            var settings = new GatewaySettings();
#if MODBUS_ASCII
            settings.Ascii = true;
            settings.DataBits = 7;
#else
            settings.Ascii = false;
            settings.DataBits = 8;
#endif
            settings.BaudRate = 9600;
            settings.Parity = Parity.Even;
            settings.StopBits = 1;
            settings.ResponseTimeout = 1500;
            settings.TcpPort = 502;
            settings.Rs485Mode = "auto";
            settings.Rs485DePin = 15;

            switch (Board.Kind)
            {
                case Board.Esp32C3:
                    // Geekble Mini: GPIO6 -> DM2472 RXD, GPIO7 <- DM2472 TXD 연결.
                    settings.TxPin = 6;
                    settings.RxPin = 7;
                    break;
                case Board.Esp32S3:
                    // Geekble Nano: D8/GPIO17 -> DM2472 RXD, D9/GPIO18 <- DM2472 TXD 연결.
                    settings.TxPin = 17;
                    settings.RxPin = 18;
                    break;
                default:
                    settings.TxPin = 17;
                    settings.RxPin = 16;
                    break;
            }

            return settings;
        }

        public static GatewaySettings Load(out string source, out string error)
        {
            error = null;
            string text;
            try
            {
                string path = null;
                if (File.Exists(FilePath))
                    path = FilePath;
                else if (File.Exists(TempFilePath))
                    path = TempFilePath;

                if (path == null)
                {
                    source = GatewayHost.SourceDefault;
                    return CreateDefault();
                }

                text = File.ReadAllText(path);
            }
            catch (Exception storageError)
            {
                source = GatewayHost.SourceStorageError;
                error = ErrorText.Describe(storageError);
                return CreateDefault();
            }

            GatewaySettings settings = CreateDefault();
            error = settings.Apply(text);
            if (error == null)
                error = settings.Validate();

            if (error == GatewayHost.ProfileMismatchError)
            {
                // 다른 변환 프로필이 남긴 파일이라면 설정 파일과 포인트 파일을 지워,
                // 외부 프로필의 데이터가 표시되지 않고 깨끗한 상태로 시작합니다.
                Reset();
                nanoGate.Core.DeviceStorage.DeletePointFiles();
                source = GatewayHost.SourceDefault;
                error = null;
                return CreateDefault();
            }

            if (error != null)
            {
                source = GatewayHost.SourceInvalid;
                return CreateDefault();
            }

            source = GatewayHost.SourceFile;
            return settings;
        }

        public static string Reset()
        {
            try
            {
                if (File.Exists(TempFilePath))
                    File.Delete(TempFilePath);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                return null;
            }
            catch (Exception storageError)
            {
                return ErrorText.Describe(storageError);
            }
        }

        public string Save()
        {
            try
            {
                if (File.Exists(TempFilePath))
                    File.Delete(TempFilePath);

                File.WriteAllText(TempFilePath, Serialize());
                if (File.Exists(FilePath))
                    File.Delete(FilePath);

                File.Move(TempFilePath, FilePath);
                return null;
            }
            catch (Exception storageError)
            {
                return ErrorText.Describe(storageError);
            }
        }

        public GatewaySettings Clone()
        {
            var copy = new GatewaySettings();
            copy.Ascii = Ascii;
            copy.BaudRate = BaudRate;
            copy.DataBits = DataBits;
            copy.Parity = Parity;
            copy.StopBits = StopBits;
            copy.TxPin = TxPin;
            copy.RxPin = RxPin;
            copy.ResponseTimeout = ResponseTimeout;
            copy.TcpPort = TcpPort;
            copy.Rs485Mode = Rs485Mode;
            copy.Rs485DePin = Rs485DePin;
            return copy;
        }

        public string Serialize()
        {
            return "schemaVersion=" + ConfigurationSchemaVersion +
                "\ngatewayProfile=" + GatewayProfile +
                "\nprotocol=" + ProtocolName +
                "\nbaudRate=" + BaudRate +
                "\ndataBits=" + DataBits +
                "\nparity=" + ParityName +
                "\nstopBits=" + StopBits +
                "\ntxPin=" + TxPin +
                "\nrxPin=" + RxPin +
                "\ntimeout=" + ResponseTimeout +
                "\ntcpPort=" + TcpPort +
                "\nrs485Mode=" + Rs485Mode +
                "\nrs485DePin=" + Rs485DePin;
        }

        // 저장된 "key=value" 줄 형식과 HTML 폼 본문("key=value&...") 형식을 모두 받아들입니다.
        public string Apply(string text)
        {
            if (text == null)
                return "설정 내용이 비어 있습니다.";

            int seen = 0;
            int schemaVersion = 1;
            int start = 0;
            while (start <= text.Length)
            {
                int end = FindSeparator(text, start);
                string item = text.Substring(start, end - start).Trim();
                start = end + 1;
                if (item.Length == 0)
                    continue;

                int separator = item.IndexOf('=');
                if (separator <= 0)
                    return "형식이 잘못된 설정 항목이 있습니다.";

                string key = item.Substring(0, separator);
                string value = item.Substring(separator + 1);
                if (!IsAlphanumeric(key, 16) || !IsConfigValue(value))
                    return "설정 값에는 영문자와 숫자만 사용할 수 있습니다.";

                int keyFlag;
                if (key == "schemaVersion")
                {
                    keyFlag = KeySchemaVersion;
                    if (!IsDigits(value) || !int.TryParse(value, out schemaVersion))
                        return "schemaVersion 값이 올바르지 않습니다.";
                }
                else if (key == "gatewayProfile")
                {
                    keyFlag = KeyGatewayProfile;
                    if (value != GatewayProfile)
                        return GatewayHost.ProfileMismatchError;
                }
                else if (key.Length > 5 && key.StartsWith("remap"))
                {
                    // 이전 펌웨어가 저장한 파일에 남아 있는 제거된 주소 재매핑 키:
                    // 나머지 설정은 계속 로드되도록 허용한 뒤 무시합니다.
                    keyFlag = 0;
                }
                else if (key == "multiDrop" || key == "unitId")
                {
                    // 이전 펌웨어의 제거된 키 (multi-drop 토글과 단일 장치
                    // unit ID): 나머지 설정은 계속 로드되도록 허용한 뒤 무시합니다.
                    keyFlag = 0;
                }
                else
                {
                    string error = SetValue(key, value, out keyFlag);
                    if (error != null)
                        return error;
                }

                if ((seen & keyFlag) != 0)
                    return "중복된 설정 항목이 있습니다: " + key;

                seen |= keyFlag;
            }

            if ((seen & AllKeys) != AllKeys)
                return "누락된 설정 항목이 있습니다.";
            if (schemaVersion != 1 && schemaVersion != ConfigurationSchemaVersion)
                return "지원하지 않는 설정 스키마 버전입니다.";
            if ((seen & KeyGatewayProfile) != 0 && (seen & KeySchemaVersion) == 0)
                return "게이트웨이 프로필에는 schemaVersion이 필요합니다.";
            if (schemaVersion >= ConfigurationSchemaVersion && (seen & KeyGatewayProfile) == 0)
                return "새 설정 형식에 gatewayProfile이 없습니다.";

            return null;
        }

        public string Validate()
        {
            if (!IsSupportedBaudRate(BaudRate))
                return "지원하지 않는 통신 속도입니다.";
            if (Ascii)
            {
                if (DataBits != 7 && DataBits != 8)
                    return "ASCII 데이터 비트는 7 또는 8이어야 합니다.";
            }
            else if (DataBits != 8)
            {
                return "RTU 데이터 비트는 8이어야 합니다.";
            }

            if (Parity != Parity.None && Parity != Parity.Even && Parity != Parity.Odd)
                return "패리티는 None, Even, Odd 중 하나여야 합니다.";
            if (StopBits != 1 && StopBits != 2)
                return "정지 비트는 1 또는 2여야 합니다.";
            if (!Board.IsPinAllowed(TxPin))
                return "TX 핀 GPIO " + TxPin + "은(는) 이 보드에서 사용할 수 없습니다.";
            if (!Board.IsPinAllowed(RxPin))
                return "RX 핀 GPIO " + RxPin + "은(는) 이 보드에서 사용할 수 없습니다.";
            if (TxPin == RxPin)
                return "TX 핀과 RX 핀은 서로 달라야 합니다.";
            if (Rs485Mode != "auto" && Rs485Mode != "rts")
                return "rs485Mode는 auto 또는 rts여야 합니다.";
            if (Rs485Mode == "rts")
            {
                if (!Board.IsPinAllowed(Rs485DePin))
                    return "DE 핀 GPIO " + Rs485DePin + "은(는) 이 보드에서 사용할 수 없습니다.";
                if (Rs485DePin == TxPin || Rs485DePin == RxPin)
                    return "DE 핀은 TX/RX 핀과 달라야 합니다.";
            }
            if (ResponseTimeout < 100 || ResponseTimeout > 10000)
                return "응답 대기 시간은 100~10000 ms 범위여야 합니다.";
            if (TcpPort < 1 || TcpPort > 65535)
                return "Modbus TCP 포트는 1~65535 범위여야 합니다.";
            if (TcpPort == GatewayHost.ManagementPort)
                return "Modbus TCP 포트는 관리 포트(" + GatewayHost.ManagementPort + ")와 같을 수 없습니다.";
            return null;
        }

        private string SetValue(string key, string value, out int keyFlag)
        {
            int number;
            if (key == "protocol")
            {
                keyFlag = KeyProtocol;
                if (value == "RTU")
                    Ascii = false;
                else if (value == "ASCII")
                    Ascii = true;
                else
                    return "프로토콜은 RTU 또는 ASCII여야 합니다.";
                return null;
            }

            if (key == "parity")
            {
                keyFlag = KeyParity;
                if (value == "None")
                    Parity = Parity.None;
                else if (value == "Even")
                    Parity = Parity.Even;
                else if (value == "Odd")
                    Parity = Parity.Odd;
                else
                    return "패리티는 None, Even, Odd 중 하나여야 합니다.";
                return null;
            }

            if (key == "rs485Mode")
            {
                keyFlag = KeyRs485Mode;
                if (value != "auto" && value != "rts")
                    return "rs485Mode는 auto 또는 rts여야 합니다.";
                Rs485Mode = value;
                return null;
            }

            if (key == "baudRate")
                keyFlag = KeyBaudRate;
            else if (key == "dataBits")
                keyFlag = KeyDataBits;
            else if (key == "stopBits")
                keyFlag = KeyStopBits;
            else if (key == "txPin")
                keyFlag = KeyTxPin;
            else if (key == "rxPin")
                keyFlag = KeyRxPin;
            else if (key == "timeout")
                keyFlag = KeyTimeout;
            else if (key == "tcpPort")
                keyFlag = KeyTcpPort;
            else if (key == "rs485DePin")
                keyFlag = KeyRs485DePin;
            else
            {
                keyFlag = 0;
                return "알 수 없는 설정 항목입니다: " + key;
            }

            if (!IsDigits(value) || !int.TryParse(value, out number))
                return key + " 값은 숫자여야 합니다.";

            switch (keyFlag)
            {
                case KeyBaudRate:
                    BaudRate = number;
                    break;
                case KeyDataBits:
                    DataBits = number;
                    break;
                case KeyStopBits:
                    StopBits = number;
                    break;
                case KeyTxPin:
                    TxPin = number;
                    break;
                case KeyRxPin:
                    RxPin = number;
                    break;
                case KeyTimeout:
                    ResponseTimeout = number;
                    break;
                case KeyRs485DePin:
                    Rs485DePin = number;
                    break;
                default:
                    TcpPort = number;
                    break;
            }

            return null;
        }

        private static bool IsSupportedBaudRate(int baudRate)
        {
            for (int i = 0; i < BaudRates.Length; i++)
            {
                if (BaudRates[i] == baudRate)
                    return true;
            }

            return false;
        }

        private static int FindSeparator(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                char value = text[i];
                if (value == '&' || value == '\n')
                    return i;
            }

            return text.Length;
        }

        private static bool IsAlphanumeric(string value, int maxLength)
        {
            if (value.Length == 0 || value.Length > maxLength)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                    return false;
            }

            return true;
        }

        private static bool IsConfigValue(string value)
        {
            if (value.Length == 0 || value.Length > 32)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') || c == '-'))
                    return false;
            }

            return true;
        }

        private static bool IsDigits(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                    return false;
            }

            return true;
        }
    }
}
