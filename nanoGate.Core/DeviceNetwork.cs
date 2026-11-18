using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using nanoFramework.Runtime.Native;
// 선택적인 HTTP 서버 참조 때문에 유입된 System.Net.AuthenticationType과 구분합니다.
using AuthenticationType = System.Net.NetworkInformation.AuthenticationType;

namespace nanoGate.Core
{
    // 스테이션(정상 동작) 모드와 접근 점(설정 모드) Wi-Fi 모드를 전환합니다.
    internal static class DeviceNetwork
    {
        internal const string ApAddress = "192.168.4.1";
        private const string ApMask = "255.255.255.0";
        private const string DefaultApPassword = "nanogate";
        private const string NativeDefaultApPassword = "nanoframework";
        // 모드 변경을 위한 재부팅이 대기 중임을 나타내며, 반영되지 않은 변경이 반복 부팅되는 것을 막습니다.
        private const string GuardFile = "I:\\nanogate.ap";
        private const int SetupIdleMilliseconds = 600000;

        private static string _setupSsid;

        public static string Address
        {
            get
            {
                if (GatewayHost.Setup)
                    return ApAddress;

                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    // 접근 점(AP)은 꺼져 있는 상태에서도 저장된 주소를 보고합니다.
                    if (interfaces[i].NetworkInterfaceType == NetworkInterfaceType.WirelessAP)
                        continue;

                    string address = interfaces[i].IPv4Address;
                    if (!string.IsNullOrEmpty(address) && address != "0.0.0.0" && address != "127.0.0.1")
                        return address;
                }

                return string.Empty;
            }
        }

