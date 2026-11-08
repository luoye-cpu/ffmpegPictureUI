using System;
using System.IO;
using System.Text;

namespace FfmpegGui.Services
{
    /// <summary>
    /// JXL 容器 <c>jhgm</c>（gain map）box 的轻量探测结果。
    /// 契约对齐 <see cref="IsoBmffGainMapProbe.Result"/>：<see cref="Determinate"/>=false ⇒
    /// 「不知道」，调用方**不得**当成「没有」（GainMapDecoder 的可用性诚实原则）。
    /// </summary>
    public sealed class JxlGainMapScanResult
    {
        /// <summary>解析是否确定。false ⇒ <see cref="FailReason"/> 说明原因。</summary>
        public bool Determinate = true;
        /// <summary>失败原因；一律以 <c>[JxlGm] </c> 开头（本仓 <c>[tag]</c> 诊断桶约定，不进 UI 桶）。</summary>
        public string? FailReason;

        /// <summary>是否探测到增益图（jhgm / brob(jhgm) box 在场）。</summary>
        public bool HasGainMap;
        /// <summary>承载形式：<c>jhgm</c>（裸 box，字段可读）/ <c>jhgm-brob</c>（brob 压缩壳，仅原始类型可识别）。</summary>
        public string? Form;

        /// <summary>jhgm 载荷版本字节；-1 = 未取（brob 壳细节不可得）。</summary>
        public int JhgmVersion = -1;
        /// <summary>ISO 21496-1 元数据字节数；-1 = 未取。</summary>
        public long MetadataLength = -1;
        /// <summary>alt ICC 压缩字节数；-1 = 未取。</summary>
        public long AltIccLength = -1;
        /// <summary>增益图裸码流字节数；-1 = 未取。</summary>
        public long GainMapBytes = -1;
    }

    /// <summary>
    /// JXL 容器增益图（<c>jhgm</c> box）纯托管探测（2026-11-08 R1）。
    /// <para>背景：libjxl 0.11 起 JXL 容器支持 <c>jhgm</c> box 承载 ISO 21496-1 增益图。载荷布局
    /// （vendored <c>tools/src/libjxl/lib/extras/gain_map.cc:118-145</c> 实测复刻）：
    /// <c>version(1B) + BE16 元数据长 + ISO blob + 颜色编码长(1B) + [颜色编码] + BE32 alt ICC 长 +
    /// [alt ICC] + 增益图裸码流（剩余全部，尺寸不落盘，gain_map.cc:222-226）</c>。
    /// 本探测只做**长度算术闭合**检查，ISO blob 对解析器不透明（随包 jxlinfo 同口径），不做语义解析。</para>
    /// <para><c>brob</c> 壳可**零解压**识别：brob 载荷前 4 字节是原始 box 类型
    /// （vendored <c>encode.cc:1144</c> "Prepend the original box type"，<c>decode.h:281-286</c> 文档化契约）。
    /// 不引 Brotli 托管解压——brob 形态字段细节不可得是接受的边界（presence 已足够 R1/R2）。</para>
    /// <para>判定纪律：**不做**字节窗口扫描兜底——压缩码流里出现 ASCII "jhgm" 字节序列是可能的，
    /// 窗口扫描会误报；只在 box 遍历 complete（JxlInspector.ScanTopLevelBoxes）时对「不在场」下结论，
    /// 链中断 = fail-closed（Determinate=false）。</para>
    /// </summary>
    public static class JxlGainMapProbe
    {
        /// <summary>纯托管解析的文件上限（与 GainMapDecoder.MaxManagedParseBytes 同口径）。</summary>
        internal const long MaxManagedParseBytes = 256L * 1024 * 1024;

