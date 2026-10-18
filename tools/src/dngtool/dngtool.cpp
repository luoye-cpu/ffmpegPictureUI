// ═══════════════════════════════════════════════════════════
//  dngtool — DNG/RAW 解码 + DNG 编码 CLI
//  LibRaw + Adobe DNG SDK 1.7.1 (JXL-DNG 支持)
//
//  用法 (dcraw 兼容参数):
//    dngtool -d -T -o 0 -q 3 -W -H 1 -6 "input.dng" -O "output.tiff"
//    dngtool -info "input.raw"          → 输出 JSON 媒体信息
//    dngtool -engines                    → 列出可用引擎
//    dngtool -e -i input.cr2 -O out.dng [-lossless|-jxl -q 90]
//                                      → 编码 DNG (任意 RAW → DNG)
//
//  dcraw 兼容参数说明:
//    -4  16-bit 线性输出
//    -T  输出 TIFF
//    -o 0  原始相机色彩空间
//    -q 3  AHD 高质量去马赛克
//    -W  相机白平衡
//    -H 1 高光保留
//    -6  16-bit 位深
//    -O <path>  输出路径 (dcraw 无此参数, dngtool 扩展)
// ═══════════════════════════════════════════════════════════
#include <cstdio>
#include <cstring>
#include <ctime>
#include <string>
#include <vector>
#include "zlib.h"        // OriginalRawFileData 的 ZIP 分块 (规范 50828)
#include <algorithm>
#include <thread>
#include <atomic>
#include <queue>
#include <mutex>
#include <condition_variable>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

#include "libraw/libraw.h"

// DNG SDK 集成 (USE_DNGSDK 编译时启用)
#ifdef USE_DNGSDK
#include "dng_host.h"
#include "dng_negative.h"
#include "dng_exif.h"
#include "dng_info.h"
#include "dng_image.h"
#include "dng_camera_profile.h"
#include "dng_misc_opcodes.h"   // dng_opcode_MapTable + dng_area_spec (规范 51022 OpcodeList3)
#include "dng_lens_correction.h" // FixVignetteRadial(3)/WarpRectilinear(1)/WarpRectilinear2(14) + 参数类
#include <cmath>                 // pow()/fabs(): 镜头表到标准形式的拟合
#include "dng_preview.h"
#include "dng_date_time.h"
#include "dng_matrix.h"
#include "dng_simple_image.h"
#include "dng_file_stream.h"
#include "dng_image_writer.h"
#include "dng_memory_stream.h"
#include "dng_jpeg_image.h"
#include "dng_jxl.h"
#include "dng_tag_values.h"
#include "dng_utils.h"
#include "dng_area_task.h"
#endif

// ── 输入路径打开: 非 ASCII 路径必须走 wide 重载 ──
// LibRaw 的窄路径重载在 Windows 上是把 char 逐个零扩展成 wchar_t 再交给 CreateFileW
// (libraw_datastream.cpp: std::wstring fpath(fn.begin(), fn.end())), 只对 ASCII 成立
// ⇒ 中文名/带重音的 RAW 一律 LIBRAW_IO_ERROR。wide 重载在 MSVC 下由 libraw.h 自动启用
// (LIBRAW_WIN32_UNICODEPATHS), 走 CreateFileW(真 wide 串)。
static int OpenInputPath(LibRaw &processor, const char *path)
{
#ifdef _WIN32
    wchar_t wpath[4096];
    const int wcap = (int)(sizeof(wpath) / sizeof(wpath[0]));
    // argv 交到手里已经是窄串: 先按 UTF-8 解(本机 ACP=65001 时是它), 解不动再按 ANSI 码页解。
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wpath, wcap) > 0 ||
        MultiByteToWideChar(CP_ACP, 0, path, -1, wpath, wcap) > 0)
        return processor.open_file(wpath);
#endif
    return processor.open_file(path);
}

#ifdef USE_DNGSDK
// ── LibRaw 子类: 访问 protected 的 tiff_ifd (DNG 原始标签解析结果) ──
// tiff_ifd[].dng_color 包含 ColorMatrix1/2 + ForwardMatrix1/2 + CameraCalibration1/2
// (Adobe 官方 DNG 可 100% 无损保留); dng_levels 包含 BaselineExposure/AnalogBalance 等
class DngToolRaw : public LibRaw
{
public:
    tiff_ifd_t* Tiff() { return tiff_ifd; }
};

// ═══════════════════════════════════════════════════════════
//  DngToolHost — 多线程 dng_host 子类 (2026-08-15 性能优化)
//
//  问题: dng_host::PerformAreaTask 默认单线程执行 range 列表,
//        PerformAreaTaskThreads 默认 1 → libjxl 的 dng_jxl_parallel_runner
//        (内部用 dng_range_parallel_task::Do → Run → PerformAreaTask)
//        单线程顺序执行 → JXL tile 编码全部单线程。
//        实测 9504x6336 线性 DNG 无损 JXL (effort=7) 耗时 234s!
//
//  方案: 覆盖 PerformAreaTaskThreads() 返回硬件线程数 +
//        覆盖 PerformAreaTask() 用线程池并行执行 area 分块。
//
//  ⚠️ 重入保护: dng_range_parallel_task::Run 在"并行上下文"中会再次调用
//     PerformAreaTask (libjxl runner → Do → Run → PerformAreaTask 嵌套)。
//     用 thread_local 标记检测重入 → 重入时退回单线程顺序执行,
//     避免无限递归 + 线程池爆炸 (实测 0xC0000005, 2026-08-15)。
// ═══════════════════════════════════════════════════════════
class DngToolHost : public dng_host
{
public:
    explicit DngToolHost(uint32 threads = 0)
        : dng_host()
        , fThreads(threads > 0 ? threads
                               : std::max(1u, std::thread::hardware_concurrency()))
        , fWorkers(fThreads > 1 ? (fThreads - 1) : 0)
        , fPool(fWorkers > 0 ? new WorkerPool(fWorkers) : nullptr)
    {
    }

    ~DngToolHost() override { delete fPool; }

    /// 报告可用线程数 (libjxl runner 分块依据)
    uint32 PerformAreaTaskThreads() override { return fThreads; }

    /// 并行执行面积任务 (白名单: 仅并行线程安全的 task)
    /// ⚠️ 2026-08-15 实测: 通用并行在 dng_inplace_opcode_task (Opcode 应用)
    ///    崩溃 (0xC0000005, 内部共享状态)。白名单只并行纯数据/原子取任务类:
    ///   - dng_compressed_image_encode_task: JXL tile 编码 (atomic 自取 tile,
    ///     Adobe 专为并行设计) — 234s 瓶颈所在
    ///   - dng_copy_buffer_task / dng_get_buffer_task: 纯像素拷贝
    ///   其余 (线性化/Opcode/ICC 等) 顺序执行。
    void PerformAreaTask(dng_area_task& task,
                         const dng_rect& area,
                         dng_area_task_progress* progress = nullptr) override
    {
        const char* name = task.Name();
        bool canParallel =
            name != nullptr &&
            (strcmp(name, "dng_compressed_image_encode_task") == 0 ||
             strcmp(name, "dng_copy_buffer_task") == 0 ||
             strcmp(name, "dng_get_buffer_task") == 0);

        if (!canParallel || fThreads <= 1 || fPool == nullptr || area.H() < 2)
        {
            dng_host::PerformAreaTask(task, area, progress);
            return;
        }

        const int32 height = area.H();
        const uint32 chunks = std::min<uint32>(fThreads, (uint32)height);
        const int32 chunkH = std::max(1, height / (int32)chunks);

        dng_point tileSize(task.FindTileSize(area));

        std::vector<dng_rect> regions;
        regions.reserve(chunks);
        for (uint32 c = 0; c < chunks; c++)
        {
            int32 y0 = area.t + (int32)c * chunkH;
            int32 y1 = std::min(area.b, y0 + chunkH);
            if (y0 >= y1) break;
            regions.push_back(dng_rect(y0, area.l, y1, area.r));
        }
        if (regions.empty()) { dng_host::PerformAreaTask(task, area, progress); return; }

        std::atomic<int> next{ 0 };
        auto worker = [&](uint32 threadIndex)
        {
            while (true)
            {
                int idx = next.fetch_add(1);
                if (idx >= (int)regions.size()) break;
                task.ProcessOnThread(threadIndex, regions[idx], tileSize, nullptr, progress);
            }
        };

        if (fWorkers == 0) { worker(0); return; }

        std::atomic<uint32> done{ 0 };
        for (uint32 w = 0; w < fWorkers; w++)
        {
            fPool->Enqueue([&worker, &done]()
            {
                try { worker(1 + done.fetch_add(1)); }
                catch (...) { }
            });
        }
        worker(0);
        fPool->WaitAll();
    }

private:
    /// 简单固定线程池 (C++11 标准库, 无第三方依赖)
    class WorkerPool
    {
    public:
        explicit WorkerPool(uint32 count)
        {
            for (uint32 i = 0; i < count; i++)
                fThreads.emplace_back([this] { Loop(); });
        }

        ~WorkerPool()
        {
            {
                std::lock_guard<std::mutex> lock(fMutex);
                fStop = true;
            }
            fCv.notify_all();
            for (auto& t : fThreads)
                if (t.joinable()) t.join();
        }

        void Enqueue(std::function<void()> fn)
        {
            {
                std::lock_guard<std::mutex> lock(fMutex);
                fQueue.push(std::move(fn));
            }
            fCv.notify_one();
        }

        void WaitAll()
        {
            std::unique_lock<std::mutex> lock(fMutex);
            fCvDone.wait(lock, [this] { return fQueue.empty() && fActive == 0; });
        }

    private:
        void Loop()
        {
            while (true)
            {
                std::function<void()> fn;
                {
                    std::unique_lock<std::mutex> lock(fMutex);
                    fCv.wait(lock, [this] { return fStop || !fQueue.empty(); });
                    if (fStop && fQueue.empty()) return;
                    fn = std::move(fQueue.front());
                    fQueue.pop();
                    fActive++;
                }
                try { fn(); }
                catch (...) { }
                {
                    std::lock_guard<std::mutex> lock(fMutex);
                    fActive--;
                }
                fCvDone.notify_all();
            }
        }

        std::vector<std::thread> fThreads;
        std::queue<std::function<void()>> fQueue;
        std::mutex fMutex;
        std::condition_variable fCv;
        std::condition_variable fCvDone;
        bool fStop = false;
        uint32 fActive = 0;
    };

    uint32 fThreads;
    uint32 fWorkers;
    WorkerPool* fPool;
};
#endif

static void PrintUsage(const char* prog)
{
    printf("dngtool — DNG/RAW decoder + encoder (LibRaw + DNG SDK 1.7.1)\n");
    printf("Usage: %s [options]\n", prog);
    printf("  -d                decode mode\n");
    printf("  -info <file>      print media info as JSON\n");
    printf("  -idc <file>       dump the ACTIVE SonyIDC version of a Sony ARW, or of a\n");
    printf("                    DNG whose OriginalRawFileData holds one\n");
    printf("                    (JSON to stdout; tag ids only, no value interpretation)\n");
    printf("  -engines          list engines and exit\n");
    printf("  -i <file>         input file\n");
    printf("  -O <file>         output file\n");
    printf("  -o <n>            output color space (0=raw camera)\n");
    printf("  -q <n>            demosaic quality (3=AHD)\n");
    printf("  -W                use camera white balance\n");
    printf("  -H <n>            highlight mode\n");
    printf("  -4 / -6           bit depth (16)\n");
    printf("  -T                output TIFF\n");
    printf("  -v                verbose\n");
    printf("  --- encode mode ---\n");
    printf("  -e                encode DNG (any RAW -> DNG)\n");
    printf("  -lossless         lossless compression (default: lossless JPEG)\n");
    printf("  -jxl [-jxlq <n>]  JXL compression (DNG 1.7, jxlq=0 lossless / 1-100 lossy)\n");
    printf("                    (⚠ 是 -jxlq 不是 -q; -q 是解码侧的 demosaic 质量)\n");
    printf("  -effort <1-9>     JXL encode effort (higher = smaller, slower; default 7)\n");
    printf("  -decode_speed <1-4>  JXL decode speed hint (DNG tag; default 4)\n");
    printf("  -linear           write linear DNG (no CFA, for linear/demosaiced input)\n");
    printf("  -no-raw-meta      drop the source-RAW transport: no EXIF capture fields,\n");
    printf("                    no embedded original raw (OriginalRawFileData), no previews\n");
    printf("  -idc-look FILE    write FILE's curve into a standard DNG slot\n");
    printf("                    curve=x:y ...   -> main profile's ProfileToneCurve (50940)\n");
    printf("                    curveR/G/B=...  -> OpcodeList3 (51022) MapTable per plane\n");
    printf("                    (points ascending in [0,1]; the two carriers are mutually exclusive;\n");
    printf("                     optional policy=0|1|2; identity tables are skipped, not written)\n");
    printf("  -lens-corr        convert the source RAW's own lens tables into standard OpcodeList3\n");
    printf("                    (Sony image-SubIFD 0x7032/0x7035/0x7037 -> FixVignetteRadial +\n");
    printf("                     [WarpRectilinear2 optional] + [WarpRectilinear]; OFF by default\n");
    printf("                     because it changes rendered pixels. Absent / switched-off / identity\n");
    printf("                     tables are named on stderr as lens-skipped=... and written as nothing)\n");
}

// 引擎能力描述
static const char* GetJxlSupport()
{
#ifdef USE_DNGSDK
    return "yes";
#else
    return "no";
#endif
}

// ── 验证模式: 用 DNG SDK 严格解析 DNG 文件 (验证 JXL 压缩可读) ──
static int VerifyDng(const char* path, uint32 threadCount)
{
#ifdef USE_DNGSDK
    try
    {
        DngToolHost host(threadCount);
        dng_info info;
        AutoPtr<dng_stream> stream(new dng_file_stream(path, false));

        info.Parse(host, *stream);
        info.PostParse(host);

        if (!info.IsValidDNG())
        {
            fprintf(stderr, "[verify] NOT a valid DNG\n");
            return 1;
        }

        AutoPtr<dng_negative> negative(host.Make_dng_negative());

        negative->Parse(host, *stream, info);
        negative->PostParse(host, *stream, info);

        // 主图压缩方式: 优先压缩图像对象, 否则读**主图所在 IFD** 的 Compression。
        // ⚠ 不是 IFD0: 本机产物把 raw 图放在 SubIFD (实测 JXL 出口 SubIFD.Compression=52546
        // =ccJXL, 而 IFD0 恒是 7) ⇒ 读 fIFD[0] 会把 JXL 产物误报成无损 JPEG。
        // 取法与 SDK 自己一致 (dng_negative.cpp:3294 `*info.fIFD [info.fMainIndex]`)。
        const char* compName = "uncompressed/lossless JPEG";
        uint32 mainComp = 0;
        if (info.fMainIndex >= 0 && (uint32) info.fMainIndex < info.IFDCount () &&
            info.fIFD [(uint32) info.fMainIndex])
            mainComp = info.fIFD [(uint32) info.fMainIndex]->fCompression;
        if (negative->RawLossyCompressedImage())
        {
            uint32 code = negative->RawLossyCompressedImage()->fCompressionCode;
            compName = (code == ccJXL) ? "JPEG XL" : "other";
            printf("[verify] Compression: %s (mainIFD=%d comp=%u; JXL tags: distance=%.2f effort=%d decodespeed=%d)\n",
                   compName,
                   (int) info.fMainIndex, (unsigned) mainComp,
                   negative->RawLossyCompressedImage()->JXLDistance(),
                   negative->RawLossyCompressedImage()->JXLEffort(),
                   negative->RawLossyCompressedImage()->JXLDecodeSpeed());
        }
        else
        {
            if (mainComp == ccJXL)
                compName = "JPEG XL (lossless)";
            printf("[verify] Compression: %s (mainIFD=%d comp=%u)\n",
                   compName, (int) info.fMainIndex, (unsigned) mainComp);
        }

        // 完整解码主图 (解压 JXL tile)
        negative->ReadStage1Image(host, *stream, info);

        if (negative->Stage1Image())
        {
            const dng_image* img = negative->Stage1Image();
            printf("[verify] Image: %ux%u planes=%u pixelType=%s\n",
                   img->Width(), img->Height(), img->Planes(),
                   img->PixelType() == ttFloat ? "float" :
                   img->PixelType() == ttHalfFloat ? "half" :
                   img->PixelType() == ttShort ? "uint16" : "other");
            // 文案必须跟着实测的压缩方式走: 旧版无条件打印 "JXL 解码成功", 而这份产物
            // 是无损 JPEG ⇒ 那句话当时对 JPEG 出口也是"成功", 说的是自己。
            printf("[verify] Stage1 主图读回成功（压缩=%s）\n", compName);

            // ── 调试: 打印 Stage1 首行前 8 像素 (验证 3 平面数据完整性) ──
            dng_simple_image* simg = dynamic_cast<dng_simple_image*>(const_cast<dng_image*>(img));
            if (simg)
            {
                dng_pixel_buffer pb;
                simg->GetPixelBuffer(pb);
                if (pb.fData && pb.fPlanes >= 3 && img->PixelType() == ttShort)
                {
                    const ushort* p = (const ushort*)pb.fData;
                    printf("[verify] Stage1 row0: ");
                    for (int i = 0; i < 8; i++)
                        printf("(%u,%u,%u) ", p[i*3], p[i*3+1], p[i*3+2]);
                    printf("\n");
                }
            }
            return 0;
        }
        else
        {
            fprintf(stderr, "[verify] ❌ Stage1Image 为空 (解码失败)\n");
            return 2;
        }
    }
    catch (dng_exception& e)
    {
        fprintf(stderr, "[verify] ❌ DNG 解析失败 (error code %d)\n", (int)e.ErrorCode());
        return 3;
    }
    catch (...)
    {
        fprintf(stderr, "[verify] ❌ DNG 解析失败 (unknown)\n");
        return 3;
    }
#else
    fprintf(stderr, "[verify] compiled without DNG SDK\n");
    return 1;
#endif
}

// 输出 JSON 媒体信息
static int PrintInfo(const char* path)
{
    LibRaw processor;
    int ret = OpenInputPath(processor, path);
    if (ret != LIBRAW_SUCCESS)
    {
        printf("{\"error\":\"open failed: %s\",\"code\":%d}\n", libraw_strerror(ret), ret);
        return 1;
    }

    const libraw_image_sizes_t& sizes = processor.imgdata.sizes;
    const libraw_iparams_t& iparams = processor.imgdata.idata;
    const libraw_colordata_t& colordata = processor.imgdata.color;
    const libraw_imgother_t& other = processor.imgdata.other;

    printf("{\n");
    printf("  \"file\": \"%s\",\n", path);
    printf("  \"make\": \"%s\",\n", iparams.make);
    printf("  \"model\": \"%s\",\n", iparams.model);
    printf("  \"width\": %u,\n", sizes.width);
    printf("  \"height\": %u,\n", sizes.height);
    printf("  \"raw_width\": %u,\n", sizes.raw_width);
    printf("  \"raw_height\": %u,\n", sizes.raw_height);
    printf("  \"raw_count\": %d,\n", iparams.raw_count);
    printf("  \"filters\": %u,\n", iparams.filters);
    printf("  \"colors\": %d,\n", iparams.colors);
    printf("  \"cfa_pattern\": \"%s\",\n", iparams.cdesc);
    printf("  \"max_raw\": %u,\n", colordata.maximum);
    printf("  \"black\": %u,\n", colordata.black);
    printf("  \"iso\": %f,\n", other.iso_speed);
    printf("  \"shutter\": %f,\n", other.shutter);
    printf("  \"aperture\": %f,\n", other.aperture);
    printf("  \"focal_len\": %f,\n", other.focal_len);
    printf("  \"compression\": %d,\n", processor.imgdata.idata.raw_count > 0 ? 0 : 0);
    printf("  \"jxl_support\": \"%s\",\n", GetJxlSupport());
    printf("  \"dng_version\": %d.%d.%d.%d\n",
           (processor.imgdata.idata.dng_version >> 24) & 0xFF,
           (processor.imgdata.idata.dng_version >> 16) & 0xFF,
           (processor.imgdata.idata.dng_version >> 8) & 0xFF,
           processor.imgdata.idata.dng_version & 0xFF);
    printf("}\n");

    processor.recycle();
    return 0;
}

