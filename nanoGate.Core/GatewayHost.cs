using System;
using System.Device.Gpio;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using VagabondK.nF.Web;

namespace nanoGate.Core
{
    // 컨버터마다 하나씩 구현하며, 공용 호스트와 네트워크, 웹 UI가 이를 호출합니다.
    // 게이트웨이 프로젝트가 구현하므로 public입니다(InternalsVisibleTo는 사용하지 않습니다).
    public interface IGatewayProfile
    {
        // 고정된 프로필 이름으로, 설정과 백업에도 저장됩니다.
        string Id { get; }

        // /api/status에서 "capabilities"(JSON 객체)로 제공하는 서버 자기 서술.
        // 기존의 manifest.features를 대체합니다: points/name/scale/byteOrder 플래그와 접근
        // 목록, 그리고 UI가 마크업만으로는 유추할 수 없는 구조화된 addr/cacheCols 데이터.
        string CapabilitiesJson { get; }

        // 이 프로필이 POST api/settings에서 실제로 받는 설정 키들의 JSON 배열.
        string SettingsKeysJson { get; }

        // 설정을 읽고 GatewayHost.SettingsSource / SettingsError에 결과를 알립니다.
        void Load();

        // 컨버터를 시작하며, 영구히 차단될 수 있습니다. GatewayHost.Ready / Error에 결과를 알립니다.
        void Run();

        // 프로필 필드를 status 객체에 덧붙인다. 메인 writer에 Prop(...)로 바로 기록하므로
        // 문자열 조각·쉼표 계약이 없다(GatewayHost.GetStatusJson이 컨텍스트를 관리).
        void WriteStatus(JsonWriter writer);

        // 게이트웨이가 포인트를 폴링하여 /api/cache로 노출하는 라이브 cache에 채우는 경우 true.
        bool HasPointCache { get; }

        // 캐시 포인트 객체를 /api/cache의 points 배열에 기록한다. 호출 시 배열 컨텍스트이므로
        // 각 포인트를 BeginObject()...EndObject()로 나열하면 된다. HasPointCache true일 때만 호출.
        void WriteCache(JsonWriter writer);

        // urlencoded 설정 폼을 적용하며, 성공하면 null을 반환합니다.
        string SaveSettings(string form, out bool storageFailure);

        string ResetSettings();

        void ResetStatistics();

        // /api/status의 "limits" 객체에 덧붙이는 프로필별 한계값(JSON 필드 나열, 중괄호 없이).
        // 포인트 매핑처럼 공용 Core가 알지 못하는 필드는 own하는 어셈블리가 보고합니다. 없으면 "".
        string ExtraLimitsJson { get; }
    }

    public static class GatewayHost
    {
        // 게이트웨이 프로젝트가 직접 읽으므로 public입니다(InternalsVisibleTo는 사용하지 않습니다).
        public const int ManagementPort = 80;
        public const string SourceFile = "file";
        public const string SourceDefault = "default";
        public const string SourceInvalid = "default-invalid";
        public const string SourceStorageError = "default-storage";
        // 저장된 파일이 다른 컨버터의 것일 때 Apply가 반환하고, Load가 그 파일을 지웁니다.
        public const string ProfileMismatchError = "다른 게이트웨이 프로필의 설정 파일입니다.";

        // 프로필 전용 API 컨트롤러(예: DNP3 범위 규칙 엔드포인트)로, 웹 서버를 시작하기 전에
        // 공용 컨트롤러 목록에 덧붙입니다. 프로필이 추가하는 것이 없으면 null.
        public static Type[] ExtraControllers;

        // 게이트웨이가 부트스트랩에서 WebUiRoutes.LoginPath로 설정하는 로그인 페이지 경로.
        public static string LoginPagePath = "";

        private const int ConnectTimeoutMilliseconds = 300000;
        internal const int ButtonPollMilliseconds = 50;

        internal static IGatewayProfile Profile;
        // 이하 4개 필드는 프로필이 자신의 상태/설정 로드 결과를 알리는 통로로 게이트웨이에서 씁니다.
        public static bool Ready;
        public static string Error;
        public static string SettingsSource;
        public static string SettingsError;
        internal static bool Setup;
        // 마지막 설정 클라이언트 요청 시점의 Environment.TickCount64. 유휴 설정 모드는 정상 모드로 돌아갑니다.
        internal static long LastActivity;

        // 컨버터 트랜잭션 스택이 매핑 파서의 할당 피크와 겹치지 않도록 합니다.
        public static readonly object ConfigurationSync = new object();
        private static readonly object ButtonSync = new object();
        private static int _buttonAction;

        public static bool ButtonActionPending
        {
            get
            {
                lock (ButtonSync)
                    return _buttonAction != SetupButton.None;
            }
        }

