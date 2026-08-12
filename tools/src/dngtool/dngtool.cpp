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
static int VerifyDng(const char* path)
{
#ifdef USE_DNGSDK
    try
    {
        dng_host host;
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

        // 主图压缩方式
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
            printf("[verify] Compression: uncompressed/lossless JPEG\n");
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
    bool verbose)
{
    LibRaw processor;

#ifdef USE_DNGSDK
    // DNG SDK host — 必须在使用 processor 之前创建并保持存活
    dng_host host;
    processor.set_dng_host(&host);
    // 使用全部 DNG SDK 能力 (float/linear/deflate/xtrans/other/8bit)
    processor.imgdata.rawparams.use_dngsdk = LIBRAW_DNG_ALL;
    // JXL 压缩 DNG 的 Opcode2/3 处理 (DNG 1.7)
    processor.imgdata.rawparams.options |= (1u << 27); // LIBRAW_RAWOPTIONS_DNG_STAGE23_IFPRESENT_JPGJXL
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
    bool verbose)
{
#ifdef USE_DNGSDK
    LibRaw processor;

    // DNG SDK host (保持存活)
    dng_host host;

    // 解码参数: 需要 raw 数据 (不 demosaic)
    processor.imgdata.params.output_bps = 16;
    processor.imgdata.params.no_auto_bright = 1;

#ifdef USE_DNGSDK
    // 支持 JXL-DNG 输入 (重编码场景)
    dng_host dngSdkHost;
    processor.set_dng_host(&dngSdkHost);
    processor.imgdata.rawparams.use_dngsdk = LIBRAW_DNG_ALL;
    processor.imgdata.rawparams.options |= (1u << 27); // LIBRAW_RAWOPTIONS_DNG_STAGE23_IFPRESENT_JPGJXL
#endif

    if (verbose) fprintf(stderr, "[dngtool-e] opening %s ...\n", inputPath);
    int ret = processor.open_file(inputPath);
    if (ret != LIBRAW_SUCCESS)
    {
        fprintf(stderr, "[dngtool-e] ERROR: cannot open %s: %s\n",
                inputPath, libraw_strerror(ret));
        return 2;
    }

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

    // ── 构造 dng_negative ──
    AutoPtr<dng_negative> negative;
    negative.Reset(host.Make_dng_negative());

    negative->SetColorChannels(3);

    // CFA 模式: filters 是 dcraw 风格的 32-bit 值, 每 2-bit 一个像素颜色
    // 值 0=RGGB, 1=GRBG, 2=GBRG, 3=BGGR (2x2 相位)
    bool isBayer = (filters != 0) && (filters != 9) && !forceLinear;
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
    uint32 white;
    if (outFloat)
        white = 1;
    else if (!isBayer || colordata.raw_bps >= 16)
        white = 0xFFFF;
    else if (colordata.maximum > 0)
        white = colordata.maximum;
    else
        white = (1u << (colordata.raw_bps > 0 ? colordata.raw_bps : 14)) - 1;
    negative->SetWhiteLevel(white);
    if (colordata.black > 0)
        negative->SetBlackLevel(colordata.black, 0);

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

    // ── 相机色彩配置文件 (ColorMatrix1 + ForwardMatrix1) ──
    // DNG 规范: 彩色 DNG 必需 ColorMatrix1 (XYZ D50 → 参考相机空间)。
    // LibRaw cam_xyz: 相机空间 → XYZ (D50) 3x3; ColorMatrix1 = inverse(cam_xyz);
    // ForwardMatrix1 = 相机空间 → XYZ (D50), 行归一化使 D50 白点映射准确。
    {
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
                dng_matrix_3by3 camToXyz(cm[0], cm[1], cm[2],
                                         cm[3], cm[4], cm[5],
                                         cm[6], cm[7], cm[8]);
                dng_matrix xyzToCam = Invert(camToXyz);

                AutoPtr<dng_camera_profile> profile(new dng_camera_profile);
                profile->SetColorMatrix1(xyzToCam);
                profile->SetCalibrationIlluminant1(lsStandardLightA);

                // ForwardMatrix1: 行归一化到 D50 白点 (0.9642, 1.0, 0.8249)
                dng_matrix fm = camToXyz;
                const real64 d50[3] = { 0.9642, 1.0, 0.8249 };
                for (int r = 0; r < 3; r++)
                {
                    real64 rowSum = 0;
                    for (int c = 0; c < 3; c++) rowSum += camToXyz[r][c];
                    if (rowSum > 0.001)
                    {
                        real64 scale = d50[r] / rowSum;
                        for (int c = 0; c < 3; c++) fm[r][c] *= scale;
                    }
                }
                profile->SetForwardMatrix1(fm);

                negative->AddProfile(profile);
                if (verbose)
                    fprintf(stderr, "[dngtool-e] camera profile: ColorMatrix1 + ForwardMatrix1 (illuminant A)\n");
            }
            catch (...)
            {
                if (verbose)
                    fprintf(stderr, "[dngtool-e] WARNING: cam_xyz 不可逆, 跳过 ColorMatrix\n");
            }
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

    // 线性 DNG 输出: 32-bit float (JXL 压缩) 或 16-bit 整数 (无损 JPEG)
    const uint32 outPixelType = outFloat ? ttFloat : ttShort;

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
                // float 源 → 16-bit 输出
                const float* srcRow = (const float*)((const char*)rawSrc + (uint64)y * srcPitch);
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
                row.fRowStep = (int32)(rawWidth * 3 * 2);
                row.fColStep = 3;
                row.fPlaneStep = 1;
                row.fPixelType = ttShort;
                row.fData = dstRow;
                img->Put(row);
                delete[] dstRow;
            }
            else
            {
                // 16-bit: raw_alloc (Bayer 1平面 或 linear 3平面交错)
                dng_pixel_buffer row;
                row.fArea = dng_rect(y, 0, y + 1, rawWidth);
                row.fPlane = 0;
                row.fRowStep = (int32)rowBytes;
                row.fColStep = pixelStride;
                row.fPlaneStep = 1;
                row.fPixelType = ttShort;
                row.fData = (void*)((const char*)rawSrc + (uint64)y * srcPitch);
                img->Put(row);
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

        jxlImage->Encode(host, writer, *negative->Stage1Image(), useCase,
                         negative.Get());

        // DNG 1.7 规范: JXL 压缩图像应携带 JXLDistance / JXLEffort / JXLDecodeSpeed 标签
        // (Adobe Camera Raw 校验必需; 默认 -1 会省略标签导致 ACR 拒绝打开)
        jxlImage->fJXLDistance = (quality <= 0)
            ? 0.0f
            : (float)((100 - quality) * 15.0 / 100.0);
        jxlImage->fJXLEffort      = 7;
        jxlImage->fJXLDecodeSpeed = 4;

        AutoPtr<dng_lossy_compressed_image> lossy(jxlImage.Release());
        negative->SetRawLossyCompressedImage(lossy);

        if (verbose) fprintf(stderr, "[dngtool-e] JXL compression (q=%d, useCase=%d)\n",
                             quality, (int)useCase);
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
        else if (strcmp(a, "-4") == 0 || strcmp(a, "-6") == 0) { /* 16-bit 固定 */ }
        else if (strcmp(a, "-T") == 0) { /* TIFF 输出固定 */ }
        else if (strcmp(a, "-v") == 0) verbose = true;
        else if (strcmp(a, "-lossless") == 0) encodeCompression = 0;
        else if (strcmp(a, "-jxl") == 0) encodeCompression = 1;
        else if (strcmp(a, "-linear") == 0) forceLinear = true;
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
        return VerifyDng(inputPath.c_str());
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
                         encodeCompression, jxlQuality, forceLinear, verbose);
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
                      outputColor, quality, useCameraWb, highlight, verbose);
    }

    PrintUsage(argv[0]);
    return 0;
}