// 解码模式
static int Decode(
    const char* inputPath,
    const char* outputPath,
    int outputColor,
    int quality,
    int useCameraWb,
    int highlight,
    bool verbose,
    uint32 threadCount)   // 2026-08-15: 多线程
{
    LibRaw processor;

#ifdef USE_DNGSDK
    // DNG SDK host — 必须在使用 processor 之前创建并保持存活
    // 2026-08-15: 多线程 host (JXL 解码/去马赛克并行)
    DngToolHost host(threadCount);
    processor.set_dng_host(&host);
    // 使用全部 DNG SDK 能力 (float/linear/deflate/xtrans/other/8bit)
    processor.imgdata.rawparams.use_dngsdk = LIBRAW_DNG_ALL;
    // JXL 压缩 DNG 的 Opcode2/3 处理 (DNG 1.7)
    processor.imgdata.rawparams.options |= (1u << 27); // LIBRAW_RAWOPTIONS_DNG_STAGE23_IFPRESENT_JPGJXL
    // ⚠⚠ 2026-09-20：STAGE2 / STAGE3 **不再无条件启用**（原先无条件 ⇒ 改为 `unpack()` 后按需重试）。
    //   原因：它们会强制走 DNG SDK 的 BuildStage2Image（**去马赛克**）分支 ⇒ 对
    //   **保留 CFA 的 DNG**（我们自己用 `-e -lossless` 写的）会**误去马赛克**：
    //   实测 `unpack()` 后 `filters` 被清 0、数据改走 `color3_image`（3 平面）
    //   ⇒ 解码结果与源差 **100%~170%**（Fujifilm RAF → DNG 实测，见 RAW_PIPELINE_AUDIT §5.7）。
    //   但**线性 DNG / 自编码 JXL-DNG**（3 平面、无 OpcodeList）**确实需要**它们，
    //   否则 `dcraw_process` 输出 G/B=0（原注释记录的现象）。
    //   ⇒ 改为在 `unpack()` 后**按需重试**（见下方 gotCfa 判断）。
    // ⚠️ 必须启用 ALLOWSIZECHANGE (1<<14): 带 ActiveArea 的 DNG (如 Adobe 官方
    // 03_jxl_bayer_raw_integer.dng, ActiveArea 6376x9600 ≠ raw 7168x10240)
    // BuildStage2Image 裁剪到 ActiveArea → 尺寸不匹配 → 无此选项报 DATA_ERROR
    // → fallback placeholder → "Unsupported file format"。2026-08-14 修复。
    processor.imgdata.rawparams.options |= (1u << 14); // LIBRAW_RAWOPTIONS_DNG_ALLOWSIZECHANGE
#endif

    // dcraw 兼容参数
    processor.imgdata.params.output_bps = 16;    // -4 / -6
    processor.imgdata.params.output_color = outputColor;
    processor.imgdata.params.user_qual = quality;
    processor.imgdata.params.use_camera_wb = useCameraWb;
    processor.imgdata.params.highlight = highlight;
    processor.imgdata.params.no_auto_bright = 1; // 不做自动亮度
    processor.imgdata.params.output_tiff = 1;    // -T

    if (verbose) fprintf(stderr, "[dngtool] opening %s ...\n", inputPath);
    int ret = OpenInputPath(processor, inputPath);
    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool] ERROR: cannot open %s: %s\n",
                inputPath, libraw_strerror(ret));
        return 2;
    }

    // 检查 JXL 压缩 DNG
    if (processor.imgdata.idata.dng_version > 0 && verbose)
    {
        fprintf(stderr, "[dngtool] DNG version %d.%d.%d.%d\n",
                (processor.imgdata.idata.dng_version >> 24) & 0xFF,
                (processor.imgdata.idata.dng_version >> 16) & 0xFF,
                (processor.imgdata.idata.dng_version >> 8) & 0xFF,
                processor.imgdata.idata.dng_version & 0xFF);
    }

    ret = processor.unpack();

    // ⚠⚠ 2026-09-20：**按需启用 DNG SDK STAGE2/3**（原先无条件启用 ⇒ 会误去马赛克，见上方注释）。
    //   判据：`unpack()` 后是否拿到「**CFA 单平面**」（`raw_image` 非空 且 `filters != 0`）。
    //   · 是 ⇒ 这是**保留 CFA 的 DNG**（我们自己 `-e -lossless` 写的）⇒ **不需要** STAGE2/3 ✓
    //   · 否 ⇒ 是**线性/已去马赛克的 DNG**（自编码 JXL-DNG、官方 float/linear DNG）
    //          ⇒ 必须启用 STAGE2/3，否则 `dcraw_process` 输出 G/B=0 ⇒ 重开重试 ✓
    {
        const bool gotCfa = (ret == LIBRAW_SUCCESS)
                         && processor.imgdata.rawdata.raw_image != nullptr
                         && processor.imgdata.idata.filters != 0;
        if (!gotCfa && processor.imgdata.idata.dng_version > 0)
        {
            if (verbose)
                fprintf(stderr, "[dngtool] not a CFA DNG (filters=%u raw_image=%p) -> retry with DNG SDK STAGE2/3\n",
                        processor.imgdata.idata.filters, (void*)processor.imgdata.rawdata.raw_image);
            processor.recycle();
            processor.imgdata.rawparams.options |= (1u << 12); // LIBRAW_RAWOPTIONS_DNG_STAGE2
            processor.imgdata.rawparams.options |= (1u << 13); // LIBRAW_RAWOPTIONS_DNG_STAGE3
            ret = OpenInputPath(processor, inputPath);
            if (ret == LIBRAW_SUCCESS)
                ret = processor.unpack();
        }
    }

    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool] ERROR: unpack failed: %s\n", libraw_strerror(ret));
        processor.recycle();
        return 3;
    }

    if (verbose)
    {
        fprintf(stderr, "[dngtool] unpack OK: filters=%u colors=%d raw_count=%d\n",
                processor.imgdata.idata.filters,
                processor.imgdata.idata.colors,
                processor.imgdata.idata.raw_count);
        fprintf(stderr, "[dngtool] raw_image=%p color4_image=%p color3_image=%p float3_image=%p\n",
                (void*)processor.imgdata.rawdata.raw_image,
                (void*)processor.imgdata.rawdata.color4_image,
                (void*)processor.imgdata.rawdata.color3_image,
                (void*)processor.imgdata.rawdata.float3_image);
        fprintf(stderr, "[dngtool] warnings=0x%X (DNGSDK_PROCESSED=%s)\n",
                processor.imgdata.process_warnings,
                (processor.imgdata.process_warnings & LIBRAW_WARN_DNGSDK_PROCESSED) ? "YES" : "no");
        // ── 调试: 打印 color3_image / raw_alloc 首行前 8 像素 (linear 3 平面) ──
        if (processor.imgdata.rawdata.color3_image)
        {
            const ushort* p = (const ushort*)processor.imgdata.rawdata.color3_image;
            fprintf(stderr, "[dngtool] color3_image row0: ");
            for (int i = 0; i < 8; i++)
                fprintf(stderr, "(%u,%u,%u) ", p[i*3], p[i*3+1], p[i*3+2]);
            fprintf(stderr, "\n[dngtool] color3_image row1: ");
            const ushort* p1 = (const ushort*)((const char*)processor.imgdata.rawdata.color3_image + processor.imgdata.sizes.raw_pitch);
            for (int i = 0; i < 4; i++)
                fprintf(stderr, "(%u,%u,%u) ", p1[i*3], p1[i*3+1], p1[i*3+2]);
            fprintf(stderr, "\n");
        }
    }

    // （2026-09-20 临时调试代码已移除）

    if (verbose) fprintf(stderr, "[dngtool] processing ...\n");
    ret = processor.dcraw_process();
    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool] ERROR: dcraw_process failed: %s\n", libraw_strerror(ret));
        processor.recycle();
        return 4;
    }

    if (verbose) fprintf(stderr, "[dngtool] writing %s ...\n", outputPath);
    ret = processor.dcraw_ppm_tiff_writer(outputPath);
    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool] ERROR: write failed: %s\n", libraw_strerror(ret));
        processor.recycle();
        return 5;
    }

    if (verbose) fprintf(stderr, "[dngtool] done.\n");
    processor.recycle();
    return 0;
}

// ── 编码模式: 任意 RAW → DNG ──
// LibRaw 解码 raw 数据 → DNG SDK 构造 dng_negative → dng_image_writer 写 DNG
#ifdef USE_DNGSDK
// ════════════════════════════════════════════════════════════════════════
//  源 RAW 元数据保真 (2026-10-16 立, 2026-10-17 换成规范载体)
//
//  问题: ARW/SR2 把厂商注记、SR2 链 (含加密的 SR2SubIFD 里的镜头校正表)、
//  以及 Imaging Edge 写的 IDC 版本栈分散在像素数据的**前后两侧**;
//  DNG 没有承载这些语义的标签, LibRaw 也不解析 IDC。此前 -e 编码时一条不搬。
//
//  载体 = 规范 50828 OriginalRawFileData（整个原文件按 ZIP 分块嵌入）+ 50829
//  OriginalRawFileName + 50973 OriginalRawFileDigest。厂商数据以它自己的格式
//  原样在档, 任何 conformant 读者都能取出并交给厂商工具。
//  不自造容器: DNG 规范把 DNGPrivateData 定义为"相机厂商给自己的 raw 转换器
//  留的私有数据", 且要求块内偏移一律**相对该私有数据起点** —— 原文件的 TIFF
//  偏移相对的是原文件基址, 硬塞进去即违规, 且规范允许任何应用丢弃它。
//
//  下面这套 slab 只用于**读侧**: 把一个 RAW 的元数据切成两段, 原地不解指针,
//  给 IDC 解析与预览/缩略图定位当一个虚拟地址窗口用。
//     head = 文件 [0, pixelStart)   起点即原 TIFF 基址 ⇒ 段内所有偏移原地有效
//     tail = 文件 [rawEnd, fileLen) 记下其原始起点 tailOrigStart
//  读者解析原文件里的任意偏移 X:
//     X < headLen            → head[X]
//     X >= tailOrigStart     → headLen + (X - tailOrigStart)
//  两段之外没有任何数据被引用 (实测: 索尼的全部偏移要么落在像素之前, 要么
//  落在像素之后的尾部, 像素区本身不存元数据)。
//  读的是内存里的窗口, 写进产物的只有那份完整原件 ⇒ 产物里没有我方格式。
// ════════════════════════════════════════════════════════════════════════

struct RawMetaSlabs
{
    bool ok;
    uint64 fileSize, pixelStart, rawEnd;
    uint64 previewOff, previewLen;   // IFD0 的大预览 (可能是相机原图, 也可能已被 IDC 重写)
    uint64 thumbOff,   thumbLen;     // IFD1 的缩略图
    std::vector<uint8> head, tail;
    std::vector<std::pair<uint64, uint64> > jpegSlots;   // (起点, 长度) 全部 JPEG 段
    std::string reason;
    RawMetaSlabs() : ok(false), fileSize(0), pixelStart(0), rawEnd(0),
                     previewOff(0), previewLen(0), thumbOff(0), thumbLen(0) {}
};

namespace {

const uint64 kMetaHeadCap = 16ull * 1024 * 1024;   // 元数据区上界, 超了就不解析
const uint64 kMetaTailCap =  8ull * 1024 * 1024;

class TiffWalker
{
public:
    TiffWalker(const uint8* p, uint32 n) : f(p), fLen(n), fLE(true) {}

    // 填 pixelStart/rawEnd 与两段 JPEG 位置; 失败时 reason 说明原因
    void Run(RawMetaSlabs& out)
    {
        if (fLen < 8) { out.reason = "TIFF 头不足 8 字节"; return; }
        if (f[0] == 'I' && f[1] == 'I')      fLE = true;
        else if (f[0] == 'M' && f[1] == 'M') fLE = false;
        else { out.reason = "非 TIFF 字节序头"; return; }

        uint32 magic = U16(2);
        if (magic != 42) { out.reason = "仅支持经典 TIFF (magic=42), 实际 " +
                                        std::to_string(magic); return; }

        uint64 firstIfd = U32(4);
        bool found = false;
        // IFD0 → IFD1 → ... 链; 每个 IFD 再下钻 0x014a SubIFDs
        for (uint64 o = firstIfd, hop = 0; o && hop < 8; hop++)
        {
            WalkIFD(o, out, found, 0);
            uint64 nx = NextIFD(o, out);
            if (nx == o) break;
            o = nx;
        }
        if (!found) { out.reason = "未找到非 JPEG 的条带/瓦片像素跨度"; return; }

        // 挑档: 字节数最大的当预览, 剩下最小的当缩略图
        if (!jpegSlots.empty ())
        {
            size_t big = 0;
            for (size_t j = 1; j < jpegSlots.size (); j++)
                if (jpegSlots[j].second > jpegSlots[big].second) big = j;
            out.previewOff = jpegSlots[big].first;
            out.previewLen = jpegSlots[big].second;
            for (size_t j = 0; j < jpegSlots.size (); j++)
                if (j != big && (out.thumbLen == 0 || jpegSlots[j].second < out.thumbLen))
                { out.thumbOff = jpegSlots[j].first; out.thumbLen = jpegSlots[j].second; }
        }
    }

private:
    uint16 U16(uint64 o) const
    {
        if (o + 2 > fLen) return 0;
        return fLE ? (uint16)(f[o] | (f[o+1] << 8)) : (uint16)(f[o+1] | (f[o] << 8));
    }
    uint32 U32(uint64 o) const
    {
        if (o + 4 > fLen) return 0;
        return fLE ? (uint32(f[o]) | (uint32(f[o+1]) << 8) | (uint32(f[o+2]) << 16) | (uint32(f[o+3]) << 24))
                   : (uint32(f[o+3]) | (uint32(f[o+2]) << 8) | (uint32(f[o+1]) << 16) | (uint32(f[o]) << 24));
    }
    uint64 EntryEnd(uint64 e) const { return e + 12; }

    // 读一个 entry 的 (type,count,value/offset); 返回该 entry 的数据起始位置与内联标志
    bool Entry(uint64 e, uint16& tag, uint16& type, uint32& count, uint64& dataPos, bool& inline_) const
    {
        if (EntryEnd(e) > fLen) return false;
        tag = U16(e); type = U16(e + 2); count = U32(e + 4);
        static const uint32 sz[13] = {0,1,1,2,4,8,1,1,2,4,8,4,8};
        uint32 ts = (type < 13) ? sz[type] : 0;
        if (ts == 0) return false;
        uint64 total = (uint64)ts * count;
        inline_ = (total <= 4);
        dataPos = inline_ ? (e + 8) : (uint64)U32(e + 8);
        return true;
    }

    void WalkIFD(uint64 off, RawMetaSlabs& out, bool& found, int depth)
    {
        if (off + 2 > fLen || depth > 3) return;   // 深度上限: 畸形 SubIFD 链不得吃栈
        uint32 n = U16(off);
        uint64 jpegOff = 0, jpegLen = 0;
        uint32 compression = 0;
        uint64 stripOff = 0, stripCnt = 0, tileBase = 0;
        uint32 stripN = 0;
        std::vector<uint64> subIfds;

        for (uint32 i = 0; i < n; i++)
        {
            uint64 e = off + 2 + 12ull * i;
            uint16 tag, type; uint32 count; uint64 dp; bool inl;
            if (!Entry(e, tag, type, count, dp, inl)) continue;
            if (dp + 4 > fLen && !inl) continue;
            switch (tag)
            {
            case 0x0103: if (count == 1 && type == 3) compression = U16(dp); break;
            case 0x0111: case 0x0142:            // StripOffsets / TileOffsets
                if (tag == 0x0111 && compression == 6) break;   // JPEG 条带 = 预览, 不是像素
                if (type != 3 && type != 4) break;
                stripN = count;
                if (count == 1) { stripOff = inl ? U32(dp) : 0; if (!inl) continue; }
                else { if (dp + 8ull * count > fLen) continue; tileBase = dp; }
                break;
            case 0x0117: case 0x0143:            // StripByteCounts / TileByteCounts
                if (tag == 0x0117 && compression == 6) break;
                if (type != 3 && type != 4) break;
                if (count == 1 && inl) stripCnt = U32(dp);
                break;
            case 0x0201:                          // JPEGInterchangeFormat (预览/缩略图起点)
                if (count == 1 && inl) jpegOff = U32(dp);
                break;
            case 0x0202:                          // JPEGInterchangeFormatLength
                if (count == 1 && inl) jpegLen = U32(dp);
                break;
            case 0x014a:                          // SubIFDs
                // ⚠ 索尼 SubIFD 指针是 count=1 的内联 int32u: 值本身就是 IFD 偏移,
                //   不能再 U32(值) 读一次 (多解一层引用会把 27 当成 IFD 地址)。
                if (type == 4)
                    for (uint32 j = 0; j < count; j++)
                    {
                        uint64 val = U32(dp + 4ull * j);
                        if (val + 2 <= fLen) subIfds.push_back(val);
                    }
                break;
            default: break;
            }
        }

        // 像素跨度: 只在单条带时成立 (索尼 uncompressed RAW = 1 strip)。
        // 多_strip_/_tile_ 的输入本轮不搬 (reason 记账, 不做半吊子推断)。
        if (stripN == 1 && stripOff && stripCnt &&
            stripOff < out.fileSize && stripOff + stripCnt <= out.fileSize)
        {
            uint64 ps = stripOff, pe = stripOff + stripCnt;
            if (!found) { out.pixelStart = ps; out.rawEnd = pe; found = true; }
            else
            {
                if (ps < out.pixelStart) out.pixelStart = ps;
                if (pe > out.rawEnd)     out.rawEnd = pe;
            }
        }
        else if (stripN > 1)
        {
            if (out.reason.empty()) out.reason = "多条带/瓦片像素布局, 本轮不搬运";
        }

        if (compression == 6 && jpegOff && jpegLen && jpegOff + jpegLen <= out.fileSize)
        {
            // 记下来, 结束时按尺寸挑: 大的当预览, 小的当缩略图 (不依赖 IFD 顺序)
            bool dup = false;
            for (size_t j = 0; j < jpegSlots.size(); j++)
                if (jpegSlots[j].first == jpegOff) { dup = true; break; }
            if (!dup) jpegSlots.push_back(std::make_pair(jpegOff, jpegLen));
        }

        for (size_t j = 0; j < subIfds.size() && j < 8; j++)
            if (subIfds[j] != off) WalkIFD(subIfds[j], out, found, depth + 1);
    }

    uint64 NextIFD(uint64 off, const RawMetaSlabs& out)
    {
        (void)out;
        if (off + 2 > fLen) return 0;
        uint32 n = U16(off);
        uint64 p = off + 2 + 12ull * n;
        if (p + 4 > fLen) return 0;
        return U32(p);
    }

    const uint8* f;
    uint32 fLen;
    bool fLE;
    std::vector<std::pair<uint64, uint64> > jpegSlots;
};

bool ReadRange(const char* path, uint64 start, uint64 len, std::vector<uint8>& dst)
{
    FILE* fp = fopen(path, "rb");
    if (!fp) return false;
    bool got = false;
    fseek(fp, 0, SEEK_END);
#ifdef _WIN32
    long long sz = _ftelli64(fp);
#else
    long long sz = ftello(fp);
#endif
    if (sz > 0 && start + len <= (uint64)sz)
    {
#ifdef _WIN32
        if (_fseeki64(fp, (__int64)start, SEEK_SET) == 0)
#else
        if (fseeko(fp, (off_t)start, SEEK_SET) == 0)
#endif
        {
            dst.resize((size_t)len);
            got = len == 0 || fread(dst.data(), 1, (size_t)len, fp) == (size_t)len;
        }
    }
    fclose(fp);
    return got;
}

} // namespace

// 读输入文件的 TIFF 结构, 算出读侧两段窗口 (head/tail)。全程只读, 任一条件不
// 满足就返回 ok=false 并在 reason 里记因, 不做部分解析 (半截窗口比没有更误导)。
static void ReadRawMetaWindows(const char* inputPath, uint64 fileSize, RawMetaSlabs& out)
{
    out.ok = false;
    out.fileSize = fileSize;
    if (fileSize < 16) { out.reason = "文件过小"; return; }

    uint64 prefix = fileSize < kMetaHeadCap ? fileSize : kMetaHeadCap;
    std::vector<uint8> buf;
    if (!ReadRange(inputPath, 0, prefix, buf) || buf.size() < 8)
    { out.reason = "读不出头部区间"; return; }

    TiffWalker w(buf.data(), (uint32)buf.size());
    w.Run(out);
    if (!out.pixelStart || out.rawEnd <= out.pixelStart || out.rawEnd > fileSize)
    { if (out.reason.empty()) out.reason = "像素跨度不可定"; return; }
    if (out.pixelStart > kMetaHeadCap)
    { out.reason = "元数据区超过 16MB 上界"; return; }

    uint64 tailLen = fileSize - out.rawEnd;
    if (tailLen > kMetaTailCap) { out.reason = "尾部超过 8MB 上界"; return; }

    out.head.assign(buf.begin(), buf.begin() + (size_t)out.pixelStart);
    if (tailLen && !ReadRange(inputPath, out.rawEnd, tailLen, out.tail))
    { out.reason = "读不出尾部"; out.head.clear(); out.tail.clear(); return; }

    out.ok = true;
}

// ── 规范 50828 OriginalRawFileData: 原文件的规范载体 ─────────────────────────
// DNG_Spec_1_7_1_0.pdf 原文: "If the DNG file was converted from a non-DNG raw file,
// then this tag contains the compressed contents of that original raw file." ⇒ 想把
// 厂商私有数据留在标准 DNG 里, 规范给的就是这一条; 自造容器没有位置 (同一份规范对
// DNGPrivateData 写明是"给相机厂商自己的 raw 转换器用", 且私有数据内所有偏移必须
// **相对私有数据起点** —— 原文件里的 TIFF 偏移相对的是原文件基址, 搬进去即违规)。
//
// 结构 (整块大端) = 8 个数据块依序:
//   0 原文件 data fork   1 原文件 resource fork   2 mac file type   3 mac creator
//   4..7 同名 .THM 侧车的同样四块
// 每个 fork: ForkLength(u32, 解压后字节数; 0 = 本 fork 无后续数据)
//            + (ForkBlocks+1) 个 u32 索引 + 每 64K 一块的**独立 zlib 流**(ZIP)
// ⚠ 索引"从哪算起"规范文字有歧义, 由第二个独立实现裁决: exiftool 的 DNG.pm
//   ProcessOriginalRaw 取 hdrLen = 4*(n+2)、首块起点 = pos + Get32u(pos+4)
//   ⇒ 偏移量从 ForkLength 字段自己开始算。
static void PutBE32 (std::vector<uint8>& v, uint32 x)
{
    v.push_back ((uint8) (x >> 24)); v.push_back ((uint8) (x >> 16));
    v.push_back ((uint8) (x >> 8));  v.push_back ((uint8) x);
}

static uint32 GetBE32 (const uint8* p)
{
    return ((uint32) p [0] << 24) | ((uint32) p [1] << 16) | ((uint32) p [2] << 8) | (uint32) p [3];
}

static void PatchBE32 (std::vector<uint8>& v, size_t at, uint32 x)
{
    v [at] = (uint8) (x >> 24); v [at + 1] = (uint8) (x >> 16);
    v [at + 2] = (uint8) (x >> 8); v [at + 3] = (uint8) x;
}

// 把一个磁盘文件按规范编成"一个 fork"追加到 out; 返回解压后长度。
static bool EncodeZipFork (const char* path, std::vector<uint8>& out, std::string& why)
{
    FILE* fp = fopen (path, "rb");
    if (!fp) { why = "打不开原文件"; return false; }
    fseek (fp, 0, SEEK_END);
#ifdef _WIN32
    long long sz64 = _ftelli64 (fp);
#else
    long long sz64 = ftello (fp);
#endif
    fseek (fp, 0, SEEK_SET);
    if (sz64 <= 0) { fclose (fp); why = "原文件长度为 0"; return false; }
    uint64 sz = (uint64) sz64;
    if (sz > 0xFFFFFFF0ull / 3u) { fclose (fp); why = "原文件过大, 32 位长度放不下"; return false; }

    uint64 blocks = (sz + 65535u) / 65536u;
    size_t hdr = out.size ();
    PutBE32 (out, (uint32) sz);
    size_t idx = out.size ();
    for (uint64 k = 0; k <= blocks; k++) PutBE32 (out, 0);

    std::vector<uint8> chunk (65536);
    std::vector<uint8> comp (compressBound (65536));
    for (uint64 k = 0; k < blocks; k++)
    {
        size_t have = fread (chunk.data (), 1, chunk.size (), fp);
        if (have == 0)
            { fclose (fp); why = "读到的字节数少于声明长度"; return false; }
        uLongf clen = (uLongf) comp.size ();
        if (compress2 (comp.data (), &clen, chunk.data (), (uLong) have, 6) != Z_OK)
            { fclose (fp); why = "zlib compress2 失败"; return false; }
        PatchBE32 (out, idx + (size_t) k * 4u, (uint32) (out.size () - hdr));
        out.insert (out.end (), comp.data (), comp.data () + clen);
    }
    fclose (fp);
    PatchBE32 (out, idx + (size_t) blocks * 4u, (uint32) (out.size () - hdr));
    return true;
}

static AutoPtr<dng_memory_block> BuildOriginalRawFileData (dng_host& host, const char* path,
                                                           uint64& rawSize, std::string& why)
{
    std::vector<uint8> blob;
    if (!EncodeZipFork (path, blob, why)) return AutoPtr<dng_memory_block> ();
    rawSize = GetBE32 (&blob [0]);

    PutBE32 (blob, 0);                                   // 1 原文件 resource fork: 零长度
    PutBE32 (blob, 0); PutBE32 (blob, 0);                // 2/3 mac file type / creator: 未知存 0
    PutBE32 (blob, 0); PutBE32 (blob, 0);                // 4/5 .THM 的两个 fork: 无侧车
    PutBE32 (blob, 0); PutBE32 (blob, 0);                // 6/7 .THM 的 type / creator

    if (blob.size () > 0xFFFFFF00ull) { why = "编码后超过 32 位上限"; return AutoPtr<dng_memory_block> (); }
    AutoPtr<dng_memory_block> blk (host.Allocate ((uint32) blob.size ()));
    if (!blk.Get ()) { why = "内存分配失败"; return AutoPtr<dng_memory_block> (); }
    memcpy (blk->Buffer (), blob.data (), blob.size ());
    return AutoPtr<dng_memory_block> (blk.Release ());   // AutoPtr 不可复制 ⇒ 交 prvalue
}

