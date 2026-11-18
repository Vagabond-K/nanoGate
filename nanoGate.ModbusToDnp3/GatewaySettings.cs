using System;
using System.IO;
using System.IO.Ports;
using nanoGate.Core;

namespace nanoGate.ModbusToDnp3
{
    internal sealed class GatewaySettings
    {
        internal const int ConfigurationSchemaVersion = 4;
        internal const string GatewayProfile = "modbus-to-dnp3";

        private const string FilePath = "I:\\nanogate.cfg";
        private const string TempFilePath = "I:\\nanogate.tmp";
        private const int KeyProtocol = 1;
        private const int KeyBaudRate = 2;
        private const int KeyDataBits = 4;
        private const int KeyParity = 8;
        private const int KeyStopBits = 16;
        private const int KeyTxPin = 32;
        private const int KeyRxPin = 64;
        // unitId는 폐기되었습니다(이제 규칙이 unit을 명시적으로 담습니다); 이 키는 BuildMap이
        // 동적 규칙을 마이그레이션할 수 있도록 오래된 파일에서는 여전히 파싱하지만, 저장하지 않고 필수도 아닙니다.
        private const int KeyUnitId = 128;
        private const int KeyTimeout = 256;
        private const int KeyDnp3Port = 512;
        private const int KeyLocalAddress = 1024;
        private const int KeyMasterAddress = 2048;
        private const int KeyPollInterval = 4096;
        private const int KeyMappingMode = 32768;
        private const int KeyAutoCoils = 65536;
        private const int KeyAutoDiscretes = 131072;
        private const int KeyAutoInputType = 262144;
        private const int KeyAutoInputCount = 524288;
        private const int KeyAutoHoldingType = 1048576;
        private const int KeyAutoHoldingCount = 2097152;
        private const int KeyAutoByteOrder = 4194304;
        // mappingMode/auto* 키(비트 15와 16-22)는 더 이상 필수가 아닙니다: 자동 매핑
        // 모드는 범위 규칙 파일로 대체되었기 때문입니다. Apply는 ModbusToDnp3Profile이
        // 이를 규칙으로 한 번 마이그레이션할 수 있도록 오래된 파일에서는 여전히 파싱합니다.
        // unitId(비트 7) 역시 선택 사항입니다: 레거시 동적 규칙 마이그레이션에만 사용됩니다.
        private const int AllKeys = 8063;
        private const int KeySchemaVersion = 8192;
        private const int KeyGatewayProfile = 16384;
        // RS485 방향 제어: AllKeys에 포함하지 않습니다 — 옛 파일은 두 키가 없어도
        // 기본값(auto/15)으로 정상 로드되고, 저장하면 기록됩니다.
        private const int KeyRs485Mode = 8388608;
        private const int KeyRs485DePin = 16777216;

        private static readonly int[] BaudRates = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

        public bool Ascii;
        public int BaudRate;
        public int DataBits;
        public Parity Parity;
        public int StopBits;
        public int TxPin;
        public int RxPin;
        // 폐기된 설정: 규칙 마이그레이션을 위해 오래된 파일에서만 읽고, 저장하지 않습니다.
        public int UnitId;
        public int ResponseTimeout;
        public int Dnp3Port;
        public int LocalAddress;
        public int MasterAddress;
        public int PollInterval;
        // "auto" = 송수신 자동 제어 모듈(핀 불필요), "rts" = 네이티브 RTS로 DE//RE 제어.
        public string Rs485Mode;
        public int Rs485DePin;
        public string MappingMode;
        public int AutoCoils;
        public int AutoDiscretes;
        public string AutoInputType;
        public int AutoInputCount;
        public string AutoHoldingType;
        public int AutoHoldingCount;
        public string AutoByteOrder;

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
            // mappingMode/auto* 필드는 비어 두면 됩니다: 레거시 파일 마이그레이션용 존재일 뿐입니다.
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
            settings.UnitId = 1;
            settings.ResponseTimeout = 1500;
            settings.Dnp3Port = 20000;
            settings.LocalAddress = 1;
            settings.MasterAddress = 2;
            settings.PollInterval = 1000;
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
                // 다른 변환 프로필이 남긴 파일입니다: 이 파일과 포인트·규칙 파일을 모두 지워,
                // 타 프로젝트 행이 보이는 대신 장치가 깨끗한 상태로 시작하도록 합니다.
                Reset();
                nanoGate.Core.DeviceStorage.DeletePointFiles();
                RangeStore.DeleteAll();
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
            copy.UnitId = UnitId;
            copy.ResponseTimeout = ResponseTimeout;
            copy.Dnp3Port = Dnp3Port;
            copy.LocalAddress = LocalAddress;
            copy.MasterAddress = MasterAddress;
            copy.PollInterval = PollInterval;
            copy.Rs485Mode = Rs485Mode;
            copy.Rs485DePin = Rs485DePin;
            copy.MappingMode = MappingMode;
            copy.AutoCoils = AutoCoils;
            copy.AutoDiscretes = AutoDiscretes;
            copy.AutoInputType = AutoInputType;
            copy.AutoInputCount = AutoInputCount;
            copy.AutoHoldingType = AutoHoldingType;
            copy.AutoHoldingCount = AutoHoldingCount;
            copy.AutoByteOrder = AutoByteOrder;
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
                "\ndnp3Port=" + Dnp3Port +
                "\nlocalAddress=" + LocalAddress +
                "\nmasterAddress=" + MasterAddress +
                "\npollInterval=" + PollInterval +
                "\nrs485Mode=" + Rs485Mode +
                "\nrs485DePin=" + Rs485DePin + "\n";
        }

