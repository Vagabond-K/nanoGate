using System;

namespace nanoGate.Core
{
    // 바이트 단위 텍스트 검증·변환 유틸. Core의 설정 폼과 nanoGate.Points의 표 해석이 함께 쓰며,
    // 어느 쪽 어셈블리도 다른 쪽에 의존하지 않도록 공용 계층에 두었습니다.
    public static class ByteTextUtil
    {
        public static bool IsValidUtf8(byte[] bytes, int count)
        {
            int i = 0;
            while (i < count)
            {
                int b = bytes[i++];
                if (b < 0x80)
                    continue;

                int needed;
                int minimum = 0x80;
                int maximum = 0xBF;
                if (b >= 0xC2 && b <= 0xDF)
                {
                    needed = 1;
                }
                else if (b >= 0xE0 && b <= 0xEF)
                {
                    needed = 2;
                    if (b == 0xE0)
                        minimum = 0xA0;
                    else if (b == 0xED)
                        maximum = 0x9F;
                }
                else if (b >= 0xF0 && b <= 0xF4)
                {
                    needed = 3;
                    if (b == 0xF0)
                        minimum = 0x90;
                    else if (b == 0xF4)
                        maximum = 0x8F;
                }
                else
                {
                    return false;
                }

                if (count - i < needed)
                    return false;

                int next = bytes[i++];
                if (next < minimum || next > maximum)
                    return false;

                for (int j = 1; j < needed; j++)
                {
                    next = bytes[i++];
                    if (next < 0x80 || next > 0xBF)
                        return false;
                }
            }

            return true;
        }

        public static int HexValue(int value)
        {
            if (value >= '0' && value <= '9')
                return value - '0';
            if (value >= 'A' && value <= 'F')
                return value - 'A' + 10;
            if (value >= 'a' && value <= 'f')
                return value - 'a' + 10;
            return -1;
        }
    }
}