// 反向: 从 DNG 的 50828 里把原文件解出来 (只解 fork 0 = 数据 fork; 其余三支是
// 规范给 Mac 资源分支留的 type/creator 位, 索尼 RAW 恒为 0)。
static bool DecodeZipFork0 (const uint8* p, size_t len, std::vector<uint8>& out, std::string& why)
{
    if (len < 8) { why = "OriginalRawFileData 过短"; return false; }
    uint32 raw = GetBE32 (p);
    if (!raw) { why = "fork 0 的 ForkLength=0 (没嵌原文件)"; return false; }
    uint64 blocks = (raw + 65535u) / 65536u;
    size_t hdr = 4u + 4u * (size_t) (blocks + 1u);
    if (len < hdr) { why = "索引不完整"; return false; }
    out.clear ();
    out.reserve (raw);
    for (uint64 k = 0; k < blocks; k++)
    {
        size_t b0 = GetBE32 (p + 4u + (size_t) k * 4u);
        size_t b1 = (k + 1u == blocks) ? GetBE32 (p + 4u + (size_t) blocks * 4u)
                                       : GetBE32 (p + 4u + (size_t) (k + 1u) * 4u);
        if (b0 < hdr || b1 <= b0 || b1 > len) { why = "块偏移越界"; return false; }
        std::vector<uint8> buf (65536);
        z_stream zs;
        memset (&zs, 0, sizeof (zs));
        if (inflateInit (&zs) != Z_OK) { why = "inflateInit 失败"; return false; }
        zs.next_in  = (Bytef *) (p + b0);
        zs.avail_in = (uInt) (b1 - b0);
        for (;;)
            {
            zs.next_out  = buf.data ();
            zs.avail_out = (uInt) buf.size ();
            int st = inflate (&zs, Z_NO_FLUSH);
            size_t got = buf.size () - zs.avail_out;
            out.insert (out.end (), buf.data (), buf.data () + got);
            if (st == Z_STREAM_END) break;
            if (st != Z_OK) { inflateEnd (&zs); why = "inflate 失败"; return false; }
            if (zs.avail_in == 0) { inflateEnd (&zs); why = "压缩数据在流结束前耗尽"; return false; }
            }
        inflateEnd (&zs);
    }
    if ((uint32) out.size () != raw) { why = "解出长度与 ForkLength 不符"; return false; }
    return true;
}

// 现成 JPEG 的关键参数 (宽高/分量/采样因子上界)
// ── 索尼 IDC 版本栈只读解析 (`-idc`) ────────────────────────────────
// 输入既可以是源 ARW, 也可以是本工具产出的 DNG —— 后者把规范 50828 里的原文件解回
// 内存再走同一条链, 于是"编辑值能否从产物恢复"是**规范载体 + 索尼自己的字节**上的事实。
// 刻意**不**抄 exiftool 的标签名表: 这里只出 tag/type/count/值, 命名由调用方与
// exiftool 的读数对齐 —— 避免本仓出现第二份"索尼语义真值"抄件。
namespace {

struct IdcEntry
{
    uint32 tag;
    uint32 type;
    uint32 count;
    std::vector<int64> vals;
    std::string text;      // type 2 (ASCII): 给字符串, 不要把每个字节当数印出来
    bool     rational;     // type 5/10: vals 是 [num, den, num, den, ...] 交替
    IdcEntry () : tag (0), type (0), count (0), rational (false) {}
};

struct IdcChange
{
    uint32 tag;
    uint32 type;
    std::vector<int64> from, to;   // 数值面
    std::string fromText, toText;  // type 2
};

struct IdcInfo
{
    bool        ok;
    std::string why;                 // 失败原因, 不静默
    std::string origin;              // "arw" | "dng-embedded-original"
    uint64      sr2Dir, idcDir;
    int32       currentVersion;
    uint32      versionCount;
    uint32      chosenEntries;
    std::vector<uint64> dirOffsets;      // 各版本目录在"原文件坐标"下的地址 (读者自证定位用)
    uint64      chosenOffset;            // 生效版本目录自己的地址
    std::vector<IdcEntry> entries;
    std::vector<IdcEntry> initial;   // hop 0 = 机身写下的初始版本
    bool        initialIsChosen;     // 生效版本就是初始版本 ⇒ 无从判"改动"
    std::vector<IdcChange> changes;  // 生效版本相对初始版本被改动的项
    IdcInfo () : ok (false), sr2Dir (0), idcDir (0), currentVersion (-1),
                 versionCount (0), chosenEntries (0), chosenOffset (0), initialIsChosen (false) {}
};

// slab 寻址: head 覆盖 [0, pixelStart), tail 覆盖 [rawEnd, EOF) —— 与封存时同一定义。
static bool SlabRead (const RawMetaSlabs& sl, uint64 pos, uint32 n, const uint8** out)
{
    if (pos + n < pos) return false;
    if (pos + n <= sl.head.size ())
        { *out = sl.head.data () + pos; return true; }
    if (pos >= sl.rawEnd && pos + n <= sl.rawEnd + sl.tail.size ())
        { *out = sl.tail.data () + (pos - sl.rawEnd); return true; }
    return false;                    // 跨 slab 边界的引用本工具不产生
}

static bool SlabU16 (const RawMetaSlabs& sl, uint64 pos, uint32& v)
{
    const uint8* p = NULL;
    if (!SlabRead (sl, pos, 2, &p)) return false;
    v = (uint32) p [0] | ((uint32) p [1] << 8);
    return true;
}

static bool SlabU32 (const RawMetaSlabs& sl, uint64 pos, uint64& v)
{
    const uint8* p = NULL;
    if (!SlabRead (sl, pos, 4, &p)) return false;
    v = (uint64) p [0] | ((uint64) p [1] << 8) | ((uint64) p [2] << 16) | ((uint64) p [3] << 24);
    return true;
}

// 读一个 IFD 目录的条目 (含非内联数据的解引用); 返回条目数, 并给出"下一目录"指针。
// relBase != 0 时, 非内联数据的偏移按该基址解释 —— 索尼 IDC 链就是这么存的
// (实测: IDC2 @0x2F48000 的 0xd001 值 = 0x2A, 数据落在 0x2F4802A; 两个版本目录
//  在 base+0x32 与 base+0x348, 各自 u16 都是 58 条目)。IFD0/SR2 层仍是 TIFF 绝对址。
static uint32 ReadDir (const RawMetaSlabs& sl, uint64 dir, std::vector<IdcEntry>& out,
                       uint64& next, uint32 limit = 400, uint64 relBase = 0)
{
    out.clear (); next = 0;
    uint32 n = 0;
    if (!SlabU16 (sl, dir, n) || n > limit) return 0;
    static const uint32 szOf [13] = {0,1,1,2,4,8,1,1,2,4,8,4,8};

    for (uint32 i = 0; i < n; i++)
    {
        uint64 e = dir + 2 + 12ull * i;
        IdcEntry en; uint32 tag = 0, type = 0;
        if (!SlabU16 (sl, e, tag)) break;
        if (!SlabU16 (sl, e + 2, type)) break;
        uint64 cnt = 0;
        if (!SlabU32 (sl, e + 4, cnt)) break;
        en.tag = tag; en.type = type; en.count = (uint32) cnt;
        en.rational = (type == 5 || type == 10);
        out.push_back (en);

        uint32 ts = (type < 13) ? szOf [type] : 0;
        if (ts == 0 || cnt == 0) continue;
        uint64 total = (uint64) ts * cnt;
        uint64 base = (total <= 4) ? (e + 8) : 0;
        if (total > 4 && !SlabU32 (sl, e + 8, base)) continue;
        if (total > 4 && relBase) base += relBase;

        uint32 take = cnt > 64 ? 64 : (uint32) cnt;     // 值上限只影响打印, 不影响结构
        std::vector<int64>& vv = out.back ().vals;
        for (uint32 j = 0; j < take; j++)
        {
            const uint8* p = NULL; int64 v = 0;
            if (!SlabRead (sl, base + (uint64) ts * j, ts, &p)) break;
            switch (type)
            {
            case 1: case 6: case 7:
                // BYTE[4] 常是"小端塞一个 uint32"(IFD0 的 0xC634 就是), 与 exiftool 的
                // "int8u[4] read as int32u[1]" 同口径: 整体给一个值, 不是 4 个字节
                if (type == 1 && cnt == 4 && j == 0)
                {
                    // 4 字节就是一个小端 uint32 (IFD0 的 0xC634): 合成一个值后收尾,
                    // 不再逐字节追加 —— 与 exiftool "int8u[4] read as int32u[1]" 同口径
                    vv.push_back((int64)(uint32)(p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24)));
                    take = 1;
                    continue;
                }
                v = p [0]; break;
            case 2:
                if (p [0]) out.back ().text += (char) p [0];
                continue;                       // 字符串已收进 text, 不进 vals
            case 3:  v = (int64)(p [0] | (p [1] << 8)); break;
            case 8:  v = (int64)(int16)(p [0] | (p [1] << 8)); break;
            case 4:  v = (int64)(uint32)(p [0] | (p [1] << 8) | (p [2] << 16) | (p [3] << 24)); break;
            case 9:  v = (int64)(int32)(p [0] | (p [1] << 8) | (p [2] << 16) | (p [3] << 24)); break;
            case 5: case 10:
                {
                    // 分母不能丢: 曾经按 num/den 取整, 于是 2/3 印成 0、
                    // 0.67 那档印成 200 —— 一个看着像读数的错值比缺值更坏。
                    uint64 num = 0, den = 0;
                    SlabU32 (sl, base + (uint64) ts * j, num);
                    SlabU32 (sl, base + (uint64) ts * j + 4, den);
                    vv.push_back ((int64) num);
                    vv.push_back ((int64) den);
                    continue;                   // 该元素已整体消费, 别再 push 兜底的 0
                }
            default: v = 0; break;
            }
            vv.push_back (v);
        }
    }
    SlabU32 (sl, dir + 2 + 12ull * n, next);
    // 版本链的"下一目录"指针同样是 IDC2 相对址 (实测 Version0 的该字段 = 0x348)
    if (next && relBase) next += relBase;
    return (uint32) out.size ();
}

static bool DirFind (const std::vector<IdcEntry>& dir, uint32 tag, IdcEntry& hit)
{
    for (size_t i = 0; i < dir.size (); i++)
        if (dir [i].tag == tag) { hit = dir [i]; return true; }
    return false;
}

// 生效版本 vs 初始版本的差异: 只比"两版都有同 tag"的项。
// 刻意只出 tag id 不出名字 —— 本仓不留第二份索尼语义表 (`-idc` 段开头那条注释定的规矩)。
// 排除 0x0201/0x0202 (TIFF 标准的 JPEGInterchangeFormat/Length): 版本目录里的这两个字段
// 是**相对各自目录**的偏移, 两版基址不同 ⇒ 直接比原始值会把"我的 diff"变成"基址之差"。
// exiftool 正是栽在这上面: 它按 IsOffset 解析后报 Version0=49578034 / Version1=49578824,
// 而文件里两处存的都是 00 00 00 00 (那两个数恰好就是两个版本目录自己的地址)。
static void DiffIdcVersions (const std::vector<IdcEntry>& base,
                             const std::vector<IdcEntry>& cur,
                             std::vector<IdcChange>& out)
{
    for (size_t i = 0; i < cur.size (); i++)
    {
        const IdcEntry& c = cur [i];
        if (c.tag == 0x0201 || c.tag == 0x0202) continue;
        const IdcEntry* b = NULL;
        for (size_t j = 0; j < base.size (); j++)
            if (base [j].tag == c.tag) { b = &base [j]; break; }
        if (!b) continue;
        bool diff = (c.type == 2) ? (b->text != c.text) : (b->vals != c.vals);
        if (!diff) continue;
        IdcChange ch;
        ch.tag = c.tag;
        ch.type = c.type;
        ch.from = b->vals;
        ch.to   = c.vals;
        ch.fromText = b->text;
        ch.toText   = c.text;
        out.push_back (ch);
    }
}

static void ParseIdc (const RawMetaSlabs& sl, const std::string& origin, IdcInfo& out)
{
    out.origin = origin;
    uint64 ifd0 = 0;
    if (!SlabU32 (sl, 4, ifd0)) { out.why = "读不到首个 IFD 偏移"; return; }

    std::vector<IdcEntry> root; uint64 next = 0;
    if (!ReadDir (sl, ifd0, root, next) || root.empty ())
        { out.why = "IFD0 解析失败"; return; }

    // 索尼把 SR2 链挂在 IFD0 的 0xC634 上 (这个号在 DNG 里正是 DNGPrivateData)
    IdcEntry sr2;
    if (!DirFind (root, 0xC634, sr2) || sr2.vals.empty ())
        { out.why = "无 0xC634 SR2Private 入口"; return; }
    out.sr2Dir = (uint64) sr2.vals [0];

    std::vector<IdcEntry> sr2Dir; uint64 s2 = 0;
    if (!ReadDir (sl, out.sr2Dir, sr2Dir, s2) || sr2Dir.empty ())
        { out.why = "SR2 目录解析失败"; return; }

    IdcEntry idc; uint64 idcPtr = 0;
    if (DirFind (sr2Dir, 0x7241, idc) && !idc.vals.empty () && idc.vals [0])
        idcPtr = (uint64) idc.vals [0];
    else if (DirFind (sr2Dir, 0x7240, idc) && !idc.vals.empty () && idc.vals [0])
        idcPtr = (uint64) idc.vals [0];
    else { out.why = "SR2 目录内没有 IDC_IFD/IDC2_IFD"; return; }
    out.idcDir = idcPtr;

    std::vector<IdcEntry> stack; uint64 vnext = 0;
    if (!ReadDir (sl, idcPtr, stack, vnext, 400, idcPtr) || stack.empty ())
        { out.why = "IDC 版本栈目录解析失败"; return; }

    IdcEntry cur, ver;
    if (DirFind (stack, 0xd000, cur) && !cur.vals.empty ())
        out.currentVersion = (int32) cur.vals [0];
    else { out.why = "无 0xd000 CurrentVersion"; return; }
    if (!DirFind (stack, 0xd001, ver) || ver.vals.empty ())
        { out.why = "无 0xd001 VersionIFD"; return; }

    // 版本目录的定位 (本机实测, 两处都推翻了一眼看上去合理的猜法):
    //   · IDC 链里**每个目录的非内联偏移都相对该目录自己** —— 不是 TIFF 绝对址,
    //     也不是统一的 IDC2 基址 (两个版本目录的 0x8021 若按 IDC2 基址解释会撞到
    //     同一个位置, 读出的是别的结构字节 ⇒ 必错)。
    //   · 0xd001 是 int32u[**2**]: 两个版本目录的地址各占一格, 且它们的 next 字段
    //     都是 0 ⇒ 没有"下一 IFD"链可跟。所以先按数组取, 再用 next 兜底补 (不同机身
    //     两种写法都出现过), 两者去重。
    std::vector<uint64> dirs;
    for (size_t j = 0; j < ver.vals.size (); j++)
    {
        if (ver.vals [j] <= 0) continue;
        uint64 d = idcPtr + (uint64) ver.vals [j];
        if (find (dirs.begin (), dirs.end (), d) == dirs.end ()) dirs.push_back (d);
    }
    if (dirs.empty ()) { out.why = "0xd001 里没有可用的版本目录地址"; return; }

    for (size_t guard = 0; guard < 24 && guard < dirs.size (); guard++)
    {
        std::vector<IdcEntry> probe; uint64 nx = 0;
        uint64 d = dirs [guard];
        if (!ReadDir (sl, d, probe, nx, 400, d)) continue;         // 不是有效目录: 留着无害, 选择时再判
        if (nx && nx != d && find (dirs.begin (), dirs.end (), nx) == dirs.end ())
            dirs.push_back (nx);                                    // 有链就继续长
    }

    int want = out.currentVersion < 0 ? 0 : (int) out.currentVersion;
    if (want >= (int) dirs.size ())
    {
        out.why = "CurrentVersion=" + std::to_string ((int) out.currentVersion) +
                  " 超出链上的 " + std::to_string ((int) dirs.size ()) + " 个版本目录";
        return;
    }
    for (uint32 hop = 0; hop < (uint32) dirs.size (); hop++)
    {
        std::vector<IdcEntry> e; uint64 nx = 0;
        uint32 n = ReadDir (sl, dirs [hop], e, nx, 400, dirs [hop]);
        if (!n) continue;
        out.versionCount++;
        out.dirOffsets.push_back (dirs [hop]);
        if (hop == 0) out.initial = e;
        if ((int) hop == want)
            { out.entries = e; out.chosenEntries = n; out.initialIsChosen = (want == 0);
              out.chosenOffset = dirs [hop]; }
    }
    if (out.entries.empty ())
    {
        out.why = "CurrentVersion=" + std::to_string ((int) out.currentVersion) +
                  " 但链上只有 " + std::to_string ((int) out.versionCount) + " 个版本目录";
        return;
    }
    out.ok = true;
    if (!out.initialIsChosen && !out.initial.empty ())
        DiffIdcVersions (out.initial, out.entries, out.changes);
}

// 从产物的 50828 里取回原文件 ⇒ IDC 读的是**索尼自己的字节**, 不是任何自造结构。
static bool FindIfd0Tag (const std::vector<uint8>& f, uint32 want, uint32& ty, uint32& cnt, uint64& dataAt)
{
    if (f.size () < 8) return false;
    bool le = (f [0] == 'I' && f [1] == 'I');
    if (!le && !(f [0] == 'M' && f [1] == 'M')) return false;
    auto rd16 = [&] (size_t o) { return le ? (uint32) (f [o] | (f [o + 1] << 8))
                                           : (uint32) (f [o + 1] | (f [o] << 8)); };
    auto rd32 = [&] (size_t o) { return le ? (uint32) (f [o] | (f [o + 1] << 8) | ((uint32) f [o + 2] << 16) | ((uint32) f [o + 3] << 24))
                                           : (uint32) (((uint32) f [o] << 24) | ((uint32) f [o + 1] << 16) |
                                                       ((uint32) f [o + 2] << 8) | (uint32) f [o + 3]); };
    if (rd16 (2) != 42) return false;                    // 只处理经典 TIFF (本工具产物都是)
    uint32 n = rd16 (rd32 (4));
    if (n < 1 || n > 600) return false;
    static const uint32 SZ [] = { 1, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 1 };
    for (uint32 j = 0; j < n; j++)
    {
        size_t e = rd32 (4) + 2 + 12 * (size_t) j;
        if (e + 12 > f.size ()) return false;
        uint32 tag = rd16 (e), t = rd16 (e + 2), c = rd32 (e + 4);
        if (tag != want) continue;
        ty = t; cnt = c;
        uint64 bytes = (uint64) c * (t < 14 ? SZ [t] : 1u);
        dataAt = (bytes <= 4u) ? (uint64) (e + 8) : (uint64) rd32 (e + 8);
        return dataAt + bytes <= f.size ();
    }
    return false;
}

static bool SlabsFromDngOriginal (const std::vector<uint8>& dng, RawMetaSlabs& sl, std::string& why)
{
    uint32 ty = 0, cnt = 0;
    uint64 at = 0;
    if (!FindIfd0Tag (dng, 0xC68C, ty, cnt, at))      // 50828 OriginalRawFileData
        { why = "没有 OriginalRawFileData(50828)"; return false; }
    if (at + cnt > dng.size ()) { why = "50828 越界"; return false; }
    std::vector<uint8> raw;
    if (!DecodeZipFork0 (&dng [at], (size_t) cnt, raw, why)) return false;

    // 从 50828 解回的就是完整原件 ⇒ 偏移天然原地有效, 不需要任何自造的寻址复现。
    sl.fileSize   = raw.size ();
    sl.pixelStart = raw.size ();
    sl.rawEnd     = raw.size ();
    sl.head       = raw;
    sl.tail.clear ();
    sl.ok = true;
    return true;
}

// 取一个 IFD0 标签的原始字节：先读头部 4 MB, IFD 在外面才整读一次。
static bool TagBytesFromPath (const char* path, uint32 want, std::vector<uint8>& out, std::string& why)
{
    FILE* fp = fopen (path, "rb");
    if (!fp) { why = "打不开输入"; return false; }
    fseek (fp, 0, SEEK_END);
#ifdef _WIN32
    long long sz64 = _ftelli64 (fp);
#else
    long long sz64 = ftello (fp);
#endif
    fseek (fp, 0, SEEK_SET);
    if (sz64 < 16) { fclose (fp); why = "输入文件过短"; return false; }
    uint64 sz = (uint64) sz64;
    size_t head = (size_t) (sz < (uint64) (4 << 20) ? sz : (uint64) (4 << 20));
    std::vector<uint8> buf (head);
    if (fread (buf.data (), 1, head, fp) != head) { fclose (fp); why = "读头部失败"; return false; }

    uint32 ty = 0, cnt = 0;
    uint64 at = 0;
    bool ok = FindIfd0Tag (buf, want, ty, cnt, at);
    if (!ok && (uint64) head < sz)
    {
        buf.resize ((size_t) sz);
        fseek (fp, 0, SEEK_SET);
        if (fread (buf.data (), 1, (size_t) sz, fp) == (size_t) sz)
            ok = FindIfd0Tag (buf, want, ty, cnt, at);
    }
    fclose (fp);
    if (!ok) { why = "输入里没有该标签"; return false; }

    static const uint32 SZT [] = { 1, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8, 1 };
    uint64 bytes = (uint64) cnt * (ty < 14 ? SZT [ty] : 1u);
    if (!bytes || bytes > 0xFFFFFFF0ull) { why = "标签长度不合理"; return false; }
    if (at + bytes <= buf.size ())
        { out.assign (buf.begin () + (long) at, buf.begin () + (long) (at + bytes)); return true; }
    out.assign ((size_t) bytes, 0);
    return ReadRange (path, at, (uint32) bytes, out);
}

static const char* BaseNameOf (const char* p)
{
    const char* base = p;
    for (const char* q = p; *q; q++) if (*q == '/' || *q == '\\') base = q + 1;
    return base;
}

} // namespace

static void PutJsonText (const char* s)
{
    putchar ('"');
    for (const char* p = s; *p; p++)
        { if (*p == '"' || *p == '\\') putchar ('\\'); putchar (*p); }
    putchar ('"');
}

static void PutJsonVals (const char* key, const std::vector<int64>& v)
{
    printf ("\"%s\":[", key);
    for (size_t j = 0; j < v.size (); j++)
        printf ("%s%lld", j ? "," : "", (long long) v [j]);
    printf ("]");
}

