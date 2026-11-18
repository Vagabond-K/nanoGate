using System;
using System.IO;

namespace nanoGate.Core
{
    // 게이트웨이 공용 데이터 파일 경로와 잔여 파일 정리.
    // 포인트 표의 해석은 nanoGate.Points 어셈블리의 역할이고, 여기서는 파일 존재 삭제만 알면 충분하므로
    // 포인트 매핑을 쓰지 않는 제품(예: Modbus Serial↔TCP)도 다른 프로필의 잔여를 정리할 수 있습니다.
    public static class DeviceStorage
    {
        // GatewayHost가 프로필 Load 전에 설정하는 고유 키(예: "modbus-to-mqtt").
        // 포인트 표를 컨버터별로 격리해 서로 다른 게이트웨이 간 오염을 막습니다.
        // 빈 문자열이면 과거의 공용 파일명을 그대로 써서 동작을 유지합니다.
        public static string ProfileId = "";

        private const string PointsBasePath = "I:\\nanogate.points";

        public static string PointsFilePath
        {
            get
            {
                return ProfileId.Length > 0 ? PointsBasePath + "." + ProfileId : PointsBasePath;
            }
        }

        public static string PointsTempFilePath
        {
            get { return PointsFilePath + ".tmp"; }
        }

        public static string PointsBackupFilePath
        {
            get { return PointsFilePath + ".bak"; }
        }

        public static string DeletePointFiles()
        {
            try
            {
                // 프로필 키가 바뀌기 전(초기화·백업 화면) 호출될 수 있으므로
                // 현재 이름과 과거 공용 이름, 그리고 알려진 모든 프로필 이름을 정리합니다.
                DeleteSet(PointsFilePath);
                DeleteSet(PointsBasePath);
                string[] ids = new string[] { "modbus-to-mqtt", "modbus-to-dnp3", "modbus-serial-tcp" };
                for (int i = 0; i < ids.Length; i++)
                    DeleteSet(PointsBasePath + "." + ids[i]);
                return null;
            }
            catch (Exception storageError)
            {
                return ErrorText.Describe(storageError);
            }
        }

        // 현재 프로필의 포인트 파일만 정리합니다 (다른 컨버터 매핑 유지).
        public static string DeleteCurrentPointFiles()
        {
            try
            {
                DeleteSet(PointsFilePath);
                return null;
            }
            catch (Exception storageError)
            {
                return ErrorText.Describe(storageError);
            }
        }

        private static void DeleteSet(string path)
        {
            if (File.Exists(path + ".bak"))
                File.Delete(path + ".bak");
            if (File.Exists(path + ".tmp"))
                File.Delete(path + ".tmp");
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
