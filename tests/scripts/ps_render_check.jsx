#target photoshop
// PS 色彩正确性验证: 打开 dngtool 输出 vs 官方样本, 导出 16-bit TIFF 供亮度对比
// 结果: rawcheck/ps_render_dngtool.tif + ps_render_official.tif
var outDir = "C:/PLAN/ffmpegPictureUI/tests/output/results/rawcheck/";
var log = new File(outDir + "ps_render_result.txt");
log.encoding = "UTF-8";
log.open("w");

var tests = [
    { name: "dngtool_v7", path: outDir + "bayer_cfa_v7.dng" },
    { name: "official", path: "C:/PLAN/ffmpegPictureUI/tools/src/dng_sdk/dng_sdk_1_7_1/sample_files/03_jxl_bayer_raw_integer.dng" }
];

for (var i = 0; i < tests.length; i++) {
    var t = tests[i];
    var f = new File(t.path);
    log.writeln("=== " + t.name + " ===");
    try {
        var doc = app.open(f);
        if (!doc) { log.writeln("OPEN_FAIL: null"); continue; }
        log.writeln("OPEN_OK " + doc.width.value + "x" + doc.height.value + " mode=" + doc.mode + " bits=" + doc.bitsPerChannel);

        // 统一导出: 8-bit PNG (亮度对比用)
        var outFile = new File(outDir + "ps_render_" + t.name + ".png");
        // 转 8-bit
        doc.bitsPerChannel = BitsPerChannelType.EIGHT;
        var pngOpts = new PNGSaveOptions();
        pngOpts.compression = 6;
        doc.saveAs(outFile, pngOpts, true, Extension.LOWERCASE);
        log.writeln("SAVED " + outFile.name);

        // 采样中心区域像素统计 (读 100x100 中心块 RGB 平均)
        var x0 = Math.floor(doc.width.value / 2) - 50;
        var y0 = Math.floor(doc.height.value / 2) - 50;
        var px = doc.selection;
        var region = [[x0, y0], [x0 + 100, y0 + 100]];
        doc.selection.select(region, SelectionType.REPLACE, 0, false);
        var hist = doc.histogram;
        // 用 histogram 的均值近似
        var sum = 0, cnt = 0;
        for (var ch = 0; ch < 3; ch++) {
            doc.channels[ch].histogram = hist; // no-op 保持
        }
        // 直接读像素: 转 RGB 8bit 选区复制
        var copyDoc = app.documents.add(100, 100, 72, "sample");
        copyDoc.selection.selectAll();
        doc.selection.copy();
        copyDoc.paste();
        copyDoc.flatten();
        var jpgFile = new File(outDir + "ps_sample_" + t.name + ".png");
        var pngOpts = new PNGSaveOptions();
        copyDoc.saveAs(jpgFile, pngOpts, true, Extension.LOWERCASE);
        copyDoc.close(SaveOptions.DONOTSAVECHANGES);
        doc.selection.deselect();
        log.writeln("SAMPLE_SAVED " + jpgFile.name);

        doc.close(SaveOptions.DONOTSAVECHANGES);
    } catch (e) {
        log.writeln("ERROR: " + e.message);
    }
}
log.writeln("=== DONE ===");
log.close();
app.quit();