static int PrintIdcInfo (const char* path)
{
    std::vector<uint8> file;
    {
        FILE* fp = fopen (path, "rb");
        if (!fp) { fprintf (stderr, "[dngtool-idc] ERROR: 打不开 %s\n", path); return 1; }
        fseek (fp, 0, SEEK_END);
#ifdef _WIN32
        long long sz = _ftelli64 (fp);
#else
        long long sz = ftello (fp);
#endif
        fseek (fp, 0, SEEK_SET);
        file.resize ((size_t) sz);
        size_t got = (sz > 0) ? fread (file.data (), 1, (size_t) sz, fp) : 0;
        fclose (fp);
        if (got != (size_t) sz) { fprintf (stderr, "[dngtool-idc] ERROR: 读取不完整\n"); return 1; }
    }

    RawMetaSlabs sl;
    const char* origin = "dng-embedded-original";
    std::string whyNoOrig;
    if (!SlabsFromDngOriginal (file, sl, whyNoOrig))
    {
        origin = "arw";
        sl = RawMetaSlabs ();
        ReadRawMetaWindows (path, (uint64) file.size (), sl);
        if (!sl.ok)
        {
            fprintf (stderr, "[dngtool-idc] ERROR: 既没取回规范嵌入的原文件 50828 (%s), 也不是可解析的 "
                             "TIFF 基 RAW (%s)\n", whyNoOrig.c_str (), sl.reason.c_str ());
            return 1;
        }
    }

    IdcInfo info;
    ParseIdc (sl, origin, info);
    if (!info.ok)
    {
        fprintf (stderr, "[dngtool-idc] ERROR: %s\n", info.why.c_str ());
        return 1;
    }

    // changed = 生效版本相对初始版本(hop 0)被改动的项 —— 这就是"源到底被改了什么"的
    // 机检面, 不需要任何标定; 版本没分开时它是空的 (不是缺失)。
    printf ("{\"origin\":\"%s\",\"sr2Dir\":%llu,\"idcDir\":%llu,\"currentVersion\":%d,"
            "\"versionDirs\":%u,\"chosenEntries\":%u,\"chosenOffset\":%llu,\"versionOffsets\":[",
            info.origin.c_str (), (unsigned long long) info.sr2Dir,
            (unsigned long long) info.idcDir, (int) info.currentVersion,
            info.versionCount, info.chosenEntries, (unsigned long long) info.chosenOffset);
    for (size_t i = 0; i < info.dirOffsets.size (); i++)
        printf ("%s%llu", i ? "," : "", (unsigned long long) info.dirOffsets [i]);
    printf ("],\"changedCount\":%u,\"changed\":[", (unsigned) info.changes.size ());
    for (size_t i = 0; i < info.changes.size (); i++)
    {
        const IdcChange& c = info.changes [i];
        printf ("%s{\"tag\":\"0x%04X\",\"type\":%u,", i ? "," : "", c.tag, c.type);
        if (c.type == 2)
            { printf ("\"from\":"); PutJsonText (c.fromText.c_str ());
              printf (",\"to\":");  PutJsonText (c.toText.c_str ()); }
        else
            { PutJsonVals ("from", c.from); printf (","); PutJsonVals ("to", c.to); }
        printf ("}");
    }
    printf ("],\"entries\":[");
    for (size_t i = 0; i < info.entries.size (); i++)
    {
        const IdcEntry& e = info.entries [i];
        printf ("%s{\"tag\":\"0x%04X\",\"type\":%u,\"count\":%u",
                i ? "," : "", e.tag, e.type, e.count);
        if (e.type == 2)
        {
            putchar (','); putchar ('"'); printf ("text"); printf ("\""); putchar (':');
            putchar ('"');
            for (size_t k = 0; k < e.text.size (); k++)
            {
                char c = e.text [k];
                if (c == '"' || c == '\\') putchar ('\\');
                putchar (c);
            }
            printf ("\"}");
            continue;
        }
        if (e.rational) printf (",\"rational\":1");
        printf (",\"values\":[");
        for (size_t j = 0; j < e.vals.size (); j++)
            printf ("%s%lld", j ? "," : "", (long long) e.vals [j]);
        printf ("]}");
    }
    printf ("]}\n");
    return 0;
}

static bool JpegPeek(const std::vector<uint8>& j, uint32& w, uint32& h, uint32& comp,
                     uint32& hs, uint32& vs, uint32& prec)
{
    if (j.size() < 12 || j[0] != 0xFF || j[1] != 0xD8) return false;
    size_t i = 2;
    while (i + 4 <= j.size())
    {
        if (j[i] != 0xFF) { i++; continue; }
        uint8 marker = j[i + 1];
        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
        if (marker == 0xFF) { i++; continue; }
        uint32 seglen = (uint32(j[i + 2]) << 8) | j[i + 3];
        if (seglen < 2 || i + 2 + seglen > j.size()) break;
        size_t b = i + 4;                                   // 段体起始
        bool sof = (marker >= 0xC0 && marker <= 0xCF) &&
                   marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
        if (sof)
        {
            prec = j[b];                                    // 精度 (bit/sample)
            h = (uint32(j[b + 1]) << 8) | j[b + 2];
            w = (uint32(j[b + 3]) << 8) | j[b + 4];
            comp = j[b + 5];
            hs = vs = 1;
            if (comp >= 1) { hs = (j[b + 7] >> 4) & 0xF; vs = j[b + 7] & 0xF; }
            return w && h && comp && (comp == 1 || comp == 3) && prec == 8 && marker != 0xC2;
        }
        i += 2 + seglen;
    }
    return false;
}

// 把一段现成的 JPEG 字节作为 DNG preview 挂进 preview_list: 只借 dng_simple_image
// 传尺寸给 SetIFDInfo (它只读 Width/Height/Planes/PixelType), 随后 SetCompressedData
// 会把那份占位图像丢掉 ⇒ 不重新编码, 原字节入档。
static bool AppendJpegPreview(dng_host& host, dng_preview_list& list,
                              const std::vector<uint8>& jpeg, bool isPrimary, bool verbose)
{
    uint32 w = 0, h = 0, comp = 0, hs = 0, vs = 0, prec = 0;
    if (!JpegPeek(jpeg, w, h, comp, hs, vs, prec))
    {
        if (verbose) fprintf(stderr, "[dngtool-e] WARNING: JPEG 预览头解析失败, 跳过嵌入\n");
        return false;
    }

    AutoPtr<dng_jpeg_preview> prev(new dng_jpeg_preview);
    prev->fInfo.fIsPrimary = isPrimary;
    prev->fInfo.fRawToPreviewGain = 1.0;

    AutoPtr<dng_image> stub(new dng_simple_image(dng_rect(dng_point((int32)h, (int32)w)),
                                                 comp, ttByte));
    prev->SetIFDInfo(host, *stub.Get());
    if (comp == 3) prev->SetYCbCr(hs ? hs : 1, vs ? vs : 1);

    AutoPtr<dng_memory_block> data(new dng_malloc_block((uint32)jpeg.size()));
    memcpy(data->Buffer(), jpeg.data(), jpeg.size());
    prev->SetCompressedData(data);

    AutoPtr<dng_preview> p(prev.Release ());
    list.Append(p);
    if (verbose)
        fprintf(stderr, "[dngtool-e] %s 嵌入: %ux%u comp=%u samp=%u:%u %u B\n",
                isPrimary ? "预览" : "缩略图", w, h, comp, hs, vs, (unsigned)jpeg.size());
    return true;
}

// ── 拍摄字段搬运 ──
// 只搬 LibRaw 已经解析出来的值; 0 / 空串一律不写 (拿 0 冒充等于造一条错元数据)。
// 注意这里**不碰**白平衡与色彩宣称: 那是语义裁决, 属另一件事。
static void FillExifCapture(dng_exif* exif, const libraw_imgother_t& other,
                            const libraw_lensinfo_t& lens, bool verbose)
{
    int filled = 0;

    if (other.shutter > 0.0)
        { exif->SetExposureTime ((real64)other.shutter, false); filled++; }

    if (other.aperture > 0.0)
        { exif->SetFNumber ((real64)other.aperture); filled++; }

    if (lens.EXIF_MaxAp > 0.0)
        {
            // ⚠ MaxApertureValue 存的是 **APEX** (2*log2(f))，不是 f 值。
            //   直接把 f/1.8 写进去，读者按 APEX 解出的是 f/1.86 ⇒ 数值看着只差
            //   0.1，其实是口径错。用 SDK 的换算函数，别自己写 log2。
            exif->fMaxApertureValue.Set_real64 (dng_exif::FNumberToApertureValue ((real64)lens.EXIF_MaxAp));
            filled++;
        }

    if (other.iso_speed > 0.0)
        { exif->fISOSpeedRatings [0] = (uint32)(other.iso_speed + 0.5f); filled++; }

    if (other.focal_len > 0.0)
        { exif->fFocalLength.Set_real64 ((real64)other.focal_len); filled++; }

    if (lens.MinFocal > 0.0 && lens.MaxFocal > 0.0)
        {
            exif->fLensInfo [0].Set_real64 ((real64)lens.MinFocal);
            exif->fLensInfo [1].Set_real64 ((real64)lens.MaxFocal);
            exif->fLensInfo [2].Set_real64 ((real64)lens.MaxAp4MinFocal);
            exif->fLensInfo [3].Set_real64 ((real64)lens.MaxAp4MaxFocal);
            filled++;
        }

    if (lens.Lens [0])
        { exif->fLensName.Set_ASCII (lens.Lens); filled++; }        // EXIF 2.3 LensModel
    if (lens.LensMake [0])
        { exif->fLensMake.Set_ASCII (lens.LensMake); filled++; }
    if (lens.LensSerial [0])
        { exif->fLensSerialNumber.Set_ASCII (lens.LensSerial); filled++; }

    if (other.timestamp > 0)
        {
            struct tm tmv;
            bool have = false;
#ifdef _WIN32
            have = (localtime_s (&tmv, &other.timestamp) == 0);
#else
            struct tm* p = localtime (&other.timestamp);
            if (p) { tmv = *p; have = true; }
#endif
            if (have)
            {
                dng_date_time dt (tmv.tm_year + 1900, tmv.tm_mon + 1, tmv.tm_mday,
                                  tmv.tm_hour, tmv.tm_min, tmv.tm_sec);
                if (dt.IsValid ())
                {
                    dng_date_time_info di;
                    di.SetDateTime (dt);
                    exif->fDateTimeOriginal  = di;   // 拍摄时刻
                    exif->fDateTime          = di;   // 文件时刻
                    exif->fDateTimeDigitized = di;   // 数字化时刻
                    filled += 3;
                }
            }
        }

    if (other.desc [0])
        { exif->fImageDescription.Set_ASCII (other.desc); filled++; }
    if (other.artist [0])
        { exif->fArtist.Set_ASCII (other.artist); filled++; }

    // 镜头组标签 (LensMake / LensSpecification / LensModel / LensSerial) 在
    // dng_image_writer 里被 AtLeastVersion0230() 挡着, 不声明 EXIF 版本就整组不写。
    if (filled > 0 && exif->fExifVersion == 0)
        exif->fExifVersion = DNG_CHAR4 ('0','2','3','0');

    if (verbose)
        fprintf (stderr, "[dngtool-e] 拍摄字段搬运: %d 项\n", filled);
}

#endif // USE_DNGSDK (元数据搬运辅助块)

// ── `-idc-look`: 把标定后的曲线写进**主 profile 的 ProfileToneCurve** ──────────
// 规格文件 = 一行一对的 key=value, '#' 起头算注释:
//   curve=0:0 0.25:0.2 0.5:0.45 1:1     控制点 x:y, ≥2 点, x 严格递增, 全在 [0,1]
// 为什么走主 profile 而不是 ExtraCameraProfiles（都是实测，不是偏好）：
//   · 规范里 ProfileToneCurve 的 Usage 就是 "IFD0 or Camera Profile IFD"
//     (DNG_Spec_1_7_1_0.pdf §ProfileToneCurve)，且它是"处理图像时可用的**默认**曲线、
//     作为用户调整的起点"，读方按三次样条插值 ⇒ 语义与"相机侧的明度曲线"对得上；
//   · 实测 SDK 1.7.1 把 extra profile 的体积估少了：产物里 ExtraCameraProfiles 指向的
//     块只剩 8 字节块头 (`II` + magic 0x4352 + IFD 偏移 8)，其后紧接的就是 StripOffsets
//     (=块址+8) ⇒ 条带数据把 profile 目录盖掉了 (dng_image_writer.cpp:9669 起的两段式
//     尺寸预算在这条路上不闭合)。写主 profile 不经过那条预算。
struct LookSpec
{
    bool           ok;
    std::string    why;
    uint32         policy;
    bool           hasPolicy;
    bool           hasCurve;
    dng_tone_curve curve;
    // 分通道曲线的规范位 = OpcodeList3(51022) 里按 plane 选通的 MapTable：
    // dng_area_spec 带 fPlane/fPlanes（dng_misc_opcodes.h:73-74），空 area 表示整幅
    // （dng_misc_opcodes.cpp:213-219 "if the fArea is empty, then dng_area_spec covers
    //  the entire image"），list 3 是 **post demosaic**（dng_negative.h:917）⇒ 三平面才谈得上分通道。
    bool           hasChannels;
    dng_tone_curve chan [3];
    std::vector<uint16> tab [3];   // 解析期就展开成 16-bit LUT ⇒ 坏曲线在编码前非零退出
    LookSpec () : ok (false), policy (0), hasPolicy (false), hasCurve (false), hasChannels (false) {}
};

static bool IsSpaceCh (char c) { return c == ' ' || c == '\t' || c == '\r'; }

static bool ParseCurveTokens (const std::string& v, dng_tone_curve& out, std::string& why)
{
    std::vector<dng_point_real64> pts;
    size_t i = 0;
    while (i < v.size ())
    {
        while (i < v.size () && IsSpaceCh (v [i])) i++;
        if (i >= v.size ()) break;
        size_t j = i;
        while (j < v.size () && !IsSpaceCh (v [j])) j++;
        std::string tok = v.substr (i, j - i);
        i = j;
        size_t c = tok.find (':');
        if (c == std::string::npos) { why = "曲线点缺 ':' (" + tok + ")"; return false; }
        std::string sh = tok.substr (0, c), sv = tok.substr (c + 1);
        char* e1 = NULL; char* e2 = NULL;
        double h = strtod (sh.c_str (), &e1);
        double g = strtod (sv.c_str (), &e2);
        if (!e1 || *e1 || !e2 || *e2) { why = "曲线点不是数 (" + tok + ")"; return false; }
        if (h < 0.0 || h > 1.0 || g < 0.0 || g > 1.0)
            { why = "曲线点越出 [0,1] (" + tok + ")"; return false; }
        if (!pts.empty () && h <= pts.back ().h)
            { why = "曲线 x 必须严格递增 (" + tok + ")"; return false; }
        // ⚠ dng_point_real64 的构造函数是 (v, h) —— 顺序反的 (dng_point.h:80),
        //   按 SDK 自己读 ProfileToneCurve 的写法逐字段赋 (dng_camera_profile.cpp:1728)。
        dng_point_real64 pt;
        pt.h = h;
        pt.v = g;
        pts.push_back (pt);
    }
    if (pts.size () < 2) { why = "曲线至少要 2 个点"; return false; }
    if (pts.size () > kMaxToneCurvePoints)
        { why = "曲线点数超过 SDK 上限 kMaxToneCurvePoints"; return false; }
    // fCoord 是 SDK 自己的 dng_std_vector, 不能从 std::vector 赋值 ⇒ 按它自己读标签时的写法
    // (dng_camera_profile.cpp:1722) resize + 逐点填。
    out.fCoord.resize ((uint32) pts.size ());
    for (size_t k = 0; k < pts.size (); k++)
        out.fCoord [k] = pts [k];
    return true;
}

// 控制点 → 逐项 16-bit 表: x 与 y 都在 [0,1] 线性域, 均匀铺到码值 0..65535, 段内线性插值。
// ⚠ 线性插值是**我方约定**（ProfileToneCurve 那条路读者按三次样条插值 = 规范原文）⇒ 两条形同而
//   不同, 同一个规格只许选一条（parser 里按互斥拒掉）, 标定数据进来时也要按这条约定采样。
static bool BuildChannelTable (const dng_tone_curve& c, std::vector<uint16>& tab, std::string& why)
{
    const uint32 N = 0x10000u;
    uint32 n = (uint32) c.fCoord.size ();
    if (n < 2) { why = "曲线至少要 2 个点"; return false; }
    tab.assign (N, 0);
    uint32 seg = 0;
    for (uint32 i = 0; i < N; i++)
    {
        double x = (double) i / 65535.0;
        while (seg + 2 < n && c.fCoord [seg + 1].h < x) seg++;
        double x0 = c.fCoord [seg].h,     y0 = c.fCoord [seg].v;
        double x1 = c.fCoord [seg + 1].h, y1 = c.fCoord [seg + 1].v;
        double t  = (x1 > x0) ? (x - x0) / (x1 - x0) : 0.0;
        if (t < 0.0) t = 0.0;
        if (t > 1.0) t = 1.0;
        double y  = y0 + (y1 - y0) * t;
        if (y < 0.0) y = 0.0;
        if (y > 1.0) y = 1.0;
        tab [i] = (uint16) (y * 65535.0 + 0.5);
    }
    return true;
}

// 与恒等逐点相同 ⇒ 这支表不改变任何像素, 写了只是造"参数已进标准位"的观感 ⇒ 调用方跳过并点名。
static bool TableIsIdentity (const std::vector<uint16>& tab)
{
    for (uint32 i = 0; i < (uint32) tab.size (); i++)
        if (tab [i] != (uint16) i) return false;
    return true;
}

static LookSpec ParseLookSpec (const char* path)
{
    LookSpec s;
    FILE* fp = fopen (path, "rb");
    if (!fp) { s.why = "打不开规格文件"; return s; }
    std::string all;
    char buf [4096];
    size_t got;
    while ((got = fread (buf, 1, sizeof (buf), fp)) > 0)
        { all.append (buf, got); if (all.size () > 65536) break; }
    fclose (fp);
    if (all.size () > 65536) { s.why = "规格文件过大"; return s; }

    size_t pos = 0;
    while (pos < all.size ())
    {
        size_t nl = all.find ('\n', pos);
        std::string line = (nl == std::string::npos) ? all.substr (pos) : all.substr (pos, nl - pos);
        pos = (nl == std::string::npos) ? all.size () : nl + 1;
        size_t a = line.find_first_not_of (" \t\r");
        if (a == std::string::npos) continue;
        line = line.substr (a);
        if (line [0] == '#') continue;
        size_t eq = line.find ('=');
        if (eq == std::string::npos) { s.why = "行里没有 '=': " + line; return s; }
        std::string key = line.substr (0, eq);
        std::string val = line.substr (eq + 1);
        size_t b = val.find_first_not_of (" \t");
        val = (b == std::string::npos) ? std::string () : val.substr (b);
        if (key == "policy")
            {
            char* e = NULL;
            long p = strtol (val.c_str (), &e, 10);
            if (!e || *e || p < 0 || p > 2) { s.why = "policy 必须是 0/1/2"; return s; }
            s.policy = (uint32) p; s.hasPolicy = true;
            }
        else if (key == "curve")  { if (!ParseCurveTokens (val, s.curve, s.why)) return s; s.hasCurve = true; }
        else if (key == "curveR") { if (!ParseCurveTokens (val, s.chan [0], s.why)) return s; s.hasChannels = true; }
        else if (key == "curveG") { if (!ParseCurveTokens (val, s.chan [1], s.why)) return s; s.hasChannels = true; }
        else if (key == "curveB") { if (!ParseCurveTokens (val, s.chan [2], s.why)) return s; s.hasChannels = true; }
        else { s.why = "不认识的键: " + key; return s; }
    }
    // 必须显式给曲线: dng_tone_curve 的默认值是 SetNull() 出来的 (0,0)(1,1) 恒等线
    // (dng_tone_curve.cpp:38-49), 它 IsValid() —— 只查 IsValid 等于放行"没写曲线"。
    if (!s.hasCurve && !s.hasChannels) { s.why = "既无 curve 也无 curveR/G/B"; return s; }
    // 两条载体互斥: 读者会**同时**应用 profile 曲线与 OpcodeList3 ⇒ 同一条曲线施加两次。
    if (s.hasCurve && s.hasChannels)
        { s.why = "curve 与 curveR/G/B 互斥（ProfileToneCurve 与 OpcodeList3 不可同写）"; return s; }
    if (s.hasCurve && !s.curve.IsValid ()) { s.why = "曲线不合法"; return s; }
    // 表在解析期展开 ⇒ 坏曲线在解码与编码之前就非零退出（与"三张坏规格不产出"同形）
    for (uint32 p = 0; p < 3; p++)
        {
        if (!s.hasChannels || s.chan [p].fCoord.size () == 0) continue;
        if (!s.chan [p].IsValid ()) { s.why = "曲线不合法"; return s; }
        if (!BuildChannelTable (s.chan [p], s.tab [p], s.why)) return s;
        }
    s.ok = true;
    return s;
}

// 曲线直接设在**建 profile 的那两处**上 (见 EncodeDng 里 block1/block2), 这里只兜底:
// 两处都没建成 profile 时, 建一条只装曲线的, 它是唯一一条 ⇒ GetProfileToEmbed 走 list[0]
// 兜底 ⇒ 写进 IFD0 主 profile 位, 不经过 extra profile 那条体积预算。
static void ApplyLookFallback (dng_negative &negative, const LookSpec &ls)
{
    AutoPtr<dng_camera_profile> solo (new dng_camera_profile);
    solo->SetToneCurve (ls.curve);
    if (ls.hasPolicy) solo->SetEmbedPolicy (ls.policy);
    negative.AddProfile (solo);
}

// ══ -lens-corr: 源 RAW 的镜头三表 → 规范 opcode（不是私有容器）════════════════
// 语义/换算/索引四件事全部回了一手材料，本机没有一样是猜的：
//   · 结点位 r_i = (i + 0.5)/(nc - 1)                      —— darktable master `src/iop/lens.cc:2225`
//   · 畸变与横向差 = 半径比例 Rin/Rout，Rout=1 定义为**输出（已校正）图**的角
//     同文件 :2409-2421 的注释原文 "The distortion polynomial maps a radius Rout in the
//     output (undistorted) image, where the corner is defined as Rout=1, to a radius in the
//     input (distorted) image"；而 SDK 的 `dng_warp_params_radial` 写的正是
//     `r_real = r_ideal * f(r_ideal)`（`dng_lens_correction.h:228-241`，ideal=已校正）
//     ⇒ **两边同向，不取倒数**
//   · 暗角 = 增益乘像素 `v' = v * g`，g = 1 + k0r² + k1r⁴ + k2r⁶ + k3r⁸ + k4r¹⁰
//     （`dng_lens_correction.h:669-680`）；dt 在 :2244-2246 存的是它的**倒数**
//     `2^(0.5 − 2^(v/2^13−1))`（它的管线按除法校正）⇒ 本处取 `2^(2^(v/2^13−1) − 0.5)`
//   · 32 条横向差 = 前 nc 条 R、后 nc 条 B，且硬校验 `2*nc == CA 计数`
//     —— dt `src/common/exif.cc:1398-1406`（`ca_r[i]=toLong(i+1); ca_b[i]=toLong(nc+i+1)`）
// 载体与 flags 的取舍由本机实测决定（`tests/output/lens-probe/probe3.log`，读者 = Adobe
// dng_validate 1.7.1）：三类都认得且**真施加**（暗角角/中比 ×1.1124/1.1110/1.1123，对上
// 表值预测 1.1117；只给平面 1 系数的 warp 确实只动绿平面，外环改动 95.45% vs R/B 各 ~1%）；
// `[14 optional] + [1 required]` 成对时后一条被规范 p.105 的跳过规则免施加（像素与只发 14
// 逐点相同），而 `[14 required] + [1 required]` 两条都施加 ⇒ **成对发**才跨读者；未知 ID 标
// required 会让 Adobe 自己的读者直接拒件（rc=106，`dng_opcodes.cpp:226-239`），标 optional
// 则零施加零报错。Adobe 自带的 `sample_files/03_jxl_bayer_raw_integer.dng` 就是 list3 里
// **required** 的 WarpRectilinear ⇒ 镜头项标 required 是现实做法，不是我们的发明。
// 恒等/缺表/开关关/残差超上界/映射非单调 ⇒ 一律**点名跳过不写**（不造"参数已进标准位"的观感）。

struct LensTables
{
    bool                ok;
    std::string         why;         // 读不到时的点名（ASCII 可 grep）
    uint32              frameW, frameH;
    std::vector<int16>  vig, dist, ca;   // 原始 int16 序列（首字 = 样本数）
    int                 swVig, swDist, swCa;   // 机内开关三态: -1 读不出 / 0 明确关 / 1 开
    LensTables () : ok (false), why ("未读"), frameW (0), frameH (0),
                    swVig (-1), swDist (-1), swCa (-1) {}
};

