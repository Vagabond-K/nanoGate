using System;
using System.IO;
using System.IO.Ports;
using nanoGate.Core;

namespace nanoGate.ModbusToMqtt
{
    internal sealed class GatewaySettings
    {
        internal const int ConfigurationSchemaVersion = 5;
        internal const string GatewayProfile = "modbus-to-mqtt";

        private const string FilePath = "I:\\nanogate.cfg";
        private const string TempFilePath = "I:\\nanogate.tmp";
        // 플래그는 long에 보관합니다: 텍스트/템플릿 키들이 모임을 31비트 이상으로 넘깁니다.
        private const long KeyProtocol = 1L;
        private const long KeyBaudRate = 2L;
        private const long KeyDataBits = 4L;
        private const long KeyParity = 8L;
        private const long KeyStopBits = 16L;
        private const long KeyTxPin = 32L;
        private const long KeyRxPin = 64L;
        // unitId는 스키마 v5에서 폐기되었습니다 (이제 각 포인트가 자신의 Modbus unit을 갖습니다);
        // 옛 파일을 그대로 로드할 수 있도록 이 키는 여전히 파싱하기는 하지만,
        // 필수도 아니고 저장되지도 않습니다.
        private const long KeyUnitId = 128L;
        private const long KeyTimeout = 256L;
        private const long KeyPollInterval = 512L;
        // 스키마 v4에서 mappingMode/auto*가 제거되었습니다 (MQTT에서는 자동 매핑을 제공하지 않습니다);
        // 비게 된 비트는 계속 미사용으로 두어 나머지 키 값이 절대 바뀌지 않게 합니다.
        private const long KeySchemaVersion = 262144L;
        private const long KeyGatewayProfile = 524288L;
        private const long KeyBrokerHost = 1048576L;
        private const long KeyBrokerPort = 2097152L;
        private const long KeyMqttClientId = 4194304L;
        private const long KeyMqttUsername = 8388608L;
        private const long KeyMqttPassword = 16777216L;
        private const long KeyKeepAlive = 33554432L;
        private const long KeyTopicPrefix = 67108864L;
        private const long KeyMqttQos = 134217728L;
        private const long KeyPublishMode = 268435456L;
        private const long KeyPublishInterval = 536870912L;
        private const long KeyPayloadFormat = 1073741824L;
        private const long KeyPublishBatch = 2147483648L;
        private const long KeyMqttTemplate = 4294967296L;
        private const long KeymqttBatchTpl = 8589934592L;
        private const long KeyMqttBatchJoin = 17179869184L;
        // RS485 방향 제어 (스키마 5에서 추가): AllKeys에 포함하지 않습니다.
        // 옛 파일에는 두 키가 없어도 기본값(auto/15)으로 정상 로드됩니다. 저장하면 두 키가 기록됩니다.
        private const long KeyRs485Mode = 34359738368L;
        private const long KeyRs485DePin = 68719476736L;
        private const long AllKeys = 34359477119L; // 비트 0-6, 8-9, 18-34 (비트 7 unitId는 v5에서 폐기; 비트 10-17은 v4에서 auto 키들이 제거되며 비어 있음)

        private static readonly int[] BaudRates = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

        public const string DefaultTemplate = "{\"id\":\"{id}\",\"v\":{v},\"q\":{q}}";
        public const string DefaultBatchTemplate = "{\"t\":{t},\"points\":{points}}";

        public bool Ascii;
        public int BaudRate;
        public int DataBits;
        public Parity Parity;
        public int StopBits;
        public int TxPin;
        public int RxPin;
        // 폐기된 설정: 옛 파일에서 파싱만 하고 무시합니다 (포인트마다 자신의 unit을 갖습니다).
        public int UnitId;
        public int ResponseTimeout;
        public int PollInterval;
        public string BrokerHost;
        public int BrokerPort;
        public string ClientId;
        public string Username;
        public string Password;
        public int KeepAlive;
        public string TopicPrefix;
        public int Qos;
        public string PublishMode;
        public int PublishInterval;
        public string PayloadFormat;
        public int Batch;
        public string Template;
        public string BatchTemplate;
        public string BatchJoin;
        // "auto" = 송수신 자동 제어 모듈(핀 불필요), "rts" = 네이티브 RTS로 DE/RE 제어.
        public string Rs485Mode;
        public int Rs485DePin;

