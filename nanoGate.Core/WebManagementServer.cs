using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using nanoGate.Core.Controllers;
using nanoFramework.WebServer;
using VagabondK.nF.Web;

namespace nanoGate.Core
{
    // nanoFramework.WebServer의 네이티브 [Route]/[Method] 컨트롤러 지원 위에
    // 관리 API를 얹는 호스트. 엔드포인트는 컨트롤러 파일들에 있고, 이 클래스는
    // 컨트롤러 타입으로 리스너를 시작하고, 일치하지 않는 경로에 응답하고, POST를
    // 방어하고, 지연된 재부팅/네트워크/설정 작업을 실행합니다.
    // 프로필 전용 컨트롤러(Dnp3의 RangeController 등)가 요청 기록/차단 헬퍼를 쓰므로 일부 멤버가 public입니다.
    public static class WebManagementServer
    {
        internal const int MaxBodySize = 768;
        internal const int RebootDelayMilliseconds = 500;
        internal const int AfterReboot = 1;
        internal const int AfterNetwork = 2;
        internal const int AfterSetupMode = 3;
        internal const int AfterFactoryReset = 4;
        internal const string MarkerHeader = "x-nanogate";
        internal const string ContentPolicy =
            "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
            "img-src data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

        // 서버는 각 요청을 따로 스레드에서 처리하므로 처리기를 직렬화하여
        // 나머지 코드의 순차 처리 전제(공용 buffer, 설정 잠금)를 성립시킵니다.
        public static readonly object RequestSync = new object();
        // 응답이 전달될 때까지 기다려야 하는 작업(AfterReboot ... AfterFactoryReset).
        // 처리기 스레드와 Run 루프에서 함께 건드리며 NF에는 volatile 지원이 없으므로
        // 모든 접근을 Interlocked로 수행합니다.
        private static int _after;
        private static NetworkForm _network;
        private static GuardedWebServer _server;

        // 실행 중인 서버 인스턴스. 컨트롤러는 이를 통해 session 토큰을 읽고
        // 로그인 cookie을 설정합니다(이름은 라이브러리 기본값이 아니라 여기서 지정합니다).
        internal static GuardedWebServer Server
        {
            get { return _server; }
        }