static bool RdU16 (FILE* fp, uint64 off, uint32& out)
{
    if (off > 0x7FFFFFFFFFFFULL || fseek (fp, (long)off, SEEK_SET) != 0) return false;
    int a = fgetc (fp), b = fgetc (fp);
    if (a == EOF || b == EOF) return false;
    out = (uint32)a | ((uint32)b << 8);
    return true;
}

static bool RdU32 (FILE* fp, uint64 off, uint32& out)
{
    if (off > 0x7FFFFFFFFFFF00ULL || fseek (fp, (long)off, SEEK_SET) != 0) return false;
    uint32 v = 0;
    for (int i = 0; i < 4; i++)
        { int c = fgetc (fp); if (c == EOF) return false; v |= ((uint32)c) << (8 * i); }
    out = v;
    return true;
}

// 取一条 IFD 里某 tag 的 int16 序列。这三张表在索尼件里 type 写的是 8(LONG) 而**载荷内容是
// int16 串**（Adobe/exiftool/dt 都这么读）⇒ 按 4*count 字节取回再两字节一解。
// 读一个"标量开关"标签：type=3 SHORT count=1 时值就**内联**在值字段的低 16 位。
static bool TagSwitch (FILE* fp, uint64 ifdOff, uint32 want, int& out)
{
    uint32 n = 0;
    if (!RdU16 (fp, ifdOff, n) || n == 0 || n > 512) return false;
    for (uint32 i = 0; i < n; i++)
    {
        uint64 e = ifdOff + 2 + 12 * (uint64)i;
        uint32 tag = 0, ty = 0, cnt = 0, val = 0;
        if (!RdU16 (fp, e, tag) || !RdU16 (fp, e + 2, ty) ||
            !RdU32 (fp, e + 4, cnt) || !RdU32 (fp, e + 8, val)) continue;
        if (tag != want) continue;
        if (ty == 3 && cnt >= 1) { out = (int)(int16_t)(val & 0xFFFFu); return true; }
        if (ty == 8 && cnt >= 1) { out = (int)(int16_t)(val & 0xFFFFu); return true; }
        return false;                     // 形状不认识 ⇒ 按"读不出"处理, 不当成"关"
    }
    return false;
}

static bool TagInt16Seq (FILE* fp, uint64 ifdOff, uint32 want, std::vector<int16>& out)
{
    uint32 n = 0;
    if (!RdU16 (fp, ifdOff, n) || n == 0 || n > 512) return false;
    for (uint32 i = 0; i < n; i++)
    {
        uint64 e = ifdOff + 2 + 12 * (uint64)i;
        uint32 tag = 0, ty = 0, cnt = 0, val = 0;
        if (!RdU16 (fp, e, tag) || !RdU16 (fp, e + 2, ty) ||
            !RdU32 (fp, e + 4, cnt) || !RdU32 (fp, e + 8, val)) continue;
        if (tag != want) continue;
        if (ty != 8 || cnt == 0 || cnt > 8192) return false;      // 形状不认 ⇒ 不猜
        uint64 bytes = (uint64)cnt * 4;
        uint64 base = (bytes <= 4) ? (e + 8) : (uint64)val;        // TIFF: 值 ≤4 字节内联
        out.clear ();
        for (uint64 k = 0; k + 1 < bytes; k += 2)
        {
            uint32 w = 0;
            if (!RdU16 (fp, base + k, w)) { out.clear (); return false; }
            out.push_back ((int16)w);
        }
        return true;
    }
    return false;
}

static bool ReadLensTables (const char* path, LensTables& t, bool verbose)
{
    FILE* fp = fopen (path, "rb");
    if (!fp) { t.why = "源打不开"; return false; }
    uint32 bo = 0, off0 = 0;
    if (!RdU16 (fp, 0, bo) || !RdU32 (fp, 4, off0) || bo != 0x4949 || off0 < 8)
        { t.why = "非 LE TIFF 头"; fclose (fp); return false; }
    // 表在**图像 SubIFD**（IFD0 的 0x14A 指过去），不在加密区；顺带取解码幅面用于报告
    uint32 n0 = 0, subBase = 0, subCnt = 0, subInline = 0;
    if (RdU16 (fp, off0, n0) && n0 && n0 <= 512)
    {
        for (uint32 i = 0; i < n0; i++)
        {
            uint64 e = off0 + 2 + 12 * (uint64)i;
            uint32 tag = 0, ty = 0, cnt = 0, val = 0;
            if (!RdU16 (fp, e, tag) || !RdU16 (fp, e + 2, ty) ||
                !RdU32 (fp, e + 4, cnt) || !RdU32 (fp, e + 8, val)) continue;
            if (tag == 0x0100 && cnt == 1 && (ty == 3 || ty == 4)) t.frameW = (ty == 3) ? (val & 0xFFFF) : val;
            if (tag == 0x0101 && cnt == 1 && (ty == 3 || ty == 4)) t.frameH = (ty == 3) ? (val & 0xFFFF) : val;
            if (tag == 0x014A && ty == 4 && cnt >= 1 && cnt <= 8)
                { subBase = val; subCnt = cnt; subInline = (cnt == 1) ? 1 : 0; }
        }
    }
    if (subBase == 0) { t.why = "IFD0 无 0x14A SubIFDs"; fclose (fp); return false; }
    // 0x14A 的值字段是 SubIFD 偏移**数组**（TIFF 规则: 计数在表项里, 数组本身不带计数头）⇒
    // 早先按"首字是数量"去解会把一个偏移当计数。另一条更阴: cnt==1 时 TIFF 把值**内联**在值字段里,
    // 那个字段本身就是偏移, 再去解引用一次就读到了缩略图 IFD 的头 ⇒ 7M2 会被误判成"无镜头表"。
    uint32 nSub = subCnt, subList [8] = { 0 };
    if (subInline)
        subList [0] = subBase;
    else
        for (uint32 s2 = 0; s2 < subCnt; s2++)
            if (!RdU32 (fp, (uint64)subBase + 4 * (uint64)s2, subList [s2])) { nSub = s2; }
    for (uint32 s = 0; s < nSub; s++)
    {
        uint32 one = subList [s];
        if (one < 8) continue;
        std::vector<int16> v, d, c;
        bool haveVig = TagInt16Seq (fp, one, 0x7032, v);
        bool haveDist = TagInt16Seq (fp, one, 0x7037, d);
        bool haveCa  = TagInt16Seq (fp, one, 0x7035, c);
        if (!haveVig && !haveDist && !haveCa) continue;
        {
            int sv = -1;
            if (haveVig  && TagSwitch (fp, one, 0x7031, sv)) t.swVig  = sv;
            if (haveDist && TagSwitch (fp, one, 0x7036, sv)) t.swDist = sv;
            if (haveCa   && TagSwitch (fp, one, 0x7034, sv)) t.swCa   = sv;
        }
        if (haveVig)  t.vig  = v;
        if (haveDist) t.dist = d;
        if (haveCa)   t.ca   = c;
        t.ok = haveVig || haveDist || haveCa;
        if (t.ok) { fclose (fp); if (verbose) fprintf (stderr, "[dngtool-e] lens: SubIFD#%u 命中三表可见性 vig=%d dist=%d ca=%d\n", s, (int)haveVig, (int)haveDist, (int)haveCa); return true; }
    }
    t.why = "图像 SubIFD 里没有 0x7032/0x7037/0x7035";
    fclose (fp);
    return false;
}

// 基函数最小二乘：y ≈ Σ coef[j]·x^powers[j]。正规方程 + 部分主元消元，本机自算不引库；
// 主元退化（奇异）⇒ 返回 false 并点名，**不猜系数**。maxRes 返回对表格结点的最大残差。
static bool FitPowers (const std::vector<double>& xs, const std::vector<double>& ys,
                       const std::vector<int>& powers, std::vector<double>& coef,
                       double& maxRes)
{
    const uint32 m = (uint32)powers.size (), nn = (uint32)xs.size ();
    if (m == 0 || nn < m) return false;
    std::vector<double> A ((size_t)m * m, 0.0), b (m, 0.0), phi (m, 0.0);
    for (uint32 i = 0; i < nn; i++)
    {
        for (uint32 j = 0; j < m; j++) phi [j] = pow (xs [i], (double)powers [j]);
        for (uint32 j = 0; j < m; j++)
        {
            b [j] += phi [j] * ys [i];
            for (uint32 k = j; k < m; k++) A [(size_t)j * m + k] += phi [j] * phi [k];
        }
    }
    for (uint32 j = 0; j < m; j++)
        for (uint32 k = 0; k < j; k++) A [(size_t)j * m + k] = A [(size_t)k * m + j];
    for (uint32 c = 0; c < m; c++)
    {
        uint32 piv = c;
        double best = fabs (A [(size_t)c * m + c]);
        for (uint32 r = c + 1; r < m; r++)
            if (fabs (A [(size_t)r * m + c]) > best) { best = fabs (A [(size_t)r * m + c]); piv = r; }
        if (best < 1e-12) return false;
        if (piv != c)
            { for (uint32 k = 0; k < m; k++) std::swap (A [(size_t)c * m + k], A [(size_t)piv * m + k]); std::swap (b [c], b [piv]); }
        double dg = A [(size_t)c * m + c];
        for (uint32 k = c; k < m; k++) A [(size_t)c * m + k] /= dg;
        b [c] /= dg;
        for (uint32 r = 0; r < m; r++)
        {
            if (r == c) continue;
            double f = A [(size_t)r * m + c];
            if (f == 0.0) continue;
            for (uint32 k = c; k < m; k++) A [(size_t)r * m + k] -= f * A [(size_t)c * m + k];
            b [r] -= f * b [c];
        }
    }
    coef = b;
    maxRes = 0.0;
    for (uint32 i = 0; i < nn; i++)
    {
        double v = 0.0;
        for (uint32 j = 0; j < m; j++) v += coef [j] * pow (xs [i], (double)powers [j]);
        double e = fabs (v - ys [i]);
        if (e > maxRes) maxRes = e;
    }
    return true;
}

static double EvalPowers (const std::vector<double>& coef, const std::vector<int>& powers, double x)
{
    double v = 0.0;
    for (uint32 j = 0; j < coef.size (); j++) v += coef [j] * pow (x, (double)powers [j]);
    return v;
}

// 结点位：r_i = (i + 0.5)/(nc - 1)（dt lens.cc:2225 原式; 末点落在角外 1.033 是它的既有做法）
static void BuildKnots (uint32 nc, std::vector<double>& knots)
{
    knots.clear ();
    for (uint32 i = 0; i < nc; i++) knots.push_back (((double)i + 0.5) / (double)(nc - 1));
}

// 逐平面 warp 的合法性：SDK 的 dng_warp_params_rectilinear 文档要求 r↦r·f(r) 单调可逆
// （dng_lens_correction.h:348-352），且 IsValid 还要 0 ≤ minValid < maxValid ≤ 1
// （dng_lens_correction.cpp:301-320）。⇒ 施加前先按 401 点扫一遍，非单调就不发。
static bool RadialMapMonotone (const std::vector<double>& coef, const std::vector<int>& powers,
                               double& minScale)
{
    double prev = -1.0;
    minScale = 1e300;
    for (int i = 0; i <= 400; i++)
    {
        double r = (double)i / 400.0;
        double f = EvalPowers (coef, powers, r);
        if (!(f > 0.0)) return false;
        if (f < minScale) minScale = f;
        double mapped = r * f;
        if (i > 0 && !(mapped > prev)) return false;
        prev = mapped;
    }
    return true;
}

// 一整套镜头 opcode 的落点。返回写进 OpcodeList3 的支数；每条臂的取舍都用 ASCII 键点名。
static uint32 ApplyLensOpcodes (dng_negative &negative, const LensTables &t,
                                const dng_rect &stage3Bounds, bool verbose)
{
    uint32 written = 0;

    // ── 1) 暗角 → FixVignetteRadial(ID=3, v1.3.0.0)，g = 1 + k0r² + … + k4r¹⁰ ──
    if (!t.vig.empty ())
    {
        uint32 nc = (uint32)t.vig [0];
        if (t.swVig == 0)     fprintf (stderr, "[dngtool-e] lens-skipped=vignette:switch-off\n");
        else if (nc < 5 || t.vig.size () < (size_t)(1 + nc))
                              fprintf (stderr, "[dngtool-e] lens-skipped=vignette:count(nc=%u, 五项拟合要 nc>=5)\n", nc);
        else
        {
            std::vector<double> knots, xs, ys;
            BuildKnots (nc, knots);
            // 透射指数 → 净增益（SDK 文档: v' = v*g ⇒ 取 dt 那个值的倒数）
            for (uint32 i = 0; i < nc; i++)
            {
                double tr = pow (2.0, (double)t.vig [1 + i] / 8192.0 - 1.0);
                xs.push_back (knots [i] * knots [i]);        // 基取 u = r²
                ys.push_back (pow (2.0, tr - 0.5) - 1.0);    // 拟合 g-1
            }
            std::vector<int> powers; for (int q = 1; q <= 5; q++) powers.push_back (q);
            std::vector<double> coef;
            double res = 0.0;
            if (!FitPowers (xs, ys, powers, coef, res))
                    fprintf (stderr, "[dngtool-e] lens-skipped=vignette:fit-singular\n");
            else if (res > 0.010)
                    fprintf (stderr, "[dngtool-e] lens-skipped=vignette:residual=%.6f>0.010\n", res);
            else
            {
                double corner = 1.0 + EvalPowers (coef, powers, 1.0);
                bool mono = true;
                for (int i = 0; i <= 200 && mono; i++)
                {
                    double q = (double)i / 200.0;
                    if (1.0 + EvalPowers (coef, powers, q * q) <= 0.0) mono = false;
                }
                double kk [5] = { coef [0], coef [1], coef [2], coef [3], coef [4] };
                bool ident = true;
                for (int q = 0; q < 5; q++) if (fabs (kk [q]) > 1e-9) ident = false;
                if (!mono)      fprintf (stderr, "[dngtool-e] lens-skipped=vignette:gain<=0\n");
                else if (ident) fprintf (stderr, "[dngtool-e] lens-skipped=vignette:identity（写了不改变任何像素）\n");
                else
                {
                    dng_std_vector<real64> vp (5);
                    for (int q = 0; q < 5; q++) vp [q] = kk [q];
                    dng_point_real64 center;                 // ⚠ 构造序是 (v,h) ⇒ 逐字段赋
                    center.h = 0.5; center.v = 0.5;
                    dng_vignette_radial_params params (vp, center);
                    AutoPtr<dng_opcode> op (new dng_opcode_FixVignetteRadial (params,
                                                                              dng_opcode::kFlag_None));
                    negative.OpcodeList3 ().Append (op);
                    written++;
                    fprintf (stderr, "[dngtool-e] lens-vignette=applied nc=%u sw=%d maxres=%.6f cornergain=%.6f\n",
                             nc, t.swVig, res, corner);
                    if (verbose)
                        fprintf (stderr, "[dngtool-e] FixVignetteRadial k=%g %g %g %g %g center=(%.4f,%.4f)\n",
                                 kk [0], kk [1], kk [2], kk [3], kk [4], center.h, center.v);
                }
            }
        }
    }

    // ── 2) 畸变(±横向色差) → [WarpRectilinear2 optional] + [WarpRectilinear required] ──
    if (!t.dist.empty ())
    {
        uint32 nc = (uint32)t.dist [0];
        bool caUsable = (!t.ca.empty () && (uint32)t.ca [0] == 2 * nc && t.ca.size () >= (size_t)(1 + 2 * nc));
        if (t.swDist == 0)  fprintf (stderr, "[dngtool-e] lens-skipped=distortion:switch-off\n");
        else if (nc < 3 || t.dist.size () < (size_t)(1 + nc))
                            fprintf (stderr, "[dngtool-e] lens-skipped=distortion:count(nc=%u)\n", nc);
        else
        {
            std::vector<double> knots;
            BuildKnots (nc, knots);
            double caScale [2] [16];     // 只在 nc ≤ 16 时用（本机两件都是 16; 超了就退回无 CA）
            if (caUsable && t.swCa != 0 && nc <= 16)
                for (uint32 ch = 0; ch < 2; ch++)
                    for (uint32 i = 0; i < nc; i++)
                        caScale [ch][i] = 1.0 + (double)t.ca [1 + ch * nc + i] / 2097152.0;
            else
                caUsable = false;
            if (t.swCa == 0 && !t.ca.empty ()) fprintf (stderr, "[dngtool-e] lens-skipped=chromatic-aberration:switch-off\n");

            const int kMaxDeg = (int)dng_warp_params_radial::kMaxTerms - 1;
            std::vector<double> coefV2 [3];
            std::vector<int>      powV2 [3];
            double                resV2 [3] = { 0.0, 0.0, 0.0 };
            bool                  planeOk = true;
            for (uint32 p = 0; p < 3 && planeOk; p++)
            {
                std::vector<double> ys (nc, 0.0);
                for (uint32 i = 0; i < nc; i++)
                {
                    double s = 1.0 + (double)t.dist [1 + i] / 16384.0;
                    if (caUsable) s *= (p == 0) ? caScale [0][i] : (p == 2) ? caScale [1][i] : 1.0;
                    ys [i] = s;
                }
                // 取"能达标的最低阶"—— 15 项插值会把龙格振铃带进 [0,1]，本机实测过这种假读数
                bool found = false;
                for (int deg = 1; deg <= kMaxDeg && !found; deg++)
                {
                    std::vector<int> pw; for (int q = 0; q <= deg; q++) pw.push_back (q);
                    std::vector<double> cf; double res = 0.0;
                    if (!FitPowers (knots, ys, pw, cf, res)) continue;
                    double minScale = 0.0;
                    if (!RadialMapMonotone (cf, pw, minScale)) continue;
                    if (res > 0.0005 && deg < kMaxDeg) continue;
                    if (res > 0.0020) { fprintf (stderr, "[dngtool-e] lens-skipped=distortion:residual=%.6f>0.0020 plane=%u\n", res, p); planeOk = false; break; }
                    coefV2 [p] = cf; powV2 [p] = pw; resV2 [p] = res; found = true;
                }
                if (!found && planeOk) { fprintf (stderr, "[dngtool-e] lens-skipped=distortion:fit plane=%u 无合格阶数\n", p); planeOk = false; }
            }
            if (planeOk)
            {
                dng_warp_params_radial rad;
                for (uint32 p = 0; p < 3; p++)
                    for (uint32 q = 0; q < dng_warp_params_radial::kMaxTerms; q++)
                        rad.fData [p] [q] = (q < coefV2 [p].size ()) ? coefV2 [p] [q] : 0.0;
                for (uint32 p = 0; p < 3; p++) { rad.fValidRange [p] [0] = 0.0; rad.fValidRange [p] [1] = 1.0; }
                rad.fUseReciprocal = false;
                dng_vector tanP [3];
                for (uint32 p = 0; p < 3; p++) { tanP [p] = dng_vector (2); tanP [p] [0] = 0.0; tanP [p] [1] = 0.0; }
                dng_point_real64 center; center.h = 0.5; center.v = 0.5;
                dng_warp_params_rectilinear wp (3, rad, tanP, center);
                // 恒等自检：三项都逐点等于 1 ⇒ 不发（发了不改变几何, 只是造观感）
                bool ident = true;
                for (uint32 p = 0; p < 3 && ident; p++)
                    for (uint32 q = 0; q < powV2 [p].size (); q++)
                    {
                        if (powV2 [p] [q] == 0) continue;
                        if (fabs (coefV2 [p] [q]) > 1e-12) { ident = false; break; }
                    }
                if (ident) fprintf (stderr, "[dngtool-e] lens-skipped=distortion:identity\n");
                else
                {
                    AutoPtr<dng_opcode> op2 (new dng_opcode_WarpRectilinear2 (wp, dng_opcode::kFlag_Optional));
                    negative.OpcodeList3 ().Append (op2);
                    written++;
                    fprintf (stderr, "[dngtool-e] lens-warp2=applied planes=3 sw=%d ca=%s maxres=%.6f/%.6f/%.6f\n",
                             t.swDist, caUsable ? "per-plane" : "off", resV2 [0], resV2 [1], resV2 [2]);

                    // v1 支只允许偶次项（PutData 取 fData[0,2,4,6]，dng_lens_correction.cpp:1908-1911）
                    // ⇒ 单独拟一条只含 {1,r²,r⁴,r⁶} 的；拟不到水位就不发兜底支，不硬凑
                    std::vector<double> coefV1; std::vector<int> powV1; double resV1 = 0.0;
                    bool v1ok = false;
                    {
                        std::vector<double> ys (nc, 0.0);
                        for (uint32 i = 0; i < nc; i++) ys [i] = 1.0 + (double)t.dist [1 + i] / 16384.0;
                        int pwArr [4] = { 0, 2, 4, 6 };
                        std::vector<int> pw (pwArr, pwArr + 4);
                        std::vector<double> cf; double res = 0.0;
                        double minScale = 0.0;
                        if (FitPowers (knots, ys, pw, cf, res) && RadialMapMonotone (cf, pw, minScale) && res <= 0.0020)
                            { coefV1 = cf; powV1 = pw; resV1 = res; v1ok = true; }
                        else
                            fprintf (stderr, "[dngtool-e] lens-skipped=warp-legacy:residual=%.6f（DNG1.3 只收偶次项）\n", res);
                    }
                    if (v1ok)
                    {
                        dng_warp_params_radial rad1;
                        for (uint32 p = 0; p < 3; p++)
                            for (uint32 q = 0; q < dng_warp_params_radial::kMaxTerms; q++)
                                rad1.fData [p] [q] = 0.0;                 // 奇次项必须为 0, 否则 v1 载荷与模型不符
                        for (uint32 p = 0; p < 3; p++)
                            for (uint32 j = 0; j < coefV1.size (); j++)
                                rad1.fData [p] [powV1 [j]] = coefV1 [j];
                        for (uint32 p = 0; p < 3; p++) { rad1.fValidRange [p] [0] = 0.0; rad1.fValidRange [p] [1] = 1.0; }
                        rad1.fUseReciprocal = false;
                        dng_warp_params_rectilinear wp1 (3, rad1, tanP, center);
                        AutoPtr<dng_opcode> op1 (new dng_opcode_WarpRectilinear (wp1, dng_opcode::kFlag_None));
                        negative.OpcodeList3 ().Append (op1);
                        written++;
                        fprintf (stderr, "[dngtool-e] lens-warp1=applied maxres=%.6f\n", resV1);
                    }
                }
            }
        }
    }

    if (written)
        fprintf (stderr, "[dngtool-e] lens-opcodes=%u 已写进 OpcodeList3(51022) 作用域=%ux%u\n",
                 (unsigned) written, (unsigned) stage3Bounds.W (), (unsigned) stage3Bounds.H ());
    if (verbose && written)
        fprintf (stderr, "[dngtool-e] 注: r 的归一化基数由**读者**按它自己处理的图像角点算 (规范 p.110),\n"
                         "            与源解码幅面 %ux%u 不同只会带来角外比例差, 不改变角=1 的定义\n",
                 t.frameW, t.frameH);
    return written;
}

