# 色彩元数据兼容性更正（2026-11-20）

## 决策边界

- 输入不是合规校验门禁。有效旧版 ICC、没有 `cicp` 或没有 Profile ID 的第三方 ICC，不因这些条件阻止图片解码。能执行的原样携带保留源字节；LUT 不可矩阵化时不能以 profile 名字或碰巧存在的矩阵假算。默认转换若没有可靠色彩路径，告警并保留可用图像，不造目标原色/TRC；明确要求转换或携带而实现不能兑现时，仍以独立原因报告请求不能完成。
- 损坏或不能描述源像素的内嵌 ICC 与明确提供错误的 `--icc-file` 不等价。前者可弃，可信独立描述可以继续使用；后者是任务要求的一部分，不能偷偷换源定义。
- 自产 ICC 固定 v4.4；在 ICC.1 规定的范围中重算 ID 时须清除 flags、rendering intent、ID 字段。旧 CLI token 兼容解析并告警，UI 不再提供 v2 写入选项。第三方 profile 的原样保留不是重写；外部 XYB JPEG 的原生 LUT ICC 不应被套上普通 RGB 的 P/T。
- 容器 CICP 描述实际存储（例如 AVIF `nclx` YUV 的矩阵、范围），ICC 内的 `cicp` 描述经过解码解释后的 RGB；两者不是同一个四元组。不能可靠命名时宁可不贴标签。只测色彩语义的门禁不要求两个独立编码器生成同一串 ICC 字节；要求原样携带的测试可以比较 SHA。
- 从视频抽静帧的 MP4/H.264 冲突用例单列，不计作图片格式覆盖。容器与已解码帧可观测证据冲突时以所用帧为先；完全不明的 P/T 不从已矛盾的容器组借值。取样是有上限的，不等价于核验完整时序。

## 证据

- [JPEG/WebP 与 PNG 输入兼容脚本](../../tests/scripts/verify-jpg-webp-matrix.ps1)：有效旧 LUT、原样保留和实际 CMS 样本对撞、损坏 tag 表、只有逆向变换、坏 `iCCP` 封装、CRC 正确但矩阵非零的 `cICP`。PNG 组比全幅 RGB 字节、有效 P/T 和坏 ICC 不回附，不靠日志独立判正。
- [AVIF/JPEG 产物脚本](../../tests/scripts/verify-color-out-matrix.ps1)：独立读取 AVIF 主项属性、Native Y 黑白和彩块、JPEG APP2、v4.4 profile 结构、ID 重算及多个内存反例；JPEG 旧 v2 选项的产物仍为 v4.4。
- [跨格式 P3 脚本](../../tests/scripts/verify-colorfmt-matrix.ps1)：PNG、JPEG、WebP、TIFF、JXL、AVIF 六格用实际色彩描述判定。ICC 判据读取 D50 PCS 原色/白点和采样 TRC，不使用另一门禁的缓存作参照，不再有不可达的 WARN 限额。
- [矩阵入口脚本](../../tests/scripts/verify-color-token-normalize.ps1) 的 E16 验证同一 H.264 码流、不同 MP4 `colr` 四轴：实际 RGB 与 PNG CICP 相同；强行使用错误矩阵和范围的负控像素不同。

## 定向结果

- `FfmpegGui.dll=1A3CC062C74EE1B2`、`ServiceProbe.dll=83EE4AAA776E8B21`（Release，0 警告/0 错误）：[JPEG/WebP/PNG 输入与范围](../../tests/output/_gate_script_verify-jpg-webp-matrix.ps1.txt) 59/0，[AVIF/JPEG 输出](../../tests/output/_gate_script_verify-color-out-matrix.ps1.txt) 149/0；`iccname` 17/0、`plan` 43/0。测试了 [PNG 3.0 §11.3.2.6](https://www.w3.org/TR/png-3/#cICP-chunk) 允许的窄范围 RGB (`F=0`)：保留 P/T，实际按 16–235 → 0–255 变换，不把窄范围判为非法，也不对原值只改标记。
- `2DBF23CCB05B3771` / `406069F67A1617F7`：[ProPhoto 保真](../../tests/output/_gate_script_verify-prophoto-faithful.ps1.txt) 42/0、[跨格式 P3](../../tests/output/_gate_script_verify-colorfmt-matrix.ps1.txt) 6/0。`925EA18BFC1B09B3` / `1845700994A76552`： [入口矩阵](../../tests/output/_gate_script_verify-color-token-normalize.ps1.txt) 104/0、[接线](../../tests/output/_gate_script_verify-color-wiring.ps1.txt) 162/0；同批起初误用无参数 `icc` 得到 usage，已以带参数的 ProPhoto 脚本和 `iccname` 重新验证。
- 增益图底图从旧假红转为真实回归证据：PNG 源 P3，增益图编码器已生成 sRGB 底图，通用 ICC 后置却把 P3 贴回 JPEG。禁止增益图执行通用 ICC 后置后，[增益图门禁](../../tests/output/_gate_script_verify-gainmap-engine.ps1.txt) ⑪b 对两个后端分别实读 `1/13/0/1` 通过。首轮整脚本 196/7：旧门禁错误要求 HDR JPEG 的 `iso` 不写 XMP、SDR 的不适用 tone-map 必须拒件，并把新生成的 HDRGM 当作输入污染；另缺三件第三方样本。将判据改为实际承载、SDR 像素不变、输入污染值 `9.9` 无回带；恢复三件原始样本并对齐登记 SHA16（`34504AA6D6DB6E6F` / `95419A66144F1A65` / `1AAF0A1AFA91A485`），同一构建最终 **221/0**。

## 范围限制

实际 CMS 执行仍只承诺已接线且验证的静态 PNG 路径；复杂 MPE、跨输出格式或帧间变化并未因为放宽准入变成已支持的准确转换。未执行 104 条全量基线，也没有出包。历史审查账本不回改。
