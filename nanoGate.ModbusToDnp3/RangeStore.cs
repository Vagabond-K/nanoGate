using System;
using System.IO;
using System.Text;
using nanoGate.Core;
using nanoGate.Points;

namespace nanoGate.ModbusToDnp3
{
    // 범위 매핑 규칙 1개: Space에서 From부터 Count개의 연속된 Modbus 값을
    // Index부터 시작하는 To의 DNP3 포인트로 변환합니다. Unit은 이 규칙을 RS-485
    // 슬레이브(1-247)에 고정합니다; 0은 unit 1과 동일하게 동작하는 레거시 "동적" 값입니다.
    // Num/Den/Off는 규칙 안의 모든 값에 스케일을 적용합니다: 표시값 = raw * Num / Den + Off.
    internal sealed class RangeRule
    {
        public int Unit;
        public int Space;
        public int From;
        public int Count;
        public int Kind;
        public int Order;
        public int To;
        public int Index;
        public int Num = 1;
        public int Den = 1;
        public int Off;
        public bool Writable;

        public bool HasScale
        {
            get { return Num != 1 || Den != 1 || Off != 0; }
        }
    }

    // 영속 규칙 테이블(I:\nanogate.rules). 규칙당 "range=..." 줄 1개.
    // 포맷 v1: range=unit|space|from|count|type|order|to|index|
    // 포맷 v2: range=unit|space|from|count|type|order|to|index|num|den|off| (비우면 기본값)
    internal static class RangeStore
    {
        internal const string FilePath = "I:\\nanogate.rules";
        internal const string TempFilePath = "I:\\nanogate.rules.tmp";
        internal const string BackupFilePath = "I:\\nanogate.rules.bak";
        internal const string SchemaHeader = "rangeVersion=1";
        internal const string SchemaHeaderV2 = "rangeVersion=2";
        internal const string LinePrefix = "range=";
        internal const int MaximumRuleCount = 64;
        internal const int MaximumFileLength = 8192;
        internal const int MaximumTotalPoints = 2048;
        internal const int MaximumCount = 1024;
        internal const string EmptyTable = SchemaHeader + "\n";

        // Space / Kind / To 코드(프로필 런타임과 공유).
        internal const int SpaceCoil = 0;
        internal const int SpaceDiscrete = 1;
        internal const int SpaceInput = 2;
        internal const int SpaceHolding = 3;

        internal const int KindBool = 0;
        internal const int KindInt16 = 1;
        internal const int KindUInt16 = 2;
        internal const int KindInt32 = 3;
        internal const int KindUInt32 = 4;
        internal const int KindFloat32 = 5;

        internal const int ToBinary = 0;
        internal const int ToBinaryOutput = 1;
        internal const int ToAnalog = 2;
        internal const int ToAnalogOutput = 3;
        internal const int ToCounter = 4;

        internal static readonly string[] SpaceNames =
        {
            "coil", "discrete-input", "input-register", "holding-register"
        };

        internal static readonly string[] KindNames =
        {
            "Bool", "Int16", "UInt16", "Int32", "UInt32", "Float32"
        };

        internal static readonly string[] OrderNames =
        {
            "ABCD", "CDAB", "BADC", "DCBA"
        };

        internal static readonly string[] ToNames =
        {
            "binary-input", "binary-output", "analog-input", "analog-output", "counter"
        };

        public static int WidthOf(int kind)
        {
            return kind >= KindInt32 ? 2 : 1;
        }

        // ---- 파일 I/O -------------------------------------------------------------