// 分通道曲线 → 规范 51022 OpcodeList3 的三支 MapTable（每支只管一个 plane）。
// 表已在解析期展开并验过形状; 恒等支跳过不写（不造观感）并点名。返回实际写入支数。
static uint32 ApplyChannelOpcodes (dng_host &host, dng_negative &negative, const LookSpec &ls,
                                   const dng_rect &imageBounds, bool verbose)
{
    static const char *NAME [3] = { "R", "G", "B" };
    uint32 written = 0, skipped = 0;
    for (uint32 p = 0; p < 3; p++)
    {
        if (ls.tab [p].empty ()) continue;
        if (TableIsIdentity (ls.tab [p]))
            {
            skipped++;
            fprintf (stderr, "[dngtool-e] curve%s 逐点等于恒等 ⇒ 不写这支 OpcodeList3"
                            "（写了不改变任何像素, 只是造「参数已进标准位」的观感）\n", NAME [p]);
            continue;
            }
        // area 必须给**具体矩形**，不能留空。空 area 在读者侧不可往返：
        // dng_area_spec::GetData 先按 fArea.H()/W()（空矩形=0）钳制 pitch ⇒ 1 被钳成 0，
        // 接着 "if (fArea.IsEmpty ()) { if (fRowPitch != 1 || fColPitch != 1) ThrowBadFormat(); }"
        // 直接把整支 opcode 判坏（dng_misc_opcodes.cpp:141-155，实测 Adobe dng_validate 与
        // dngtool -verify 都报 error 100006）。写整幅矩形才是读者认的形状（list 3 作用在
        // image->Bounds() 上，见 dng_opcodes.cpp 的 dng_inplace_opcode::Apply）。
        dng_area_spec area (dng_rect (0, 0, imageBounds.H (), imageBounds.W ()), p, 1);
        // Append 收的是 AutoPtr<dng_opcode>&（dng_opcode_list.h:130）⇒ 用基类指针持有
        AutoPtr<dng_opcode> op (new dng_opcode_MapTable (host, area, &ls.tab [p] [0], 0x10000));
        negative.OpcodeList3 ().Append (op);
        written++;
        if (verbose)
            fprintf (stderr, "[dngtool-e] OpcodeList3 追加 MapTable: plane=%u(%s) 表=65536 项 uint16\n",
                     p, NAME [p]);
    }
    if (written)
        fprintf (stderr, "[dngtool-e] opcode-channels=%u 已写进 OpcodeList3(51022)\n", (unsigned) written);
    // ASCII 键: 门禁要复核"恒等被跳过"这件事真发生, 中文句在非 UTF-8 码页会变形不能当锚点
    if (skipped)
        fprintf (stderr, "[dngtool-e] opcode-skipped=%u\n", (unsigned) skipped);
    return written;
}

static int EncodeDng(
    const char* inputPath,
    const char* outputPath,
    int compression,       // 0=lossless JPEG, 1=JXL
    int quality,           // JXL 质量 (0=无损, 1-100=有损)
    bool forceLinear,
    int jxlEffort,         // JXL 编码努力 (1-9, 默认 7)
    int jxlDecodeSpeed,    // JXL 解码速度提示 (DNG 规范 1-4, 默认 4)
    int highlight,         // 高光模式 (LibRaw -H: 0=裁剪, 1=恢复, 2=blend) 2026-08-14
    int outputBps,         // 输出位深 (8/16) 2026-08-14
    uint32 threadCount,    // 多线程数 (2026-08-15)
    bool carryRawMetadata, // 假 = 放弃源 RAW 元数据搬运 (隐私剥除请求下由产品侧传入)
    const char* lookSpecPath, // 非空 = 按该规格给主 profile 写 ProfileToneCurve
    bool lensCorr,           // 真 = 把源 RAW 自己的镜头三表换算成规范 opcode (默认关: 它改渲染像素)
    bool verbose)
{
#ifdef USE_DNGSDK
    DngToolRaw processor;   // 子类: 可访问 tiff_ifd (DNG 原始色彩标签)

    // DNG SDK host (保持存活) — 2026-08-15: 多线程 (JXL tile 编码并行)
    DngToolHost host(threadCount);

    // -idc-look: 先解析规格, 不合法就在解码之前退出 (不做无谓的 30s 工作)
    LookSpec look;
    bool lookOn = (lookSpecPath && *lookSpecPath);
    bool lookApplied = false;
    if (lookOn)
    {
        look = ParseLookSpec (lookSpecPath);
        if (!look.ok)
        {
            fprintf (stderr, "[dngtool-e] ERROR: -idc-look 规格无效 (%s) ⇒ 不产出\n", look.why.c_str ());
            return 2;
        }
    }

    // 解码参数: 需要 raw 数据 (不 demosaic)
    // ⚠️ 2026-08-14 修复: highlight / output_bps 此前写死 (16-bit 固定), UI 的
    //    高光模式与 8-bit 位深选项对 DNG 编码完全无效 (实测 -H 0 vs -H 2 哈希相同)
    processor.imgdata.params.output_bps = outputBps;   // -4 / -6
    processor.imgdata.params.highlight = highlight;    // -H (0/1/2)
    processor.imgdata.params.no_auto_bright = 1;

#ifdef USE_DNGSDK
    // 支持 JXL-DNG 输入 (重编码场景)
    // 2026-08-15: 用多线程 host (LibRaw DNG SDK 解码也受益)
    processor.set_dng_host(&host);
    processor.imgdata.rawparams.use_dngsdk = LIBRAW_DNG_ALL;
    // ⚠️ 2026-08-14: STAGE23_IFPRESENT_JPGJXL / STAGE2 / STAGE3 全部移至
    //   open_file 之后按输入类型动态启用！
    //   - 带 OpcodeList 的官方 JXL-DNG (如 03 的 WarpRectilinear) + 1<<27
    //     → BuildStage2/3Image 强制去马赛克 → 输出 Linear DNG, CFA 丢失
    //   - Bayer 输入 (filters!=0) 必须走 dng_read_image 直读分支读原始 CFA
    // ALLOWSIZECHANGE (带 ActiveArea 的 DNG, 2026-08-14)
    processor.imgdata.rawparams.options |= (1u << 14); // LIBRAW_RAWOPTIONS_DNG_ALLOWSIZECHANGE
#endif

    if (verbose) fprintf(stderr, "[dngtool-e] opening %s ...\n", inputPath);
    int ret = OpenInputPath(processor, inputPath);
    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool-e] ERROR: cannot open %s: %s\n",
                inputPath, libraw_strerror(ret));
        return 2;
    }

#ifdef USE_DNGSDK
    // ── 按输入类型动态启用 STAGE2/3 相关选项 ──
    // Bayer 输入 (filters!=0): 全部关闭 → DNG SDK 直读分支读原始 CFA 单平面
    //   (dng_read_image 支持 JXL 52546 压缩), 保留 CFA 相位供重编码;
    //   若保留 1<<27, 官方 JXL-DNG 带 OpcodeList 会触发 BuildStage2/3 去马赛克。
    // 线性输入 (filters==0): 开启 STAGE2/3 确保 color3_image 3 平面正确
    //   (自编码 JXL-DNG 无 OpcodeList 时直读分支 G/B=0 的修复)。
    if (processor.imgdata.idata.filters == 0)
    {
        processor.imgdata.rawparams.options |= (1u << 27); // STAGE23_IFPRESENT_JPGJXL
        processor.imgdata.rawparams.options |= (1u << 12); // LIBRAW_RAWOPTIONS_DNG_STAGE2
        processor.imgdata.rawparams.options |= (1u << 13); // LIBRAW_RAWOPTIONS_DNG_STAGE3
        if (verbose) fprintf(stderr, "[dngtool-e] linear input: STAGE2/3 enabled (color3_image)\n");
    }
    else
    {
        processor.imgdata.rawparams.options &= ~((1u << 27) | (1u << 12) | (1u << 13));
        if (verbose) fprintf(stderr, "[dngtool-e] Bayer input (filters=0x%X): STAGE2/3 disabled (preserve CFA)\n",
                processor.imgdata.idata.filters);
    }
