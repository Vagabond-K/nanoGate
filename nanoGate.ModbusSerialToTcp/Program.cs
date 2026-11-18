using System;
using nanoGate.Core;

namespace nanoGate.ModbusSerialToTcp
{
    // 애플리케이션 진입점.
    // Modbus RTU/ASCII(시리얼) <-> Modbus TCP 변환 프로필을 nanoGate.Core 호스트에 주입하여 실행합니다.
    internal sealed class Program
    {
        public static void Main()
        {
            // 페이지 라우터(WebUiRoutes)는 게이트웨이 프로젝트마다 생성되므로 여기서 주입합니다.
            GatewayHost.ExtraControllers = new Type[] { typeof(WebUiRoutes) };
            GatewayHost.LoginPagePath = WebUiRoutes.LoginPath;
            GatewayHost.Run(new ModbusGatewayProfile());
        }
    }
}
