using System;

namespace nanoGate.Core
{
    // 예외를 사람이 읽을 한 문장으로 요약합니다. 게이트웨이에서도 사용합니다.
    public static class ErrorText
    {
        public static string Describe(Exception error)
        {
            string message = error.Message;
            string typeName = error.GetType().Name;
            return message == null || message.Length == 0 ? typeName : typeName + ": " + message;
        }
    }
}
