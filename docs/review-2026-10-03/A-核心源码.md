# A — 核心源码独立复查（IsoBmffGainMapWriter / GainMapEncoder / GainMapDecoder / ThumbnailService / 512 项缓存）

- 复查基准：`git diff e7de117..HEAD`，HEAD = `bdd4b0b`，基准 `e7de117`（v1.6.0-beta3）。
- 本次复查**只读**：未修改任何源码、未 `git add/commit`、未执行 `dotnet build`。
- 取证手段（全部落在 `%TEMP%\avifrev`，未写入仓库）：
  1. 本机 ffmpeg（`git-2026-09-26-8864fd0aec`，即 `/c/PLAN/FFmpeg/ffmpeg`）按 `GainMapEncoder.EncodeAvifFileAsync` 的**同一套参数**造出底图/增益图单图 AVIF；
  2. 把 `IsoBmffGainMapWriter` **逐行机械移植**成 Python（不改语义、不改常量），对真实 ffmpeg 产物跑一遍；
  3. 用**另写的独立 BMFF 解析器**回读产物，核对 box 长度闭合、iloc 偏移、属性盒内容；
  4. 用 ffmpeg/ffprobe 实测产物能否解出主图与增益图、实测 CICP/color_range、实测「缺 Temporal Delimiter」的报错原文。
- 结论口径：**代码事实 + 实测**；凡是实测过的条目都附上实测结果。

---

## 一、严重

### S1. 底图 `colr` 的 full_range 被硬编码为 full，而 ffmpeg 实际产出的是 limited（tv） ⇒ 「宣称 ≠ 交付」

- **位置**：`src/FfmpegGui/Services/IsoBmffGainMapWriter.cs:78`（默认参数 `bool baseFullRange = true`）、`:253-266`（`PatchColr`，其中 `:259-260` 写 `new[] { (byte)(baseFullRange ? 0x80 : 0x00) }`）、`:121-126`（给 `tmap`/替代图另建的 `colr` 同样用 `baseFullRange`）；唯一调用点 `src/FfmpegGui/Services/GainMapEncoder.cs:621`（`Build(baseBytes, gmBytes, tmapPayload, cPrim, cTrc, cMtx)` —— **未传** `baseFullRange`，因此恒为 `true`）。
- **为什么是问题**：`PatchColr` 会**整盒替换**底图 `colr`，把 ffmpeg 写的 `full_range_flag` 从 `0x00`（tv）改写成 `0x80`（full）。像素本身一次都没动 ⇒ 同一批像素被重新贴了一个与之不符的 range 标签。查看器按 full range 解 tv 像素 ⇒ 暗部抬高、对比度压缩，是**可见的色彩错误**，而且落在**主图**上。
- **实测证据**：
  - 按 `GainMapEncoder.cs:573` 的参数（`yuv420p` + `-color_primaries bt709 -color_trc iec61966-2-1 -colorspace bt709`）造出的 `base.avif`：盒内 `colr` = `nclx prim=2 trc=2 mtx=1 range=0x00`；`ffprobe` 报 `color_range=tv`；解码后 Y 平面 `min=20 max=220`（典型 limited 编码，非 0–255）。
  - 同一份像素经本写出器组装后：`ffprobe` 报 `color_range=pc`，`color_primaries=bt709`、`color_transfer=iec61966-2-1`（说明 `PatchColr` 生效且被读者采信）。
  - `wideBase` 分支（`bt2020/bt709/bt2020nc`）同样：源 `colr range=0x00`、`ffprobe color_range=tv`；组装后仍被改写成 `0x80`。两条出口都中。
  - 同文件内部还自相矛盾：底图 `colr`（ipco 第 4 项）= `0x80`，而增益图 `colr`（原样搬运，第 6 项）= `0x00`，`tmap`/替代图 `colr`（第 9 项）= `0x80`。
