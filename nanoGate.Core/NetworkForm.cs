using System.Text;

namespace nanoGate.Core
{
    // urlencoded 네트워크 폼을 해석하고 검증하며, 값은 DeviceNetwork가 적용합니다.
    internal sealed class NetworkForm
    {
        // /api/status의 limits로 클라이언트에 전달되는 SSID 최대 바이트 수.
        public const int SsidMaxBytes = 31;

        public string Ssid;
        // null이면 저장된 스테이션 암호를 유지합니다.
        public string Password;
        public bool Open;
        public bool Dhcp;
        public string Ip;
        public string Subnet;
        public string Gateway;
        public string Dns1;
        public string Dns2;
        // null이면 저장된 설정 접근 점(AP) 암호를 유지합니다.
        public string ApPassword;
        public bool SsidChanged;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding();

        public string Parse(byte[] body, int length, string currentSsid, bool currentOpen)
        {
            var scratch = new byte[64];
            string ssid = null, password = null, open = null, dhcp = null, ip = null, subnet = null;
            string gateway = null, dns1 = null, dns2 = null, apPassword = null;
            int start = 0;
            while (start < length)
            {
                int end = start;
                while (end < length && body[end] != '&')
                    end++;

                if (end > start)
                {
                    int equals = start;
                    while (equals < end && body[equals] != '=')
                        equals++;
                    if (equals == start || equals == end)
                        return "형식이 잘못된 네트워크 설정 항목이 있습니다.";

                    string key = Decode(body, start, equals, scratch);
                    string value = Decode(body, equals + 1, end, scratch);
                    if (key == null || value == null)
                        return "네트워크 설정 값이 너무 길거나 인코딩이 잘못되었습니다.";

                    string previous;
                    if (key == "ssid") { previous = ssid; ssid = value; }
                    else if (key == "password") { previous = password; password = value; }
                    else if (key == "open") { previous = open; open = value; }
                    else if (key == "dhcp") { previous = dhcp; dhcp = value; }
                    else if (key == "ip") { previous = ip; ip = value; }
                    else if (key == "subnet") { previous = subnet; subnet = value; }
                    else if (key == "gateway") { previous = gateway; gateway = value; }
                    else if (key == "dns1") { previous = dns1; dns1 = value; }
                    else if (key == "dns2") { previous = dns2; dns2 = value; }
                    else if (key == "apPassword") { previous = apPassword; apPassword = value; }
                    else return "알 수 없는 네트워크 설정 항목입니다: " + key;

                    if (previous != null)
                        return "중복된 네트워크 설정 항목이 있습니다: " + key;
                }

                start = end + 1;
            }

            if (ssid == null || dhcp == null)
                return "네트워크 설정 항목이 누락되었습니다.";
            if (dhcp != "1" && dhcp != "0")
                return "dhcp 값은 1 또는 0이어야 합니다.";
            if (open != null && open != "1" && open != "0")
                return "open 값은 1 또는 0이어야 합니다.";

            int ssidBytes = Utf8.GetBytes(ssid).Length;
            if (ssidBytes < 1 || ssidBytes > SsidMaxBytes)
                return "Wi-Fi SSID는 UTF-8 기준 1~" + SsidMaxBytes.ToString() + "바이트여야 합니다.";
            for (int i = 0; i < ssid.Length; i++)
            {
                if (ssid[i] < ' ' || ssid[i] == (char)0x7F)
                    return "Wi-Fi SSID에 제어 문자를 사용할 수 없습니다.";
            }

            Ssid = ssid;
            SsidChanged = ssid != currentSsid;
            Open = open == "1";
            string error;
            if (Open)
            {
                Password = string.Empty;
            }
            else if (password == null || password.Length == 0)
            {
                if (SsidChanged || currentOpen)
                    return "Wi-Fi 암호를 입력하세요.";
                Password = null;
            }
            else
            {
                error = CheckPassword(password, "Wi-Fi 암호");
                if (error != null)
                    return error;
                Password = password;
            }

            if (apPassword != null && apPassword.Length > 0)
            {
                error = CheckPassword(apPassword, "설정 AP 암호");
                if (error != null)
                    return error;
                ApPassword = apPassword;
            }

            Dhcp = dhcp == "1";
            if (Dhcp)
                return null;

            uint address, mask;
            if (!TryParseAddress(ip, out address))
                return "IP 주소 형식이 올바르지 않습니다.";
            if (!TryParseAddress(subnet, out mask))
                return "서브넷 마스크 형식이 올바르지 않습니다.";

            uint hostBits = ~mask;
            if (mask == 0 || (hostBits & (hostBits + 1)) != 0 || hostBits < 3)
                return "서브넷 마스크는 /1~/30 범위의 연속된 마스크여야 합니다.";
            if (!IsHost(address, mask))
                return "IP 주소는 서브넷 안의 사용 가능한 호스트 주소여야 합니다.";

            uint router = 0;
            if (gateway != null && gateway.Length > 0)
            {
                if (!TryParseAddress(gateway, out router) || !IsHost(router, mask) ||
                    (router & mask) != (address & mask) || router == address)
                    return "게이트웨이는 같은 서브넷에 있는 다른 호스트 주소여야 합니다.";
            }

            uint primary = 0, secondary = 0;
            if (dns1 != null && dns1.Length > 0 && (!TryParseAddress(dns1, out primary) || !IsUnicast(primary)))
                return "기본 DNS 주소가 올바르지 않습니다.";
            if (dns2 != null && dns2.Length > 0 && (!TryParseAddress(dns2, out secondary) || !IsUnicast(secondary)))
                return "보조 DNS 주소가 올바르지 않습니다.";
            if (primary == 0)
            {
                primary = secondary;
                secondary = 0;
            }

            Ip = Format(address);
            Subnet = Format(mask);
            Gateway = Format(router);
            Dns1 = primary == 0 ? null : Format(primary);
            Dns2 = secondary == 0 ? null : Format(secondary);
            return null;
        }