        /// <summary>
        /// 探测 .jxl 文件的增益图承载。文件不存在 / 读失败返回 null（调用方按「未知」处理）；
        /// 超过 <see cref="MaxManagedParseBytes"/> 返回 Determinate=false（不许把「没解析」装成「没有」）。
        /// </summary>
        public static JxlGainMapScanResult? Scan(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if (fi.Length > MaxManagedParseBytes)
                {
                    return new JxlGainMapScanResult
                    {
                        Determinate = false,
                        FailReason = "[JxlGm] 文件超过托管解析上限（256MB）⇒ 无法断定有无增益图"
                    };
                }

                using var fs = File.OpenRead(path);
                var head = new byte[12];
                if (!JxlInspector.ReadExact(fs, head, 12))
                {
                    // <12B：裸码流下限也 ≥12B（JxlInspector 注释：1×1 实测 60B）。
                    if (head[0] == 0xFF && head[1] == 0x0A)
                        return new JxlGainMapScanResult();   // 裸码流：构造性判无
                    return new JxlGainMapScanResult
                    {
                        Determinate = false,
                        FailReason = "[JxlGm] 文件过短（<12B）且非裸码流 ⇒ 无法断定有无增益图"
                    };
                }
                if (head[0] == 0xFF && head[1] == 0x0A)
                    return new JxlGainMapScanResult();       // 裸码流：构造上无容器 box ⇒ 判无
                if (!(head[4] == (byte)'J' && head[5] == (byte)'X' && head[6] == (byte)'L' && head[7] == (byte)' '))
                    return new JxlGainMapScanResult
                    {
                        Determinate = false,
                        FailReason = "[JxlGm] 文件头既非裸码流也非 JXL 容器签名 ⇒ 无法断定有无增益图"
                    };

                var (complete, boxes) = JxlInspector.ScanTopLevelBoxes(fs);
                long fileLen = fs.Length;

                JxlGainMapScanResult? unreadableBrob = null;
                foreach (var b in boxes)
                {
                    if (b.Type == "jhgm")
                        return ScanJhgmBox(fs, fileLen, b.Offset, b.Size, b.HeaderSize);
                    if (b.Type == "brob" && unreadableBrob == null)
                    {
                        var inner = ReadBrobInnerType(fs, fileLen, b.Offset, b.Size, b.HeaderSize);
                        if (inner == "jhgm")
                        {
                            // brob 壳内嵌 jhgm：presence 无歧义 ⇒ Determinate=true；
                            // 字段（version/尺寸）在 brotli 壳内 ⇒ 不可得（不引托管解压）。
                            return new JxlGainMapScanResult { HasGainMap = true, Form = "jhgm-brob" };
                        }
                        if (inner == null)
                        {
                            // brob 壳载荷 <4B：无法识别原始类型 ⇒ 不能排除里面是 jhgm（fail-closed）。
                            // 先记下继续扫后面的 box——若后面有裸 jhgm，presence 仍是确定的。
                            unreadableBrob = new JxlGainMapScanResult
                            {
                                Determinate = false,
                                FailReason = "[JxlGm] brob 壳载荷不足 4 字节，无法识别原始 box 类型 ⇒ 无法断定有无增益图"
                            };
                        }
                        // inner 为其它类型（Exif/xml 等）⇒ 与增益图无关，继续
                    }
                }

                if (unreadableBrob != null) return unreadableBrob;
                if (complete) return new JxlGainMapScanResult();   // 链走满且无 jhgm/brob(jhgm) ⇒ 确定无
                return new JxlGainMapScanResult
                {
                    Determinate = false,
                    FailReason = "[JxlGm] 顶层 box 链解析中断（截断或 box 头非法）⇒ 无法断定有无增益图"
                };
            }
            catch
            {
                return null;   // 读失败按「未知」处理（调用方不得当成没有）
            }
        }

