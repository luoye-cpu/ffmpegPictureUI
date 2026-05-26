using System;
using System.IO;
using System.Threading.Tasks;
using FfmpegGui.Services;
using FfmpegGui.Models;

class Program
{
    static async Task Main()
    {
        Environment.SetEnvironmentVariable("FFMPEGGUI_CJXL_STUB", "1");

        var temp = Path.Combine(Path.GetTempPath(), "ffmpeggui_multi_test");
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        Directory.CreateDirectory(temp);

        var inputA = Path.Combine(temp, "A", "sub", "img1.jpg");
        var inputB = Path.Combine(temp, "B", "x", "y", "img2.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(inputA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(inputB)!);
        await File.WriteAllTextAsync(inputA, "jpg1");
        await File.WriteAllTextAsync(inputB, "jpg2");

        var outDir = Path.Combine(temp, "out");
        Directory.CreateDirectory(outDir);

        // 配置应用设置
        AppSettingsService.Current.OutputDirectory = outDir;
        AppSettingsService.Current.PreserveInputFolderStructure = true;

        // 创建两个 QueueItem，分别记录各自的输入基目录
        var qi1 = new QueueItem
        {
            InputPath = inputA,
            InputBaseDir = Path.Combine(temp, "A"),
            Options = new FfmpegOptions { Format = "jxl", JxlLosslessJpeg = true, Threads = 2 }
        };
        var rel1 = Path.GetRelativePath(qi1.InputBaseDir!, qi1.InputPath);
        var base1 = Path.GetFileName(qi1.InputBaseDir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var relDir1 = Path.GetDirectoryName(rel1);
        qi1.OutputPath = !string.IsNullOrEmpty(relDir1)
            ? Path.Combine(outDir, base1, relDir1, Path.GetFileNameWithoutExtension(qi1.InputPath) + ".jxl")
            : Path.Combine(outDir, base1, Path.GetFileNameWithoutExtension(qi1.InputPath) + ".jxl");

        var qi2 = new QueueItem
        {
            InputPath = inputB,
            InputBaseDir = Path.Combine(temp, "B"),
            Options = new FfmpegOptions { Format = "jxl", JxlLosslessJpeg = true, Threads = 2 }
        };
        var rel2 = Path.GetRelativePath(qi2.InputBaseDir!, qi2.InputPath);
        var base2 = Path.GetFileName(qi2.InputBaseDir!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var relDir2 = Path.GetDirectoryName(rel2);
        qi2.OutputPath = !string.IsNullOrEmpty(relDir2)
            ? Path.Combine(outDir, base2, relDir2, Path.GetFileNameWithoutExtension(qi2.InputPath) + ".jxl")
            : Path.Combine(outDir, base2, Path.GetFileNameWithoutExtension(qi2.InputPath) + ".jxl");

        var qp = new QueueProcessor(qi =>
        {
            Console.WriteLine($"[UPDATE] {qi.InputPath} -> {qi.OutputPath} | {qi.Status}");
            if (!string.IsNullOrEmpty(qi.Log)) Console.Write(qi.Log);
        }, () => Console.WriteLine("[QUEUE] stopped"));

        qp.Add(qi1);
        qp.Add(qi2);

        qp.Start(2);

        await Task.Delay(2000);
        qp.Stop();

        Console.WriteLine($"Out1 exists: {File.Exists(qi1.OutputPath)} -> {qi1.OutputPath}");
        Console.WriteLine($"Out2 exists: {File.Exists(qi2.OutputPath)} -> {qi2.OutputPath}");
        if (File.Exists(qi1.OutputPath)) Console.WriteLine(await File.ReadAllTextAsync(qi1.OutputPath));
        if (File.Exists(qi2.OutputPath)) Console.WriteLine(await File.ReadAllTextAsync(qi2.OutputPath));

        Console.WriteLine("Done");
    }
}