#target photoshop
// PS 兼容性验证: 打开 dngtool 输出的 CFA DNG / 线性 DNG / 原始样本
// 结果写入 ps_result.txt (OPEN_OK 尺寸/模式 或 OPEN_FAIL 错误)
var outDir = "C:/PLAN/ffmpegPictureUI/tests/output/results/rawcheck/";
var files = [
    "bayer_cfa_v5.dng",       // 16-bit CFA JXL-DNG (修复后)
    "e_4_v3.dng",             // 8-bit CFA JXL-DNG (2026-08-14 新)
    "e_4_ljpeg.dng",          // 8-bit CFA 无损 JPEG DNG (2026-08-14 新)
    "C:/PLAN/ffmpegPictureUI/tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/03_jxl_bayer_raw_integer.dng" // Adobe 官方
];
var log = new File(outDir + "ps_result.txt");
log.encoding = "UTF-8";
log.open("w");
for (var i = 0; i < files.length; i++) {
    var full = files[i].indexOf("C:/") === 0 ? files[i] : outDir + files[i];
    var f = new File(full);
    log.writeln("=== " + f.name + " ===");
    log.writeln("fsName=" + f.fsName + " exists=" + f.exists);
    try {
        var doc = app.open(f);
        if (doc) {
            log.writeln("OPEN_OK " + doc.width.value + "x" + doc.height.value + " mode=" + doc.mode + " bits=" + doc.bitsPerChannel);
            doc.close(SaveOptions.DONOTSAVECHANGES);
        } else {
            log.writeln("OPEN_FAIL: app.open returned null");
        }
    } catch (e) {
        log.writeln("OPEN_FAIL: " + e.message);
    }
}
log.writeln("=== DONE ===");
log.close();
app.quit();