        public static bool HasAp
        {
            get
            {
                try
                {
                    return GetAp() != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static string CurrentSsid
        {
            get
            {
                try
                {
                    Wireless80211Configuration station = GetStation();
                    if (station != null && station.Ssid != null)
                        return station.Ssid;
                }
                catch (Exception)
                {
                }

                return string.Empty;
            }
        }

        public static bool CurrentOpen
        {
            get
            {
                try
                {
                    Wireless80211Configuration station = GetStation();
                    return station != null && station.Authentication == AuthenticationType.Open;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static string SetupSsid
        {
            get
            {
                if (_setupSsid == null)
                {
                    NetworkInterface network = FindInterface(NetworkInterfaceType.WirelessAP);
                    byte[] mac = network == null ? null : network.PhysicalAddress;
                    if (mac == null || mac.Length < 2)
                    {
                        network = FindInterface(NetworkInterfaceType.Wireless80211);
                        mac = network == null ? null : network.PhysicalAddress;
                    }

                    _setupSsid = mac == null || mac.Length < 2
                        ? "nanoGate"
                        : "nanoGate-" + mac[mac.Length - 2].ToString("X2") + mac[mac.Length - 1].ToString("X2");
                }

                return _setupSsid;
            }
        }

        public static NetworkInterface FindInterface(NetworkInterfaceType type)
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                if (interfaces[i].NetworkInterfaceType == type)
                    return interfaces[i];
            }

            return null;
        }

        // 장치가 설정 모드로 부팅했을 때 true를 반환합니다. 다른 Wi-Fi 모드는 스테이션 전용으로 정규화합니다.
        public static bool Prepare()
        {
            Wireless80211Configuration station;
            WirelessAPConfiguration ap;
            try
            {
                station = GetStation();
                ap = GetAp();
            }
            catch (Exception error)
            {
                Debug.WriteLine("Reading Wi-Fi configuration failed: " + ErrorText.Describe(error));
                return false;
            }

            if (station == null || ap == null)
                return false;

            bool stationOn = ((int)station.Options & (int)Wireless80211Configuration.ConfigurationOptions.Enable) != 0;
            bool apOn = ((int)ap.Options & (int)WirelessAPConfiguration.ConfigurationOptions.Enable) != 0;
            bool guard = File.Exists(GuardFile);
            if (guard)
                DeleteGuard();
            if (apOn && !stationOn)
                return true;
            if (guard)
                return false;

            if (string.IsNullOrEmpty(station.Ssid))
            {
                CreateGuard();
                EnterSetup(false);
            }
            else if (!stationOn || apOn)
            {
                CreateGuard();
                try
                {
                    station.Options = Wireless80211Configuration.ConfigurationOptions.AutoConnect;
                    station.SaveConfiguration();
                    ap.Options = WirelessAPConfiguration.ConfigurationOptions.None;
                    ap.SaveConfiguration();
                }
                catch (Exception error)
                {
                    Debug.WriteLine("Normalizing Wi-Fi mode failed: " + ErrorText.Describe(error));
                }

                Reboot();
            }

            return false;
        }

        public static void EnterSetup(bool resetPassword)
        {
            Debug.WriteLine("Entering setup mode.");
            try
            {
                NetworkInterface network = FindInterface(NetworkInterfaceType.WirelessAP);
                if (network != null)
                    network.EnableStaticIPv4(ApAddress, ApMask, ApAddress);
            }
            catch (Exception error)
            {
                Debug.WriteLine("Setting access point address failed: " + ErrorText.Describe(error));
            }

            try
            {
                WirelessAPConfiguration ap = GetAp();
                ap.Ssid = SetupSsid;
                string password = ap.Password;
                if (resetPassword || password == null || password.Length < 8 || password == NativeDefaultApPassword)
                    ap.Password = DefaultApPassword;
                ap.Authentication = AuthenticationType.WPA2;
                ap.Encryption = EncryptionType.WPA2;
                ap.MaxConnections = 2;
                if (ap.Channel < 1 || ap.Channel > 13)
                    ap.Channel = 6;
                ap.Options = WirelessAPConfiguration.ConfigurationOptions.AutoStart;
                ap.SaveConfiguration();

                Wireless80211Configuration station = GetStation();
                station.Options = Wireless80211Configuration.ConfigurationOptions.None;
                station.SaveConfiguration();
            }
            catch (Exception error)
            {
                Debug.WriteLine("Enabling setup access point failed: " + ErrorText.Describe(error));
            }

            Reboot();
        }

        // 저장된 스테이션 SSID가 없을 때에만 반환합니다.
        public static void ExitSetup()
        {
            if (CurrentSsid.Length == 0)
                return;

            Debug.WriteLine("Leaving setup mode.");
            try
            {
                WirelessAPConfiguration ap = GetAp();
                ap.Options = WirelessAPConfiguration.ConfigurationOptions.None;
                ap.SaveConfiguration();
                Wireless80211Configuration station = GetStation();
                station.Options = Wireless80211Configuration.ConfigurationOptions.AutoConnect;
                station.SaveConfiguration();
            }
            catch (Exception error)
            {
                Debug.WriteLine("Leaving setup mode failed: " + ErrorText.Describe(error));
            }

            Reboot();
        }

        public static void Apply(NetworkForm form)
        {
            try
            {
                if (form.SsidChanged || form.Password != null || GatewayHost.Setup)
                {
                    Wireless80211Configuration station = GetStation();
                    station.Ssid = form.Ssid;
                    if (form.Password != null)
                        station.Password = form.Password;
                    else if (station.Password == null)
                        station.Password = string.Empty;

                    if (form.Open)
                    {
                        station.Authentication = AuthenticationType.Open;
                        station.Encryption = EncryptionType.None;
                    }
                    else if (form.Password != null)
                    {
                        station.Authentication = AuthenticationType.WPA2;
                        station.Encryption = EncryptionType.WPA2;
                    }

                    station.Options = Wireless80211Configuration.ConfigurationOptions.AutoConnect;
                    station.SaveConfiguration();
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine("Saving Wi-Fi station failed: " + ErrorText.Describe(error));
            }

            try
            {
                if (form.ApPassword != null || GatewayHost.Setup)
                {
                    WirelessAPConfiguration ap = GetAp();
                    if (string.IsNullOrEmpty(ap.Ssid))
                        ap.Ssid = SetupSsid;
                    if (form.ApPassword != null)
                        ap.Password = form.ApPassword;
                    if (GatewayHost.Setup)
                        ap.Options = WirelessAPConfiguration.ConfigurationOptions.None;
                    ap.SaveConfiguration();
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine("Saving setup access point failed: " + ErrorText.Describe(error));
            }

            ApplyIp(form);
            Reboot();
        }

        public static void FactoryReset()
        {
            Debug.WriteLine("Factory reset.");
            string error = GatewayHost.Profile.ResetSettings();
            if (error != null)
                Debug.WriteLine("Resetting settings failed: " + error);
            error = DeviceStorage.DeletePointFiles();
            if (error != null)
                Debug.WriteLine("Deleting point mapping failed: " + error);
            DeviceAuth.Delete();
            DeleteGuard();

            ApplyIp(new NetworkForm { Dhcp = true });

            try
            {
                Wireless80211Configuration station = GetStation();
                station.Ssid = string.Empty;
                station.Password = string.Empty;
                station.Options = Wireless80211Configuration.ConfigurationOptions.None;
                station.SaveConfiguration();
            }
            catch (Exception resetError)
            {
                Debug.WriteLine("Clearing Wi-Fi station failed: " + ErrorText.Describe(resetError));
            }

            EnterSetup(true);
        }

        // ESP32 네이티브 시작 처리는 lwIP 인터페이스 번호를 알기 전에 저장된 주소 모드를
        // 적용하려 하므로, 정적 설정은 재부팅 후에 조용히 DHCP로 되돌아갑니다.
                // 시작 시 관리 코드에서 저장된 정적 주소를 한 번 다시 적용합니다.
        public static void ReapplyStaticAddress()
        {
            try
            {
                NetworkInterface network = FindInterface(NetworkInterfaceType.Wireless80211);
                if (network == null || network.IsDhcpEnabled)
                    return;

                string ip = network.IPv4Address;
                if (string.IsNullOrEmpty(ip) || ip == "0.0.0.0")
                    return;

                network.EnableStaticIPv4(ip, network.IPv4SubnetMask, network.IPv4GatewayAddress);
                Debug.WriteLine("Static address re-applied at start-up: " + ip);
            }
            catch (Exception error)
            {
                Debug.WriteLine("Re-applying static address failed: " + ErrorText.Describe(error));
            }
        }

        private static void ApplyIp(NetworkForm form)
        {
            NetworkInterface network;
            try
            {
                network = FindInterface(NetworkInterfaceType.Wireless80211);
                if (network == null)
                    throw new InvalidOperationException("Wi-Fi station interface is unavailable.");
            }
            catch (Exception error)
            {
                Debug.WriteLine("Reading IP settings failed: " + ErrorText.Describe(error));
                return;
            }

            try
            {
                if (form.Dhcp)
                    network.EnableDhcp();
                else
                    network.EnableStaticIPv4(form.Ip, form.Subnet, form.Gateway);
            }
            catch (Exception error)
            {
                Debug.WriteLine("Saving/applying IP settings failed: " + ErrorText.Describe(error));
            }

            // AP 전용 모드에서는 네이티브 API가 주소를 저장한 뒤 비활성 상태인 스테이션에
            // 적용하는 과정에서 예외를 던질 수 있습니다. DNS는 별도 갱신이므로 계속 시도해야 합니다.
            try
            {
                if (form.Dhcp || form.Dns1 == null)
                    EnableAutomaticDns(network);
                else if (form.Dns2 == null)
                    network.EnableStaticIPv4Dns(new string[] { form.Dns1 });
                else
                    network.EnableStaticIPv4Dns(new string[] { form.Dns1, form.Dns2 });
            }
            catch (Exception error)
            {
                Debug.WriteLine("Saving/applying DNS settings failed: " + ErrorText.Describe(error));
            }
        }

        // 설정 클라이언트용 최소한의 DHCP 서버. 메인 스레드에서 실행되며 반환하지 않습니다.
        public static void RunSetup()
        {
            var request = new byte[SetupDhcp.ReceiveBufferBytes];
            var reply = new byte[SetupDhcp.ReplyBytes];
            var dhcp = new SetupDhcp();
            bool canLeave = CurrentSsid.Length > 0;
            EndPoint clients = new IPEndPoint(IPAddress.Broadcast, 68);

            while (true)
            {
                Socket socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
                    try
                    {
                        // 접근 점(AP) 주소에 바인딩하면 그 인터페이스로 브로드캐스트 응답이 전송됩니다.
                        socket.Bind(new IPEndPoint(IPAddress.Parse(ApAddress), 67));
                    }
                    catch (SocketException)
                    {
                        socket.Bind(new IPEndPoint(IPAddress.Any, 67));
                    }

                    Debug.WriteLine("Setup DHCP server ready on " + ApAddress + ".");
                    while (true)
                    {
                        if (canLeave && Environment.TickCount64 - GatewayHost.LastActivity > SetupIdleMilliseconds)
                            ExitSetup();

                        if (!socket.Poll(1000000, SelectMode.SelectRead))
                            continue;

                        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                        int length = socket.ReceiveFrom(request, ref remote);
                        int size = dhcp.Answer(request, length, reply, Environment.TickCount64);
                        if (size > 0)
                        {
                            GatewayHost.LastActivity = Environment.TickCount64;
                            socket.SendTo(reply, 0, size, SocketFlags.None, clients);
                        }
                    }
                }
                catch (Exception error)
                {
                    Debug.WriteLine("Setup DHCP server failed: " + ErrorText.Describe(error));
                }
                finally
                {
                    if (socket != null)
                        socket.Close();
                }

                Thread.Sleep(1000);
            }
        }

        private static Wireless80211Configuration GetStation()
        {
            Wireless80211Configuration[] configurations = Wireless80211Configuration.GetAllWireless80211Configurations();
            return configurations == null || configurations.Length == 0 ? null : configurations[0];
        }

        private static WirelessAPConfiguration GetAp()
        {
            WirelessAPConfiguration[] configurations = WirelessAPConfiguration.GetAllWirelessAPConfigurations();
            return configurations == null || configurations.Length == 0 ? null : configurations[0];
        }

#pragma warning disable CS0618 // EnableAutomaticDns는 지원 중단되었지만 DHCP가 제공한 DNS로 돌아가는 유일한 방법입니다.
        private static void EnableAutomaticDns(NetworkInterface network)
        {
            network.EnableAutomaticDns();
        }
#pragma warning restore CS0618

        private static void CreateGuard()
        {
            try
            {
                File.Create(GuardFile).Dispose();
            }
            catch (Exception error)
            {
                Debug.WriteLine("Creating Wi-Fi mode guard failed: " + ErrorText.Describe(error));
            }
        }

        private static void DeleteGuard()
        {
            try
            {
                if (File.Exists(GuardFile))
                    File.Delete(GuardFile);
            }
            catch (Exception error)
            {
                Debug.WriteLine("Deleting Wi-Fi mode guard failed: " + ErrorText.Describe(error));
            }
        }

        internal static void Reboot()
        {
            Power.RebootDevice(RebootOption.NormalReboot);
            // 재부팅은 런타임이 처리하므로 호출자의 코드로 절대 돌아가지 않습니다.
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