        public string ProtocolName
        {
            get { return Ascii ? "ASCII" : "RTU"; }
        }

        public string RowSeparator
        {
            get { return BatchJoin == "newline" ? "\n" : ","; }
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
            settings.UnitId = 1;
            settings.ResponseTimeout = 1500;
            settings.PollInterval = 1000;
            settings.BrokerHost = "192.168.0.100";
            settings.BrokerPort = 1883;
            settings.ClientId = "nanogate";
            settings.Username = string.Empty;
            settings.Password = string.Empty;
            settings.KeepAlive = 30;
            settings.TopicPrefix = "nanogate";
            settings.Qos = 0;
            settings.PublishMode = "change";
            settings.PublishInterval = 10000;
            settings.PayloadFormat = "json";
            settings.Batch = 0;
            settings.Template = DefaultTemplate;
            settings.BatchTemplate = DefaultBatchTemplate;
            settings.BatchJoin = "comma";
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
            error = settings.Apply(text, false);
            if (error == null)
                error = settings.Validate();

            if (error == GatewayHost.ProfileMismatchError)
            {
                // 다른 변환 프로필이 남긴 파일입니다: 포인트 파일과 함께 삭제하여
                // 타사 행을 보여주지 않고 장치가 깨끗한 상태로 시작하도록 합니다.
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
            copy.UnitId = UnitId;
            copy.ResponseTimeout = ResponseTimeout;
            copy.PollInterval = PollInterval;
            copy.BrokerHost = BrokerHost;
            copy.BrokerPort = BrokerPort;
            copy.ClientId = ClientId;
            copy.Username = Username;
            copy.Password = Password;
            copy.KeepAlive = KeepAlive;
            copy.TopicPrefix = TopicPrefix;
            copy.Qos = Qos;
            copy.PublishMode = PublishMode;
            copy.PublishInterval = PublishInterval;
            copy.PayloadFormat = PayloadFormat;
            copy.Batch = Batch;
            copy.Template = Template;
            copy.BatchTemplate = BatchTemplate;
            copy.BatchJoin = BatchJoin;
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
                "\npollInterval=" + PollInterval +
                "\nbrokerHost=" + BrokerHost +
                "\nbrokerPort=" + BrokerPort +
                "\nmqttClientId=" + ClientId +
                "\nmqttUsername=" + Username +
                "\nmqttPassword=" + Password +
                "\nkeepAlive=" + KeepAlive +
                "\ntopicPrefix=" + TopicPrefix +
                "\nmqttQos=" + Qos +
                "\npublishMode=" + PublishMode +
                "\npublishInterval=" + PublishInterval +
                "\npayloadFormat=" + PayloadFormat +
                "\npublishBatch=" + Batch +
                "\nmqttTemplate=" + Template +
                "\nmqttBatchTpl=" + BatchTemplate +
                "\nmqttBatchJoin=" + BatchJoin +
                "\nrs485Mode=" + Rs485Mode +
                "\nrs485DePin=" + Rs485DePin + "\n";
        }

        // 저장된 "key=value" 줄과 HTML 폼 본문("key=value&...")을 모두 받아들입니다.
        // 폼 본문은 application/x-www-form-urlencoded이므로 값에 먼저 퍼센트 디코딩을
        // 적용합니다; 저장된 파일은 원시 값을 담고 있으므로 다시 디코딩하면 안 됩니다.
        public string Apply(string text, bool formEncoded)
        {
            if (text == null)
                return "설정 내용이 비어 있습니다.";

            long seen = 0;
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
                if (!IsAlphanumeric(key, 16))
                    return "설정 키 이름이 올바르지 않습니다.";

                if (formEncoded)
                {
                    string decodeError;
                    value = DecodeFormValue(value, out decodeError);
                    if (decodeError != null)
                        return decodeError;
                }

                if (IsTextKey(key))
                {
                    if (!IsTextValue(value))
                        return "설정 값에 사용할 수 없는 문자가 있습니다: " + key;
                }
                else if (value.Length == 0 || !IsConfigValue(value))
                {
                    return "설정 값에는 영문자와 숫자만 사용할 수 있습니다.";
                }

                long keyFlag;
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
            if (schemaVersion != ConfigurationSchemaVersion && schemaVersion != 4)
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
            if (PollInterval < 100 || PollInterval > 60000)
                return "폴링 주기는 100~60000 ms 범위여야 합니다.";

            if (BrokerHost.Length == 0 || BrokerHost.Length > 63 || !IsHostValue(BrokerHost))
                return "브로커 주소는 영문자, 숫자, '.', '-'로만 63자 이내로 입력해야 합니다.";
            if (BrokerPort < 1 || BrokerPort > 65535)
                return "브로커 포트는 1~65535 범위여야 합니다.";
            if (BrokerPort == GatewayHost.ManagementPort)
                return "브로커 포트는 관리 포트(" + GatewayHost.ManagementPort + ")와 같을 수 없습니다.";
            if (ClientId.Length == 0 || ClientId.Length > 64 || !IsClientIdValue(ClientId))
                return "클라이언트 ID는 영문자, 숫자, '-', '_'로만 64자 이내로 입력해야 합니다.";
            if (Username.Length > 64)
                return "사용자 이름은 64자 이하여야 합니다.";
            if (Password.Length > 64)
                return "비밀번호는 64자 이하여야 합니다.";
            if (KeepAlive < 0 || KeepAlive > 600)
                return "Keep Alive는 0~600초 범위여야 합니다.";
            if (TopicPrefix.Length == 0 || TopicPrefix.Length > 64 || !IsTopicValue(TopicPrefix))
                return "토픽 접두사는 영문자, 숫자, '-', '_', '.', '/', ':'만 사용할 수 있습니다.";
            if (TopicPrefix[0] == '/' || TopicPrefix[TopicPrefix.Length - 1] == '/' || TopicPrefix.IndexOf("//") >= 0)
                return "토픽 접두사는 '/'로 시작하거나 끝낼 수 없습니다.";
            if (Qos < 0 || Qos > 2)
                return "QoS는 0, 1, 2 중 하나여야 합니다.";
            if (PublishMode != "change" && PublishMode != "interval")
                return "게시 모드는 change 또는 interval이어야 합니다.";
            if (PublishInterval < 100 || PublishInterval > 3600000)
                return "게시 주기는 100~3600000 ms 범위여야 합니다.";
            if (PayloadFormat != "json" && PayloadFormat != "template" && PayloadFormat != "binary" && PayloadFormat != "plain")
                return "페이로드 형식은 json, template, binary, plain 중 하나여야 합니다.";
            if (Batch != 0 && Batch != 1)
                return "publishBatch는 0 또는 1이어야 합니다.";
            if (Batch == 1 && PayloadFormat != "json" && PayloadFormat != "template")
                return "배치 게시는 JSON 또는 템플릿 페이로드 형식에서만 사용할 수 있습니다.";
            if (BatchJoin != "comma" && BatchJoin != "newline")
                return "mqttBatchJoin은 comma 또는 newline이어야 합니다.";
            if (BatchTemplate.Length == 0 || BatchTemplate.IndexOf("{points}") < 0)
                return "배치 템플릿에는 {points}가 있어야 합니다.";
            if (BatchTemplate.Length > 200)
                return "배치 템플릿은 200자 이하여야 합니다.";
            if (PayloadFormat == "template")
            {
                if (Template.Length == 0 || Template.IndexOf('{') < 0)
                    return "템플릿에는 {id}, {v}, {q}, {t} 중 하나 이상을 포함해야 합니다.";
                if (Template.Length > 200)
                    return "템플릿은 200자 이하여야 합니다.";
            }

            return null;
        }

        internal static int RegisterWidth(string dataType)
        {
            return dataType == "Int32" || dataType == "Float32" ? 2 : 1;
        }

        private string SetValue(string key, string value, out long keyFlag)
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

            if (key == "publishMode")
            {
                keyFlag = KeyPublishMode;
                if (value != "change" && value != "interval")
                    return "게시 모드는 change 또는 interval이어야 합니다.";
                PublishMode = value;
                return null;
            }

            if (key == "payloadFormat")
            {
                keyFlag = KeyPayloadFormat;
                if (value != "json" && value != "template" && value != "binary" && value != "plain")
                    return "페이로드 형식은 json, template, binary, plain 중 하나여야 합니다.";
                PayloadFormat = value;
                return null;
            }

            if (key == "brokerHost")
            {
                keyFlag = KeyBrokerHost;
                BrokerHost = value;
                return null;
            }

            if (key == "mqttClientId")
            {
                keyFlag = KeyMqttClientId;
                ClientId = value;
                return null;
            }

            if (key == "mqttUsername")
            {
                keyFlag = KeyMqttUsername;
                Username = value;
                return null;
            }

            if (key == "mqttPassword")
            {
                keyFlag = KeyMqttPassword;
                Password = value;
                return null;
            }

            if (key == "topicPrefix")
            {
                keyFlag = KeyTopicPrefix;
                TopicPrefix = value;
                return null;
            }

            if (key == "mqttTemplate")
            {
                keyFlag = KeyMqttTemplate;
                Template = value;
                return null;
            }

            if (key == "mqttBatchTpl")
            {
                keyFlag = KeymqttBatchTpl;
                BatchTemplate = value;
                return null;
            }

            if (key == "mqttBatchJoin")
            {
                keyFlag = KeyMqttBatchJoin;
                if (value != "comma" && value != "newline")
                    return "mqttBatchJoin은 comma 또는 newline이어야 합니다.";
                BatchJoin = value;
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
            else if (key == "pollInterval")
                keyFlag = KeyPollInterval;
            else if (key == "brokerPort")
                keyFlag = KeyBrokerPort;
            else if (key == "keepAlive")
                keyFlag = KeyKeepAlive;
            else if (key == "mqttQos")
                keyFlag = KeyMqttQos;
            else if (key == "publishInterval")
                keyFlag = KeyPublishInterval;
            else if (key == "publishBatch")
                keyFlag = KeyPublishBatch;
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
                case KeyUnitId:
                    UnitId = number;
                    break;
                case KeyTimeout:
                    ResponseTimeout = number;
                    break;
                case KeyBrokerPort:
                    BrokerPort = number;
                    break;
                case KeyKeepAlive:
                    KeepAlive = number;
                    break;
                case KeyMqttQos:
                    Qos = number;
                    break;
                case KeyPublishInterval:
                    PublishInterval = number;
                    break;
                case KeyPublishBatch:
                    Batch = number;
                    break;
                case KeyRs485DePin:
                    Rs485DePin = number;
                    break;
                default:
                    PollInterval = number;
                    break;
            }

            return null;
        }