#endif

    ret = processor.unpack();
    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool-e] ERROR: unpack failed: %s\n", libraw_strerror(ret));
        processor.recycle();
        return 3;
    }

    if (!processor.imgdata.rawdata.raw_image && !processor.imgdata.rawdata.raw_alloc)
    {
        fprintf(stderr, "[dngtool-e] ERROR: no raw data available\n");
        processor.recycle();
        return 4;
    }

    const libraw_image_sizes_t& S = processor.imgdata.sizes;
    const libraw_iparams_t& idata = processor.imgdata.idata;
    const libraw_colordata_t& colordata = processor.imgdata.color;
    const unsigned filters = idata.filters;

    // ── 尺寸 ──
    const int rawWidth  = (int)S.raw_width;
    const int rawHeight = (int)S.raw_height;
    const int activeW   = (int)S.width;
    const int activeH   = (int)S.height;
    const int leftMargin = (int)S.left_margin;
    const int topMargin  = (int)S.top_margin;

    if (verbose)
        fprintf(stderr, "[dngtool-e] raw %dx%d active %dx%d margin %d,%d filters=0x%X bps=%u\n",
                rawWidth, rawHeight, activeW, activeH,
                leftMargin, topMargin, filters, colordata.raw_bps);

    // ── 数据布局安全检查 ──
    // isBayer 判定: filters!=0 && filters!=9 && !forceLinear
    bool isBayer = (filters != 0) && (filters != 9) && !forceLinear;
    // ⚠️ X-Trans (filters==9): DNG 规范不支持 X-Trans CFA 映射, 且
    //    单通道数据按 3 平面交错读会损坏 → 拒绝
    if (filters == 9)
    {
        fprintf(stderr, "[dngtool-e] ERROR: X-Trans 传感器 (filters=9) 无法编码为 DNG (DNG 规范仅支持 Bayer CFA)\n");
        processor.recycle();
        return 7;
    }
    // ⚠️ -linear + Bayer 输入: 单通道 Bayer 数据按 3 平面交错读会损坏 → 拒绝
    if (forceLinear && filters != 0)
    {
        fprintf(stderr, "[dngtool-e] ERROR: -linear 仅适用于线性/去马赛克输入 (filters=0); Bayer 输入 (filters=0x%X) 请使用保留 CFA 模式\n",
                filters);
        processor.recycle();
        return 7;
    }
    // ⚠️ 黑白传感器 (filters=0, colors=1): 单通道数据按 3 平面读会损坏 → 拒绝
    if (filters == 0 && processor.imgdata.idata.colors == 1)
    {
        fprintf(stderr, "[dngtool-e] ERROR: 黑白传感器 (colors=1) 暂不支持编码为 DNG\n");
        processor.recycle();
        return 7;
    }

    // ── 构造 dng_negative ──
    AutoPtr<dng_negative> negative;
    negative.Reset(host.Make_dng_negative());

    negative->SetColorChannels(3);

    // CFA 模式: filters 是 dcraw 风格的 32-bit 值
    // ⚠⚠ 2026-09-20 修复（三处 bug 叠加，实测症状：产物 CFA 相位错 ⇒ 解码后颜色错）：
    //   bug① 位移常量错：dcraw 的 `FC(row,col) = (filters >> ((((row<<1 & 14) + (col&1)) << 1)) & 3`
    //        ⇒ (0,0)→shift 0, (0,1)→2, **(1,0)→4, (1,1)→6**；旧代码写 `>>8` / `>>10`（错）。
    //   bug② 未处理 dcraw 的 `3`（= **第二个绿 G2**）：旧代码的四个相位模式只用了 0/1/2
    //        ⇒ 常见值 0xB4B4B4B4（= [0,1,3,2]）**四个模式全不匹配** ⇒ 落 else。
    //   bug③ **相位语义与 DNG SDK 相反**：`dng_negative::SetBayerMosaic(phase)` 的真实映射
    //        （权威来源 `dng_sdk/source/dng_negative.cpp:2957`）是
    //          phase 0 ⇒ [G,R][B,G] = **GRBG**   phase 1 ⇒ [R,G][G,B] = **RGGB**
    //          phase 2 ⇒ [B,G][G,R] = **BGGR**   phase 3 ⇒ [G,B][R,G] = **GBRG**
    //        即与「0=RGGB」的直觉 **0↔1、2↔3 互换**。旧代码按直觉传 phase ⇒ 写出的 CFA 相位反了。
    //   实测证据（Sony ILCE-7S.ARW，源 filters=0xB4B4B4B4 即 RGGB）：
    //        旧产物 CFAPattern = [Green,Red][Blue,Green]（**GRBG**，错）；且日志报
    //        `WARNING: unusual CFA pattern, using phase 0`（bug② 的直接指纹）。
    if (isBayer)
    {
        // dcraw 颜色编码: 0=R, 1=G, 2=B, 3=G2（第二个绿）⇒ 先把 G2 归一化成 G 再匹配相位
        auto normG = [](int c) { return c == 3 ? 1 : c; };
        int c00 = normG((filters >> 0) & 3);   // (row0,col0)
        int c01 = normG((filters >> 2) & 3);   // (row0,col1)
        int c10 = normG((filters >> 4) & 3);   // (row1,col0)
        int c11 = normG((filters >> 6) & 3);   // (row1,col1)
        // ⚠ 按 **DNG SDK 的真实相位顺序**匹配（0=GRBG, 1=RGGB, 2=BGGR, 3=GBRG）
        unsigned phase = 1;                    // 退化默认用 RGGB（最常见）
        if      (c00 == 1 && c01 == 0 && c10 == 2 && c11 == 1) phase = 0;   // GRBG
        else if (c00 == 0 && c01 == 1 && c10 == 1 && c11 == 2) phase = 1;   // RGGB
        else if (c00 == 2 && c01 == 1 && c10 == 1 && c11 == 0) phase = 2;   // BGGR
        else if (c00 == 1 && c01 == 2 && c10 == 0 && c11 == 1) phase = 3;   // GBRG
        else
            fprintf(stderr, "[dngtool-e] WARNING: unusual CFA pattern (0x%X -> %d,%d,%d,%d), assuming RGGB\n",
                    filters, c00, c01, c10, c11);

        negative->SetColorKeys(colorKeyRed, colorKeyGreen, colorKeyBlue, colorKeyGreen);
        negative->SetBayerMosaic(phase);
        if (verbose) fprintf(stderr, "[dngtool-e] Bayer CFA phase %u (0=GRBG,1=RGGB,2=BGGR,3=GBRG)\n", phase);
    }
    else
    {
        // Linear DNG: 无 CFA, 3 平面 RGB
        if (verbose) fprintf(stderr, "[dngtool-e] Linear DNG (no CFA)\n");
    }

    // 输出类型: 16-bit 整数 (JXL 与无损 JPEG 均支持)
    // 注1: fp16 (ttHalfFloat) 触发 libjxl 0.8 编码器崩溃 (0xC0000409), 不可用
    // 注2: fp32 有损压缩质量差 (浮点量化步长过大), 优先整数输出
    bool outFloat = false;

    // ── 尺寸与裁剪 ──
    // ⚠⚠ 2026-09-20 修复（缺陷②）：`DefaultCropOrigin` 按 DNG 规范是**相对 `ActiveArea` 的偏移**，
    //   而 `ActiveArea` 本身已经包含了 (topMargin, leftMargin)。旧代码把 margin **又填进 origin**
    //   ⇒ 「DefaultCrop 落在 ActiveArea 之外」⇒ **JXL 压缩路径解码时直接报错**：
    //     `*** Error: Default crop extends outside ActiveArea ***`
    //   （实测 Canon EOS R6，源 margin=156,108）。⚠ 无损 JPEG 路径**不做该校验**所以看不出来，
    //   但产物同样带着错误的裁剪框（换解码器/换软件就可能暴露）。
    negative->SetActiveArea(dng_rect(topMargin, leftMargin,
                                     topMargin + activeH, leftMargin + activeW));
    negative->SetDefaultCropSize(activeW, activeH);
    negative->SetDefaultCropOrigin(0, 0);
    negative->SetDefaultScale(dng_urational(1, 1), dng_urational(1, 1));

    // ⚠️ OriginalDefaultFinalSize 必须 = DefaultFinalSize!
    // 手动构造 negative 时 fOriginalDefaultFinalSize 默认为 (0,0),
    // dng_image_writer 比较 OriginalDefaultFinalSize != DefaultFinalSize
    // (dng_point(0,0) != 实际尺寸) → 写出 OriginalDefaultFinalSize: 0 0 +
    // OriginalDefaultCropSize: undef undef → Adobe Camera Raw 拒绝打开
    // (实测 PS 2026: "应为相对于现有文件/文件夹的参考")。2026-08-14 修复。
    negative->SetOriginalDefaultFinalSize(dng_point(activeH, activeW));
    // OriginalDefaultCropSize 同样需 = DefaultCropSize (默认 0/0=undef 时
    // writer 仍会写出 OriginalDefaultCropSize: undef undef)
    negative->SetOriginalDefaultCropSize(dng_urational(activeW, 1),
                                         dng_urational(activeH, 1));

    // EXIF 尺寸 + 制造商/型号
    dng_exif* exif = negative->GetExif();
    exif->fPixelXDimension = activeW;
    exif->fPixelYDimension = activeH;
    if (idata.make[0]) exif->fMake.Set_ASCII(idata.make);
    if (idata.model[0]) exif->fModel.Set_ASCII(idata.model);
    exif->fSoftware.Set_ASCII("dngtool (LibRaw + DNG SDK)");
    if (idata.model[0]) negative->SetModelName(idata.model);

    // ── 黑电平 / 白电平 ──
    // float 输出: 白电平 = 1.0 (数据范围 [0..1])
    // linear DNG (无 CFA): 像素已是 16-bit 全范围, WhiteLevel 用 65535
    // Bayer DNG: 用传感器最大值 (最大位深), 由 raw 位深决定
    if (verbose)
        fprintf(stderr, "[dngtool-e] raw_bps=%u maximum=%u black=%u cblack0=%u "
                        "linear_max=%u/%u/%u (源声明的线性上限, 与 maximum 不等时=相机留的高光余量)\n",
                colordata.raw_bps, colordata.maximum, colordata.black,
                colordata.cblack[0],
                colordata.linear_max[0], colordata.linear_max[1], colordata.linear_max[2]);

    uint32 white;
    if (outFloat)
        white = 1;
    else if (outputBps <= 8)
        white = 255;                 // 8-bit 输出 (2026-08-14)
    else if (!isBayer || colordata.raw_bps >= 16)
        white = 0xFFFF;
    else if (colordata.maximum > 0)
        {
        white = colordata.maximum;
        // 源自己声明了线性上限 (索尼 14-bit: maximum=16383 而 linear_max=15360) 时,
        // 搬运**声明值**而不是位深上限: 15360..16383 是相机留的高光余量, 已非线性响应区。
        // 只在三通道相等且确实低于 maximum 时搬, 否则维持原口径 (不做半截推断)。
        // 仅非 DNG 输入: DNG→DNG 走上面的标签无损复制, 口径由源标签决定。
        const uint32 lm0 = colordata.linear_max [0];
        if (processor.imgdata.idata.dng_version == 0 &&
            isBayer && lm0 > 0 && lm0 == colordata.linear_max [1] &&
            lm0 == colordata.linear_max [2] && lm0 < white)
            {
            if (verbose)
                fprintf(stderr, "[dngtool-e] WhiteLevel: %u → 源声明的线性上限 %u\n",
                        white, lm0);
            white = lm0;
            }
        }
    else
        white = (1u << (colordata.raw_bps > 0 ? colordata.raw_bps : 14)) - 1;
    negative->SetWhiteLevel(white);
    // 黑电平: 优先 cblack[0] (DNG 每通道黑电平, 官方样本 03 = 512)
    // LibRaw 对 DNG 输入将 BlackLevel 存于 cblack, black 常为 0
    // 8-bit 输出时按位深缩放 (2026-08-14)
    uint32 blackLevel = colordata.black;
    if (blackLevel == 0 && colordata.cblack[0] > 0)
        blackLevel = colordata.cblack[0];
    if (outputBps <= 8) blackLevel = (blackLevel + 128) / 256;
    if (blackLevel > 0)
        negative->SetBlackLevel(blackLevel, 0);

    // ── DNG 输入: 从 tiff_ifd 读精确 DNG 标签黑/白电平 ──
    // LibRaw 的 colordata.black/cblack 在 DNG SDK 桥接路径下可能为 0
    // (JXL-DNG 直读分支实测 black=0 cblack0=0), 而 tiff_ifd[].dng_levels
    // 保留原始 DNG 标签值 (官方 03: dng_cblack[0]=512, dng_whitelevel[0]=16383)。
    // 2026-08-14 修复: 若 tiff_ifd 有值则覆盖, 确保 ACR 渲染亮度正确。
    bool levelsFromDngSdk = false;
    if (processor.imgdata.idata.dng_version > 0)
    {
        tiff_ifd_t* tiff = processor.Tiff();
        if (tiff)
        {
            for (int ifd = 0; ifd < 4; ifd++)
            {
                const libraw_dng_levels_t& dl = tiff[ifd].dng_levels;
                if (!(dl.parsedfields & (LIBRAW_DNGFM_BLACK | LIBRAW_DNGFM_WHITE)))
                    continue;
                if (verbose)
                    fprintf(stderr, "[dngtool-e] DNG levels IFD%d: black=%u cblack[0..3]=%u,%u,%u,%u white=%u (flags=0x%X)\n",
                            ifd, dl.dng_black, dl.dng_cblack[0], dl.dng_cblack[1],
                            dl.dng_cblack[2], dl.dng_cblack[3], dl.dng_whitelevel[0],
                            dl.parsedfields);

                // 黑电平 (优先级: cblack[0] > black); 8-bit 输出时按位深缩放 (2026-08-14)
                uint32 dngBlack = dl.dng_cblack[0] > 0 ? dl.dng_cblack[0] : dl.dng_black;
                if (outputBps <= 8) dngBlack = (dngBlack + 128) / 256;
                if (dl.parsedfields & LIBRAW_DNGFM_BLACK)
                {
                    if (dngBlack > 0)
                    {
                        negative->SetBlackLevel(dngBlack, 0);
                        if (verbose) fprintf(stderr, "[dngtool-e] BlackLevel=%u (from DNG tag%s)\n", dngBlack, outputBps <= 8 ? ", 8bit 缩放" : "");
                        levelsFromDngSdk = true;
                    }
                }
                // 白电平 (仅当有值且合理时覆盖); 8-bit 输出时按位深缩放 (2026-08-14)
                if ((dl.parsedfields & LIBRAW_DNGFM_WHITE) && dl.dng_whitelevel[0] > 0)
                {
                    uint32 wl = dl.dng_whitelevel[0];
                    if (outputBps <= 8) wl = (wl + 128) / 256;
                    negative->SetWhiteLevel(wl);
                    if (verbose) fprintf(stderr, "[dngtool-e] WhiteLevel=%u (from DNG tag%s)\n", wl, outputBps <= 8 ? ", 8bit 缩放" : "");
                }
                break; // 取第一个有值的 IFD
            }
        }

        // ── 回退: 用 DNG SDK 直接解析输入文件读黑电平 ──
        // LibRaw 对 JXL-DNG 直读分支丢失 BlackLevel 解析 (dng_levels 全 0),
        // 而原始 DNG 标签有值 (官方 03: BlackLevel=512)。用 DNG SDK 的
        // dng_negative 重新解析输入文件获取。2026-08-14。
        if (!levelsFromDngSdk)
        {
            try
            {
                dng_file_stream inStream(inputPath, false);
                dng_host srcHost;
                dng_info srcInfo;
                srcInfo.Parse(srcHost, inStream);
                srcInfo.PostParse(srcHost);
                if (verbose) fprintf(stderr, "[dngtool-e] DNG SDK reparse: IsValidDNG=%d\n",
                                     srcInfo.IsValidDNG() ? 1 : 0);
                if (srcInfo.IsValidDNG())
                {
                    AutoPtr<dng_negative> srcNeg;
                    srcNeg.Reset(srcHost.Make_dng_negative());
                    srcNeg->Parse(srcHost, inStream, srcInfo);
                    srcNeg->PostParse(srcHost, inStream, srcInfo);
                    // RawImageBlackLevel: 16-bit 空间的黑电平 (官方 03 = 512)
                    uint16 blk = srcNeg->RawImageBlackLevel();
                    if (verbose) fprintf(stderr, "[dngtool-e] DNG SDK reparse: RawImageBlackLevel=%u\n", blk);
                    // RawImageBlackLevel 对 JXL-DNG 可能为 0; 回退读 LinearizationInfo
                    if (blk == 0 && srcNeg->GetLinearizationInfo())
                    {
                        real64 maxBlk = srcNeg->GetLinearizationInfo()->MaxBlackLevel(0);
                        if (maxBlk > 0 && maxBlk < 65535)
                        {
                            blk = (uint16)maxBlk;
                            if (verbose) fprintf(stderr, "[dngtool-e] DNG SDK reparse: MaxBlackLevel=%.0f\n", maxBlk);
                        }
                    }
                    if (blk > 0 && blk < 65535)
                    {
                        // 8-bit 输出时按位深缩放 (2026-08-14)
                        uint16 outBlk = outputBps <= 8 ? (uint16)((blk + 128) / 256) : blk;
                        negative->SetBlackLevel(outBlk, 0);
                        if (verbose) fprintf(stderr, "[dngtool-e] BlackLevel=%u (DNG SDK reparse%s)\n", outBlk, outputBps <= 8 ? ", 8bit 缩放" : "");
                    }
                    // WhiteLevel 从 fWhiteLevel 读 (若公开) — 已由 tiff_ifd 覆盖, 跳过
                }
            }
            catch (...) { /* 解析失败则保持现有值 */ }
        }
    }

    // ── 白平衡 (Camera Neutral) ──
    // LibRaw cam_mul 是 R/G/B 增益, 归一化为 DNG CameraNeutral (1/gain 归一化)
    float cm0 = colordata.cam_mul[0], cm1 = colordata.cam_mul[1], cm2 = colordata.cam_mul[2];
    if (cm0 > 0 && cm1 > 0 && cm2 > 0)
    {
        // CameraNeutral = 1/cam_mul 归一化
        dng_vector neutral(3);
        neutral[0] = 1.0 / cm0;
        neutral[1] = 1.0 / cm1;
        neutral[2] = 1.0 / cm2;
        // 归一化使 G=1
        real64 g = neutral[1];
        if (g > 0) { neutral[0] /= g; neutral[1] = 1.0; neutral[2] /= g; }
        negative->SetCameraNeutral(neutral);
    }

    // ── 相机色彩配置文件 ──
    // DNG 规范: 彩色 DNG 必需 ColorMatrix (XYZ D50 → 参考相机空间)。
    //
    // 策略 (2026-08-13 验证):
    //  1. DNG 输入 (idata.dng_version > 0): 从 LibRaw tiff_ifd[].dng_color 无损复制
    //     Adobe 原始 ColorMatrix1/2 + ForwardMatrix1/2 + CameraCalibration1/2
    //     (实测与 Adobe 标签逐位一致, 100% 无损保留!)
    //  2. 私有 RAW: dcraw 内置 cam_xyz = Adobe ColorMatrix2 (D65) 本身 (实测数值一致)
    //     → 直接作为 ColorMatrix2 (illum D65); ColorMatrix1 (illum A) 用 Bradford
    //     色适应近似推导 (优于旧版 Invert(cam_xyz) 的错误方向)
    const bool isDngInput = (processor.imgdata.idata.dng_version > 0);
    {
        bool profileOk = false;

        if (isDngInput)
        {
            // ── 方案 1: DNG 输入, 从 tiff_ifd 无损复制 ──
            tiff_ifd_t* tiff = processor.Tiff();
            libraw_dng_color_t* dc0 = nullptr, * dc1 = nullptr;
            for (int ifd = 0; ifd < 4 && (!dc0 || !dc1); ifd++)
            {
                if (!dc0 && (tiff[ifd].dng_color[0].parsedfields & LIBRAW_DNGFM_COLORMATRIX))
                    dc0 = &tiff[ifd].dng_color[0];
                if (!dc1 && (tiff[ifd].dng_color[1].parsedfields & LIBRAW_DNGFM_COLORMATRIX))
                    dc1 = &tiff[ifd].dng_color[1];
            }
            if (dc0)
            {
                try
                {
                    AutoPtr<dng_camera_profile> profile(new dng_camera_profile);
                    // ColorMatrix1 (illum A 或 DNG 自带光源)
                    dng_matrix_3by3 cm1(dc0->colormatrix[0][0], dc0->colormatrix[0][1], dc0->colormatrix[0][2],
                                        dc0->colormatrix[1][0], dc0->colormatrix[1][1], dc0->colormatrix[1][2],
                                        dc0->colormatrix[2][0], dc0->colormatrix[2][1], dc0->colormatrix[2][2]);
                    profile->SetColorMatrix1(cm1);
                    profile->SetCalibrationIlluminant1(
                        dc0->illuminant == 17 ? lsStandardLightA : lsD65);
                    // ForwardMatrix1
                    if (dc0->parsedfields & LIBRAW_DNGFM_FORWARDMATRIX)
                    {
                        dng_matrix_3by3 fm1(dc0->forwardmatrix[0][0], dc0->forwardmatrix[0][1], dc0->forwardmatrix[0][2],
                                            dc0->forwardmatrix[1][0], dc0->forwardmatrix[1][1], dc0->forwardmatrix[1][2],
                                            dc0->forwardmatrix[2][0], dc0->forwardmatrix[2][1], dc0->forwardmatrix[2][2]);
                        profile->SetForwardMatrix1(fm1);
                    }
                    // CameraCalibration1 (在 negative 上设置)
                    if (dc0->parsedfields & LIBRAW_DNGFM_CALIBRATION)
                    {
                        dng_matrix_3by3 cal1(dc0->calibration[0][0], dc0->calibration[0][1], dc0->calibration[0][2],
                                             dc0->calibration[1][0], dc0->calibration[1][1], dc0->calibration[1][2],
                                             dc0->calibration[2][0], dc0->calibration[2][1], dc0->calibration[2][2]);
                        negative->SetCameraCalibration1(cal1);
                    }
                    // ColorMatrix2 + ForwardMatrix2 (D65)
                    if (dc1)
                    {
                        dng_matrix_3by3 cm2(dc1->colormatrix[0][0], dc1->colormatrix[0][1], dc1->colormatrix[0][2],
                                            dc1->colormatrix[1][0], dc1->colormatrix[1][1], dc1->colormatrix[1][2],
                                            dc1->colormatrix[2][0], dc1->colormatrix[2][1], dc1->colormatrix[2][2]);
                        profile->SetColorMatrix2(cm2);
                        profile->SetCalibrationIlluminant2(
                            dc1->illuminant == 21 ? lsD65 : lsStandardLightA);
                        if (dc1->parsedfields & LIBRAW_DNGFM_FORWARDMATRIX)
                        {
                            dng_matrix_3by3 fm2(dc1->forwardmatrix[0][0], dc1->forwardmatrix[0][1], dc1->forwardmatrix[0][2],
                                                dc1->forwardmatrix[1][0], dc1->forwardmatrix[1][1], dc1->forwardmatrix[1][2],
                                                dc1->forwardmatrix[2][0], dc1->forwardmatrix[2][1], dc1->forwardmatrix[2][2]);
                            profile->SetForwardMatrix2(fm2);
                        }
                        if (dc1->parsedfields & LIBRAW_DNGFM_CALIBRATION)
                        {
                            dng_matrix_3by3 cal2(dc1->calibration[0][0], dc1->calibration[0][1], dc1->calibration[0][2],
                                                 dc1->calibration[1][0], dc1->calibration[1][1], dc1->calibration[1][2],
                                                 dc1->calibration[2][0], dc1->calibration[2][1], dc1->calibration[2][2]);
                            negative->SetCameraCalibration2(cal2);
                        }
                    }
                    if (lookOn && look.hasCurve)
                        {
                        profile->SetToneCurve (look.curve);
                        if (look.hasPolicy) profile->SetEmbedPolicy (look.policy);
                        lookApplied = true;
                        }
                    negative->AddProfile(profile);
                    profileOk = true;
                    if (verbose)
                        fprintf(stderr, "[dngtool-e] DNG input: copied ColorMatrix1/2 + ForwardMatrix1/2 + Calibration1/2 (无损保留)\n");
                }
                catch (...) { /* 回退到方案 2 */ }
            }
        }

        if (!profileOk)
        {
            // ── 方案 2: 私有 RAW, dcraw 内置矩阵 (cam_xyz = Adobe ColorMatrix2 D65) ──
            float cm[9] = {
                colordata.cam_xyz[0][0], colordata.cam_xyz[0][1], colordata.cam_xyz[0][2],
                colordata.cam_xyz[1][0], colordata.cam_xyz[1][1], colordata.cam_xyz[1][2],
                colordata.cam_xyz[2][0], colordata.cam_xyz[2][1], colordata.cam_xyz[2][2]
            };
            bool valid = true;
            for (int i = 0; i < 9; i++)
                if (!(cm[i] > -100.0 && cm[i] < 100.0)) { valid = false; break; }

            if (valid)
            {
                try
                {
                    AutoPtr<dng_camera_profile> profile(new dng_camera_profile);
                    dng_matrix_3by3 cm1b;   // ColorMatrix1 (Bradford 近似), 块外供 ForwardMatrix1 使用
                    // cam_xyz = ColorMatrix2 (D65 校准), 直接使用
                    dng_matrix_3by3 cm2(cm[0], cm[1], cm[2],
                                        cm[3], cm[4], cm[5],
                                        cm[6], cm[7], cm[8]);
                    profile->SetColorMatrix2(cm2);
                    profile->SetCalibrationIlluminant2(lsD65);

                    // ColorMatrix1 (A 光): Bradford 色适应 D65→A 近似
                    // (Adobe 实测值与纯 Bradford 推导有偏差, 但方向/量级正确)
                    {
                        // Bradford 锥响应矩阵
                        static const real64 bradford[3][3] = {
                            { 0.8951,  0.2664, -0.1614 },
                            { -0.7502, 1.7135,  0.0367 },
                            { 0.0389, -0.0685,  1.0296 }
                        };
                        // D65 / A 白点 XYZ
                        static const real64 d65w[3] = { 0.95047, 1.0, 1.08883 };
                        static const real64 aw[3]  = { 1.09850, 1.0, 0.35585 };
                        // 锥响应
                        real64 sd[3] = { 0, 0, 0 }, sa[3] = { 0, 0, 0 };
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 3; c++) { sd[r] += bradford[r][c] * d65w[c]; sa[r] += bradford[r][c] * aw[c]; }
                        // 适应对角阵 D[i] = sa[i]/sd[i]
                        real64 d[3] = { sa[0]/sd[0], sa[1]/sd[1], sa[2]/sd[2] };
                        // M_adapt = B^-1 * diag(D) * B; 用伴随法求 B^-1
                        real64 detB = bradford[0][0]*(bradford[1][1]*bradford[2][2]-bradford[1][2]*bradford[2][1])
                                    - bradford[0][1]*(bradford[1][0]*bradford[2][2]-bradford[1][2]*bradford[2][0])
                                    + bradford[0][2]*(bradford[1][0]*bradford[2][1]-bradford[1][1]*bradford[2][0]);
                        real64 binv[3][3];
                        binv[0][0] = (bradford[1][1]*bradford[2][2]-bradford[1][2]*bradford[2][1])/detB;
                        binv[0][1] = (bradford[0][2]*bradford[2][1]-bradford[0][1]*bradford[2][2])/detB;
                        binv[0][2] = (bradford[0][1]*bradford[1][2]-bradford[0][2]*bradford[1][1])/detB;
                        binv[1][0] = (bradford[1][2]*bradford[2][0]-bradford[1][0]*bradford[2][2])/detB;
                        binv[1][1] = (bradford[0][0]*bradford[2][2]-bradford[0][2]*bradford[2][0])/detB;
                        binv[1][2] = (bradford[0][2]*bradford[1][0]-bradford[0][0]*bradford[1][2])/detB;
                        binv[2][0] = (bradford[1][0]*bradford[2][1]-bradford[1][1]*bradford[2][0])/detB;
                        binv[2][1] = (bradford[0][1]*bradford[2][0]-bradford[0][0]*bradford[2][1])/detB;
                        binv[2][2] = (bradford[0][0]*bradford[1][1]-bradford[0][1]*bradford[1][0])/detB;
                        // DB = diag(D)*B
                        real64 db[3][3];
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 3; c++) db[r][c] = d[r] * bradford[r][c];
                        // M_adapt = binv * DB
                        real64 madapt[3][3] = { {0,0,0},{0,0,0},{0,0,0} };
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 3; c++)
                                for (int k = 0; k < 3; k++) madapt[r][c] += binv[r][k] * db[k][c];
                        // ColorMatrix1 = ColorMatrix2 * M_adapt (近似)
                        real64 cm1a[3][3] = { {0,0,0},{0,0,0},{0,0,0} };
                        for (int r = 0; r < 3; r++)
                            for (int c = 0; c < 3; c++)
                                for (int k = 0; k < 3; k++) cm1a[r][c] += cm[r*3+k] * madapt[k][c];
                        cm1b = dng_matrix_3by3(cm1a[0][0], cm1a[0][1], cm1a[0][2],
                                               cm1a[1][0], cm1a[1][1], cm1a[1][2],
                                               cm1a[2][0], cm1a[2][1], cm1a[2][2]);
                        profile->SetColorMatrix1(cm1b);
                        profile->SetCalibrationIlluminant1(lsStandardLightA);
                    }

                    // ForwardMatrix2 = Invert(ColorMatrix2) 行归一化到 D50 白点
                    // (DNG 规范: ForwardMatrix 把相机空间转回 XYZ D50)
                    {
                        // ForwardMatrix = Invert(ColorMatrix) 行归一化到 D50 白点
                        // (DNG 规范: ForwardMatrix 把相机空间转回 XYZ D50)
                        dng_matrix_3by3 cm2m(cm[0], cm[1], cm[2],
                                             cm[3], cm[4], cm[5],
                                             cm[6], cm[7], cm[8]);
                        dng_matrix camToXyz = Invert(cm2m);
                        const real64 d50[3] = { 0.9642, 1.0, 0.8249 };
                        for (int r = 0; r < 3; r++)
                        {
                            real64 rowSum = 0;
                            for (int c = 0; c < 3; c++) rowSum += camToXyz[r][c];
                            if (rowSum > 0.001)
                            {
                                real64 scale = d50[r] / rowSum;
                                for (int c = 0; c < 3; c++) camToXyz[r][c] *= scale;
                            }
                        }
                        profile->SetForwardMatrix2(camToXyz);
                        // ForwardMatrix1 同法 (从近似 ColorMatrix1)
                        dng_matrix camToXyz1 = Invert(cm1b);
                        for (int r = 0; r < 3; r++)
                        {
                            real64 rowSum = 0;
                            for (int c = 0; c < 3; c++) rowSum += camToXyz1[r][c];
                            if (rowSum > 0.001)
                            {
                                real64 scale = d50[r] / rowSum;
                                for (int c = 0; c < 3; c++) camToXyz1[r][c] *= scale;
                            }
                        }
                        profile->SetForwardMatrix1(camToXyz1);
                    }

                    // -idc-look: 曲线直接设在主 profile 上 (规范里 ProfileToneCurve 的 Usage
                    // 就是 "IFD0 or Camera Profile IFD") ⇒ 不走 extra profile 那条实测会溢出的预算。
                    // 分通道曲线不走这里（走 OpcodeList3, 见 ApplyChannelOpcodes）。
                    if (lookOn && look.hasCurve)
                        {
                        profile->SetToneCurve (look.curve);
                        if (look.hasPolicy) profile->SetEmbedPolicy (look.policy);
                        lookApplied = true;
                        }
                    negative->AddProfile(profile);
                    if (verbose)
                        fprintf(stderr, "[dngtool-e] private RAW: ColorMatrix2=dcraw(D65, 同Adobe) + ColorMatrix1=Bradford近似(A)\n");
                }
                catch (...)
                {
                    if (verbose)
                        fprintf(stderr, "[dngtool-e] WARNING: 矩阵处理失败, 跳过 ColorMatrix\n");
                }
            }
        }

        // ── BaselineExposure / AnalogBalance / LinearResponseLimit ──
        // DNG 输入: 从 dng_levels 无损保留; 私有 RAW: 无这些数据, 保持默认
        if (isDngInput)
        {
            tiff_ifd_t* tiff = processor.Tiff();
            libraw_dng_levels_t lv = tiff[0].dng_levels;
            if (lv.parsedfields & LIBRAW_DNGFM_BASELINEEXPOSURE)
                negative->SetBaselineExposure(lv.baseline_exposure);
            if (lv.parsedfields & LIBRAW_DNGFM_LINEARRESPONSELIMIT)
                negative->SetLinearResponseLimit(lv.LinearResponseLimit);
            if (lv.parsedfields & LIBRAW_DNGFM_ANALOGBALANCE)
            {
                dng_vector ab(4);
                ab[0] = lv.analogbalance[0]; ab[1] = lv.analogbalance[1];
                ab[2] = lv.analogbalance[2]; ab[3] = 1.0;
                negative->SetAnalogBalance(ab);
            }
            if (verbose)
                fprintf(stderr, "[dngtool-e] DNG input: baseline_exposure=%.3f LRL=%.2f (无损保留)\n",
                        lv.baseline_exposure, lv.LinearResponseLimit);
        }
    }

    // 嵌入件是**整个原文件** ⇒ 源里的 GPS 随件在档。不改它：改过的嵌入件就不是原件,
    // 而规范 50828 要存的正是原件; 整条搬运也不该为一个标签就关掉 (降级不是修复)。
    // 代价响亮点名: 容器层的 GPS 仍由产品侧复制阶段的 absorb 排除项处理,
    // 要严格剥除走已有的 -no-raw-meta (拍摄字段/嵌入原件/预览一起关)。
    if (!isDngInput && carryRawMetadata)
    {
        std::vector<uint8> gb;
        std::string gwhy;
        if (TagBytesFromPath (inputPath, 0x8825, gb, gwhy))
            fprintf (stderr, "[dngtool-e] gps-in-embedded-original=1 源含 GPS (IFD0 0x8825) ⇒ 嵌入的原件内含同一份 GPS "
                            "(按规范存原件, 不删改) —— 产物可见标签仍无 GPS; "
                            "要严格剥除请用 -no-raw-meta 关掉整条搬运\n");
    }

    // ── 元数据搬运 (仅非 DNG 输入) ──
    // DNG→DNG 走上面的标签无损复制路径, 本轮不动它 ⇒ 现有 DNG 门禁基线不受影响。
    dng_preview_list previews;
    uint32 previewCount = 0;
    if (!isDngInput && carryRawMetadata)
    {
        FillExifCapture(exif, processor.imgdata.other, processor.imgdata.lens, verbose);

        uint64 fsize = 0;
        {
            FILE* fp = fopen(inputPath, "rb");
            if (fp)
            {
                fseek(fp, 0, SEEK_END);
#ifdef _WIN32
                fsize = (uint64)_ftelli64(fp);
#else
                fsize = (uint64)ftello(fp);
#endif
                fclose(fp);
            }
        }

        RawMetaSlabs slabs;
        ReadRawMetaWindows(inputPath, fsize, slabs);

        // 规范 50828: 把**整个原文件**按 ZIP 分块嵌进 DNG (原件就是权威载体,
        // 厂商注记/SR2/IDC 都以它自己的格式原样在档, 任何 conformant 读者都能取出)。
        // 这一步不依赖 slab —— slab 只服务下面的预览定位与改动点名。
        uint64 rawSize = 0;
        std::string owhy;
        AutoPtr<dng_memory_block> orig = BuildOriginalRawFileData (host, inputPath, rawSize, owhy);
        if (orig.Get())
        {
            negative->SetOriginalRawFileName (BaseNameOf (inputPath));
            uint32 encLen = orig->LogicalSize();
            negative->SetOriginalRawFileData (orig);            // 接管所有权
            negative->SetHasOriginalRawFileData (true);
            fprintf(stderr, "[dngtool-e] 原文件按规范嵌入 OriginalRawFileData: %llu B -> %llu B (%.1f%%)\n",
                    (unsigned long long) rawSize, (unsigned long long) encLen,
                    rawSize ? 100.0 * (double) encLen / (double) rawSize : 0.0);
            if (verbose && encLen >= rawSize)
                fprintf(stderr, "[dngtool-e]   (索尼原件已是无损压缩 ⇒ ZIP 再压不省, 这是规范载体的固有代价)\n");
        }
        else
            fprintf(stderr, "[dngtool-e] WARNING: 原文件未嵌入 (%s) ⇒ 厂商注记/SR2/IDC 链不进产物\n",
                    owhy.empty() ? "未知原因" : owhy.c_str());

        if (!slabs.ok)
        {
            fprintf(stderr, "[dngtool-e] WARNING: 原文件的元数据区定位失败 (%s) ⇒ "
                            "预览/缩略图不内嵌、改动点名不发 (原文件嵌入不受影响)\n",
                    slabs.reason.empty() ? "未知原因" : slabs.reason.c_str());
        }
        else
        {
            // "源到底被改了什么": 版本 0 是机身写下的初始值, 生效版本是导出后的值。
            // 这一对比不需要任何标定 ⇒ 能把"只随原件在档、未映射到 DNG 标准位"从沉默变成点名。
            IdcInfo idc;
            ParseIdc (slabs, "arw", idc);
            if (idc.ok && !idc.changes.empty ())
            {
                uint32 shown = 0;
                // idc-changed=<n> 是给机器读的 ASCII 键 —— 门禁要复核这个数, 但中文串在非 UTF-8
                // 码页的机器上到读者手里会变形成别的字节, 拿中文当解析锚点会假红。
                fprintf (stderr, "[dngtool-e] idc-changed=%u 源声明 %u 项参数相对初始版本被改动 ⇒ "
                                "只随原件在档、未映射到 DNG 标准位: ",
                         (unsigned) idc.changes.size (), (unsigned) idc.changes.size ());
                for (size_t i = 0; i < idc.changes.size () && shown < 8; i++)
                {
                    const IdcChange& c = idc.changes [i];
                    if (c.type == 2)
                        fprintf (stderr, " 0x%04X 文本改动", c.tag);
                    else if (c.from.size () == 1 && c.to.size () == 1)
                        fprintf (stderr, " 0x%04X %lld->%lld", c.tag,
                                 (long long) c.from [0], (long long) c.to [0]);
                    else
                        fprintf (stderr, " 0x%04X 多项(%u 值)", c.tag, (unsigned) c.to.size ());
                    shown++;
                }
                if (idc.changes.size () > shown)
                    fprintf (stderr, " ... (共 %u 项, 全表见 dngtool -idc)", (unsigned) idc.changes.size ());
                fprintf (stderr, "\n");
            }

            // 预览与缩略图按原字节挂成 DNG preview (嵌入的原件里还有第二份, 刻意保留)
            std::vector<uint8> pj, tj;
            if (slabs.previewLen) ReadRange(inputPath, slabs.previewOff, slabs.previewLen, pj);
            if (slabs.thumbLen)   ReadRange(inputPath, slabs.thumbOff,   slabs.thumbLen,   tj);
            if (!pj.empty() && AppendJpegPreview(host, previews, pj, true,  verbose)) previewCount++;
            if (!tj.empty() && AppendJpegPreview(host, previews, tj, false, verbose)) previewCount++;
            if (verbose && pj.empty() && tj.empty())
                fprintf(stderr, "[dngtool-e] 源文件里没有可解析的 JPEG 预览/缩略图\n");
        }
    }
    else if (isDngInput && carryRawMetadata)
    {
        // DNG→DNG: 上一轮按规范嵌进 50828 的原文件必须跟着过重编码,
        // 否则"保真通道"在一次再压缩后就静默消失。
        std::vector<uint8> ob;
        std::string owhy;
        if (TagBytesFromPath (inputPath, 0xC68C, ob, owhy))     // 50828 OriginalRawFileData
        {
            AutoPtr<dng_memory_block> blk (host.Allocate ((uint32) ob.size ()));
            if (blk.Get ())
            {
                memcpy (blk->Buffer (), ob.data (), ob.size ());
                std::vector<uint8> nb;
                std::string nwhy;
                if (TagBytesFromPath (inputPath, 0xC68B, nb, nwhy) && !nb.empty ())
                {
                    while (!nb.empty () && nb.back () == 0) nb.pop_back ();
                    negative->SetOriginalRawFileName ((const char *) nb.data ());
                }
                else
                    negative->SetOriginalRawFileName (BaseNameOf (inputPath));
                negative->SetOriginalRawFileData (blk);          // 接管所有权
                negative->SetHasOriginalRawFileData (true);
                fprintf (stderr, "[dngtool-e] 输入已嵌原文件 ⇒ 按规范带过重编码 (50828 %u B)\n",
                         (unsigned) ob.size ());
            }
        }
        else if (verbose)
            fprintf (stderr, "[dngtool-e] 输入 DNG 无 50828 原文件 (%s)\n", owhy.c_str ());
    }
    else if (!isDngInput && !carryRawMetadata && verbose)
        fprintf(stderr, "[dngtool-e] -no-raw-meta: 放弃源 RAW 保真搬运 (拍摄字段/嵌入原文件/预览均不写)\n");

    // ── 构造 dng_image ──
    // 数据源: raw_alloc/raw_image (16-bit Bayer 或 linear) 或
    //         float3_image/color3_image (float linear, 如 JXL-DNG float 输入)
    uint32 planes = isBayer ? 1 : 3;
    bool isFloatData = false;
    bool sourceIsFloat = false;
    void* rawSrc = (void*)processor.imgdata.rawdata.raw_alloc;
    uint32 srcPitch = S.raw_pitch;

    if (!rawSrc && processor.imgdata.rawdata.raw_image)
    {
        rawSrc = processor.imgdata.rawdata.raw_image;
        srcPitch = S.raw_pitch;
    }
    if (!rawSrc && processor.imgdata.rawdata.float3_image)
    {
        rawSrc = processor.imgdata.rawdata.float3_image;
        srcPitch = S.raw_pitch;
        isFloatData = true;
        sourceIsFloat = true;
    }
    if (!rawSrc && processor.imgdata.rawdata.color3_image)
    {
        rawSrc = processor.imgdata.rawdata.color3_image;
        srcPitch = S.raw_pitch;
    }

    if (!rawSrc)
    {
        fprintf(stderr, "[dngtool-e] ERROR: no raw buffer available\n");
        processor.recycle();
        return 5;
    }

    // ── 调试: 打印编码输入数据 (验证 G/B 平面) ──
    if (verbose)
    {
        const ushort* dbg = (const ushort*)rawSrc;
        fprintf(stderr, "[dngtool-e] rawSrc=%p srcPitch=%u raw_pitch=%u planes=%u isFloat=%d\n",
                rawSrc, srcPitch, S.raw_pitch, planes, (int)isFloatData);
        if (!isFloatData)
        {
            fprintf(stderr, "[dngtool-e] input row0: ");
            for (int i = 0; i < 6; i++)
                fprintf(stderr, "(%u,%u,%u) ", dbg[i*3], dbg[i*3+1], dbg[i*3+2]);
            fprintf(stderr, "\n");
            const ushort* dbg1 = (const ushort*)((const char*)rawSrc + srcPitch);
            fprintf(stderr, "[dngtool-e] input row1: ");
            for (int i = 0; i < 4; i++)
                fprintf(stderr, "(%u,%u,%u) ", dbg1[i*3], dbg1[i*3+1], dbg1[i*3+2]);
            fprintf(stderr, "\n");
        }
    }

    // 线性 DNG 输出: 32-bit float (JXL 压缩) 或 8/16-bit 整数 (2026-08-14)
    const uint32 outPixelType = outFloat ? ttFloat : (outputBps <= 8 ? ttByte : ttShort);
    const bool out8Bit = !outFloat && outputBps <= 8;

    AutoPtr<dng_image> img(host.Make_dng_image(
        dng_rect(0, 0, rawHeight, rawWidth), planes, outPixelType));

    // 拷贝 LibRaw raw 数据
    {
        const uint32 rowBytes = (uint32)rawWidth * planes * 2;
        const uint32 pixelStride = isFloatData ? 1 : planes;

        for (int y = 0; y < rawHeight; y++)
        {
            if (outFloat)
            {
                // 32-bit float 输出: 3 平面 RGB [0..1]
                float* dstRow = new float[(size_t)rawWidth * 3];
                if (isFloatData)
                {
                    // float 源 → 直接拷贝
                    const float* srcRow = (const float*)((const char*)rawSrc + (uint64)y * srcPitch);
                    memcpy(dstRow, srcRow, (size_t)rawWidth * 3 * sizeof(float));
                }
                else
                {
                    // ushort 源 → float [0..1]
                    const ushort* srcRow16 = (const ushort*)((const char*)rawSrc + (uint64)y * srcPitch);
                    for (int x = 0; x < rawWidth; x++)
                    {
                        dstRow[x * 3 + 0] = srcRow16[x * 3 + 0] / 65535.0f;
                        dstRow[x * 3 + 1] = srcRow16[x * 3 + 1] / 65535.0f;
                        dstRow[x * 3 + 2] = srcRow16[x * 3 + 2] / 65535.0f;
                    }
                }

                dng_pixel_buffer row;
                row.fArea = dng_rect(y, 0, y + 1, rawWidth);
                row.fPlane = 0;
                row.fPlanes = 3;               // ⚠️ 必须设置: 默认 fPlanes=1 只写 R 平面!
                row.fRowStep = (int32)(rawWidth * 3 * 4);
                row.fColStep = 3;
                row.fPlaneStep = 1;
                row.fPixelType = ttFloat;
                row.fData = dstRow;
                img->Put(row);
                delete[] dstRow;
            }
            else if (isFloatData)
            {
                // float 源 → 16-bit 或 8-bit 输出
                const float* srcRow = (const float*)((const char*)rawSrc + (uint64)y * srcPitch);
                if (out8Bit)
                {
                    uint8* dstRow = new uint8[(size_t)rawWidth * 3];
                    for (int x = 0; x < rawWidth; x++)
                    {
                        float r = srcRow[x * 3 + 0];
                        float g = srcRow[x * 3 + 1];
                        float b = srcRow[x * 3 + 2];
                        dstRow[x * 3 + 0] = (uint8)(r < 0 ? 0 : (r > 1.0f ? 255 : (uint8)(r * 255.0f)));
                        dstRow[x * 3 + 1] = (uint8)(g < 0 ? 0 : (g > 1.0f ? 255 : (uint8)(g * 255.0f)));
                        dstRow[x * 3 + 2] = (uint8)(b < 0 ? 0 : (b > 1.0f ? 255 : (uint8)(b * 255.0f)));
                    }
                    dng_pixel_buffer row;
                    row.fArea = dng_rect(y, 0, y + 1, rawWidth);
                    row.fPlane = 0;
                    row.fPlanes = 3;               // ⚠️ 必须设置: 默认 fPlanes=1 只写 R 平面!
                    row.fRowStep = (int32)(rawWidth * 3);
                    row.fColStep = 3;
                    row.fPlaneStep = 1;
                    row.fPixelType = ttByte;
                    row.fData = dstRow;
                    img->Put(row);
                    delete[] dstRow;
                }
                else
                {
                    // float 源 → 16-bit 输出
                    ushort* dstRow = new ushort[(size_t)rawWidth * 3];
                    for (int x = 0; x < rawWidth; x++)
                    {
                        float r = srcRow[x * 3 + 0];
                        float g = srcRow[x * 3 + 1];
                        float b = srcRow[x * 3 + 2];
                        dstRow[x * 3 + 0] = (ushort)(r < 0 ? 0 : (r > 1.0f ? 65535 : (ushort)(r * 65535.0f)));
                        dstRow[x * 3 + 1] = (ushort)(g < 0 ? 0 : (g > 1.0f ? 65535 : (ushort)(g * 65535.0f)));
                        dstRow[x * 3 + 2] = (ushort)(b < 0 ? 0 : (b > 1.0f ? 65535 : (ushort)(b * 65535.0f)));
                    }
                    dng_pixel_buffer row;
                    row.fArea = dng_rect(y, 0, y + 1, rawWidth);
                    row.fPlane = 0;
                    row.fPlanes = 3;               // ⚠️ 必须设置: 默认 fPlanes=1 只写 R 平面!
                    row.fRowStep = (int32)(rawWidth * 3 * 2);
                    row.fColStep = 3;
                    row.fPlaneStep = 1;
                    row.fPixelType = ttShort;
                    row.fData = dstRow;
                    img->Put(row);
                    delete[] dstRow;
                }
            }
            else
            {
                // 16-bit: raw_alloc (Bayer 1平面 或 linear 3平面交错)
                const ushort* srcRow16 = (const ushort*)((const char*)rawSrc + (uint64)y * srcPitch);
                if (out8Bit)
                {
                    // 16-bit 源 → 8-bit 输出 (取高 8 位)
                    uint8* dstRow = new uint8[(size_t)rawWidth * planes];
                    for (int x = 0; x < rawWidth * (int)planes; x++)
                        dstRow[x] = (uint8)(srcRow16[x] >> 8);
                    dng_pixel_buffer row;
                    row.fArea = dng_rect(y, 0, y + 1, rawWidth);
                    row.fPlane = 0;
                    row.fPlanes = planes;          // ⚠️ 必须设置: 默认 fPlanes=1 只写 R 平面!
                    row.fRowStep = (int32)(rawWidth * planes);
                    row.fColStep = pixelStride;
                    row.fPlaneStep = 1;
                    row.fPixelType = ttByte;
                    row.fData = dstRow;
                    img->Put(row);
                    delete[] dstRow;
                }
                else
                {
                    dng_pixel_buffer row;
                    row.fArea = dng_rect(y, 0, y + 1, rawWidth);
                    row.fPlane = 0;
                    row.fPlanes = planes;          // ⚠️ 必须设置: 默认 fPlanes=1 只写 R 平面!
                    row.fRowStep = (int32)rowBytes;
                    row.fColStep = pixelStride;
                    row.fPlaneStep = 1;
                    row.fPixelType = ttShort;
                    row.fData = (void*)((const char*)rawSrc + (uint64)y * srcPitch);
                    img->Put(row);
                }
            }
        }
    }

    // 设置 stage1 图像
    AutoPtr<dng_image> stage1(img.Release());
    negative->SetStage1Image(stage1);

    // ── `-idc-look` 兜底: 两处 profile 都没建成时, 单建一条只装曲线的 ──
    if (lookOn && look.hasCurve && !lookApplied)
        {
        ApplyLookFallback (*negative, look);
        }
    if (lookOn && look.hasCurve)
        fprintf (stderr, "[dngtool-e] ProfileToneCurve 已写入主 profile: 控制点=%u 首点=%.6g/%.6g 末点=%.6g/%.6g\n",
                 (unsigned) look.curve.fCoord.size (),
                 look.curve.fCoord [0].h, look.curve.fCoord [0].v,
                 look.curve.fCoord [look.curve.fCoord.size () - 1].h,
                 look.curve.fCoord [look.curve.fCoord.size () - 1].v);

    // ── 分通道曲线 → 规范 51022 OpcodeList3（post demosaic, 三支表各管一个 plane）──
    if (lookOn && look.hasChannels && negative->Stage1Image ())
        ApplyChannelOpcodes (host, *negative, look, negative->Stage1Image ()->Bounds (), verbose);

    // ── `-lens-corr`: 源 RAW 的镜头三表 → 规范 51022 OpcodeList3 ──
    // 读不到 / 机内开关关 / 恒等 / 残差超上界 / 映射非单调 ⇒ ApplyLensOpcodes 逐条点名并一条不写;
    // 这里只补"整件没有这三张表"那一层, 让非索尼源也讲得清为什么没动。
    if (lensCorr && negative->Stage1Image ())
        {
        LensTables lt;
        if (ReadLensTables (inputPath, lt, verbose))
            {
            uint32 n = ApplyLensOpcodes (*negative, lt, negative->Stage1Image ()->Bounds (), verbose);
            if (n == 0)
                fprintf (stderr, "[dngtool-e] lens-opcodes=0 (表在盘上但每条臂都被点名拒绝)\n");
            }
        else
            fprintf (stderr, "[dngtool-e] lens-tables=absent (%s) ⇒ 本件没有可写的标准镜头校正, 像素不动\n",
                     lt.why.c_str ());
        }

    // ── 写 DNG ──
    if (verbose) fprintf(stderr, "[dngtool-e] writing %s ...\n", outputPath);

    dng_file_stream stream(outputPath, true);
    dng_image_writer writer;

    uint32 maxBackwardVersion = dngVersion_1_7_0_0;
    bool uncompressed = false;

    if (compression == 1) // JXL
    {
        // JXL 压缩: 用 dng_jxl_image::Encode 压缩原始数据, 再挂载到 negative
        // 无损: LosslessMosaic (Bayer) / LosslessMainImage (linear)
        AutoPtr<dng_jxl_image> jxlImage(new dng_jxl_image);

        dng_host::use_case_enum useCase;
        if (quality <= 0)
            useCase = isBayer ? dng_host::use_case_LosslessMosaic
                              : dng_host::use_case_LosslessMainImage;
        else
            useCase = isBayer ? dng_host::use_case_LossyMosaic
                              : dng_host::use_case_MainImage;

        // 显式构造编码设置: 使 effort / decodeSpeed 真正作用于 libjxl 编码器。
        // (useCase 重载内部固定 effort=7 且不写 decodeSpeed, 参数会被忽略)
        AutoPtr<dng_jxl_encode_settings> settings (new dng_jxl_encode_settings);
        settings->SetDistance ((quality <= 0) ? 0.0f
                                              : (float)((100 - quality) * 15.0 / 100.0));
        settings->SetEffort ((jxlEffort >= 1 && jxlEffort <= 9) ? (uint32)jxlEffort : 7);
        settings->SetDecodeSpeed ((uint32)jxlDecodeSpeed);   // SDK Pin 0-4
        if (quality <= 0)
            settings->SetUseOriginalColorEncoding (true);

        jxlImage->Encode(host, writer, *negative->Stage1Image(), *settings);

        // DNG 1.7 规范标签 (JXLDistance / JXLEffort / JXLDecodeSpeed)
        // 由 EncodeTiles 自动从 settings 写入, 无需手动设置
        // (Adobe Camera Raw 校验必需; 缺省会省略标签导致 ACR 拒绝打开)

        AutoPtr<dng_lossy_compressed_image> lossy(jxlImage.Release());
        negative->SetRawLossyCompressedImage(lossy);

        if (verbose) fprintf(stderr, "[dngtool-e] JXL compression (q=%d, distance=%.2f, effort=%u, decodeSpeed=%u, useCase=%d)\n",
                             quality, settings->Distance(), settings->Effort(),
                             settings->DecodeSpeed(), (int)useCase);
    }
    else
    {
        // 默认: lossless JPEG (ccJPEG)
        if (verbose) fprintf(stderr, "[dngtool-e] lossless JPEG compression\n");
    }

    try
    {
        writer.WriteDNG(host, stream, *negative.Get(),
                        previewCount ? &previews : NULL,
                        maxBackwardVersion, uncompressed, true);
        stream.Flush();
    }
    catch (dng_exception& e)
    {
        fprintf(stderr, "[dngtool-e] ERROR: write failed (error code %d)\n",
                (int)e.ErrorCode());
        processor.recycle();
        return 6;
    }
    catch (...)
    {
        fprintf(stderr, "[dngtool-e] ERROR: write failed (unknown)\n");
        processor.recycle();
        return 6;
    }

    if (verbose) fprintf(stderr, "[dngtool-e] done.\n");
    processor.recycle();
    return 0;
