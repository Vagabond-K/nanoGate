using System;
using System.Net;
using System.Text;
using nanoFramework.WebServer;
using VagabondK.nF.Web;
using nanoGate.Core;

namespace nanoGate.ModbusToDnp3
{
    // DNP3 프로필 UI용 범위 매핑 규칙 엔드포인트(GET/POST /api/rules).
    // GatewayHost.ExtraControllers를 통해 등록되며, 규칙은 재부팅 후 적용됩니다.
    internal sealed class RangeController
    {
        [Route("api/rules")]
        [Method("GET")]
        public void Get(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                HttpListenerResponse response = e.Context.Response;
                string error;
                string text = RangeStore.ReadAll(out error);
                if (error != null)
                {
                    WebApi.Error(response, 500, "규칙 파일을 읽지 못했습니다: " + error);
                    return;
                }

                WebApi.Text(response, 200, WebApi.TextType, text);
            }
        }

        // 규칙 테이블 전체를 교체합니다. 본문: text/plain 규칙 파일 내용.
        [Route("api/rules")]
        [Method("POST")]
        public void Put(WebServerEventArgs e)
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

                if (length > RangeStore.MaximumFileLength)
                {
                    WebApi.Error(response, 413,
                        "규칙 파일은 " + RangeStore.MaximumFileLength + "바이트를 넘을 수 없습니다.");
                    return;
                }

                string contentType = request.ContentType;
                if (contentType == null || !contentType.StartsWith("text/plain"))
                {
                    WebApi.Error(response, 415, "규칙 파일은 text/plain 형식이어야 합니다.");
                    return;
                }

                string text = WebApi.ReadBodyAsAscii(request, RangeStore.MaximumFileLength);
                if (text == null)
                    return;

                string error;
                RangeRule[] rules;
                error = RangeStore.Parse(text, out rules);
                if (error != null)
                {
                    WebApi.Error(response, 400, error);
                    return;
                }

                lock (GatewayHost.ConfigurationSync)
                    error = RangeStore.Save(text);
                if (error != null)
                {
                    WebApi.Error(response, 500, "규칙 저장 실패: " + error);
                    return;
                }

                var w = new JsonWriter(48);
                w.BeginObject().Prop("ok", true).Prop("count", rules.Length).EndObject();
                WebApi.Json(response, 200, w.ToString());
            }
        }

        // 규칙 파일을 삭제합니다(빈 테이블로 복원).
        [Route("api/rules/reset")]
        [Method("POST")]
        public void Reset(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                HttpListenerResponse response = e.Context.Response;
                string error;
                lock (GatewayHost.ConfigurationSync)
                    error = RangeStore.DeleteAll();
                if (error != null)
                {
                    WebApi.Error(response, 500, "규칙 삭제 실패: " + error);
                    return;
                }

                WebApi.Json(response, 200, WebApi.OkJson);
            }
        }
    }
}