        private static string Decode(byte[] body, int start, int end, byte[] scratch)
        {
            int count = 0;
            for (int i = start; i < end; i++)
            {
                int value = body[i];
                if (value == '+')
                {
                    value = ' ';
                }
                else if (value == '%')
                {
                    if (i + 2 >= end)
                        return null;
                    int high = ByteTextUtil.HexValue(body[i + 1]);
                    int low = ByteTextUtil.HexValue(body[i + 2]);
                    if (high < 0 || low < 0)
                        return null;
                    value = (high << 4) | low;
                    i += 2;
                }

                if (count == scratch.Length)
                    return null;
                scratch[count++] = (byte)value;
            }

            if (!ByteTextUtil.IsValidUtf8(scratch, count))
                return null;
            return count == 0 ? string.Empty : Utf8.GetString(scratch, 0, count);
        }

        private static string CheckPassword(string value, string label)
        {
            if (value.Length < 8 || value.Length > 63)
                return label + "는 8~63자여야 합니다.";
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] < ' ' || value[i] > '~')
                    return label + "에는 영문, 숫자, 기호만 사용할 수 있습니다.";
            }

            return null;
        }

        private static bool TryParseAddress(string text, out uint address)
        {
            address = 0;
            if (text == null)
                return false;

            int parts = 0, digits = 0, octet = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '.')
                {
                    if (digits == 0 || octet > 255)
                        return false;
                    address = (address << 8) | (uint)octet;
                    parts++;
                    digits = 0;
                    octet = 0;
                    continue;
                }

                char c = text[i];
                if (c < '0' || c > '9' || ++digits > 3)
                    return false;
                octet = octet * 10 + (c - '0');
            }

            return parts == 4;
        }

        private static bool IsUnicast(uint address)
        {
            uint first = address >> 24;
            return first != 0 && first != 127 && first < 224;
        }

        private static bool IsHost(uint address, uint mask)
        {
            uint host = address & ~mask;
            return IsUnicast(address) && host != 0 && host != ~mask;
        }

        private static string Format(uint address)
        {
            return (address >> 24) + "." + ((address >> 16) & 255) + "." + ((address >> 8) & 255) + "." + (address & 255);
        }
    }
}
