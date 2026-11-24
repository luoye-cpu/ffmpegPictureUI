using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FfmpegGui.Services.ColorMapping
{
    /// <summary>
    /// AVIF <c>sato</c>（Sample Transform Derived Image Item）表达式求值器。
    /// <para>
    /// 依据：AVIF 规范 §4.2.3.3 / Appendix A。16-bit AVIF 无法由单个 AV1 item 承载
    /// （AV1 <c>BitDepth</c> 只有 8/10/12），必须由 <c>sato</c> 派生项把两条 <c>av01</c>
    /// item 按后缀表达式合成。实测 libavif/avifenc 的 <c>--depth 12,4</c> 载荷：
    /// <c>02 07 00 00 00 00 10 01 82 02 00 00 00 00 10 83 80</c>，即
    /// <c>out = 16 * sample1 + sample2 / 16</c>（<c>--depth 8,8</c> 为
    /// <c>out = 256 * sample1 + sample2</c>）。
    /// </para>
    /// <para>
    /// 本类只做**语法与算术**，不负责文件/流布局；调用方负责把两条 AV1 item 解成
    /// 同尺寸、同平面数的 raw 采样，再按本表达式逐样本合成。
    /// </para>
    /// </summary>
    public sealed class AvifSampleTransform
    {
        private enum OpKind
        {
            Constant,
            Sample,      // 1-based sample index
            Negation,
            Abs,
            Not,
            Bsr,
            Sum,
            Difference,
            Product,
            Quotient,
            And,
            Or,
            Xor,
            Pow,
            Min,
            Max,
        }

        private readonly struct Op
        {
            public readonly OpKind Kind;
            public readonly long Value;

            public Op(OpKind kind, long value = 0)
            {
                Kind = kind;
                Value = value;
            }
        }

        private readonly List<Op> _ops;
        private readonly int _firstTokenCount;

        /// <summary>sato 声明的有符号常量、栈及中间运算位深；不是输入 AV1 item 位深。</summary>
        public int ArithmeticBitDepth { get; }

        /// <summary>兼容旧调用名；该值实际为算术位深，不得用于判断输入采样位深。</summary>
        [Obsolete("Use ArithmeticBitDepth; sato bit_depth does not declare input image depth.")]
        public int InputBitDepth => ArithmeticBitDepth;

        /// <summary>表达式引用的最大输入序号（本解码器支持最多两项）。</summary>
        public int RequiredInputCount { get; }

        /// <summary>token 数（载荷第 2 字节）。</summary>
        public int TokenCount => _firstTokenCount;

        /// <summary>人读表达式，用于播报/诊断。</summary>
        public string Expression { get; }

        private AvifSampleTransform(List<Op> ops, int tokenCount, int arithmeticBitDepth, int requiredInputCount, string expression)
        {
            _ops = ops;
            _firstTokenCount = tokenCount;
            ArithmeticBitDepth = arithmeticBitDepth;
            RequiredInputCount = requiredInputCount;
            Expression = expression;
        }

        /// <summary>
        /// 解析 <c>sato</c> item 载荷。失败返回 null 并给出原因（调用方必须 fail-closed，
        /// 不得静默退回 12-bit 基图）。
        /// </summary>
        public static AvifSampleTransform? Parse(byte[] payload, out string? error)
        {
            error = null;
            if (payload == null || payload.Length < 2)
            {
                error = "sato payload shorter than 2 bytes";
                return null;
            }

            int version = payload[0] >> 6;
            if (version != 0)
            {
                error = $"sato version={version} (only 0 supported)";
                return null;
            }

            // AVIF §4.2.3.3: bit_depth is signed arithmetic width (8 << code), not
            // input image depth. libavif writes code=2 even for the 12-bit + 8-bit recipe.
            // Match the supported upstream implementation in tools/src/libavif/src/sampletransform.c.
            int bitDepthCode = payload[0] & 0x03;
            int arithmeticBitDepth = 8 << bitDepthCode;
            if (arithmeticBitDepth != 32)
            {
                error = $"sato arithmetic bit-depth={arithmeticBitDepth} unsupported (only signed 32-bit supported)";
                return null;
            }

            int tokenCount = payload[1];
            if (tokenCount <= 0)
            {
                error = "sato token_count=0";
                return null;
            }

            var ops = new List<Op>(tokenCount);
            var sb = new StringBuilder();
            int pos = 2;
            for (int i = 0; i < tokenCount; i++)
            {
                if (pos >= payload.Length)
                {
                    error = $"sato token #{i + 1} truncated (pos={pos}, len={payload.Length})";
                    return null;
                }

                byte token = payload[pos++];
                switch (token)
                {
                    case 0x00: // constant: 4-byte big-endian signed
                        if (pos + 4 > payload.Length)
                        {
                            error = $"sato constant truncated (pos={pos}, len={payload.Length})";
                            return null;
                        }
                        long c = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(pos, 4));
                        pos += 4;
                        ops.Add(new Op(OpKind.Constant, c));
                        sb.Append("const ").Append(c.ToString(CultureInfo.InvariantCulture)).Append(", ");
                        break;

                    case >= 0x01 and <= 0x20: // sample 1..32
                        int index = token; // 1-based
                        ops.Add(new Op(OpKind.Sample, index));
                        sb.Append("sample").Append(index.ToString(CultureInfo.InvariantCulture)).Append(", ");
                        break;

                    case 0x40: ops.Add(new Op(OpKind.Negation)); sb.Append("negation, "); break;
                    case 0x41: ops.Add(new Op(OpKind.Abs)); sb.Append("abs, "); break;
                    case 0x42: ops.Add(new Op(OpKind.Not)); sb.Append("not, "); break;
                    case 0x43: ops.Add(new Op(OpKind.Bsr)); sb.Append("bsr, "); break;
                    case 0x80: ops.Add(new Op(OpKind.Sum)); sb.Append("sum, "); break;
                    case 0x81: ops.Add(new Op(OpKind.Difference)); sb.Append("difference, "); break;
                    case 0x82: ops.Add(new Op(OpKind.Product)); sb.Append("product, "); break;
                    case 0x83: ops.Add(new Op(OpKind.Quotient)); sb.Append("quotient, "); break;
                    case 0x84: ops.Add(new Op(OpKind.And)); sb.Append("and, "); break;
                    case 0x85: ops.Add(new Op(OpKind.Or)); sb.Append("or, "); break;
                    case 0x86: ops.Add(new Op(OpKind.Xor)); sb.Append("xor, "); break;
                    case 0x87: ops.Add(new Op(OpKind.Pow)); sb.Append("pow, "); break;
                    case 0x88: ops.Add(new Op(OpKind.Min)); sb.Append("min, "); break;
                    case 0x89: ops.Add(new Op(OpKind.Max)); sb.Append("max, "); break;

                    default:
                        error = $"sato unknown token 0x{token:X2} (#{i + 1})";
                        return null;
                }
            }

            // token_count describes the complete payload, not a prefix. Match libavif's
            // avifParseSampleTransformTokens: even zero trailing bytes are not tokens/padding.
            if (pos != payload.Length)
            {
                error = $"sato payload has {payload.Length - pos} trailing bytes";
                return null;
            }

            int stackSize = 0, requiredInputCount = 0;
            foreach (var op in ops)
            {
                if (op.Kind == OpKind.Sample)
                {
                    if (op.Value > 2)
                    {
                        error = $"sato sample{op.Value}: only 2 input items are supported";
                        return null;
                    }
                    requiredInputCount = Math.Max(requiredInputCount, (int)op.Value);
                }
                if (op.Kind is OpKind.Constant or OpKind.Sample) stackSize++;
                else if (op.Kind is OpKind.Negation or OpKind.Abs or OpKind.Not or OpKind.Bsr)
                {
                    if (stackSize < 1) { error = $"sato {op.Kind} stack underflow"; return null; }
                }
                else
                {
                    if (stackSize < 2) { error = $"sato {op.Kind} stack underflow"; return null; }
                    stackSize--;
                }
            }
            if (stackSize != 1)
            {
                error = $"sato expression left {stackSize} values (expected 1)";
                return null;
            }

            if (sb.Length > 2) sb.Length -= 2;
            return new AvifSampleTransform(ops, tokenCount, arithmeticBitDepth, requiredInputCount, sb.ToString());
        }

        /// <summary>逐样本求值；每步钳到声明的有符号 32 位域，最终输出位深的钳位由调用方执行。</summary>
        public long Evaluate(long sample1, long sample2)
        {
            if ((ulong)sample1 > ushort.MaxValue || (ulong)sample2 > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(sample1), "sato input samples must be unsigned 8/10/12/16-bit values");
            if (TryFast(sample1, sample2, out var v)) return v;
            if (!TryEvaluateStack(sample1, sample2, out v, out var error))
                throw new InvalidOperationException(error ?? "sato expression could not be evaluated");
            return v;
        }

        /// <summary>
        /// 快速路径：识别两种 libavif 实际配方，避免逐像素构造栈。
        /// <list type="bullet">
        /// <item><c>const c1, sample1, product, sample2, const c2, quotient, sum</c> ⇒ <c>c1*s1 + s2/c2</c></item>
        /// <item><c>const c1, sample1, product, sample2, sum</c> ⇒ <c>c1*s1 + s2</c></item>
        /// </list>
        /// </summary>
        private bool TryFast(long s1, long s2, out long value)
        {
            value = 0;
            if (_ops.Count == 7
                && _ops[0].Kind == OpKind.Constant
                && _ops[1].Kind == OpKind.Sample && _ops[1].Value == 1
                && _ops[2].Kind == OpKind.Product
                && _ops[3].Kind == OpKind.Sample && _ops[3].Value == 2
                && _ops[4].Kind == OpKind.Constant
                && _ops[5].Kind == OpKind.Quotient
                && _ops[6].Kind == OpKind.Sum)
            {
                long c1 = _ops[0].Value;
                long c2 = _ops[4].Value;
                long q = c2 == 0 ? s2 : Clamp32(s2 / c2);
                value = Clamp32(Clamp32(c1 * s1) + q);
                return true;
            }

            if (_ops.Count == 5
                && _ops[0].Kind == OpKind.Constant
                && _ops[1].Kind == OpKind.Sample && _ops[1].Value == 1
                && _ops[2].Kind == OpKind.Product
                && _ops[3].Kind == OpKind.Sample && _ops[3].Value == 2
                && _ops[4].Kind == OpKind.Sum)
            {
                value = Clamp32(Clamp32(_ops[0].Value * s1) + s2);
                return true;
            }

            return false;
        }

        private bool TryEvaluateStack(long s1, long s2, out long result, out string? error)
        {
            error = null;
            result = 0;
            int sp = 0;
            // token_count is one byte, so this is bounded even for untrusted expressions.
            Span<long> stack = stackalloc long[_ops.Count];

            for (int i = 0; i < _ops.Count; i++)
            {
                var op = _ops[i];
                switch (op.Kind)
                {
                    case OpKind.Constant:
                        stack[sp++] = op.Value;
                        break;
                    case OpKind.Sample:
                        if (op.Value == 1) stack[sp++] = s1;
                        else if (op.Value == 2) stack[sp++] = s2;
                        else
                        {
                            error = $"sato sample{op.Value}: only 2 input items are supported";
                            return false;
                        }
                        break;
                    case OpKind.Negation:
                    case OpKind.Abs:
                    case OpKind.Not:
                    case OpKind.Bsr:
                        if (sp < 1) { error = $"sato {op.Kind} stack underflow"; return false; }
                        stack[sp - 1] = op.Kind switch
                        {
                            OpKind.Negation => Clamp32(-stack[sp - 1]),
                            OpKind.Abs => Clamp32(Math.Abs(stack[sp - 1])),
                            OpKind.Not => ~(int)stack[sp - 1],
                            OpKind.Bsr => BitScanReverse(stack[sp - 1]),
                            _ => throw new InvalidOperationException("unsupported sato unary operator"),
                        };
                        break;
                    case OpKind.Sum:
                    case OpKind.Difference:
                    case OpKind.Product:
                    case OpKind.Quotient:
                    case OpKind.And:
                    case OpKind.Or:
                    case OpKind.Xor:
                    case OpKind.Pow:
                    case OpKind.Min:
                    case OpKind.Max:
                        if (sp < 2) { error = $"sato {op.Kind} stack underflow"; return false; }
                        {
                            long b = stack[--sp];
                            long a = stack[sp - 1];
                            stack[sp - 1] = op.Kind switch
                            {
                                OpKind.Sum => Clamp32(a + b),
                                OpKind.Difference => Clamp32(a - b),
                                OpKind.Product => Clamp32(a * b),
                                OpKind.Quotient => b == 0 ? a : Clamp32(a / b),
                                OpKind.And => (int)a & (int)b,
                                OpKind.Or => (int)a | (int)b,
                                OpKind.Xor => (int)a ^ (int)b,
                                OpKind.Pow => Pow(a, b),
                                OpKind.Min => Math.Min(a, b),
                                OpKind.Max => Math.Max(a, b),
                                _ => throw new InvalidOperationException("unsupported sato binary operator"),
                            };
                        }
                        break;
                    default:
                        error = $"sato operator {op.Kind} is not implemented";
                        return false;
                }
            }

            if (sp != 1)
            {
                error = $"sato expression left {sp} values (expected 1)";
                return false;
            }

            result = stack[0];
            return true;
        }

        private static long Clamp32(long value)
            => Math.Clamp(value, (long)int.MinValue, int.MaxValue);

        private static long BitScanReverse(long value)
        {
            if (value <= 0) return 0;
            int highest = 0;
            while ((value >>= 1) != 0) highest++;
            return highest;
        }

        // AVIF Appendix A / vendored libavif sampletransform.c:243-267. After each
        // operation the signed arithmetic domain saturates; overflow must never wrap.
        private static long Pow(long a, long b)
        {
            if (a is 0 or 1) return a;
            if (a == -1) return (b & 1) == 0 ? 1 : -1;
            if (b == 0) return 1;
            if (b == 1) return a;
            if (b < 0) return 0;
            long result = a;
            for (long i = 1; i < b; i++)
            {
                result *= a; // two int32-domain operands fit safely in signed int64
                if (result < int.MinValue || result > int.MaxValue)
                    return a > 0 || (b & 1) == 0 ? int.MaxValue : int.MinValue;
            }
            return result;
        }

        /// <summary>钳到 16-bit 无符号（sato 的目标位深为 16）。</summary>
        public static ushort ClampTo16(long value)
            => value <= 0 ? (ushort)0 : value >= 65535 ? (ushort)65535 : (ushort)value;
    }

    /// <summary>
    /// CICP token → H.273 数字码。编码侧的 P/T 标注和实际采样矩阵独立决议；解码侧只转已知 P/T。
    /// </summary>
    public static class AvifCicp
    {
        /// <summary>编码 P/T 只来自计划；每个不可命名的分量独立写 2（unspecified）。</summary>
        internal static (byte P, byte T) FromOutputTokens(string? primaries, string? transfer)
            => (PrimariesCode(primaries) ?? 2, TransferCode(transfer) ?? 2);

        /// <summary>由已探测的源 CICP token 生成 <c>--cicp</c>（解码侧写 PNG cICP 用）。</summary>
        public static (byte P, byte T)? FromSourceTokens(string? primaries, string? transfer)
        {
            byte? p = PrimariesCode(primaries);
            byte? t = TransferCode(transfer);
            if (p == null || t == null) return null;
            return (p.Value, t.Value);
        }

        internal static string? PrimariesToken(int? code) => code switch
        {
            1 => "bt709", 4 => "bt470m", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m",
            8 => "film", 9 => "bt2020", 10 => "smpte428", 11 => "smpte431", 12 => "smpte432", 22 => "ebu3213", _ => null,
        };

        internal static string? TransferToken(int? code) => code switch
        {
            1 => "bt709", 4 => "bt470m", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m",
            8 => "linear", 9 => "log100", 10 => "log316", 11 => "iec61966-2-4", 12 => "bt1361",
            13 => "iec61966-2-1", 14 => "bt2020-10", 15 => "bt2020-12", 16 => "smpte2084",
            17 => "smpte428", 18 => "arib-std-b67", _ => null,
        };

        internal static string? MatrixToken(int? code) => code switch
        {
            0 => "rgb", 1 => "bt709", 4 => "fcc", 5 => "bt470bg", 6 => "smpte170m", 7 => "smpte240m",
            8 => "ycgco", 9 => "bt2020nc", 10 => "bt2020c", 11 => "smpte2085",
            12 => "chroma-derived-nc", 13 => "chroma-derived-c", 14 => "ictcp", _ => null,
        };

        private static byte? PrimariesCode(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var t = Services.ColorSpaceRegistry.NormalizePrimariesToken(token) ?? token.Trim().ToLowerInvariant();
            return t switch
            {
                "bt709" => 1,
                "bt470m" => 4,
                "bt470bg" => 5,
                "smpte170m" => 6,
                "smpte240m" => 7,
                "film" => 8,
                "bt2020" => 9,
                "smpte428" => 10,
                "smpte431" => 11,
                "smpte432" => 12,
                "ebu3213" => 22,
                _ => null,
            };
        }

        private static byte? TransferCode(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var t = Services.ColorSpaceRegistry.NormalizeTrcToken(token) ?? token.Trim().ToLowerInvariant();
            return t switch
            {
                "bt709" => 1,
                "bt470m" => 4,
                "bt470bg" => 5,
                "smpte170m" => 6,
                "smpte240m" => 7,
                "linear" => 8,
                "log100" => 9,
                "log316" => 10,
                "iec61966-2-4" => 11,
                "bt1361" => 12,
                "iec61966-2-1" => 13,
                "bt2020-10" => 14,
                "bt2020-12" => 15,
                "smpte2084" => 16,
                "smpte428" => 17,
                "arib-std-b67" => 18,
                _ => null,
            };
        }

        internal static byte? MatrixCode(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            var t = Services.ColorSpaceRegistry.NormalizeMatrixToken(token) ?? token.Trim().ToLowerInvariant();
            return t switch
            {
                "rgb" or "gbr" => 0,
                "bt709" => 1,
                "fcc" => 4,
                "bt470bg" => 5,
                "smpte170m" => 6,
                "smpte240m" => 7,
                "ycgco" => 8,
                "bt2020nc" => 9,
                "bt2020c" => 10,
                "smpte2085" => 11,
                "chroma-derived-nc" => 12,
                "chroma-derived-c" => 13,
                "ictcp" => 14,
                _ => null,
            };
        }
    }
}