        private static bool IsTextKey(string key)
        {
            return key == "brokerHost" || key == "mqttClientId" || key == "mqttUsername" ||
                key == "mqttPassword" || key == "topicPrefix" || key == "mqttTemplate" ||
                key == "mqttBatchTpl";
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

        // application/x-www-form-urlencoded 본문의 퍼센트 디코딩: %XX와 '+'(공백)을 처리합니다.
        private static string DecodeFormValue(string value, out string error)
        {
            if (value.IndexOf('%') < 0 && value.IndexOf('+') < 0)
            {
                error = null;
                return value;
            }

            var buffer = new char[value.Length];
            int length = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '+')
                {
                    buffer[length++] = ' ';
                }
                else if (c == '%')
                {
                    if (i + 2 >= value.Length)
                    {
                        error = "설정 값의 % 인코딩이 형식에 맞지 않습니다.";
                        return null;
                    }

                    int high = HexDigit(value[i + 1]);
                    int low = HexDigit(value[i + 2]);
                    if (high < 0 || low < 0)
                    {
                        error = "설정 값의 % 인코딩이 형식에 맞지 않습니다.";
                        return null;
                    }

                    buffer[length++] = (char)((high << 4) | low);
                    i += 2;
                }
                else
                {
                    buffer[length++] = c;
                }
            }

            error = null;
            return new string(buffer, 0, length);
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;
            return -1;
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
            if (value.Length > 32)
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

        // 텍스트 값(broker host, client id, 자격 증명, topic 접두사, 템플릿):
        // 인쇄 가능 ASCII만 허용; '&'은 폼 구분자이므로 원문으로 절대 등장하면 안 됩니다.
        private static bool IsTextValue(string value)
        {
            if (value.Length > 200)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < ' ' || c > '~' || c == '&')
                    return false;
            }

            return true;
        }

        private static bool IsHostValue(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') || c == '-' || c == '.'))
                    return false;
            }

            return true;
        }

        private static bool IsClientIdValue(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') || c == '-' || c == '_'))
                    return false;
            }

            return true;
        }

        private static bool IsTopicValue(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') || c == '-' || c == '_' || c == '.' || c == '/' || c == ':'))
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
