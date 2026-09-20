using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace FfmpegGui.Services
{
    /// <summary>
    /// 媒体探测结果缓存（键 = 文件路径 + 最后写入时间）。
    /// 避免重复调用 ffprobe 探测同一文件的色彩/位深/范围等元数据。
    /// </summary>
    public static class MediaProbeCache
    {
        private static readonly ConcurrentDictionary<string, (DateTime LastWrite, string Json)> _cache = new();
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        /// <summary>
        /// 获取或探测媒体信息（自动缓存）。
        /// </summary>
        /// <param name="path">文件路径</param>
        /// <param name="probeAsync">实际探测函数（仅在缓存未命中时调用）</param>
        /// <returns>JSON 字符串（来自 ffprobe -print_format json）</returns>
        public static async Task<string> GetOrProbeAsync(
            string path,
            Func<Task<string>> probeAsync)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;

            var fi = new FileInfo(path);
            var key = $"{path}|{fi.LastWriteTimeUtc.Ticks}";

            // 缓存命中
            if (_cache.TryGetValue(key, out var cached) && cached.LastWrite == fi.LastWriteTimeUtc)
                return cached.Json;

            // 缓存未命中，执行探测
            var json = await probeAsync();
            if (!string.IsNullOrWhiteSpace(json) && json.TrimStart().StartsWith("{"))
            {
                _cache[key] = (fi.LastWriteTimeUtc, json);
                // 定期清理过期条目（简单策略：每 100 次插入清理一次）
                if (_cache.Count % 100 == 0)
                    CleanupExpired();
            }
            return json;
        }

        /// <summary>
        /// 显式刷新缓存（文件被外部修改时调用）。
        /// </summary>
        public static void Invalidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            var fi = new FileInfo(path);
            var key = $"{path}|{fi.LastWriteTimeUtc.Ticks}";
            _cache.TryRemove(key, out _);
        }

        private static void CleanupExpired()
        {
            var cutoff = DateTime.UtcNow - MaxAge;
            var toRemove = new System.Collections.Generic.List<string>();
            foreach (var kv in _cache)
            {
                if (kv.Value.LastWrite < cutoff)
                    toRemove.Add(kv.Key);
            }
            foreach (var k in toRemove)
                _cache.TryRemove(k, out _);
        }

        /// <summary>
        /// 获取缓存统计信息（用于调试/监控）。
        /// </summary>
        public static (int Count, long MemoryEstimateKb) GetStats()
        {
            long mem = 0;
            foreach (var kv in _cache)
            {
                mem += kv.Key.Length + (kv.Value.Json?.Length ?? 0);
            }
            return (_cache.Count, mem / 1024);
        }
    }
}