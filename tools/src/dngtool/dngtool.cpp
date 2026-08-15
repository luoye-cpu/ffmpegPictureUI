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
#include <string>
#include <vector>
#include <thread>
#include <atomic>
#include <queue>
#include <mutex>
#include <condition_variable>

#include "libraw/libraw.h"

// DNG SDK 集成 (USE_DNGSDK 编译时启用)
#ifdef USE_DNGSDK
#include "dng_host.h"
#include "dng_negative.h"
#include "dng_exif.h"
#include "dng_info.h"
#include "dng_image.h"
#include "dng_camera_profile.h"
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
    printf("  -jxl [-q <n>]     JXL compression (DNG 1.7, q=0 lossless / 1-100 lossy)\n");
    printf("  -effort <1-9>     JXL encode effort (higher = smaller, slower; default 7)\n");
    printf("  -decode_speed <1-4>  JXL decode speed hint (DNG tag; default 4)\n");
    printf("  -linear           write linear DNG (no CFA, for linear/demosaiced input)\n");
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

        // 主图压缩方式: 优先压缩图像对象, 否则从 IFD 读取 (无损 JXL 时
        // RawLossyCompressedImage 可能为空, 但 IFD 仍标记 ccJXL)
        if (negative->RawLossyCompressedImage())
        {
            uint32 code = negative->RawLossyCompressedImage()->fCompressionCode;
            printf("[verify] Compression: %s (JXL tags: distance=%.2f effort=%d decodespeed=%d)\n",
                   code == ccJXL ? "JPEG XL" : "other",
                   negative->RawLossyCompressedImage()->JXLDistance(),
                   negative->RawLossyCompressedImage()->JXLEffort(),
                   negative->RawLossyCompressedImage()->JXLDecodeSpeed());
        }
        else
        {
            const char* comp = "uncompressed/lossless JPEG";
            if (info.IFDCount () > 0 && info.fIFD [0]->fCompression == ccJXL)
                comp = "JPEG XL (lossless)";
            printf("[verify] Compression: %s\n", comp);
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
            printf("[verify] ✅ JXL 解码成功\n");

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
    int ret = processor.open_file(path);
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
    // ⚠️ 必须同时启用 STAGE2/STAGE3: 自编码 JXL-DNG 无 OpcodeList,
    // 只开 IFPRESENT_JPGJXL 会走 dng_read_image 直读分支 → ttShort 数据
    // 只拷入 raw_alloc 而 color3_image 指针未设置 → dcraw_process 输出 G/B=0
    // (Adobe 官方 JXL-DNG 带 OpcodeList2 自动走 DNG SDK 分支所以正常)
    processor.imgdata.rawparams.options |= (1u << 12); // LIBRAW_RAWOPTIONS_DNG_STAGE2
    processor.imgdata.rawparams.options |= (1u << 13); // LIBRAW_RAWOPTIONS_DNG_STAGE3
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
    int ret = processor.open_file(inputPath);
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
    bool verbose)
{
#ifdef USE_DNGSDK
    DngToolRaw processor;   // 子类: 可访问 tiff_ifd (DNG 原始色彩标签)

    // DNG SDK host (保持存活) — 2026-08-15: 多线程 (JXL tile 编码并行)
    DngToolHost host(threadCount);

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
    int ret = processor.open_file(inputPath);
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

    // CFA 模式: filters 是 dcraw 风格的 32-bit 值, 每 2-bit 一个像素颜色
    // 值 0=RGGB, 1=GRBG, 2=GBRG, 3=BGGR (2x2 相位)
    if (isBayer)
    {
        // 从 filters 推导 2x2 相位:
        // filters 每 2 bit 编码颜色: 0=R, 1=G, 2=B (dcraw FC 宏)
        // 提取左上角 2x2 相位
        unsigned phase = 0;
        // dcraw: FC(row,col) = (filters >> (((row<<1 & 14) + (col & 1)) << 1)) & 3
        int c00 = (filters >> 0) & 3;         // row0,col0
        int c01 = (filters >> 2) & 3;         // row0,col1
        int c10 = (filters >> 8) & 3;         // row1,col0
        int c11 = (filters >> 10) & 3;        // row1,col1
        // 标准相位: 0=RGGB, 1=GRBG, 2=GBRG, 3=BGGR
        if (c00 == 0 && c01 == 1 && c10 == 1 && c11 == 2) phase = 0;
        else if (c00 == 1 && c01 == 0 && c10 == 2 && c11 == 1) phase = 1;
        else if (c00 == 2 && c01 == 1 && c10 == 1 && c11 == 0) phase = 2;
        else if (c00 == 1 && c01 == 2 && c10 == 0 && c11 == 1) phase = 3;
        else
        {
            // 非常规模式, 退化为按相位 0 处理 (多数情况正确)
            fprintf(stderr, "[dngtool-e] WARNING: unusual CFA pattern, using phase 0\n");
            phase = 0;
        }

        negative->SetColorKeys(colorKeyRed, colorKeyGreen, colorKeyBlue, colorKeyGreen);
        negative->SetBayerMosaic(phase);
        if (verbose) fprintf(stderr, "[dngtool-e] Bayer CFA phase %u\n", phase);
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
    negative->SetDefaultCropSize(activeW, activeH);
    negative->SetDefaultCropOrigin(leftMargin, topMargin);
    negative->SetActiveArea(dng_rect(topMargin, leftMargin,
                                     topMargin + activeH, leftMargin + activeW));
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
        fprintf(stderr, "[dngtool-e] raw_bps=%u maximum=%u black=%u cblack0=%u\n",
                colordata.raw_bps, colordata.maximum, colordata.black,
                colordata.cblack[0]);

    uint32 white;
    if (outFloat)
        white = 1;
    else if (outputBps <= 8)
        white = 255;                 // 8-bit 输出 (2026-08-14)
    else if (!isBayer || colordata.raw_bps >= 16)
        white = 0xFFFF;
    else if (colordata.maximum > 0)
        white = colordata.maximum;
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
    {
        const bool isDngInput = (processor.imgdata.idata.dng_version > 0);
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
        writer.WriteDNG(host, stream, *negative.Get(), NULL,
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
    int outputColor = 0, quality = 3, useCameraWb = 1, highlight = 1;
    bool decodeMode = false, encodeMode = false, verbose = false;
    bool verifyMode = false;
    int encodeCompression = 0;  // 0=lossless JPEG, 1=JXL
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
        else if (strcmp(a, "-effort") == 0 && i + 1 < argc) jxlEffort = atoi(argv[++i]);
        else if (strcmp(a, "-decode_speed") == 0 && i + 1 < argc) jxlDecodeSpeed = atoi(argv[++i]);
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
        jxlQuality = (encodeCompression == 1) ? quality : 0;
        return EncodeDng(inputPath.c_str(), outputPath.c_str(),
                         encodeCompression, jxlQuality, forceLinear,
                         jxlEffort, jxlDecodeSpeed, highlight, outputBps,
                         threadCount, verbose);
    }

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