        // 규칙 파일을 읽습니다; 커밋 도중 중단된 경우 백업 사본으로 복구합니다.
        // 파일이 없으면 오류 없이 빈 테이블을 반환합니다.
        public static string ReadAll(out string error)
        {
            error = null;
            try
            {
                if (PointMappingStore.FileSystem.Exists(BackupFilePath))
                {
                    if (PointMappingStore.FileSystem.Exists(FilePath))
                        PointMappingStore.FileSystem.Delete(FilePath);
                    PointMappingStore.FileSystem.Move(BackupFilePath, FilePath);
                }

                if (!PointMappingStore.FileSystem.Exists(FilePath))
                    return EmptyTable;

                byte[] raw = new byte[MaximumFileLength + 1];
                int length = 0;
                using (Stream file = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
                {
                    int read;
                    while ((read = file.Read(raw, length, raw.Length - length)) > 0)
                    {
                        length += read;
                        if (length == raw.Length)
                        {
                            error = "규칙 파일이 너무 깁니다.";
                            return EmptyTable;
                        }
                    }
                }

                return BytesToAscii(raw, 0, length);
            }
            catch (Exception ex)
            {
                error = ErrorText.Describe(ex);
                return EmptyTable;
            }
        }

        // 전체 규칙 파일을 검증하고 원자적으로 교체합니다. 텍스트가 null이면 파일을 삭제합니다.
        public static string Save(string text)
        {
            if (text == null)
                return DeleteAll();

            RangeRule[] rules;
            string error = Parse(text, out rules);
            if (error != null)
                return error;

            string canonical = Serialize(rules);
            byte[] bytes = Encoding.UTF8.GetBytes(canonical);

            try
            {
                if (PointMappingStore.FileSystem.Exists(TempFilePath))
                    PointMappingStore.FileSystem.Delete(TempFilePath);

                using (Stream file = new FileStream(TempFilePath, FileMode.Create, FileAccess.Write))
                    file.Write(bytes, 0, bytes.Length);

                if (PointMappingStore.FileSystem.Exists(FilePath))
                    PointMappingStore.FileSystem.Move(FilePath, BackupFilePath);

                PointMappingStore.FileSystem.Move(TempFilePath, FilePath);
                if (PointMappingStore.FileSystem.Exists(BackupFilePath))
                    PointMappingStore.FileSystem.Delete(BackupFilePath);
            }
            catch (Exception ex)
            {
                error = ErrorText.Describe(ex);
                try
                {
                    if (PointMappingStore.FileSystem.Exists(TempFilePath))
                        PointMappingStore.FileSystem.Delete(TempFilePath);
                }
                catch (Exception)
                {
                }
                return error;
            }

            return null;
        }

        public static string DeleteAll()
        {
            try
            {
                if (PointMappingStore.FileSystem.Exists(BackupFilePath))
                    PointMappingStore.FileSystem.Delete(BackupFilePath);
                if (PointMappingStore.FileSystem.Exists(TempFilePath))
                    PointMappingStore.FileSystem.Delete(TempFilePath);
                if (PointMappingStore.FileSystem.Exists(FilePath))
                    PointMappingStore.FileSystem.Delete(FilePath);
            }
            catch (Exception ex)
            {
                return ErrorText.Describe(ex);
            }

            return null;
        }

        // ---- 파싱 / 직렬화 ----------------------------------------------------

        // 규칙 텍스트 전체를 검증하며 파싱합니다. 오류가 있으면 rules는 null입니다.
        public static string Parse(string text, out RangeRule[] rules)
        {
            rules = null;
            if (text == null)
                return "규칙 내용이 비어 있습니다.";

            var list = new RangeRule[MaximumRuleCount];
            int count = 0;
            int total = 0;

            int start = 0;
            bool first = true;
            while (start <= text.Length)
            {
                int end = text.IndexOf('\n', start);
                string line = end < 0 ? text.Substring(start) : text.Substring(start, end - start);
                start = end < 0 ? text.Length + 1 : end + 1;

                if (line.Length > 0 && line[line.Length - 1] == '\r')
                    line = line.Substring(0, line.Length - 1);
                if (line.Length == 0)
                    continue;

                if (first)
                {
                    first = false;
                    if (line != SchemaHeader && line != SchemaHeaderV2)
                        return "규칙 파일 헤더가 없습니다.";
                    continue;
                }

                if (line.Length > 128)
                    return "규칙 한 줄이 너무 깁니다.";
                if (line.Length < LinePrefix.Length || line.Substring(0, LinePrefix.Length) != LinePrefix)
                    return "알 수 없는 규칙 줄: " + line;
                if (count == MaximumRuleCount)
                    return "규칙은 최대 " + MaximumRuleCount + "개입니다.";

                RangeRule rule;
                string error = ParseLine(line.Substring(LinePrefix.Length), count + 1, out rule);
                if (error != null)
                    return error;

                total += rule.Count;
                if (total > MaximumTotalPoints)
                    return "규칙을 합친 포인트 수가 " + MaximumTotalPoints + "개를 넘습니다.";

                list[count++] = rule;
            }

            var result = new RangeRule[count];
            Array.Copy(list, result, count);

            string overlap = CheckOverlaps(result);
            if (overlap != null)
                return overlap;

            rules = result;
            return null;
        }

        // 규칙 1줄의 본문("range=" 이후)을 파싱합니다: unit|space|from|count|type|order|to|index|
        // 여기에 선택적 v2 스케일 필드 num|den|off|가 뒤따릅니다(비어 있으면 1|1|0).
        private static string ParseLine(string body, int lineNumber, out RangeRule rule)
        {
            rule = null;
            string[] fields = SplitFields(body);
            if (fields == null)
                return "규칙 " + lineNumber + ": 필드 형식이 올바르지 않습니다.";

            int unit;
            if (fields[0].Length == 0)
                unit = 0;
            else
            {
                string error = ParseNumber(fields[0], 1, 247, "Unit ID", lineNumber, out unit);
                if (error != null)
                    return error;
            }

            int space = NameOf(SpaceNames, fields[1]);
            if (space < 0)
                return "규칙 " + lineNumber + ": 원본 영역은 coil/discrete-input/input-register/holding-register여야 합니다.";

            int from;
            string error2 = ParseNumber(fields[2], 0, 65535, "시작 주소", lineNumber, out from);
            if (error2 != null)
                return error2;

            int count;
            string error3 = ParseNumber(fields[3], 1, MaximumCount, "개수", lineNumber, out count);
            if (error3 != null)
                return error3;

            int kind = NameOf(KindNames, fields[4]);
            if (kind < 0)
                return "규칙 " + lineNumber + ": 데이터 형식은 Bool/Int16/UInt16/Int32/UInt32/Float32여야 합니다.";

            int order = -1;
            if (fields[5].Length > 0)
            {
                order = NameOf(OrderNames, fields[5]);
                if (order < 0)
                    return "규칙 " + lineNumber + ": 바이트 순서는 ABCD/CDAB/BADC/DCBA여야 합니다.";
            }

            int to = NameOf(ToNames, fields[6]);
            if (to < 0)
                return "규칙 " + lineNumber + ": 목적지는 binary-input/binary-output/analog-input/analog-output/counter여야 합니다.";

            int index;
            string error4 = ParseNumber(fields[7], 0, 65535, "DNP3 인덱스", lineNumber, out index);
            if (error4 != null)
                return error4;

            int num = 1, den = 1, off = 0;
            if (fields[8].Length > 0)
            {
                string error5 = ParseSigned(fields[8], "분자", lineNumber, out num);
                if (error5 != null)
                    return error5;
            }
            if (fields[9].Length > 0)
            {
                string error6 = ParseSigned(fields[9], "분모", lineNumber, out den);
                if (error6 != null)
                    return error6;
                if (den == 0)
                    return "규칙 " + lineNumber + ": 스케일 분모는 0일 수 없습니다.";
            }
            if (fields[10].Length > 0)
            {
                string error7 = ParseSigned(fields[10], "오프셋", lineNumber, out off);
                if (error7 != null)
                    return error7;
            }
            if ((space == SpaceCoil || space == SpaceDiscrete) && (num != 1 || den != 1 || off != 0))
                return "규칙 " + lineNumber + ": Coil/Discrete Input 규칙에는 스케일을 적용할 수 없습니다.";

            string compat = CheckCombination(space, kind, order, to, count, from, index, lineNumber);
            if (compat != null)
                return compat;

            var result = new RangeRule();
            result.Unit = unit;
            result.Space = space;
            result.From = from;
            result.Count = count;
            result.Kind = kind;
            result.Order = order < 0 ? 0 : order;
            result.To = to;
            result.Index = index;
            result.Num = num;
            result.Den = den;
            result.Off = off;
            result.Writable = (space == SpaceCoil && to == ToBinaryOutput) ||
                              (space == SpaceHolding && to == ToAnalogOutput);
            rule = result;
            return null;
        }

        private static string CheckCombination(int space, int kind, int order, int to, int count, int from, int index, int lineNumber)
        {
            bool bitSpace = space == SpaceCoil || space == SpaceDiscrete;
            if (bitSpace && kind != KindBool)
                return "규칙 " + lineNumber + ": Coil/Discrete Input은 Bool 형식만 사용할 수 있습니다.";
            if (!bitSpace && kind == KindBool)
                return "규칙 " + lineNumber + ": Register 영역에는 Bool 형식을 사용할 수 없습니다.";

            if (space == SpaceDiscrete && to != ToBinary)
                return "규칙 " + lineNumber + ": Discrete Input은 Binary Input으로만 매핑됩니다.";
            if (space == SpaceCoil && to != ToBinary && to != ToBinaryOutput)
                return "규칙 " + lineNumber + ": Coil은 Binary Input/Binary Output으로만 매핑됩니다.";

            if (bitSpace && to != ToBinary && to != ToBinaryOutput)
                return "규칙 " + lineNumber + ": Coil/Discrete Input은 Binary 포인트로만 매핑됩니다.";

            if (!bitSpace)
            {
                if (space == SpaceInput && to == ToAnalogOutput)
                    return "규칙 " + lineNumber + ": Input Register는 Analog Output(쓰기 가능)으로 매핑할 수 없습니다.";
                if (to == ToCounter && kind != KindInt32 && kind != KindUInt32)
                    return "규칙 " + lineNumber + ": Counter는 Int32/UInt32 형식만 사용할 수 있습니다.";
                if (to != ToAnalog && to != ToAnalogOutput && to != ToCounter)
                    return "규칙 " + lineNumber + ": Register 영역은 Analog Input/Analog Output/Counter로만 매핑됩니다.";
            }

            if (order >= 0 && WidthOf(kind) < 2)
                return "규칙 " + lineNumber + ": 바이트 순서는 32비트 형식(Int32/UInt32/Float32)에만 지정할 수 있습니다.";

            long consumed = (long)from + (long)count * WidthOf(kind);
            if (consumed > 65536)
                return "규칙 " + lineNumber + ": 원본 주소 범위가 65536을 넘습니다.";
            if ((long)index + count > 65536)
                return "규칙 " + lineNumber + ": DNP3 인덱스 범위가 65536을 넘습니다.";
            return null;
        }

        // 두 규칙은 겹치는 인덱스에 동일한 DNP3 포인트 유형을 게시하면 안 됩니다.
        private static string CheckOverlaps(RangeRule[] rules)
        {
            for (int i = 0; i < rules.Length; i++)
            {
                for (int j = i + 1; j < rules.Length; j++)
                {
                    if (rules[i].To != rules[j].To)
                        continue;
                    if (rules[i].Index < rules[j].Index + rules[j].Count &&
                        rules[j].Index < rules[i].Index + rules[i].Count)
                        return "규칙 " + (i + 1) + "번과 " + (j + 1) + "번의 DNP3 인덱스 범위가 겹칩니다.";
                }
            }

            return null;
        }

        public static string Serialize(RangeRule[] rules)
        {
            bool hasScale = false;
            for (int i = 0; i < rules.Length; i++)
                if (rules[i].HasScale)
                {
                    hasScale = true;
                    break;
                }

            var text = new StringBuilder(EmptyTable.Length + rules.Length * 48);
            text.Append(hasScale ? SchemaHeaderV2 : EmptyTable);
            if (hasScale)
                text.Append('\n');
            for (int i = 0; i < rules.Length; i++)
            {
                RangeRule r = rules[i];
                text.Append(LinePrefix);
                if (r.Unit != 0)
                    text.Append(r.Unit.ToString());
                text.Append('|').Append(SpaceNames[r.Space]).Append('|');
                text.Append(r.From.ToString()).Append('|');
                text.Append(r.Count.ToString()).Append('|');
                text.Append(KindNames[r.Kind]).Append('|');
                if (WidthOf(r.Kind) >= 2)
                    text.Append(OrderNames[r.Order]);
                text.Append('|').Append(ToNames[r.To]).Append('|');
                text.Append(r.Index.ToString()).Append('|');
                if (hasScale)
                {
                    if (r.Num != 1)
                        text.Append(r.Num.ToString());
                    text.Append('|');
                    if (r.Den != 1)
                        text.Append(r.Den.ToString());
                    text.Append('|');
                    if (r.Off != 0)
                        text.Append(r.Off.ToString());
                    text.Append('|');
                }
                text.Append('\n');
            }

            return text.ToString();
        }

        // 레거시 자동 매핑 모드를 재현하는 규칙: 각 Modbus 영역의 첫 N개 주소를
        // DNP3 인덱스 0..N-1에 1:1로 매핑합니다.
        public static string BuildAutoRules(int coils, int discretes, string inputType, int inputCount,
            string holdingType, int holdingCount, string byteOrder)
        {
            var text = new StringBuilder(EmptyTable.Length + 160);
            text.Append(EmptyTable);
            if (coils > 0)
                text.Append(LinePrefix).Append("|coil|0|").Append(coils.ToString())
                    .Append("|Bool||binary-output|0|\n");
            if (discretes > 0)
                text.Append(LinePrefix).Append("|discrete-input|0|").Append(discretes.ToString())
                    .Append("|Bool||binary-input|0|\n");
            if (inputCount > 0)
            {
                text.Append(LinePrefix).Append("|input-register|0|").Append(inputCount.ToString())
                    .Append('|').Append(inputType).Append('|');
                AppendOrder(text, inputType, byteOrder);
                text.Append("|analog-input|0|\n");
            }
            if (holdingCount > 0)
            {
                text.Append(LinePrefix).Append("|holding-register|0|").Append(holdingCount.ToString())
                    .Append('|').Append(holdingType).Append('|');
                AppendOrder(text, holdingType, byteOrder);
                text.Append("|analog-output|0|\n");
            }

            return text.ToString();
        }

        private static void AppendOrder(StringBuilder text, string typeName, string byteOrder)
        {
            if (typeName == "Int32" || typeName == "UInt32" || typeName == "Float32")
                text.Append(byteOrder);
        }

        // ---- 작은 헬퍼 ----------------------------------------------------------

        // "a|b|c|"를 필드로 분리합니다; 텍스트는 '|'로 끝나야 하고 필드가 8개 또는 11개여야 합니다.
        // 없는 v2 스케일 필드는 빈 문자열로 반환됩니다.
        private static string[] SplitFields(string body)
        {
            if (body.Length == 0 || body[body.Length - 1] != '|')
                return null;

            var fields = new string[11];
            int count = 0, at = 0;
            // 각 필드는 '|'로 끝나며, 마지막 필드(줄 끝의 '|')도 포함됩니다.
            while (at < body.Length)
            {
                if (count == 11)
                    return null;
                int bar = body.IndexOf('|', at);
                if (bar < 0)
                    bar = body.Length - 1;
                fields[count++] = body.Substring(at, bar - at);
                at = bar + 1;
            }

            if (count != 8 && count != 11)
                return null;
            for (int i = count; i < 11; i++)
                fields[i] = string.Empty;
            return fields;
        }

        private static int NameOf(string[] names, string value)
        {
            for (int i = 0; i < names.Length; i++)
                if (names[i] == value)
                    return i;
            return -1;
        }

        private static string ParseNumber(string text, int min, int max, string label, int lineNumber, out int value)
        {
            value = 0;
            if (text.Length == 0)
                return "규칙 " + lineNumber + ": " + label + " 값이 비어 있습니다.";
            for (int i = 0; i < text.Length; i++)
                if (text[i] < '0' || text[i] > '9')
                    return "규칙 " + lineNumber + ": " + label + " 값은 숫자여야 합니다.";
            int parsed = 0;
            for (int i = 0; i < text.Length; i++)
            {
                parsed = parsed * 10 + (text[i] - '0');
                if (parsed > max)
                    return "규칙 " + lineNumber + ": " + label + " 값이 범위를 넘습니다.";
            }

            if (parsed < min)
                return "규칙 " + lineNumber + ": " + label + " 값이 범위를 넘습니다.";

            value = parsed;
            return null;
        }

        private static string ParseSigned(string text, string label, int lineNumber, out int value)
        {
            value = 0;
            int parsed;
            if (!int.TryParse(text, out parsed) || parsed < -1000000 || parsed > 1000000)
                return "규칙 " + lineNumber + ": " + label + " 값이 범위를 넘습니다.";
            value = parsed;
            return null;
        }

        private static string BytesToAscii(byte[] bytes, int index, int length)
        {
            var text = new char[length];
            for (int i = 0; i < length; i++)
                text[i] = (char)(bytes[index + i] & 0x7F);
            return new string(text);
        }
    }
}
