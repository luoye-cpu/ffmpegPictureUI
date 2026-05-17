using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FfmpegGui.Models;
using System.Timers;

namespace FfmpegGui
{
    public partial class ProgressWindow : Window
    {
        private TextBlock? TitleLabel;
        private TextBlock? StatusLabel;
        private TextBlock? ProgressLabel;
        private TextBlock? QualityInfoLabel;
        private TextBox? CommandLabel;
        private TextBox? LogBox;
        private TextBlock? AnalysisLabel;

        private readonly QueueItem? _item;

        public ProgressWindow()
        {
            InitializeComponent();
        }

        public ProgressWindow(QueueItem item, string command) : this()
        {
            _item = item;
            Title = $"编码详情 — {System.IO.Path.GetFileName(item.InputPath)}";

            TitleLabel = this.FindControl<TextBlock>("TitleLabel");
            StatusLabel = this.FindControl<TextBlock>("StatusLabel");
            ProgressLabel = this.FindControl<TextBlock>("ProgressLabel");
            QualityInfoLabel = this.FindControl<TextBlock>("QualityInfoLabel");
            CommandLabel = this.FindControl<TextBox>("CommandLabel");
            LogBox = this.FindControl<TextBox>("LogBox");
            AnalysisLabel = this.FindControl<TextBlock>("AnalysisLabel");

            if (TitleLabel != null)
                TitleLabel.Text = System.IO.Path.GetFileName(item.InputPath);
            if (StatusLabel != null)
                StatusLabel.Text = item.Status;
            if (CommandLabel != null)
                CommandLabel.Text = command;

            // 定时刷新
            var timer = new System.Timers.Timer(500);
            timer.Elapsed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => Refresh());
            };
            timer.Start();
            Closed += (_, _) => timer.Stop();
        }

        public void Refresh()
        {
            if (_item == null) return;
            if (StatusLabel != null)
                StatusLabel.Text = _item.Status;

            if (LogBox != null)
            {
                // 截断过长日志
                var log = _item.Log;
                if (log.Length > 10000)
                    log = "...(已截断)\n" + log.Substring(log.Length - 10000);
                LogBox.Text = log;
                LogBox.CaretIndex = log.Length;
            }

            // 解析 ffmpeg stderr 中的进度和质量信息
            ParseProgress(_item.Log);
        }

        private void ParseProgress(string log)
        {
            if (ProgressLabel == null) return;
            if (string.IsNullOrWhiteSpace(log))
            {
                ProgressLabel.Text = "等待开始...";
                QualityInfoLabel!.Text = "-";
                return;
            }

            // ffmpeg 进度输出可能在连续更新的同一行，也可能多行
            // 格式: "frame=  123 fps= 30 q=28.0 size=    1024kB time=00:00:05.00 bitrate=... speed=..."
            // 单帧图片可能没有 fps= 字段
            var lines = log.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            string? lastProgress = null;
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                if ((l.Contains("frame=") || l.Contains("q=")) && l.Contains("size="))
                {
                    lastProgress = l.Trim();
                    break;
                }
            }

            if (lastProgress != null)
            {
                ProgressLabel.Text = lastProgress;

                // 提取质量值 q=XX.X
                var qIdx = lastProgress.IndexOf("q=");
                if (qIdx >= 0)
                {
                    var qPart = lastProgress.Substring(qIdx + 2).Trim();
                    var space2 = qPart.IndexOf(' ');
                    var qVal = space2 > 0 ? qPart.Substring(0, space2) : qPart;
                    QualityInfoLabel!.Text = $"q={qVal} (数值越小质量越高)";
                }
                else
                {
                    QualityInfoLabel!.Text = "无质量数据";
                }
            }
            else if (_item.Status == "已完成" || _item.Status.Contains("失败"))
            {
                ProgressLabel.Text = _item.Status;
                QualityInfoLabel!.Text = "-";
            }
            else
            {
                ProgressLabel.Text = "正在初始化...";
                QualityInfoLabel!.Text = "-";
            }
        }

        private async void AnalyzeQuality_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_item == null) return;

            // 防止重复点击
            if (sender is Button btn) btn.IsEnabled = false;
            try
            {
                if (AnalysisLabel != null)
                    AnalysisLabel.Text = "正在分析 SSIM + PSNR...";

                var result = await Services.QualityAnalysisService.AnalyzeAsync(
                    _item.InputPath, _item.OutputPath);

                if (AnalysisLabel != null)
                {
                    if (result.Success && (result.SsimAll.HasValue || result.PsnrAverage.HasValue))
                    {
                        // 检测无损编码: SSIM ≈ 1.0 且 PSNR 无穷大
                        var isLossless = result.SsimAll is >= 0.9999 &&
                                         result.PsnrAverage is double.PositiveInfinity or >= 99;

                        var lines = new System.Collections.Generic.List<string>();

                        if (isLossless)
                        {
                            lines.Add("🔒 无损编码 — 输出与源图完全一致");
                        }

                        if (result.SsimAll.HasValue)
                        {
                            var ssimStr = $"SSIM: {result.SsimAll.Value:F6}";
                            if (result.SsimDB.HasValue && !double.IsInfinity(result.SsimDB.Value))
                                ssimStr += $" ({result.SsimDB.Value:F2} dB)";
                            else if (result.SsimDB.HasValue && double.IsPositiveInfinity(result.SsimDB.Value))
                                ssimStr += " (∞ dB)";
                            lines.Add(ssimStr);
                        }
                        if (result.PsnrAverage.HasValue)
                        {
                            var psnrStr = double.IsPositiveInfinity(result.PsnrAverage.Value)
                                ? "PSNR: ∞ dB (无损)"
                                : $"PSNR: {result.PsnrAverage.Value:F2} dB";
                            if (result.PsnrMin.HasValue && !double.IsInfinity(result.PsnrMin.Value))
                                psnrStr += $" (min {result.PsnrMin.Value:F2})";
                            if (result.PsnrMax.HasValue && !double.IsInfinity(result.PsnrMax.Value))
                                psnrStr += $" (max {result.PsnrMax.Value:F2})";
                            lines.Add(psnrStr);
                        }

                        // 质量评级（仅对有损编码）
                        if (!isLossless && result.PsnrAverage.HasValue && !double.IsInfinity(result.PsnrAverage.Value))
                        {
                            var psnr = result.PsnrAverage.Value;
                            lines.Add(psnr switch
                            {
                                >= 45 => "评级: ★★★★★ 优秀",
                                >= 38 => "评级: ★★★★ 良好",
                                >= 32 => "评级: ★★★ 一般",
                                >= 25 => "评级: ★★ 较差",
                                _ => "评级: ★ 差"
                            });
                        }

                        AnalysisLabel.Text = string.Join("\n", lines);
                    }
                    else
                    {
                        var errMsg = result.Error;
                        if (string.IsNullOrWhiteSpace(errMsg))
                            errMsg = "未检测到质量数据（ffmpeg 退出码非零但无错误信息）";

                        // 附加上下文：展示 ffmpeg 原始输出的最后几行
                        if (!string.IsNullOrWhiteSpace(result.RawOutput))
                        {
                            var rawLines = result.RawOutput
                                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            var lastLines = rawLines.Length > 5
                                ? string.Join("\n", rawLines[^5..])
                                : string.Join("\n", rawLines);
                            errMsg += "\n\nffmpeg 输出:\n" + lastLines;
                        }
                        AnalysisLabel.Text = $"分析失败: {errMsg}";
                    }
                }
            }
            finally
            {
                if (sender is Button btn2) btn2.IsEnabled = true;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
