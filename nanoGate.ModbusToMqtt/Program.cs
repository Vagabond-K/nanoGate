using System;
using nanoGate.Core;
using nanoGate.Points.Controllers;

namespace nanoGate.ModbusToMqtt
{
    // 애플리케이션 진입점.
    // Modbus RTU/ASCII(시리얼) -> MQTT 변환 프로필을 nanoGate.Core 호스트에 주입하여 실행합니다.
    internal sealed class Program
    {
        public static void Main()
        {
            // 포인트 표 컨트롤러와 페이지 라우터(WebUiRoutes)는 게이트웨이 프로젝트마다 생성되므로 여기서 주입합니다.
            GatewayHost.ExtraControllers = new Type[] { typeof(PointsController), typeof(WebUiRoutes) };
            GatewayHost.LoginPagePath = WebUiRoutes.LoginPath;
            GatewayHost.Run(new ModbusToMqttProfile());
        }
    }
}