        public static void Run()
        {
            try
            {
                // IPAddress.Any를 명시해야 합니다: 기본 오버로드는 GetDefaultLocalAddress()에
                // 바인딩하는데, 이 함수는 STA+AP 장치에서 AP 인터페이스를 고르기 때문입니다.
                DeviceAuth.Load();
                CsrfGuard.BlockedMessage = "허용되지 않은 요청입니다. 관리 화면에서 다시 시도하세요.";

                // 페이지 라우터(WebUiRoutes)는 게이트웨이가 ExtraControllers로 주입합니다.
                // (라이브러리는 생성 코드를 어셈블리에 포함하지 않습니다)
                Type[] controllers =
                {
                    typeof(StatusController),
                    typeof(SettingsController),
                    typeof(SystemController),
                    typeof(AuthController)   // VagabondK.nF.Web 라이브러리 컨트롤러(api/auth/* 6종)
                };

                Type[] extra = GatewayHost.ExtraControllers;
                if (extra != null && extra.Length > 0)
                {
                    Type[] merged = new Type[controllers.Length + extra.Length];
                    Array.Copy(controllers, merged, controllers.Length);
                    Array.Copy(extra, 0, merged, controllers.Length, extra.Length);
                    controllers = merged;
                }

                _server = new GuardedWebServer(GatewayHost.ManagementPort, controllers, DeviceAuth.Guard)
                {
                    SessionCookieName = "ngauth",
                    SessionHeaderName = "x-nanogate-session",
                    ApiKeyHeaderName = "x-nanogate-apikey"
                };

                // 미인증 페이지 요청은 서버가 로그인 페이지로 302 처리합니다(생성 라우터와 협업).
                _server.LoginPagePath = GatewayHost.LoginPagePath;

                // 표준 인증 엔드포인트는 라이브러리 AuthController가 담당합니다.
                AuthController.Server = _server;
                AuthController.CsrfMarker = MarkerHeader;
                AuthController.BodyLimit = MaxBodySize;
                AuthController.LocalAddress = DeviceNetwork.Address;
                AuthController.Persist = new AuthCredentialPersist(DeviceAuth.Save);
                AuthController.OnRequest = new AuthRequestObserver(ObserveAuthRequest);
                AuthController.MsgLocked = "로그인이 잠겼습니다. 잠시 후 다시 시도하세요.";
                AuthController.MsgBadPassword = "비밀번호가 올바르지 않습니다. 남은 시도: {0}회.";
                AuthController.MsgDisabled = "비밀번호가 설정되어 있지 않습니다.";
                AuthController.MsgNoPasswordField = "password 항목이 없습니다.";
                AuthController.MsgNoApiKeyField = "apikey 항목이 없습니다.";
                AuthController.MsgPasswordLength = "비밀번호는 8~63자여야 합니다.";
                AuthController.MsgCharset = "영문, 숫자, 기호만 사용할 수 있습니다.";
                AuthController.MsgApiKeyLength = "API 키는 64자 이하여야 합니다.";
                AuthController.MsgGrantType = "grant_type은 password 또는 refresh_token이어야 합니다.";
                AuthController.MsgBadRefresh = "리프레시 토큰이 유효하지 않습니다. 비밀번호로 다시 로그인하세요.";
                AuthController.MsgPersistFailed = "인증 파일을 저장하지 못했습니다: {0}";

                // 어떤 컨트롤러도 차지하지 않는 경로(오타, 다른 요청 메서드)는 여기서 끝납니다.
                _server.CommandReceived += OnUnmatched;
                _server.Start();
                Debug.WriteLine("Web management listening on TCP " + GatewayHost.ManagementPort + ".");
            }
            catch (Exception error)
            {
                Debug.WriteLine("Web management listener failed: " + ErrorText.Describe(error));
            }

            while (true)
            {
                GatewayHost.HandleButtonAction();

                int after = Interlocked.Exchange(ref _after, 0);
                if (after != 0)
                {
                    Thread.Sleep(RebootDelayMilliseconds);
                    if (after == AfterNetwork)
                        DeviceNetwork.Apply(_network);
                    else if (after == AfterSetupMode && GatewayHost.Setup)
                        DeviceNetwork.ExitSetup();
                    else if (after == AfterSetupMode)
                        DeviceNetwork.EnterSetup(false);
                    else if (after == AfterFactoryReset)
                        DeviceNetwork.FactoryReset();

                    Debug.WriteLine("Rebooting by web management request.");
                    DeviceNetwork.Reboot();
                }

                Thread.Sleep(GatewayHost.ButtonPollMilliseconds);
            }
        }

        // Run 루프가 처리할 작업을 대기열에 넣습니다. 호출될 때 응답은 이미 쓰인 뒤입니다.
        internal static void Defer(int kind, NetworkForm form)
        {
            if (form != null)
                _network = form;

            Interlocked.Exchange(ref _after, kind);
        }

        // 설정 화면 활동 시각을 갱신합니다(유휴 복귀 타이머용). 컨트롤러에서 호출합니다.
        public static void Touch()
        {
            GatewayHost.LastActivity = Environment.TickCount64;
        }

        // 인증 요청은 설정 모드 유휴 타이머를 갱신하고 라이브러리 훅을 통해
        // CSRF 호스트 주소를 재동기화합니다(주소는 Wi-Fi/설정 모드에 따라 바뀝니다).
        private static void ObserveAuthRequest(HttpListenerContext context)
        {
            Touch();
            AuthController.LocalAddress = DeviceNetwork.Address;
        }

        private static void OnUnmatched(object sender, WebServerEventArgs e)
        {
            Touch();
            lock (RequestSync)
                WebApi.Error(e.Context.Response, 404, "찾을 수 없는 경로입니다.");
        }

        // 모든 /api 동작이 함께 쓰는 사이트 간 POST 차단기: 사용자 정의 헤더를
        // 요구하면 이 서버가 절대 통과시키지 않는 CORS preflight가 필요하고,
        // Host 검사는 남의 이름을 겨냥한 DNS rebinding 요청을 거부합니다.
        public static bool Blocked(HttpListenerContext context)
        {
            return CsrfGuard.Check(context, MarkerHeader, DeviceNetwork.Address);
        }
    }
}
