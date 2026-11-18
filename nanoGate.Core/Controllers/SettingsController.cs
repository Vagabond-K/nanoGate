using System.Net;
using nanoFramework.WebServer;
using VagabondK.nF.Web;

namespace nanoGate.Core.Controllers
{
    // 게이트웨이 설정 저장/초기화와 통계 초기화.
    internal sealed class SettingsController
    {
        [Route("api/settings")]
        [Method("POST")]
        public void Save(WebServerEventArgs e)
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

                string form = WebApi.ReadBodyAsAscii(request, WebManagementServer.MaxBodySize);
                if (form == null)
                    return;

                bool storageFailure;
                string error = GatewayHost.Profile.SaveSettings(form, out storageFailure);
                if (error != null)
                {
                    if (storageFailure)
                        WebApi.Error(response, 500, "설정 저장 실패: " + error);
                    else
                        WebApi.Error(response, 400, error);
                    return;
                }

                WebApi.Json(response, 200, WebApi.OkJson);
                WebManagementServer.Defer(WebManagementServer.AfterReboot, null);
            }
        }

        [Route("api/settings/reset")]
        [Method("POST")]
        public void Reset(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                HttpListenerResponse response = e.Context.Response;
                string resetError = GatewayHost.Profile.ResetSettings();
                if (resetError != null)
                {
                    WebApi.Error(response, 500, "설정 초기화 실패: " + resetError);
                    return;
                }

                WebApi.Json(response, 200, WebApi.OkJson);
                WebManagementServer.Defer(WebManagementServer.AfterReboot, null);
            }
        }

        [Route("api/statistics/reset")]
        [Method("POST")]
        public void ResetStatistics(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                GatewayHost.Profile.ResetStatistics();
                WebApi.Json(e.Context.Response, 200, WebApi.OkJson);
            }
        }
    }
}