#else
    fprintf(stderr, "[dngtool-e] ERROR: compiled without DNG SDK\n");
    return 1;
#endif
}

int main(int argc, char* argv[])
{
    if (argc < 2)
    {
        PrintUsage(argv[0]);
        return 0;
    }

    // ── 引擎列表 ──
    if (strcmp(argv[1], "-engines") == 0)
    {
        printf("engines:\n");
        printf("  libraw:        yes\n");
#ifdef USE_DNGSDK
        printf("  dngsdk:        yes (DNG 1.7 JXL support)\n");
#else
        printf("  dngsdk:        no (compiled without USE_DNGSDK)\n");
#endif
        return 0;
    }

    // ── 参数解析 ──
    std::string inputPath, outputPath;
    std::string idcPath;
    int outputColor = 0, quality = 3, useCameraWb = 1, highlight = 1;
    bool decodeMode = false, encodeMode = false, verbose = false;
    bool verifyMode = false;
    int encodeCompression = 0;  // 0=lossless JPEG, 1=JXL
    bool carryRawMetadata = true;   // 源 RAW 元数据搬运开关 (默认开)
    std::string lookSpec;           // -idc-look <file>: 给主 profile 写 ProfileToneCurve 的规格 (默认不写)
    bool lensCorr = false;          // -lens-corr: 源镜头三表 → 规范 OpcodeList3 (默认关)
    int jxlQuality = 0;         // JXL 质量 (0=无损)
    bool forceLinear = false;
    int jxlEffort = 7;          // JXL 编码努力 (1-9)
    int jxlDecodeSpeed = 4;     // JXL 解码速度提示 (DNG 规范 1-4)
    int outputBps = 16;         // 输出位深 (8/16), 2026-08-14
    uint32 threadCount = 0;     // 多线程数 (0=自动, 2026-08-15)

    for (int i = 1; i < argc; i++)
    {
        const char* a = argv[i];
        if (strcmp(a, "-d") == 0) decodeMode = true;
        else if (strcmp(a, "-e") == 0) encodeMode = true;
        else if (strcmp(a, "-verify") == 0) verifyMode = true;
        else if (strcmp(a, "-info") == 0 && i + 1 < argc) { inputPath = argv[++i]; }
        else if (strcmp(a, "-idc") == 0 && i + 1 < argc) { idcPath = argv[++i]; }
        else if (strcmp(a, "-i") == 0 && i + 1 < argc) inputPath = argv[++i];
        else if (strcmp(a, "-O") == 0 && i + 1 < argc) outputPath = argv[++i];
        else if (strcmp(a, "-o") == 0 && i + 1 < argc) outputColor = atoi(argv[++i]);
        else if (strcmp(a, "-q") == 0 && i + 1 < argc) quality = atoi(argv[++i]);
        else if (strcmp(a, "-W") == 0) useCameraWb = 1;
        else if (strcmp(a, "-w") == 0) useCameraWb = 0;
        else if (strcmp(a, "-H") == 0 && i + 1 < argc) highlight = atoi(argv[++i]);
        else if (strcmp(a, "-4") == 0) outputBps = 8;   // 8-bit 输出 (2026-08-14)
        else if (strcmp(a, "-6") == 0) outputBps = 16;  // 16-bit 输出
        else if (strcmp(a, "-T") == 0) { /* TIFF 输出固定 */ }
        else if (strcmp(a, "-v") == 0) verbose = true;
        else if (strcmp(a, "-threads") == 0 && i + 1 < argc) { threadCount = (uint32)atoi(argv[++i]); if (threadCount < 1) threadCount = 1; }
        else if (strcmp(a, "-lossless") == 0) encodeCompression = 0;
        else if (strcmp(a, "-jxl") == 0) encodeCompression = 1;
        else if (strcmp(a, "-linear") == 0) forceLinear = true;
        else if (strcmp(a, "-no-raw-meta") == 0) carryRawMetadata = false;
        else if (strcmp(a, "-idc-look") == 0 && i + 1 < argc) lookSpec = argv[++i];
        else if (strcmp(a, "-lens-corr") == 0) lensCorr = true;
        else if (strcmp(a, "-effort") == 0 && i + 1 < argc) jxlEffort = atoi(argv[++i]);
        else if (strcmp(a, "-decode_speed") == 0 && i + 1 < argc) jxlDecodeSpeed = atoi(argv[++i]);
        // ⚠⚠ 2026-09-21：**JXL 质量独立参数**（`-jxlq`，0 = 无损，默认 0）。
        //   原先 JXL 质量**复用 `-q`**（= demosaic 质量，默认 **3**）⇒
        //   产品传 `-jxl` 不带 `-q` 时 `jxlQuality` 被算成 **3**
        //   ⇒ `SetDistance((100-3)*15/100 = 14.55f)` ⇒ **严重有损**！
        //   实测：JXL 产物 raw 校验和 `sum=725637202`，而源与无损 JPEG 产物都是 `711851004`。
        else if (strcmp(a, "-jxlq") == 0 && i + 1 < argc) jxlQuality = atoi(argv[++i]);
        else if (strcmp(a, "-c") == 0) { /* 保留: stdout 输出 (未实现) */ }
        else
        {
            fprintf(stderr, "[dngtool] unknown option: %s\n", a);
            return 1;
        }
    }

    // ── 分发 ──
    if (verifyMode)
    {
        if (inputPath.empty())
        {
            fprintf(stderr, "[dngtool] -verify requires -i <file>\n");
            return 1;
        }
        return VerifyDng(inputPath.c_str(), threadCount);
    }

    if (encodeMode)
    {
        if (inputPath.empty() || outputPath.empty())
        {
            fprintf(stderr, "[dngtool] -e requires -i <input> and -O <output>\n");
            return 1;
        }
        // ⚠⚠ 2026-09-21：**不再用 `-q`（demosaic 质量）覆盖 JXL 质量** ——
        //   `jxlQuality` 现在**只由 `-jxlq` 设置**（默认 **0 = 无损**）✓
        //   （旧代码 `jxlQuality = (encodeCompression == 1) ? quality : 0;` 会让
        //    产品默认调用 `-jxl`（不带 `-q`）时误取 demosaic 默认值 3 ⇒ 有损）
        return EncodeDng(inputPath.c_str(), outputPath.c_str(),
                         encodeCompression, jxlQuality, forceLinear,
                         jxlEffort, jxlDecodeSpeed, highlight, outputBps,
                         threadCount, carryRawMetadata,
                         lookSpec.empty () ? NULL : lookSpec.c_str (), lensCorr, verbose);
    }

    if (!idcPath.empty())
        return PrintIdcInfo(idcPath.c_str());

    if (!inputPath.empty() && !decodeMode)
    {
        // -info 模式
        return PrintInfo(inputPath.c_str());
    }

    if (decodeMode)
    {
        if (inputPath.empty() || outputPath.empty())
        {
            fprintf(stderr, "[dngtool] -d requires -i <input> and -O <output>\n");
            return 1;
        }
        return Decode(inputPath.c_str(), outputPath.c_str(),
                      outputColor, quality, useCameraWb, highlight, verbose,
                      threadCount);
    }

    PrintUsage(argv[0]);
    return 0;
}