- **触发条件**：任何走 AVIF 增益图出口、且 `basePrimaries >= 0` 的任务 —— 即**全部生产路径**（`GainMapEncoder.cs:621` 无条件传 `cPrim`）。不是偶发，是常态。
- **修复方向（二选一，必须由一次实测定案，不要靠默认参数）**：要么在 `EncodeAvifFileAsync` 的 ffmpeg 命令行显式加 `-color_range pc`（让像素真的变成 full），要么把 `baseFullRange` 传 `false`（标签跟随像素的 tv）。

---

## 二、中等

### M1. 单色增益图在本机 ffmpeg 上**必然失败**，恒回退 yuv420p ⇒ 增益图是 3 平面，而 tmap 声明 1 通道

- **位置**：`GainMapEncoder.cs:579-590`（先 `-pix_fmt gray`，失败后退 `"yuv420p"`）、`:615`（`BuildIso21496Metadata(multiChannel, maxLog2Gain)` 决定 `channels`）、`IsoBmffGainMapWriter.cs:103-113`（增益图属性盒原样搬运，把 3 平面的 `pixi` 带进产物）。
- **为什么是问题**：ISO 21496-1 里 `channelCount` 应与增益图图像的通道数一致。回退路把灰度 PNG 编成 `yuv420p` ⇒ 产物 `pixi ch=3`（Y/Cb/Cr，且 Cb=Cr=128 恒为中性），而 tmap 元数据声明 `channels=1` ⇒ **声明与像素平面不符**；同时白扔两个色度平面的码率。
- **实测证据**：同一 ffmpeg 构建下，`-pix_fmt gray -f avif` → **rc=183**（`Error encoding frame: Invalid parameter / Subsampling must be 0 with AOM_CICP_MC_IDENTITY`），`-pix_fmt yuv400p` → **rc=127**，只有 `-pix_fmt yuv420p` → rc=0。也就是说**回退不是小概率分支，而是本机唯一走得通的路**。
- **缓冲**：`-f obu` 取第 0 平面（Y）时值仍正确（灰 PNG → Y=灰值），自家解码链不受影响；风险集中在第三方严格校验器上。
- **触发条件**：所有 AVIF 增益图产物。

### M2. 产物直接 `WriteAllBytesAsync` 覆盖目标文件，无「临时文件 + 原子替换」

- **位置**：`GainMapEncoder.cs:608`（SDR 兜底路径）、`:627`（增益图容器路径）。
- **为什么是问题**：`File.WriteAllBytesAsync` 是先截断再逐块写；中途失败（磁盘满、权限、取消）会在 `outputPath` 上留下**半截文件**。若 `outputPath` 已存在（覆盖式导出 / 重跑同一任务），**原来的好文件先被截断**。对比 `ThumbnailService`（临时文件 + finally 删除）与 `PngCicpService`（失败不改文件）这两个同仓既有口径，本处明显更松。
- **触发条件**：写盘失败或任务在写盘期间被取消；覆盖既有 `outputPath` 时危害升级。
- **修复方向**：写到 `outputPath + ".tmp"` 再 `File.Move(..., overwrite: true)`。

### M3. 缩略图「成功」判据只看 exiftool 退出码，没有回读闭合 ⇒ 可能谎报已嵌入

- **位置**：`ThumbnailService.cs:150-158`（`embedExit != 0` 才算失败，否则记 `embedded` 并返回 0）；`ExifToolService.cs:839-855`（`EmbedThumbnailAsync` 直接返回 `RunWriteAsync` → exiftool rc）。
- **为什么是问题**：本类文档与 `FormatCapabilitiesService.cs:231-247` 的实测注释**自己承认存在「0 image files updated 但退出码仍是 0」这一形态**（tiff/gif/jxr/dng 上实测到）。同一个形态若出现在支持列表的容器（jpg/png/apng/webp/avif/jxl）上，`ApplyAsync` 会返回 0 并打出 `[thumbnail] embedded: …`，而产物其实没有缩略图 ⇒ 与「绝不静默产出一张没有缩略图的图」的自我承诺相反。
- **触发条件**：exiftool 因故未能写入但 rc=0（如目标容器缺 EXIF 槽位、写保护、大文件分段失败）。
- **旁证**：仓内探针 `tests/ServiceProbe/Program.cs:319`（`Closed()`：声明长度 == 实取字节）已经在**外部**补了这把尺子，但产品侧没有。

