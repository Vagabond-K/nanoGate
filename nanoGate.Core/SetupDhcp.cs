using System;

namespace nanoGate.Core
{
    internal sealed class SetupDhcp
    {
        internal const int ReceiveBufferBytes = 600;
        internal const int ReplyBytes = 300;
        private const int LeaseSlots = 4;
        private const int LeaseMilliseconds = 7200000;
        private const int OfferMilliseconds = 60000;
        private readonly byte[] _leases = new byte[LeaseSlots * 6];
        private readonly long[] _expires = new long[LeaseSlots];

        private static readonly byte[] ReplyOptions =
        {
            53, 1, 0, 54, 4, 192, 168, 4, 1, 51, 4, 0, 0, 0x1C, 0x20,
            1, 4, 255, 255, 255, 0, 3, 4, 192, 168, 4, 1, 255,
        };

        internal int Answer(byte[] request, int length, byte[] reply, long now)
        {
            if (length < 244 || length > request.Length || reply.Length < ReplyBytes ||
                request[0] != 1 || request[1] != 1 || request[2] != 6 || request[3] != 0 ||
                (request[28] & 1) != 0 ||
                request[24] != 0 || request[25] != 0 || request[26] != 0 || request[27] != 0 ||
                request[236] != 99 || request[237] != 130 || request[238] != 83 || request[239] != 99)
                return 0;

            int type = 0;
            int requested = -1;
            int server = 1;
            bool hasType = false, hasRequested = false, hasServer = false, ended = false;
            int i = 240;
            while (i < length)
            {
                int code = request[i++];
                if (code == 0)
                    continue;
                if (code == 255)
                {
                    ended = true;
                    break;
                }
                if (i >= length)
                    return 0;

                int size = request[i++];
                if (i + size > length)
                    return 0;
                if (code == 53)
                {
                    if (size != 1 || hasType)
                        return 0;
                    hasType = true;
                    type = request[i];
                }
                else if (code == 50)
                {
                    if (size != 4 || hasRequested)
                        return 0;
                    hasRequested = true;
                    requested = HostOf(request, i);
                }
                else if (code == 54)
                {
                    if (size != 4 || hasServer)
                        return 0;
                    hasServer = true;
                    server = HostOf(request, i);
                }
                i += size;
            }

            if (!ended || server != 1 || (type != 1 && type != 3 && type != 7))
                return 0;

            int slot = -1;
            for (int s = 0; s < LeaseSlots && slot < 0; s++)
            {
                if (_expires[s] <= now)
                    continue;
                int m = 0;
                while (m < 6 && _leases[s * 6 + m] == request[28 + m])
                    m++;
                if (m == 6)
                    slot = s;
            }

            if (type == 7)
            {
                if (slot >= 0 && HostOf(request, 12) == slot + 2)
                    _expires[slot] = 0;
                return 0;
            }

            int answer = 2;
            if (type == 1)
            {
                if (slot < 0)
                {
                    for (int s = 0; s < LeaseSlots && slot < 0; s++)
                        if (_expires[s] <= now)
                            slot = s;
                }
                // 만료되지 않은 리스를 다른 MAC에 재배정해서는 안 됩니다.
                if (slot < 0)
                    return 0;
                if (_expires[slot] < now + OfferMilliseconds)
                    _expires[slot] = now + OfferMilliseconds;
            }
            else
            {
                if (!hasRequested)
                    requested = HostOf(request, 12);
                // 서버를 다시 시작한 후에는 INIT-REBOOT/갱신이 앞선 DISCOVER 없이 도착할 수 있습니다.
                if (slot < 0 && requested >= 2 && requested < LeaseSlots + 2 &&
                    _expires[requested - 2] <= now)
                    slot = requested - 2;
                answer = requested == slot + 2 ? 5 : 6;
                if (slot < 0)
                    answer = 6;
                if (answer == 5)
                    _expires[slot] = now + LeaseMilliseconds;
            }

            if (answer != 6)
                Array.Copy(request, 28, _leases, slot * 6, 6);
            Array.Clear(reply, 0, reply.Length);
            reply[0] = 2;
            reply[1] = 1;
            reply[2] = 6;
            Array.Copy(request, 4, reply, 4, 4);
            reply[10] = request[10];
            reply[11] = request[11];
            if (answer != 6)
            {
                reply[16] = 192;
                reply[17] = 168;
                reply[18] = 4;
                reply[19] = (byte)(slot + 2);
            }

            Array.Copy(request, 24, reply, 24, 20);
            reply[236] = 99;
            reply[237] = 130;
            reply[238] = 83;
            reply[239] = 99;
            Array.Copy(ReplyOptions, 0, reply, 240, ReplyOptions.Length);
            reply[242] = (byte)answer;
            if (answer == 6)
            {
                reply[10] = 0x80;
                reply[11] = 0;
                Array.Clear(reply, 249, ReplyOptions.Length - 9);
                reply[249] = 255;
            }

            return ReplyBytes;
        }

        private static int HostOf(byte[] packet, int offset)
        {
            return packet[offset] == 192 && packet[offset + 1] == 168 && packet[offset + 2] == 4 ? packet[offset + 3] : -1;
        }
    }
}
