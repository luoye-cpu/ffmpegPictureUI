#target photoshop
var f = new File("C:/PLAN/ffmpegPictureUI/tests/output/results/rawcheck/ps_8bit_check.dng");
var log = new File("C:/PLAN/ffmpegPictureUI/tests/output/results/rawcheck/ps_8bit_result.txt");
log.encoding = "UTF-8";
log.open("w");
try {
    var doc = app.open(f);
    if (doc) {
        log.writeln("OPEN_OK " + doc.width.value + "x" + doc.height.value + " mode=" + doc.mode + " bits=" + doc.bitsPerChannel);
        doc.close(SaveOptions.DONOTSAVECHANGES);
    } else log.writeln("OPEN_FAIL: null");
} catch (e) { log.writeln("OPEN_FAIL: " + e.message); }
log.close();
app.quit();
