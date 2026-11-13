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

        /// <summary>载荷声明的**输入 item** 位深（8/10/12/16）。</summary>
        public int InputBitDepth { get; }

        /// <summary>token 数（载荷第 2 字节）。</summary>
        public int TokenCount => _firstTokenCount;

        /// <summary>人读表达式，用于播报/诊断。</summary>
        public string Expression { get; }

        private AvifSampleTransform(List<Op> ops, int tokenCount, int inputBitDepth, string expression)
        {
            _ops = ops;
            _firstTokenCount = tokenCount;
            InputBitDepth = inputBitDepth;
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

            int bitDepthCode = payload[0] & 0x03;
            int inputBitDepth = bitDepthCode switch
            {
                0 => 8,
                1 => 10,
                2 => 12,
                3 => 16,
                _ => 0,
            };
            if (inputBitDepth == 0)
            {
                error = $"sato input bit-depth code={bitDepthCode} invalid";
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

            // 允许尾部零填充（规范未要求 exact-length，但非零尾字节必须点名）。
            for (int i = pos; i < payload.Length; i++)
            {
                if (payload[i] != 0)
                {
                    error = $"sato payload has non-zero trailing byte (offset={i}, value=0x{payload[i]:X2})";
                    return null;
                }
            }

            if (sb.Length > 2) sb.Length -= 2;
            var expression = sb.ToString();

            // 结构自检：用 1/1 试算，栈必须恰好剩 1 个值。
            var probe = new AvifSampleTransform(ops, tokenCount, inputBitDepth, expression);
            if (!probe.TryEvaluateStack(1, 1, long.MinValue, out _, out var evalError))
            {
                error = evalError;
                return null;
            }

            return probe;
        }

        /// <summary>逐样本求值（通用后缀栈，返回未钳位的 long）。</summary>
        public long Evaluate(long sample1, long sample2)
        {
            if (TryFast(sample1, sample2, out var v)) return v;
            TryEvaluateStack(sample1, sample2, 0, out v, out _);
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
                long q = c2 == 0 ? 0 : s2 / c2;
                value = c1 * s1 + q;
                return true;
            }

            if (_ops.Count == 5
                && _ops[0].Kind == OpKind.Constant
                && _ops[1].Kind == OpKind.Sample && _ops[1].Value == 1
                && _ops[2].Kind == OpKind.Product
                && _ops[3].Kind == OpKind.Sample && _ops[3].Value == 2
                && _ops[4].Kind == OpKind.Sum)
            {
                value = _ops[0].Value * s1 + s2;
                return true;
            }

            return false;
        }

        private bool TryEvaluateStack(long s1, long s2, long unused, out long result, out string? error)
        {
            error = null;
            result = 0;
            int sp = 0;
            long[] stack = new long[_ops.Count + 4];

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
                        if (sp < 1) { error = $"sato {op.Kind} stack underflow"; return false; }
                        stack[sp - 1] = op.Kind switch
                        {
                            OpKind.Negation => -stack[sp - 1],
                            OpKind.Abs => Math.Abs(stack[sp - 1]),
                            _ => ~stack[sp - 1],
                        };
                        break;
                    default:
                        if (sp < 2) { error = $"sato {op.Kind} stack underflow"; return false; }
                        {
                            long b = stack[--sp];
                            long a = stack[sp - 1];
                            stack[sp - 1] = op.Kind switch
                            {
                                OpKind.Sum => a + b,
                                OpKind.Difference => a - b,
                                OpKind.Product => a * b,
                                OpKind.Quotient => b == 0 ? 0 : a / b,
                                OpKind.And => a & b,
                                OpKind.Or => a | b,
                                OpKind.Xor => a ^ b,
                                OpKind.Pow => Pow(a, b),
                                OpKind.Min => Math.Min(a, b),
                                OpKind.Max => Math.Max(a, b),
                                _ => 0,
                            };
                        }
                        break;
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

        private static long Pow(long a, long b)
        {
            if (b < 0 || b > 63) return 0;
            long r = 1;
            for (int i = 0; i < b; i++)
            {
                if (a == 0) return 0;
                r *= a;
                if (r > long.MaxValue / 2) return long.MaxValue;
            }
            return r;
        }

        /// <summary>钳到 16-bit 无符号（sato 的目标位深为 16）。</summary>
        public static ushort ClampTo16(long value)
            => value <= 0 ? (ushort)0 : value >= 65535 ? (ushort)65535 : (ushort)value;
    }

    /// <summary>
    /// CICP token → H.273 数字码。只用于 avifenc <c>--cicp P/T/M</c>；
    /// 未知 token 返回 null（调用方播报并改用安全默认，不猜一个无关的码）。
    /// </summary>
    public static class AvifCicp
    {
        /// <summary>由计划的目标描述符生成 avifenc <c>--cicp</c> 三元组。</summary>
        public static (byte P, byte T, byte M)? FromPlan(ColorTransformPlan plan, bool rgbSource)
        {
            if (plan == null) return null;
            byte? p = PrimariesCode(plan.OutPrimaries);
            byte? t = TransferCode(plan.OutTransfer);
            // RGB 源 + identity：即使计划层没给出可命名 token，也可以用 sRGB 的 H.273 默认
            //（1/13）承载；省略 --cicp 会让 avifenc 落默认矩阵 6（YUV），与 gbrp 基图自相矛盾。
            if (rgbSource)
            {
                p ??= 1;
                t ??= 13;
            }
            byte? m = rgbSource ? (byte)0 : MatrixCode(plan.OutMatrix);
            if (p == null || t == null || m == null) return null;
            return (p.Value, t.Value, m.Value);
        }

        /// <summary>由已探测的源 CICP token 生成 <c>--cicp</c>（解码侧写 PNG cICP 用）。</summary>
        public static (byte P, byte T)? FromSourceTokens(string? primaries, string? transfer)
        {
            byte? p = PrimariesCode(primaries);
            byte? t = TransferCode(transfer);
            if (p == null || t == null) return null;
            return (p.Value, t.Value);
        }

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

        private static byte? MatrixCode(string? token)
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
