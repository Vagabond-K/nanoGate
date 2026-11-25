using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using nanoFramework.Runtime.Native;
using nanoFramework.WebServer;
using VagabondK.nF.Web;

namespace nanoGate.Core.Controllers
{
    // 읽기 전용 status/system 엔드포인트와 라이브 포인트 cache.
    // 포인트 매핑 표의 다운로드/업로드는 nanoGate.Points의 PointsController로 분리했습니다.
    internal sealed class StatusController
    {
        // GC.Run(false)은 전체 GC를 블로킹으로 수행해, 폴러가 동작 중이면 1초 이상 걸립니다.
        // 따라서 GC는 #system 탭이 열릴 때만(30초 간격) 실행하고 status는 이 캐시를 읽기만 합니다.
        // 이전 값이 0이면 아직 GC를 한 번도 하지 않은 것이라 표시용(시스템 탭)에만 의미가 있습니다.
        private const long FreeMemoryRefreshMilliseconds = 30000;
        private static long _freeMemoryBytes;
        private static long _freeMemoryAtTicks;

        private static long FreeMemory()
        {
            long now = Environment.TickCount64;
            if (now - _freeMemoryAtTicks >= FreeMemoryRefreshMilliseconds)
            {
                _freeMemoryBytes = nanoFramework.Runtime.Native.GC.Run(false);
                _freeMemoryAtTicks = now;
            }

            return _freeMemoryBytes;
        }

        [Route("api/status")]
        [Method("GET")]
        public void Status(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
                WebApi.Json(e.Context.Response, 200, GetStatusJson());
        }

        [Route("api/system")]
        [Method("GET")]
        public void GetSystem(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
                WebApi.Json(e.Context.Response, 200, GetSystemJson());
        }

        // 라이브 포인트 cache로, cache 탭이 열려 있는 동안에만 가져옵니다. 그러면 매핑된
        // 포인트가 수백 개여도 5초 주기 status 응답은 작게 유지됩니다.
        [Route("api/cache")]
        [Method("GET")]
        public void GetCache(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (!GatewayHost.Profile.HasPointCache)
                {
                    WebApi.Error(e.Context.Response, 404, "이 게이트웨이는 포인트 캐시를 제공하지 않습니다.");
                    return;
                }

                var w = new JsonWriter(1024);
                w.BeginObject().Prop("points").BeginArray();
                GatewayHost.Profile.WriteCache(w);
                w.EndArray().EndObject();
                WebApi.JsonSegmented(e.Context.Response, 200, w.ToString());
            }
        }

        private static string GetStatusJson()
        {
            var w = new JsonWriter(640);
            w.BeginObject()
                .Prop("ip", DeviceNetwork.Address)
                .Prop("target", Board.TargetName)
                .Prop("allowedPins", Board.AllowedPinsText)
                .Prop("managementPort", GatewayHost.ManagementPort)
                .Prop("mode", GatewayHost.Setup ? "setup" : "normal")
                .Prop("profile", GatewayHost.Profile.Id)
                .Prop("gatewayReady", GatewayHost.Ready)
                .Prop("gatewayError", GatewayHost.Error)
                .Prop("settingsSource", GatewayHost.SettingsSource)
                .Prop("settingsError", GatewayHost.SettingsError)
                .Prop("uptime", Environment.TickCount64 / 1000)
                // freeMemory는 #system 탭만 소비: status는 절대 GC를 유발하지 않고 캐시만 노출.
                .Prop("freeMemory", _freeMemoryBytes);
            GatewayHost.Profile.WriteStatus(w);
            // 서버 자기서술: capabilities는 manifest.features의 최종 고향, settingsKeys는 실제로 받는 설정 키.
            w.Prop("limits");
            LimitsJson(w);
            w.PropRaw("capabilities", GatewayHost.Profile.CapabilitiesJson);
            w.PropRaw("settingsKeys", GatewayHost.Profile.SettingsKeysJson);
            w.EndObject();
            return w.ToString();
        }

        // 저장된 암호는 절대 반환하지 않습니다.
        // 웹 UI에 제공하는 서버 검증 한계값(shell.html의 applyLimits 참조).
        // 한계값은 컴파일 상수라 필드 조각만 캐시하고 객체는 매번 조립합니다(단일 출처).
        // 포인트 표 관련 필드는 게이트웨이마다 달라 Profile.ExtraLimitsJson이 보탭니다.
        private static void LimitsJson(JsonWriter w)
        {
            w.BeginObject().Prop("ssidMax", NetworkForm.SsidMaxBytes);
            string extra = GatewayHost.Profile.ExtraLimitsJson;
            if (extra != null && extra.Length > 0)
                w.Raw(extra);
            w.EndObject();
        }

        private static string GetSystemJson()
        {
            var w = new JsonWriter(512);
            w.BeginObject()
                .Prop("firmware", GetFirmwareVersion())
                .Prop("nanoFramework", SystemInfo.Version.ToString())
                .Prop("target", Board.TargetName)
                .Prop("platform", SystemInfo.Platform)
                .Prop("oem", SystemInfo.OEMString);
            w.Prop("limits");
            LimitsJson(w);
            w.Prop("mode", GatewayHost.Setup ? "setup" : "normal")
                .Prop("ssid", DeviceNetwork.CurrentSsid)
                .Prop("open", DeviceNetwork.CurrentOpen)
                .Prop("hasAp", DeviceNetwork.HasAp)
                .Prop("apSsid", DeviceNetwork.SetupSsid)
                .Prop("apIp", DeviceNetwork.ApAddress);

            NetworkInterface network = DeviceNetwork.FindInterface(NetworkInterfaceType.Wireless80211);
            if (network != null)
            {
                w.Prop("mac", FormatMac(network.PhysicalAddress))
                    .Prop("ip", network.IPv4Address)
                    .Prop("subnet", network.IPv4SubnetMask)
                    .Prop("gateway", network.IPv4GatewayAddress)
                    .Prop("dhcp", network.IsDhcpEnabled);
                w.Prop("dns").BeginArray();
                string[] dns = network.IPv4DnsAddresses;
                for (int i = 0; dns != null && i < dns.Length; i++)
                {
                    if (string.IsNullOrEmpty(dns[i]) || dns[i] == "0.0.0.0")
                        continue;
                    w.Str(dns[i]);
                }

                w.EndArray();
            }

            w.Prop("uptime", Environment.TickCount64 / 1000)
                .Prop("freeMemory", FreeMemory());
            w.EndObject();
            return w.ToString();
        }

        private static string GetFirmwareVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string FormatMac(byte[] address)
        {
            if (address == null)
                return string.Empty;

            var builder = new StringBuilder(18);
            for (int i = 0; i < address.Length; i++)
            {
                if (i > 0)
                    builder.Append(':');
                builder.Append(address[i].ToString("X2"));
            }

            return builder.ToString();
        }
    }
}