### M4. 增益图探针读失败时按「不带增益图」兜底 —— 是 fail-open，方向危险

- **位置**：`CjxlService.cs:245`（`catch { return false; }`）。
- **为什么是问题**：返回 `false` ⇒ `UsesLosslessJpegRewrap` 判定「可以免解码重封装」⇒ 对一张**真的带增益图的 JPEG** 发 `--lossless_jpeg=1` ⇒ cjxl 静默改道（解成 float 重编码），产物自称无损而非无损 —— 正是该文件注释里写着的「宣称≠交付」缺陷形状。全仓既有口径是「探测不确定就点名」，这里是例外。
- **触发条件**：读头失败（文件被占用 / IO 抖动 / 权限 / `File.ReadExactly` 抛错）落在带增益图的源上。

### M5. 512 项缓存没有内容失效判据

- **位置**：`CjxlService.cs:211-243`（`_gainMapJpegCache`，key = `fi.FullName`，value = bool）。
- **为什么是问题**：只按路径缓存，不看 `LastWriteTimeUtc` / 长度。队列里若出现「同名文件被换内容」（例如固定名的临时中转件、重跑时先删后建同名文件、preview 临时文件复用），会命中旧结论。M4 的 fail-open 与它叠加时，错误结论会被缓存放大（一次读失败 ⇒ 512 项寿命内一直按「无增益图」处理）。
- **触发条件**：同名路径复用 / 文件在两次查询之间被改写。

---

## 三、轻微

