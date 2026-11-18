using System;
using System.Diagnostics;
using System.IO;
using VagabondK.nF.Web;

namespace nanoGate.Core
{
    // 웹 로그인 상태를 I:\nanogate.auth에 읽고 저장하며, nanogate.cfg와 분리하여
    // 설정 가져오기/내보내기에도 자격 증명이 포함되지 않습니다.
    // 공장 초기화 시 파일을 삭제하여 장치를 열려 있는 기본 상태로 되돌립니다.
    internal static class DeviceAuth
    {
        private const string FilePath = "I:\\nanogate.auth";
        private const string TempFilePath = "I:\\nanogate.auth.tmp";

        internal static readonly LoginGuard Guard = new LoginGuard();

        // 웹 서버가 생성되기 전에 시작 시 한 번 호출됩니다.
        internal static void Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return;

                string hash = null, apiKey = null;
                string text = File.ReadAllText(FilePath);
                int start = 0;
                while (start < text.Length)
                {
                    int end = text.IndexOf('\n', start);
                    if (end < 0)
                        end = text.Length;

                    string line = text.Substring(start, end - start).TrimEnd('\r');
                    if (line.Length > 2 && line[1] == '=')
                    {
                        if (line[0] == 'h')
                            hash = line.Substring(2);
                        else if (line[0] == 'k')
                            apiKey = line.Substring(2);
                    }

                    start = end + 1;
                }

                Guard.PasswordHash = hash;
                Guard.ApiKey = apiKey;
            }
            catch (Exception error)
            {
                Debug.WriteLine("Loading auth file failed: " + ErrorText.Describe(error));
            }
        }

        // 파일 전체를 원자적으로 다시 쓰며, 빈 값은 생략합니다.
        internal static string Save()
        {
            try
            {
                string hash = Guard.PasswordHash;
                string apiKey = Guard.ApiKey;

                if ((hash == null || hash.Length == 0) && (apiKey == null || apiKey.Length == 0))
                {
                    Delete();
                    return null;
                }

                string text = (hash != null && hash.Length > 0 ? "h=" + hash + "\r\n" : string.Empty) +
                              (apiKey != null && apiKey.Length > 0 ? "k=" + apiKey + "\r\n" : string.Empty);

                if (File.Exists(TempFilePath))
                    File.Delete(TempFilePath);

                File.WriteAllText(TempFilePath, text);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
                File.Move(TempFilePath, FilePath);
                return null;
            }
            catch (Exception error)
            {
                return ErrorText.Describe(error);
            }
        }

        internal static void Delete()
        {
            try
            {
                if (File.Exists(TempFilePath))
                    File.Delete(TempFilePath);
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch (Exception error)
            {
                Debug.WriteLine("Deleting auth file failed: " + ErrorText.Describe(error));
            }
        }
    }
}
