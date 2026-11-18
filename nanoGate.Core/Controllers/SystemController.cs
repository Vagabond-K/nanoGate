using System.Net;
using nanoFramework.WebServer;
using VagabondK.nF.Web;

namespace nanoGate.Core.Controllers
{
    // 장치 단위 동작: 재부팅, Wi-Fi 네트워크 변경, 설정 모드, 공장 초기화.
    // 실제 작업은 응답이 브라우저에 먼저 도착하도록 Run 루프로 미룹니다.
    internal sealed class SystemController
    {
        [Route("api/reboot")]
        [Method("POST")]
        public void Reboot(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
                WebManagementServer.Defer(WebManagementServer.AfterReboot, null);
            }
        }

        [Route("api/network")]
        [Method("POST")]
        public void Network(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                HttpListenerRequest request = e.Context.Request;
                HttpListenerResponse response = e.Context.Response;
                long length = request.ContentLength64;
                if (length <= 0)
                {
                    WebApi.Error(response, 411, "요청 본문이 없습니다.");
                    return;
                }

                if (length > WebManagementServer.MaxBodySize)
                {
                    WebApi.Error(response, 413, "설정 내용이 너무 깁니다.");
                    return;
                }

                byte[] body = WebApi.ReadBody(request, WebManagementServer.MaxBodySize);
                if (body == null || body.Length == 0)
                    return;

                var form = new NetworkForm();
                string error = form.Parse(body, body.Length, DeviceNetwork.CurrentSsid, DeviceNetwork.CurrentOpen);
                if (error != null)
                {
                    WebApi.Error(response, 400, error);
                    return;
                }

                WebApi.Json(response, 200, WebApi.OkJson);
                WebManagementServer.Defer(WebManagementServer.AfterNetwork, form);
            }
        }

        [Route("api/setup-mode")]
        [Method("POST")]
        public void SetupMode(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                HttpListenerResponse response = e.Context.Response;
                if (GatewayHost.Setup && DeviceNetwork.CurrentSsid.Length == 0)
                {
                    WebApi.Error(response, 400, "먼저 연결할 Wi-Fi를 저장하세요.");
                    return;
                }

                if (!GatewayHost.Setup && !DeviceNetwork.HasAp)
                {
                    WebApi.Error(response, 400, "이 장치는 설정 AP를 지원하지 않습니다.");
                    return;
                }

                WebApi.Json(response, 200, WebApi.OkJson);
                WebManagementServer.Defer(WebManagementServer.AfterSetupMode, null);
            }
        }

        [Route("api/factory-reset")]
        [Method("POST")]
        public void FactoryReset(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
                WebManagementServer.Defer(WebManagementServer.AfterFactoryReset, null);
            }
        }
    }
}
