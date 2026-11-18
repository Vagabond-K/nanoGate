using System;
using nanoGate.Core;
using System.IO;
using System.Text;

namespace nanoGate.Points
{
    public enum PointAccess
    {
        ReadOnly,
        WriteOnly,
        ReadWrite
    }

    public sealed class ProtocolAddress
    {
        // /api/status의 limits로 클라이언트에 전달되는 토큰 길이 한계의 단일 출처.
        public const int MaximumTokenLength = 24;
        public const int MaximumAddressLength = 48;
        public const int MaximumIdLength = 24;

        public string Protocol { get; private set; }
        public string DeviceId { get; private set; }
        public string AddressSpace { get; private set; }
        public string Address { get; private set; }
        public string DataType { get; private set; }

        public ProtocolAddress(
            string protocol,
            string deviceId,
            string addressSpace,
            string address,
            string dataType)
        {
            Protocol = protocol;
            DeviceId = deviceId;
            AddressSpace = addressSpace;
            Address = address;
            DataType = dataType;
        }

        public string Validate(string label)
        {
            if (!IsToken(Protocol, MaximumTokenLength))
                return label + " 프로토콜은 영문·숫자·_ . : / - 로 된 1~" + MaximumTokenLength.ToString() + "자여야 합니다.";
            if (!IsToken(DeviceId, MaximumTokenLength))
                return label + " 장치 ID는 영문·숫자·_ . : / - 로 된 1~" + MaximumTokenLength.ToString() + "자여야 합니다.";
            if (!IsToken(AddressSpace, MaximumTokenLength))
                return label + " 주소 영역은 영문·숫자·_ . : / - 로 된 1~" + MaximumTokenLength.ToString() + "자여야 합니다.";
            if (!IsToken(Address, MaximumAddressLength))
                return label + " 주소는 영문·숫자·_ . : / - 로 된 1~" + MaximumAddressLength.ToString() + "자여야 합니다.";
            if (!IsDataType(DataType))
                return label + " 자료형을 지원하지 않습니다.";
            return null;
        }

        internal string GetIdentity()
        {
            return Protocol + "|" + DeviceId + "|" + AddressSpace + "|" + Address;
        }

        internal static bool IsToken(string value, int maximumLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length > maximumLength)
                return false;

            for (int i = 0; i < value.Length; i++)
            {
                if (!IsTokenCharacter(value[i]))
                    return false;
            }

            return true;
        }

        internal static bool IsTokenCharacter(int c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.' || c == ':' || c == '/';
        }

        internal static bool IsDataType(string value)
        {
            if (value == "Bool" || value == "Int16" || value == "UInt16" ||
                value == "Int32" || value == "UInt32" || value == "Float32")
                return true;

            // 32비트 형식에는 바이트 순서 접미사를 붙일 수 있습니다. 예: "Float32:CDAB".
            if (value == null || value.Length < 10 || value.Length > 12)
                return false;

            int colon = value.Length - 5;
            if (value[colon] != ':')
                return false;

            string order = value.Substring(colon + 1);
            if (order != "ABCD" && order != "CDAB" && order != "BADC" && order != "DCBA")
                return false;

            string baseType = value.Substring(0, colon);
            return baseType == "Int32" || baseType == "UInt32" || baseType == "Float32";
        }

        // 선택적인 바이트 순서 접미사를 제외한 자료형.
        public static string BaseTypeOf(string dataType)
        {
            if (dataType == null)
                return null;
            int colon = dataType.IndexOf(':');
            return colon < 0 ? dataType : dataType.Substring(0, colon);
        }

