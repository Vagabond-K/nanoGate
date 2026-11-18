using System;
using System.IO;
using System.Net;
using nanoFramework.WebServer;
using VagabondK.nF.Web;
using nanoGate.Core;

namespace nanoGate.Points.Controllers
{
    // 포인트 매핑 표 엔드포인트(GET/POST /api/points).
    // GatewayHost.ExtraControllers를 통해 등록되며, 포인트 매핑을 쓰는 게이트웨이만 참조합니다.
    // 게이트웨이 어셈블리에서 typeof로 참조하므로 public입니다.
    public sealed class PointsController
    {
        private static readonly byte[] IoBuffer = new byte[256];

        [Route("api/points")]
        [Method("GET")]
        public void Get(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                HttpListenerResponse response = e.Context.Response;
                string error;
                Stream file = PointMappingStore.OpenRead(out error);
                if (error != null)
                {
                    WebApi.Error(response, 500, "포인트 매핑을 읽지 못했습니다: " + error);
                    return;
                }

                if (file == null)
                {
                    WebApi.Text(response, 200, WebApi.TextType, PointMappingStore.EmptyTable);
                    return;
                }

                using (file)
                {
                    int remaining = (int)file.Length;
                    WebApi.BeginStream(response, 200, WebApi.TextType, remaining);
                    while (remaining > 0)
                    {
                        if (GatewayHost.ButtonActionPending)
                            return;

                        int count = file.Read(IoBuffer, 0, remaining < IoBuffer.Length ? remaining : IoBuffer.Length);
                        if (count <= 0)
                            throw new IOException("Point mapping file ended early.");

                        response.OutputStream.Write(IoBuffer, 0, count);
                        remaining -= count;
                    }
                }
            }
        }

        // 업로드를 청크 단위로 검증하고 저장하므로 32 KB 표에도 32 KB buffer가 필요 없습니다.
        [Route("api/points")]
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

                if (length > PointMappingTable.MaximumFileLength)
                {
                    WebApi.Error(response, 413,
                        "포인트 매핑은 " + PointMappingTable.MaximumFileLength + "바이트를 넘을 수 없습니다.");
                    return;
                }

                string contentType = request.ContentType;
                if (contentType == null || !contentType.StartsWith("text/plain"))
                {
                    WebApi.Error(response, 415, "포인트 매핑은 text/plain 형식이어야 합니다.");
                    return;
                }

                using (var upload = new PointMappingUpload())
                {
                    string error;
                    lock (GatewayHost.ConfigurationSync)
                        error = upload.Begin();

                    int remaining = (int)length;
                    while (remaining > 0)
                    {
                        if (GatewayHost.ButtonActionPending)
                            return;

                        int count = request.InputStream.Read(
                            IoBuffer, 0, remaining < IoBuffer.Length ? remaining : IoBuffer.Length);
                        if (count <= 0)
                            return;

                        remaining -= count;
                        if (error == null)
                        {
                            lock (GatewayHost.ConfigurationSync)
                                error = upload.Write(IoBuffer, count);
                        }
                    }

                    if (error == null)
                    {
                        lock (GatewayHost.ConfigurationSync)
                            error = upload.Commit();
                    }

                    if (error != null)
                    {
                        if (upload.StorageFailure)
                            WebApi.Error(response, 500, "포인트 매핑 저장 실패: " + error);
                        else
                            WebApi.Error(response, 400, error);
                        return;
                    }

                    var w = new JsonWriter(48);
                    w.BeginObject().Prop("ok", true).Prop("count", upload.Count).EndObject();
                    WebApi.Json(response, 200, w.ToString());
                }
            }
        }

        // 이 프로필의 포인트 파일만 삭제합니다. 다른 컨버터의 매핑에는 손대지 않습니다.
        [Route("api/points/clear")]
        [Method("POST")]
        public void Clear(WebServerEventArgs e)
        {
            WebManagementServer.Touch();
            lock (WebManagementServer.RequestSync)
            {
                if (WebManagementServer.Blocked(e.Context))
                    return;

                HttpListenerResponse response = e.Context.Response;
                string error;
                lock (GatewayHost.ConfigurationSync)
                    error = PointMappingStore.DeleteCurrent();

                if (error != null)
                {
                    WebApi.Error(response, 500, "포인트 매핑 삭제 실패: " + error);
                    return;
                }

                WebApi.Json(response, 200, WebApi.OkJson);
            }
        }
    }
}