        /// <summary>
        /// 解析裸 <c>jhgm</c> box 载荷（长度算术闭合检查；只读各长度字段，不搬大块字节）。
        /// 闭合 ⇒ Determinate=true + 四项读数；不闭合 ⇒ HasGainMap=true / Determinate=false
        /// （box 在场 = 增益图数据在场，只是解不开——对齐随包 jxlinfo 的 "Invalid gain map box found"）。
        /// </summary>
        private static JxlGainMapScanResult ScanJhgmBox(FileStream fs, long fileLen, long boxOffset, long boxSize, int headerSize)
        {
            var r = new JxlGainMapScanResult { HasGainMap = true, Form = "jhgm" };
            long payloadStart = boxOffset + headerSize;
            long payloadAvail = Math.Min(boxOffset + boxSize, fileLen) - payloadStart;
            if (payloadAvail < 3)
            {
                r.Determinate = false;
                r.FailReason = $"[JxlGm] jhgm box 载荷不足 3 字节（实际 {payloadAvail}B）⇒ 疑似截断，无法断定字段";
                return r;
            }

            var head3 = new byte[3];
            if (!ReadBytesAt(fs, payloadStart, head3))
            {
                r.Determinate = false;
                r.FailReason = "[JxlGm] jhgm 载荷读取失败（version/元数据长）⇒ 无法断定字段";
                return r;
            }
            int version = head3[0];
            int metaLen = (head3[1] << 8) | head3[2];
            long cursor = 3 + metaLen;

            var colorEnc = new byte[1];
            if (!ReadBytesAt(fs, payloadStart + cursor, colorEnc))
            {
                r.Determinate = false;
                r.FailReason = $"[JxlGm] jhgm 载荷长度算术不闭合（version={version}，元数据声明 {metaLen}B 超出盒剩余 {payloadAvail}B）⇒ 疑似损坏或截断";
                return r;
            }
            int colorEncSize = colorEnc[0];
            cursor += 1 + colorEncSize;

            var iccLen = new byte[4];
            if (!ReadBytesAt(fs, payloadStart + cursor, iccLen))
            {
                r.Determinate = false;
                r.FailReason = $"[JxlGm] jhgm 载荷长度算术不闭合（version={version}，元数据 {metaLen}B + 颜色编码 {colorEncSize}B 超出盒剩余 {payloadAvail}B）⇒ 疑似损坏或截断";
                return r;
            }
            long altIccLen = ((long)iccLen[0] << 24) | ((long)iccLen[1] << 16) | ((long)iccLen[2] << 8) | iccLen[3];
            cursor += 4 + altIccLen;

            if (cursor > payloadAvail)
            {
                r.Determinate = false;
                r.FailReason = $"[JxlGm] jhgm 载荷长度算术不闭合（version={version}，元数据 {metaLen}B + 颜色编码 {colorEncSize}B + alt ICC {altIccLen}B 超出盒剩余 {payloadAvail}B）⇒ 疑似损坏或截断";
                return r;
            }

            r.Determinate = true;
            r.JhgmVersion = version;
            r.MetadataLength = metaLen;
            r.AltIccLength = altIccLen;
            r.GainMapBytes = payloadAvail - cursor;
            return r;
        }

        /// <summary>
        /// 读 brob 壳前 4 字节原始 box 类型（decode.h:281-286 文档化契约）。
        /// 返回 null = 载荷不足 4B（原始类型不可读）。
        /// </summary>
        private static string? ReadBrobInnerType(FileStream fs, long fileLen, long boxOffset, long boxSize, int headerSize)
        {
            long payloadStart = boxOffset + headerSize;
            long payloadAvail = Math.Min(boxOffset + boxSize, fileLen) - payloadStart;
            if (payloadAvail < 4) return null;
            var t = new byte[4];
            if (!ReadBytesAt(fs, payloadStart, t)) return null;
            return Encoding.ASCII.GetString(t);
        }

        private static bool ReadBytesAt(FileStream fs, long pos, byte[] buf)
        {
            if (pos < 0) return false;
            try
            {
                fs.Seek(pos, SeekOrigin.Begin);
                return JxlInspector.ReadExact(fs, buf, buf.Length);
            }
            catch { return false; }
        }
    }
}