        // 바이트 순서 접미사. 기본값은 빅엔디안 "ABCD".
        public static string ByteOrderOf(string dataType)
        {
            if (dataType == null)
                return "ABCD";
            int colon = dataType.IndexOf(':');
            return colon < 0 ? "ABCD" : dataType.Substring(colon + 1);
        }
    }

    public sealed class PointMapping
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public ProtocolAddress Source { get; private set; }
        public ProtocolAddress Target { get; private set; }
        public PointAccess Access { get; private set; }
        public int ScaleNumerator { get; private set; }
        public int ScaleDenominator { get; private set; }
        public int Offset { get; private set; }

        public PointMapping(
            string id,
            string name,
            ProtocolAddress source,
            ProtocolAddress target,
            PointAccess access,
            int scaleNumerator,
            int scaleDenominator,
            int offset)
        {
            Id = id;
            Name = name;
            Source = source;
            Target = target;
            Access = access;
            ScaleNumerator = scaleNumerator;
            ScaleDenominator = scaleDenominator;
            Offset = offset;
        }

        public string Validate()
        {
            if (!ProtocolAddress.IsToken(Id, ProtocolAddress.MaximumIdLength))
                return "포인트 ID는 영문·숫자·_ . : / - 로 된 1~" + ProtocolAddress.MaximumIdLength.ToString() + "자여야 합니다.";
            if (string.IsNullOrEmpty(Name) || Name.Length > PointMappingTable.NameMaxLength || ContainsControlCharacter(Name))
                return "포인트 이름은 제어 문자 없이 1~" + PointMappingTable.NameMaxLength.ToString() + "자여야 합니다.";
            if (Source == null || Target == null)
                return "원본과 목적지 주소가 모두 필요합니다.";

            string error = Source.Validate("원본");
            if (error != null)
                return error;
            error = Target.Validate("목적지");
            if (error != null)
                return error;
            if (Access != PointAccess.ReadOnly && Access != PointAccess.WriteOnly && Access != PointAccess.ReadWrite)
                return "접근 모드가 올바르지 않습니다.";
            if (ScaleDenominator == 0)
                return "스케일 분모는 0일 수 없습니다.";

            return null;
        }

        private static bool ContainsControlCharacter(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < ' ' || c == (char)0x7F)
                    return true;
            }

            return false;
        }
    }

    public sealed class PointMappingTable
    {
        public const int MaximumPointCount = 32;
        public const int MaximumLineLength = 1024;
        public const int MaximumFileLength = 32768;
        public const int SchemaVersion = 1;
        // const 유지 필수: static readonly면 정적 초기자 실행 전에 접근돼
        // null+개행(1바이트 빈 표)이 되는 사고가 실장치에서 발생했다.
        // SchemaVersion이 바뀌면 이 리터럴도 함께 수정하세요.
        public const string SchemaHeader = "schemaVersion=1";

        // 디코딩된 필드는 최대 48자를 담으며, 각 문자는 UTF-8로 최대 4바이트입니다.
        public const int MaximumFieldBytes = 256;
        // /api/status의 limits로 클라이언트에 전달되는 검증 한계값의 단일 출처.
        public const int NameMaxLength = 48;
        private const int FieldCount = 16;
        private const string InvalidEncoding = "퍼센트 인코딩 또는 UTF-8 형식이 잘못된 필드가 있습니다.";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding();
        internal static readonly byte[] RecordPrefix = Utf8.GetBytes("point=");
        internal static readonly byte[] SchemaHeaderBytes = Utf8.GetBytes(SchemaHeader);
        private static readonly object FieldSeparator = (byte)'|';

        private readonly PointMapping[] _points = new PointMapping[MaximumPointCount];
        private int _count;

        public int Count
        {
            get { return _count; }
        }

        public PointMapping GetAt(int index)
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException("index");
            return _points[index];
        }

        public string Add(PointMapping point)
        {
            if (point == null)
                return "포인트 매핑이 필요합니다.";
            string error = point.Validate();
            if (error != null)
                return error;
            if (_count == MaximumPointCount)
                return "포인트는 최대 " + MaximumPointCount + "개까지 등록할 수 있습니다.";

            for (int i = 0; i < _count; i++)
            {
                if (_points[i].Id == point.Id)
                    return "이미 사용 중인 포인트 ID입니다: " + point.Id;
                if (_points[i].Target.GetIdentity() == point.Target.GetIdentity())
                    return "목적지 주소가 포인트 " + _points[i].Id + "와(과) 겹칩니다.";
            }

            _points[_count++] = point;
            return null;
        }

        public string Serialize()
        {
            var builder = new StringBuilder(SchemaHeader + "\n");
            for (int i = 0; i < _count; i++)
                AppendRecord(builder, _points[i]);

            return builder.ToString();
        }

        // 정규화된 "point=...|\n" 레코드로, 필드 순서와 인코딩이 파일 형식을 정의합니다.
        public static string FormatRecord(PointMapping point)
        {
            var builder = new StringBuilder(160);
            AppendRecord(builder, point);
            return builder.ToString();
        }

        public static bool TryParse(string text, out PointMappingTable table, out string error)
        {
            table = null;
            if (text == null || text.Length > MaximumFileLength)
            {
                error = "포인트 매핑 파일이 없거나 " + MaximumFileLength + "바이트를 넘습니다.";
                return false;
            }

            var validator = new PointMappingValidator();
            var parsed = new PointMappingTable();
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0)
                    end = text.Length;
                string line = text.Substring(start, end - start).TrimEnd('\r');
                start = end + 1;
                if (line.Length > MaximumLineLength)
                {
                    error = "한 줄이 " + MaximumLineLength + "바이트를 넘습니다.";
                    return false;
                }

                PointMapping point;
                error = validator.AcceptLine(line, out point);
                if (error == null && point != null)
                    error = parsed.Add(point);
                if (error != null)
                    return false;
            }

            error = validator.Complete();
            if (error != null)
                return false;

            table = parsed;
            return true;
        }

        // "point=" 레코드 하나를 검증이나 중복 검사 없이 해석합니다.
        public static bool TryParseRecord(string line, out PointMapping point, out string error)
        {
            point = null;
            if (line == null)
            {
                error = "포인트 항목은 point=로 시작해야 합니다.";
                return false;
            }

            byte[] bytes = Utf8.GetBytes(line);
            bool canonical;
            return TryParseRecord(bytes, bytes.Length, new byte[MaximumFieldBytes], out point, out canonical, out error);
        }

        // nanoFramework의 문자열 인덱싱은 UTF-8을 처음부터 순회하기 때문에 문자마다 반복하면
        // 제곱 계산이 됩니다. 그래서 원시 바이트로 동작합니다. canonical은 줄 바꿈을 제외하고
        // 바이트가 이미 FormatRecord 결과와 같음을 나타내므로 호출자는 그대로 저장할 수 있습니다.
        public static bool TryParseRecord(
            byte[] line,
            int length,
            byte[] scratch,
            out PointMapping point,
            out bool canonical,
            out string error)
        {
            point = null;
            error = null;
            canonical = true;
            if (!StartsWith(line, length, RecordPrefix))
            {
                error = "포인트 항목은 point=로 시작해야 합니다.";
                return false;
            }

            var fields = new string[FieldCount];
            int start = RecordPrefix.Length;
            for (int i = 0; i < FieldCount; i++)
            {
                int separator = start < length ? Array.IndexOf(line, FieldSeparator, start, length - start) : -1;
                if (separator < 0)
                {
                    error = "포인트 항목은 정확히 16개 필드여야 합니다.";
                    return false;
                }

                error = DecodeField(line, start, separator, scratch, ref canonical, out fields[i]);
                if (error != null)
                    return false;
                start = separator + 1;
            }

            if (start != length)
            {
                error = "포인트 항목은 정확히 16개 필드여야 합니다.";
                return false;
            }

            PointAccess access;
            if (fields[12] == "RO")
                access = PointAccess.ReadOnly;
            else if (fields[12] == "WO")
                access = PointAccess.WriteOnly;
            else if (fields[12] == "RW")
                access = PointAccess.ReadWrite;
            else
            {
                error = "접근 모드는 RO, WO, RW 중 하나여야 합니다.";
                return false;
            }

            int numerator;
            int denominator;
            int offset;
            if (!IsInteger(fields[13]) || !int.TryParse(fields[13], out numerator) ||
                !IsInteger(fields[14]) || !int.TryParse(fields[14], out denominator) ||
                !IsInteger(fields[15]) || !int.TryParse(fields[15], out offset))
            {
                error = "스케일과 오프셋은 32비트 정수여야 합니다.";
                return false;
            }

            if (canonical && (numerator.ToString() != fields[13] ||
                denominator.ToString() != fields[14] || offset.ToString() != fields[15]))
                canonical = false;

            point = new PointMapping(
                fields[0],
                fields[1],
                new ProtocolAddress(fields[2], fields[3], fields[4], fields[5], fields[6]),
                new ProtocolAddress(fields[7], fields[8], fields[9], fields[10], fields[11]),
                access,
                numerator,
                denominator,
                offset);
            return true;
        }

        private static void AppendRecord(StringBuilder builder, PointMapping point)
        {
            builder.Append("point=");
            AppendField(builder, point.Id);
            AppendField(builder, point.Name);
            AppendAddress(builder, point.Source);
            AppendAddress(builder, point.Target);
            AppendField(builder, point.Access == PointAccess.ReadOnly ? "RO" :
                point.Access == PointAccess.WriteOnly ? "WO" : "RW");
            AppendField(builder, point.ScaleNumerator.ToString());
            AppendField(builder, point.ScaleDenominator.ToString());
            AppendField(builder, point.Offset.ToString());
            builder.Append('\n');
        }

        private static void AppendAddress(StringBuilder builder, ProtocolAddress address)
        {
            AppendField(builder, address.Protocol);
            AppendField(builder, address.DeviceId);
            AppendField(builder, address.AddressSpace);
            AppendField(builder, address.Address);
            AppendField(builder, address.DataType);
        }

        private static void AppendField(StringBuilder builder, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            const string Hex = "0123456789ABCDEF";
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (ProtocolAddress.IsTokenCharacter(b))
                {
                    builder.Append((char)b);
                }
                else
                {
                    builder.Append('%');
                    builder.Append(Hex[b >> 4]);
                    builder.Append(Hex[b & 0x0F]);
                }
            }

            builder.Append('|');
        }

        private static bool IsInteger(string value)
        {
            int start = value.Length > 0 && value[0] == '-' ? 1 : 0;
            if (start == value.Length)
                return false;

            for (int i = start; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                    return false;
            }

            return true;
        }

        internal static bool StartsWith(byte[] line, int length, byte[] prefix)
        {
            if (length < prefix.Length)
                return false;

            for (int i = 0; i < prefix.Length; i++)
            {
                if (line[i] != prefix[i])
                    return false;
            }

            return true;
        }

        // line[start..end)를 scratch로 디코딩하고, 인코딩이 AppendField와 다르면 canonical을 해제합니다.
        private static string DecodeField(
            byte[] line,
            int start,
            int end,
            byte[] scratch,
            ref bool canonical,
            out string decoded)
        {
            decoded = null;
            int count = 0;
            for (int i = start; i < end; i++)
            {
                int c = line[i];
                if (c == '%')
                {
                    if (i + 2 >= end)
                        return InvalidEncoding;
                    int high = ByteTextUtil.HexValue(line[i + 1]);
                    int low = ByteTextUtil.HexValue(line[i + 2]);
                    if (high < 0 || low < 0)
                        return InvalidEncoding;
                    if (line[i + 1] >= 'a' || line[i + 2] >= 'a')
                        canonical = false;

                    i += 2;
                    c = (high << 4) | low;
                    if (ProtocolAddress.IsTokenCharacter(c))
                        canonical = false;
                }
                else if (c < ' ' || c > '~')
                {
                    return "포인트 매핑 본문에는 퍼센트 인코딩된 ASCII 문자만 사용할 수 있습니다.";
                }
                else if (!ProtocolAddress.IsTokenCharacter(c))
                {
                    canonical = false;
                }

                if (count == scratch.Length)
                    return "필드 값이 너무 깁니다.";
                scratch[count++] = (byte)c;
            }

            if (!ByteTextUtil.IsValidUtf8(scratch, count))
                return InvalidEncoding;

            decoded = count == 0 ? string.Empty : Utf8.GetString(scratch, 0, count);
            return null;
        }

    }

    // 해석한 포인트를 보관하지 않은 채 매핑 파일을 한 줄씩 검증합니다.
    public sealed class PointMappingValidator
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding();

        private readonly ulong[] _idHashes = new ulong[PointMappingTable.MaximumPointCount];
        private readonly ulong[] _targetHashes = new ulong[PointMappingTable.MaximumPointCount];
        private int _count;
        private int _lineNumber;
        private bool _versionSeen;

        public int Count
        {
            get { return _count; }
        }

        // 허용되면 null을 반환하며, 빈 줄과 헤더 줄에서는 point가 null입니다.
        public string AcceptLine(string line, out PointMapping point)
        {
            byte[] bytes = Utf8.GetBytes(line);
            bool canonical;
            return AcceptLine(bytes, bytes.Length, new byte[PointMappingTable.MaximumFieldBytes], out point, out canonical);
        }

        // 업로드 경로가 사용하는 바이트 형식. canonical은 point가 null이 아닐 때만 의미가 있습니다.
        public string AcceptLine(byte[] line, int length, byte[] scratch, out PointMapping point, out bool canonical)
        {
            point = null;
            canonical = false;
            _lineNumber++;
            if (length == 0)
                return null;

            if (length == PointMappingTable.SchemaHeaderBytes.Length &&
                PointMappingTable.StartsWith(line, length, PointMappingTable.SchemaHeaderBytes))
            {
                if (_versionSeen || _count != 0)
                    return Fail("스키마 헤더가 중복되었거나 순서가 잘못되었습니다.");

                _versionSeen = true;
                return null;
            }

            if (!_versionSeen || !PointMappingTable.StartsWith(line, length, PointMappingTable.RecordPrefix))
                return Fail("알 수 없는 항목이거나 스키마 헤더 앞에 항목이 있습니다.");
            if (_count == PointMappingTable.MaximumPointCount)
                return Fail("포인트는 최대 " + PointMappingTable.MaximumPointCount + "개까지 등록할 수 있습니다.");

            PointMapping parsed;
            string error;
            if (!PointMappingTable.TryParseRecord(line, length, scratch, out parsed, out canonical, out error))
                return Fail(error);

            error = parsed.Validate();
            if (error != null)
                return Fail(error);

            // 64비트 FNV-1a를 쓰면 중복 검사에 소수 백 바이트만 듭니다. 오탐은 32개 포인트에서
            // 약 1e-16 확률이며, 있어도 저장을 거부할 뿐 중복을 절대 받아들이지 않습니다.
            ulong idHash = Hash(parsed.Id, scratch, 14695981039346656037UL);
            ProtocolAddress target = parsed.Target;
            ulong targetHash = Hash(target.Protocol, scratch, 14695981039346656037UL);
            targetHash = Hash(target.DeviceId, scratch, Separator(targetHash));
            targetHash = Hash(target.AddressSpace, scratch, Separator(targetHash));
            targetHash = Hash(target.Address, scratch, Separator(targetHash));
            for (int i = 0; i < _count; i++)
            {
                if (_idHashes[i] == idHash)
                    return Fail("이미 사용 중인 포인트 ID입니다: " + parsed.Id);
                if (_targetHashes[i] == targetHash)
                    return Fail("목적지 주소가 다른 포인트와 겹칩니다: " + parsed.Id);
            }

            _idHashes[_count] = idHash;
            _targetHashes[_count] = targetHash;
            _count++;
            point = parsed;
            return null;
        }

        public string Complete()
        {
            return _versionSeen ? null : "포인트 매핑 스키마 헤더(schemaVersion=1)가 없습니다.";
        }

        private string Fail(string message)
        {
            return _lineNumber + "번째 줄: " + message;
        }

        private static ulong Separator(ulong hash)
        {
            return (hash ^ (byte)'|') * 1099511628211UL;
        }

        private static ulong Hash(string value, byte[] scratch, ulong hash)
        {
            int count = Utf8.GetBytes(value, 0, value.Length, scratch, 0);
            for (int i = 0; i < count; i++)
            {
                hash ^= scratch[i];
                hash *= 1099511628211UL;
            }

            return hash;
        }
    }

    // 업로드된 매핑 파일을 작은 청크로 검증하고 정규화된 형태를 출력에 씁니다.
    public sealed class PointMappingStreamParser
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding();
        private static readonly object NewLine = (byte)'\n';

        private readonly PointMappingValidator _validator = new PointMappingValidator();
        // 여유 바이트 하나를 두면 허용된 줄을 줄 바꿈과 함께 한 번의 호출로 다시 쓸 수 있습니다.
        private readonly byte[] _line = new byte[PointMappingTable.MaximumLineLength + 1];
        private readonly byte[] _scratch = new byte[PointMappingTable.MaximumFieldBytes];
        private readonly Stream _output;
        private int _lineLength;
        private int _written;
        private string _error;

        public PointMappingStreamParser(Stream output)
        {
            if (output == null)
                throw new ArgumentNullException("output");
            _output = output;
        }

        public int Count
        {
            get { return _validator.Count; }
        }

        public int BytesWritten
        {
            get { return _written; }
        }

        public string Write(byte[] buffer, int offset, int count)
        {
            if (_error != null)
                return _error;

            // 네이티브 IndexOf/Copy를 쓰면 바이트별 작업이 인터프리터를 거치지 않습니다.
            int end = offset + count;
            while (offset < end)
            {
                int newline = Array.IndexOf(buffer, NewLine, offset, end - offset);
                int segmentEnd = newline < 0 ? end : newline;
                int segment = segmentEnd - offset;
                if (_lineLength + segment > PointMappingTable.MaximumLineLength)
                    return _error = "한 줄이 " + PointMappingTable.MaximumLineLength + "바이트를 넘습니다.";

                Array.Copy(buffer, offset, _line, _lineLength, segment);
                _lineLength += segment;
                if (newline < 0)
                    break;

                offset = newline + 1;
                _error = ProcessLine();
                if (_error != null)
                    return _error;
            }

            return null;
        }

        public string Complete()
        {
            if (_error != null)
                return _error;
            if (_lineLength > 0)
            {
                _error = ProcessLine();
                if (_error != null)
                    return _error;
            }

            _error = _validator.Complete();
            return _error;
        }

        private string ProcessLine()
        {
            int length = _lineLength;
            _lineLength = 0;
            if (length > 0 && _line[length - 1] == '\r')
                length--;

            PointMapping point;
            bool canonical;
            string error = _validator.AcceptLine(_line, length, _scratch, out point, out canonical);
            if (error != null)
                return error;
            if (length == 0)
                return null;

            // 헤더 줄은 정확히 비교하므로 정규화된 형태입니다.
            byte[] bytes;
            int count;
            if (point == null || canonical)
            {
                _line[length] = (byte)'\n';
                bytes = _line;
                count = length + 1;
            }
            else
            {
                bytes = Utf8.GetBytes(PointMappingTable.FormatRecord(point));
                count = bytes.Length;
            }

            if (_written + count > PointMappingTable.MaximumFileLength)
                return "포인트 매핑 파일이 " + PointMappingTable.MaximumFileLength + "바이트를 넘습니다.";

            _output.Write(bytes, 0, count);
            _written += count;
            return null;
        }
    }
}
