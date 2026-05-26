using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        var baseDir = Path.GetFullPath("C:\\temp\\input");
        var input = Path.Combine(baseDir, "sub1", "sub2", "image.jpg");
        var outDir = Path.GetFullPath("C:\\temp\\output");

        Console.WriteLine($"Input: {input}");
        Console.WriteLine($"Base: {baseDir}");
        Console.WriteLine($"OutDir: {outDir}");

        var rel = Path.GetRelativePath(baseDir, input);
        Console.WriteLine($"Rel: {rel}");
        var relDir = Path.GetDirectoryName(rel);
        Console.WriteLine($"RelDir: {relDir}");

        var destDir = Path.Combine(outDir, relDir ?? "");
        var final = Path.Combine(destDir, Path.GetFileNameWithoutExtension(input) + ".jxl");
        Console.WriteLine($"Final: {final}");
    }
}