        public static void Run(IGatewayProfile profile)
        {
            Profile = profile;
            // 포인트 표 파일名에 프로필 키를 박아 컨버터 간 격리(Load/Run보다 먼저).
            DeviceStorage.ProfileId = profile.Id;
            LastActivity = Environment.TickCount64;
            profile.Load();
            Debug.WriteLine("Settings source: " + SettingsSource + (SettingsError == null ? string.Empty : " (" + SettingsError + ")"));

            Setup = DeviceNetwork.Prepare();
            new Thread(WatchButton).Start();
            if (Setup)
            {
                Error = "설정 모드에서는 게이트웨이를 시작하지 않습니다.";
                Debug.WriteLine("Setup mode: join " + DeviceNetwork.SetupSsid + " and open http://" + DeviceNetwork.ApAddress + ":" + ManagementPort + "/");
                new Thread(WebManagementServer.Run).Start();
                DeviceNetwork.RunSetup();
            }

            DeviceNetwork.ReapplyStaticAddress();
            WaitForNetwork();
            new Thread(WebManagementServer.Run).Start();
            try
            {
                profile.Run();
            }
            catch (OutOfMemoryException)
            {
                Error = "메모리 부족으로 게이트웨이가 중지되었습니다. 재부팅이 필요합니다.";
                Debug.WriteLine(Error);
            }
            catch (Exception error)
            {
                Error = "게이트웨이 실행 실패: " + ErrorText.Describe(error);
                Debug.WriteLine(Error);
            }
            Ready = false;
            Thread.Sleep(Timeout.Infinite);
        }

        private static void WaitForNetwork()
        {
            long started = Environment.TickCount64;
            long checkedAt = 0;
            bool hasAp = DeviceNetwork.HasAp;
            while (true)
            {
                HandleButtonAction();
                long now = Environment.TickCount64;
                if (now - checkedAt >= 1000)
                {
                    checkedAt = now;
                    // 정적 주소는 링크가 올라오기 전에도 보고되므로 두 검사 모두 필요합니다.
                    string address = DeviceNetwork.Address;
                    if (address.Length > 0 && NetworkInterface.GetIsNetworkAvailable())
                    {
                        Debug.WriteLine("Gateway IP address: " + address);
                        return;
                    }

                    if (hasAp && now - started > ConnectTimeoutMilliseconds)
                    {
                        Debug.WriteLine("Wi-Fi did not connect; switching to setup mode.");
                        DeviceNetwork.EnterSetup(false);
                    }
                }

                Thread.Sleep(ButtonPollMilliseconds);
            }
        }

        // 중단된 요청이 자기 파일을 해제한 뒤처럼, HTTP·파일 작업 밖에서만 실행합니다.
        internal static void HandleButtonAction()
        {
            int action;
            lock (ButtonSync)
            {
                action = _buttonAction;
                _buttonAction = SetupButton.None;
            }

            if (action == SetupButton.FactoryReset)
                DeviceNetwork.FactoryReset();
            else if (action == SetupButton.Enter && !Setup && DeviceNetwork.HasAp)
                DeviceNetwork.EnterSetup(false);
            else if (action == SetupButton.Exit && Setup)
                DeviceNetwork.ExitSetup();
        }

        // 샘플링이 느린 HTTP 클라이언트를 기다려서는 안 됩니다: 지연된 버튼 해제가 자칫 초기화가 될 수 있습니다.
        private static void WatchButton()
        {
            try
            {
                var gpio = new GpioController();
                var button = gpio.OpenPin(Board.ButtonPin, PinMode.InputPullUp);
                GpioPin led = null;
                try
                {
                    if (Board.LedPin >= 0)
                    {
                        led = gpio.OpenPin(Board.LedPin, PinMode.Output);
                        led.Write(PinValue.Low);
                    }
                }
                catch (Exception error)
                {
                    Debug.WriteLine("Status LED unavailable: " + ErrorText.Describe(error));
                    led = null;
                }

                var state = new SetupButton();
                bool lit = false;
                while (true)
                {
                    long now = Environment.TickCount64;
                    int action = state.Update(button.Read() == PinValue.Low, now, Setup);
                    if (action != SetupButton.None)
                    {
                        lock (ButtonSync)
                            if (action > _buttonAction)
                                _buttonAction = action;
                    }

                    bool on = state.IsLit(now, Setup);
                    if (led != null && on != lit)
                    {
                        try
                        {
                            led.Write(on ? PinValue.High : PinValue.Low);
                            lit = on;
                        }
                        catch (Exception error)
                        {
                            Debug.WriteLine("Status LED failed: " + ErrorText.Describe(error));
                            led = null;
                        }
                    }

                    Thread.Sleep(ButtonPollMilliseconds);
                }
            }
            catch (Exception error)
            {
                Debug.WriteLine("Button monitoring failed: " + ErrorText.Describe(error));
            }
        }
    }
}