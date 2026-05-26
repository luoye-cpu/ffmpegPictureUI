using System;
using System.IO;
using System.Threading.Tasks;
using FfmpegGui.Services;
using FfmpegGui.Models;

class Program
{
    static async Task Main(string[] args)
    {
        // 开启 cjxl 测试桩
        Environment.SetEnvironmentVariable("FFMPEGGUI_CJXL_STUB", "1");

        var temp = Path.Combine(Path.GetTempPath(), "ffmpeggui_test");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        var inputBase = Path.Combine(temp, "input");
        var inputDir = Path.Combine(inputBase, "sub1", "sub2");
        Directory.CreateDirectory(inputDir);
        var inputFile = Path.Combine(inputDir, "image.jpg");
        await File.WriteAllTextAsync(inputFile, "dummy-jpeg");

        var outDir = Path.Combine(temp, "output");
        var outputFile = Path.Combine(outDir, "sub1", "sub2", "image.jxl");

        var options = new FfmpegOptions { JxlLosslessJpeg = true, Threads = 2, Format = "jxl" };
        var item = new QueueItem { InputPath = inputFile, OutputPath = outputFile, Options = options };

        var qp = new QueueProcessor(qi =>
        {
            Console.WriteLine($"[UPDATE] {qi.InputPath} -> {qi.OutputPath} | {qi.Status}");
            if (!string.IsNullOrEmpty(qi.Log)) Console.Write(qi.Log);
        }, () => Console.WriteLine("[QUEUE] stopped"));

        qp.Add(item);
        qp.Start(1);

        // 等待处理完成
        await Task.Delay(2000);
        qp.Stop();

        Console.WriteLine($"Output exists: {File.Exists(outputFile)}");
        if (File.Exists(outputFile))
        {
            Console.WriteLine("Output content: " + await File.ReadAllTextAsync(outputFile));
        }

        Console.WriteLine("Done");
    }
}