| # | 位置 | 问题 | 触发条件 |
|---|---|---|---|
| L1 | `IsoBmffGainMapWriter.cs:186-192` | `BuildTmapPayload(byte[] iso21496Metadata)` 是 public，无 null 检查 ⇒ 直接 NRE（同类方法 `Build` 在 `:84-86` 做了显式 null 拒绝，口径不一致） | 传入 null |
| L2 | `:291` | `w.Write((byte)(a > 0x7F ? 0x7F : (a | 0x80)))` —— 属性索引 > 127 时**静默钳成 127 且丢掉 essential 位**，写出一个错误的关联而不是失败 | `ipcoChildren.Count > 127`（底图+增益图属性盒合计超过 127，正常素材不会，但无任何保护/点名） |
| L3 | `:291` | 所有属性一律标 essential（`|0x80`）。实测 ffmpeg 自产 AVIF 的 `ipma` 是 `01 02 83 04` —— 只有 `av1C` 带 essential，`ispe`/`pixi`/`colr` 均不带。本写出器把 `ispe`/`colr` 也标成 essential，与 libavif/ffmpeg 惯例不符 | 全部产物；严格读者可能按「不认识的 essential 属性 ⇒ 不处理该 item」处理 |
| L4 | `:448-450` 配 `:590` | 属性盒是**原样搬运原始字节**，保留原 size 字段。`TryReadBox` 对 `size==0`（延伸至容器末）会算出实际长度，但拷贝出的字节里 size 仍是 0 ⇒ 搬进新 `ipco` 后含义变成「延伸到 ipco 末尾」，会吞掉其后的所有属性盒 | 源 AVIF 的 `ipco` 里出现 `size==0` 的盒且不在末位（少见，但属结构性隐患） |
| L5 | `:515`、`:523-524` 配 `:654-659` | `ReadBig(d, p, n)` 无边界检查（`i < 8` 只挡了 n，不挡数组长度）；`Be16/Be32` 亦无检查 ⇒ 畸形输入抛 `IndexOutOfRangeException`，被 `:174-178` 的兜底 catch 变成一句笼统的「组装异常 IndexOutOfRangeException」，丢掉了「哪个盒越界」的信息 | 畸形/截断的源 AVIF |
| L6 | `:216-217`、`:273-274`、`:297-298` | `MemoryStream`/`BinaryWriter` 未 `using`。无未托管句柄、`ToArray()` 已取数据 ⇒ **无泄漏**，但与同文件其余写法不一致 | 静态问题 |
| L7 | `:104` 配 `:139-140`、`GainMapEncoder.cs:628` | `r.GainMapItemBytes` 在 TD 前置**之前**取值 ⇒ 成功日志里的增益图字节数比实际写出少 2 字节（`12 00`）。诊断值不精确 | 全部产物 |
| L8 | `GainMapEncoder.cs:656-662` | `WaitForExitAsync(ct)` 被取消时抛异常，子进程**不会被杀** ⇒ 孤儿 ffmpeg 继续写临时 avif | 队列任务在编码期被取消 |
| L9 | `IsoBmffGainMapWriter.cs:96-101` | 「增益图不得大于底图」的校验被 `baseImg.Width > 0 && gmImg.Width > 0` 包住 ⇒ 源缺 `ispe` 时整条校验跳过，尺寸倒置的增益图会被放行 | 源 AVIF 的 `ipco` 无 `ispe` |
| L10 | `GainMapDecoder.cs:865`、`:884` | Apple XMP 的 headroom 正则只认 `[0-9.]+`：负值、科学计数法（`-4.5` / `4.5E0`）都不匹配 ⇒ 落进「Apple v1.0 隐式 8×」分支，静默套用 `log2=3` 上限 | 极端/非规范 headroom 写法 |
| L11 | `CjxlService.cs:235` | key 用 `FileInfo.FullName` + `OrdinalIgnoreCase` —— 大小写与 `/` `\` 差异已归一（合格）；但 **8.3 短名、`\\?\` 前缀、符号链接/junction 别名、UNC 与盘符** 未归一 ⇒ 同一文件两个 key、可能给出两个不同结论 | 同一路径的不同拼写混用 |
| L12 | `GainMapEncoder.cs:606` 配 `:821` | `maxLog2Gain` 为 `NaN` 时 `NaN <= 0f` 为 false ⇒ 不进 SDR 兜底，而 `(int)MathF.Round(NaN*100)` 得到 0 ⇒ 产出 `gainMapMax=0` 的「零增益增益图」（自称带增益图、实际零增益） | headroom 计算出 NaN |
| L13 | `ThumbnailService.cs:56-60` | `scale=…:-2` 的偶数化是**向下**取整：实测 7x5 → 6x4（各少 1 px）。「只缩不放」成立，但极小图会有 1px 几何损失 | 源宽/高为奇数（尤其极小图） |
| L14 | `ThumbnailService.cs:38-39` + CLI | `ClampLongEdge` 对越界值**静默钳制不出声**（UI 侧 `NumericUpDown` 已限 16–512；CLI `--thumbnail-size=9999` 只被钳到 512，日志里没有点名） | CLI 传入越界尺寸 |

---

## 四、已核实**未发现**问题的方面（逐条对应你给的 7 类判据）

### 1. 二进制格式正确性 —— 已核，未发现字节序/长度/偏移类缺陷

- **字节序**：本文件**没有任何** `BitConverter` / `BinaryWriter.Write(int)` 直写。所有 BMFF 字段都走手写大端：`U16:636`、`U32:637`、`WriteBe16:639`、`WriteBe32:640-644`、`WriteBe32To:645-649`；`BinaryWriter` 只被用来 `Write(byte)` 和 `Write(byte[])`（`:219-221`），不引入机器序。**未发现小端 bug。**
- **box size 与实际写入字节闭合（实测）**：移植版产出 1095 B，独立解析器回读 —— `ftyp` 36 + `meta` 462 + `mdat` 597 = 1095；`mdat` 内布局与 `iloc` 三项**逐项吻合**：item1 `off=506 len=461`（= 498+8，mdat 起点+8）、item2 `off=967 len=62`（506+461）、item3 `off=1029 len=66`（967+62），且 `1029+66 = 1095 = 文件长度`。**闭合，无悬空字节。**
- **偏移量累算与两遍法**：`:143` 用零偏移建 `metaProbe` 量长度，`:147-149` 据 `ftypLen + metaLen + 8` 推出 `o1/o2/o3`，`:156` 再建一遍；因 `iloc` 内部偏移是**定长 u32**，两遍长度必然相等，且 `:157-162` 对不相等做了 fail-closed（不静默继续）。实测两遍一致。
- **FullBox version/flags**：`hdlr:213`、`pitm:214`、`iinf:223`、`iref:230`、`ipma:280`、`iloc:307`、`meta:245`、`altr:240` 均按规范写 4 字节 v/f；`infe` 用 version 2 且带**空 item_name**（`:269` 末尾那个 `0x00`）—— 实测 ffmpeg 自产 `infe` v2 是 `8+4+2+2+4 + "Color\0"`（26 B），本写出器是 `8+4+2+2+4 + ""\0`（21 B），结构同形且合法。
- **`iref`/`dimg`**：`:229` 写 `from=2, reference_count=2, to=[1,3]` —— 与 ISO/IEC 14496-12:2015 `SingleItemTypeReferenceBox`（**含** `reference_count`）一致；实测回读时 `vals=[2,1,3]`，`IsoBmffGainMapProbe.ParseIref:427` 的判别法（`vals[0] == vals.Count-1`）收敛到 `to=[1,3]`，读回路正确。
- **`iloc` 半字节打包**：`:300-301` 写 `0x44 / 0x00`（offset_size=4、length_size=4、base=0、index=0）；与 `IsoBmffGainMapProbe.ParseIloc:363-398` 的解析口径一致（旧版踩过「当两个独立字节」的坑，此处正确）。
- **32 位长度溢出**：`:150-154` 有 2 GiB 闸；`mdat` size = `8 + 数据`，在闸内必 `< int.MaxValue`；`Box():599-606` 的 `(uint)buf.Length` 不会溢出。**未发现溢出路径。**
- **Temporal Delimiter 前置的必要性（实测证实）**：ffmpeg 自产 AVIF 的 `av01` item 首字节确为 `0x0a`（OBU_SEQUENCE_HEADER，`obu_type=1`），无 TD；把该 item 直接喂 `ffmpeg -f obu` 得到原文报错 `Missing Temporal Delimiter.` / `No sequence header available`；前置 `12 00`（`:139-140`）后 `-f obu` 解码 rc=0。`:132-140` 的注释与实现**与实测完全一致**。
- **产物可用性（实测）**：组装结果 `ffprobe` 报 2 条 av1 流（64x48 主图 + 16x12 增益图），主图 `-f null` 解码 rc=0；`tmap` 载荷 62 B（=1+21+40×1 通道），与 `IsoBmffGainMapProbe:36` 记载的布局一致。
- **ffmpeg 不采纳 `-color_primaries/-color_trc` 的说法（实测证实）**：按 `GainMapEncoder.cs:573` 传参造出的底图，`colr` 仍是 `prim=2 trc=2`（unspecified），`matrix` 倒是按要求写了（1 / 9）。`:91-93` 与 `:249-252` 的注释属实，`PatchColr` 确有必要。

### 2. 缓冲区 / 截断 —— 已核，未发现

- `TrySliceItem:529-541`：先算 `total` 并闸掉 `<=0` 与 `> int.MaxValue`（`:531`），再 `new byte[total]`；逐 extent 校验 `abs + l > d.Length`（`:537`）后才 `Array.Copy`。**长度经过核算，无越界拷贝。**
- 本写出器**不产生** EXIF/XMP/ICC/base64 等变长载荷，也不做字符串编码长度计算（`Ascii:629-634` 只用于 4CC 固定 4 字节）；ISO 21496-1 载荷由 `GainMapEncoder.BuildIso21496Metadata` 按字节数拼装（定长 u32 分数），**不存在「按字符数而非字节数」的错误**。
- **无「写一半」路径**：`Build` 全程只操作内存缓冲，最后一次性 `Array.Copy`（`:165-169`）；任何一步失败都返回 `FailReason` 且 `Data == null`（`Ok => Data != null`，`:51`），不会吐出半成品。**磁盘上的半截文件风险见 M2，且只发生在调用点的写盘步。**

### 3. 资源与流 —— 已核，未发现泄漏

- 本批代码里没有 `FileStream`；`ThumbnailService` 全程用外部进程（ffmpeg / exiftool），不持有文件句柄。
- 临时缩略图 `thumbPath`（`ThumbnailService.cs:124`）在 `finally:161-164` 里删除，**异常路径也覆盖**；删除前没有任何句柄打开它；`File.Exists` 判定 + `catch {}` 吞掉删除异常，符合同仓 `PngCicpService`/`IccProfileService` 的既有口径。
- `EncodeAvifFileAsync`（`GainMapEncoder.cs:636-664`）用 `using var p`；`RedirectStandardOutput/Error` + `BeginOutputReadLine` + `WaitForExitAsync` ⇒ 不会因管道满而死锁。
- 未 `using` 的三处 `MemoryStream`/`BinaryWriter` 见 L6，属风格问题，**无资源泄漏**。
- 「先写临时文件再原子替换」—— **本批没有做**，见 M2（这不是泄漏问题，单列在中等等级）。

### 4. 异常与返回值谎报 —— 大部分已核无问题，两处谎报已列中等

- `IsoBmffGainMapWriter.Build:174-178` 的 catch 把异常转成 `FailReason` 且 `Data` 保持 null ⇒ **不谎报成功**。
- `TryExtractSingle`（`:332-393`）、`TrySliceItem`（`:491-546`）、`ParseIinfTypes`、`ParseIpmaMap`、`ParseProps` 全部返回「失败 + 点名原因」；`Build` 逐层上抛原因（`:88-89`、`:379-383`）。**未发现吞异常后返回成功。**
- **null 输入不会被当成空数据静默接受**：`:84-86` 对 `baseAvif/gainmapAvif/tmapPayload` 逐个判 `null or Length==0` 并返回点名原因，不进后续流程。✅（唯一缺口是 `BuildTmapPayload`，见 L1。）
- `EncodeAvifFileAsync:663` 的成功判据含 `p.ExitCode == 0` ⇒ 实测 gray 失败（rc=183）时正确返回 false；即使 ffmpeg 在失败时仍留下 283 B 的残缺文件（item 长度为 0），`File.Exists && Length>0` 也**不会**把它误判成成功，因为 rc 先被检查。**未发现谎报。**
- 谎报点只有两处：`ThumbnailService` 的成功判据（M3）与 `JpegCarriesGainMap` 的 fail-open（M4）。

### 5. ThumbnailService —— 已核，逻辑正确

- **长边钳制**：`ClampLongEdge:38-39` = `Min(512, Max(16, requested<=0 ? 160 : requested))` ⇒ 0/负数→160、1→16、9999→512。**已核正确**（与 `tests/ServiceProbe/Program.cs:263-265` 的断言一致）。
- **只缩不放 + 偶数化**：`BuildScaleFilter:56-60` 用 `min(iw,S)` / `-2`。实测：64x48 源在 S=160 下**保持 64x48（未放大）**；7x5 源 → 6x4（偶数化向下取整，见 L13）。**未发现放大。**
- **8-bit 降位**：命令行未指定 `-pix_fmt`（`:61-68`），mjpeg 编码器默认 8-bit ⇒ 缩略图恒 8-bit JPEG；这一「已知局限」在类文档里已写明且不参与色彩裁决（`:25-28`）。**与文档一致。**
- **tmp 清理**：见上文第 3 条 ✅。
- **与 `RestoreMetadataAsync` 的时序**：`QueueProcessor.cs:940` `RestoreMetadataAsync` → `:944` `ApplyColorPostStepsAsync`，`RestoreMetadataAsync:1606-1613` 内部是 `RestoreMetadataCoreAsync` **再** `ThumbnailService.ApplyAsync` ⇒ **缩略图排在元数据恢复之后、色彩后置之前**，与文档主张的不变量一致 ✅。另核：后置链路里 `PngCicpService.TryInsertSbit/TryInsertCicp/TryStripChunksSupersededByCicp` 都是「逐 chunk 复制、只增删目标 chunk」，**保留 `eXIf`**，不会把刚嵌入的缩略图抹掉。
- **隐私优先**：`PrivacySkipReason:80-84` 覆盖 `StripAll` 与 `StripExifAll`，返回 -2 并出声 ✅。

### 6. 缓存 —— 线程安全已核无问题；有上限淘汰；key 基本归一

- **线程安全（重点核实项）**：`CjxlService.cs:211-212` 用的是 **`ConcurrentDictionary<string,bool>`，不是 `Dictionary`** ⇒ 队列并发处理时**不存在**「非线程安全 Dictionary 导致崩溃或数据损坏」的风险。✅ 这一点与你担心的形状相反，可以排除。
- **上限**：`GainMapCacheCap = 512`（`:213`），`:241` 满则整表 `Clear()` ⇒ **有界，不会无界增长**（非 LRU，但与「值只用于别重复读头」的注释自洽）。竞态下两个线程同时 `Clear()` 或 Clear 掉刚插入的项都只是少命中一次，无害。
- **key 归一**：`FileInfo.FullName`（绝对路径，相对/分隔符差异被 .NET 归一化）+ `StringComparer.OrdinalIgnoreCase`（大小写归一）⇒ 常见的「`C:\A\b.JPG` vs `c:\a\B.jpg`」会被判为同一个 key ✅。未覆盖的别名形状见 L11。
- **无失效判据**见 M5。

### 7. 越界与除零 —— 已核，未发现

- 宽高为 0：`ThumbnailService` 不自己做尺寸运算（交给 ffmpeg 的 scale 表达式）；`IsoBmffGainMapWriter` 的尺寸校验有 `>0` 前置（`:96`），见 L9。
- 缩放比为 0：`BuildScaleFilter` 无除法（`-2` 由 ffmpeg 计算）；`RescaleGainMap`/`downsample` 不在本次改动范围，未复核。
- 质量为 0/100/越界：`MapQualityToQscale:45-50` 先夹到 `[1,100]` 再线性映射，末端再夹 `[2,31]` ⇒ 0→31、100→2、999→2 ✅（与 `tests/ServiceProbe/Program.cs:251-258` 断言一致）。
- int 溢出：`TrySliceItem` 的 `total` 有闸（`:531`）；产物总长有 2 GiB 闸（`:150`）；`Concat:619-627` 的累加受前一闸保护 ✅。**未发现可用溢出处。**

---

## 五、建议的处置顺序

1. **S1**（full_range 谎报）—— 影响每一个 AVIF 增益图产物的主图色度解读，先修并补一条「底图 `colr` 的 range 与 ffmpeg 实际像素同源」的实测判据。
2. **M1**（单色增益图恒回退）—— 至少把「恒 3 平面」这一事实在日志/元数据里说清；若要声明 1 通道，需另找能产出 YUV400 AVIF 的路（本机 libaom 不行）。
3. **M2**（原子写）—— 改成 tmp + `File.Move` 覆盖。
4. **M3 / M4** —— 成功判据加回读闭合；探针失败改 fail-closed + 点名。
5. **M5 / L11** —— 缓存键补长度+`LastWriteTimeUtc` 作为失效判据；路径归一化可加 `Path.GetFullPath` 之外的 canonical 化（短名/链接）。
6. 其余轻微项按 L1–L14 排序处理，其中 **L2 / L4** 是唯二可能写出**结构错误**文件的位置（虽然触发概率低），建议加显式失败而非静默钳制。
