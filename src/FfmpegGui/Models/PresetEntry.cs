namespace FfmpegGui.Models
{
    /// <summary>
    /// 预设条目（含名称、来源、预设数据），用于预设管理窗口的列表绑定。
    /// </summary>
    public class PresetEntry
    {
        public string Name { get; set; } = "";
        public string Source { get; set; } = "user"; // "builtin" or "user"
        public string? FilePath { get; set; }         // 用户预设的文件路径
        public PresetData Data { get; set; } = new();
    }
}
