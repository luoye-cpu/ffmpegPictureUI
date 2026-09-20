using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FfmpegGui.Models
{
    /// <summary>
    /// NativeAOT 兼容的 JSON 序列化上下文（Source Generator）。
    /// 反射序列化在裁剪/AOT 下会抛 NotSupportedException，必须使用源生成。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(PresetData))]
    [JsonSerializable(typeof(FfmpegOptions))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.IpcRequest))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.IpcResponse))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.SubmitJobPayload))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.StatusPayload))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// IPC 消息**专用**序列化上下文 —— 与全局上下文唯一的区别是 <c>WriteIndented = false</c>。
    ///
    /// 为什么必须单独一个：IPC 协议是 **JSON Lines**（每行一条 JSON）。全局 <see cref="AppJsonContext"/>
    /// 开了 <c>WriteIndented = true</c>（settings.json / 预设文件需要缩进以便人读，不能动），
    /// 于是 IPC 消息被序列化成**多行** JSON：接收端按行读只能拿到 `{`、`  "Id": 1,` 这类碎片，
    /// 反序列化全部失败并被 <c>catch { continue; }</c> 静默吞掉 ⇒ **服务端永不回包**，
    /// 客户端只能一路等到超时。2026-09-15 实测定位：服务端收到的正是被切成 4 段的 30 字节多行载荷。
    /// ⇒ 症状是「IPC 从来没有真正工作过」，而两端各自的单测/代码看着都没问题。
    /// </summary>
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.IpcRequest))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.IpcResponse))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.SubmitJobPayload))]
    [JsonSerializable(typeof(FfmpegGui.Services.IpcService.StatusPayload))]
    internal partial class IpcJsonContext : JsonSerializerContext
    {
    }
}