        // "key=value" 형식의 저장 줄과 HTML 폼 본문("key=value&...") 형식을 모두 받아들입니다.
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
            if (schemaVersion != ConfigurationSchemaVersion && schemaVersion != 2 && schemaVersion != 3)
                return "지원하지 않는 설정 스키마 버전입니다.";
            if ((seen & KeyGatewayProfile) != 0 && (seen & KeySchemaVersion) == 0)
                return "게이트웨이 프로필에는 schemaVersion이 필요합니다.";
            if ((seen & KeySchemaVersion) != 0 && (seen & KeyGatewayProfile) == 0)
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
            if (Dnp3Port < 1 || Dnp3Port > 65535)
                return "DNP3 포트는 1~65535 범위여야 합니다.";
            if (Dnp3Port == GatewayHost.ManagementPort)
                return "DNP3 포트는 관리 포트(" + GatewayHost.ManagementPort + ")와 같을 수 없습니다.";
            if (LocalAddress < 0 || LocalAddress > 65519)
                return "DNP3 국소 주소는 0~65519 범위여야 합니다.";
            if (MasterAddress < 0 || MasterAddress > 65519)
                return "DNP3 마스터 주소는 0~65519 범위여야 합니다.";
            if (LocalAddress == MasterAddress)
                return "DNP3 국소 주소와 마스터 주소는 서로 달라야 합니다.";
            if (PollInterval < 100 || PollInterval > 60000)
                return "폴링 주기는 100~60000 ms 범위여야 합니다.";
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

            if (key == "mappingMode")
            {
                keyFlag = KeyMappingMode;
                if (value != "file" && value != "auto")
                    return "매핑 모드는 file 또는 auto여야 합니다.";
                MappingMode = value;
                return null;
            }

            if (key == "autoInputType" || key == "autoHoldingType")
            {
                keyFlag = key == "autoInputType" ? KeyAutoInputType : KeyAutoHoldingType;
                if (value != "Int16" && value != "UInt16" && value != "Int32" && value != "Float32")
                    return key + "는 Int16, UInt16, Int32, Float32 중 하나여야 합니다.";
                if (key == "autoInputType")
                    AutoInputType = value;
                else
                    AutoHoldingType = value;
                return null;
            }

            if (key == "autoByteOrder")
            {
                keyFlag = KeyAutoByteOrder;
                if (value != "ABCD" && value != "CDAB" && value != "BADC" && value != "DCBA")
                    return "autoByteOrder는 ABCD, CDAB, BADC, DCBA 중 하나여야 합니다.";
                AutoByteOrder = value;
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
            else if (key == "unitId")
                keyFlag = KeyUnitId;
            else if (key == "timeout")
                keyFlag = KeyTimeout;
            else if (key == "dnp3Port")
                keyFlag = KeyDnp3Port;
            else if (key == "localAddress")
                keyFlag = KeyLocalAddress;
            else if (key == "masterAddress")
                keyFlag = KeyMasterAddress;
            else if (key == "pollInterval")
                keyFlag = KeyPollInterval;
            else if (key == "autoCoils")
                keyFlag = KeyAutoCoils;
            else if (key == "autoDiscretes")
                keyFlag = KeyAutoDiscretes;
            else if (key == "autoInputCount")
                keyFlag = KeyAutoInputCount;
            else if (key == "autoHoldingCount")
                keyFlag = KeyAutoHoldingCount;
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
                case KeyRs485DePin:
                    Rs485DePin = number;
                    break;
                case KeyUnitId:
                    UnitId = number;
                    break;
                case KeyTimeout:
                    ResponseTimeout = number;
                    break;
                case KeyDnp3Port:
                    Dnp3Port = number;
                    break;
                case KeyLocalAddress:
                    LocalAddress = number;
                    break;
                case KeyMasterAddress:
                    MasterAddress = number;
                    break;
                case KeyAutoCoils:
                    AutoCoils = number;
                    break;
                case KeyAutoDiscretes:
                    AutoDiscretes = number;
                    break;
                case KeyAutoInputCount:
                    AutoInputCount = number;
                    break;
                case KeyAutoHoldingCount:
                    AutoHoldingCount = number;
                    break;
                default:
                    PollInterval = number;
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
