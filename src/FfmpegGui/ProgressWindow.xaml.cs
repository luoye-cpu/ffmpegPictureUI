using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FfmpegGui.Controls;
using FfmpegGui.Models;
using System.Collections.Generic;
using System.Timers;

namespace FfmpegGui
{
    public partial class ProgressWindow : Window
    {
        private TextBlock? TitleLabel;
        private TextBlock? StatusBadge;
        private TextBlock? StatusLabel;
        private TextBlock? ProgressLabel;
        private TextBlock? QualityInfoLabel;
        private TextBox? CommandLabel;
        private TextBox? LogBox;
        private TextBlock? AnalysisLabel;
        private TextBlock? FilePathLabel;
        private MetadataEditor? MetadataEditorCtrl;
        private StackPanel? MediaInfoPanel;

        private readonly QueueItem? _item;

        /// <summary>上一次真正写进 <c>LogBox</c> 的文本——用来判断"这一 tick 到底变没变"。</summary>
        private string _shownLog = string.Empty;
        /// <summary>视图是否停在末尾。用户往上拖出去就置假 ⇒ 刷新时不再抢滚动。</summary>
        private bool _followTail = true;

        /// <summary>
        /// <c>LogBox</c> 模板内部的 ScrollViewer——"视图有没有停在末尾"只能问它要绝对读数。
        /// 反射实测 Avalonia 12.1.4 的 <c>ScrollChangedEventArgs</c> 只有
        /// <c>ExtentDelta/OffsetDelta/ViewportDelta</c> 三个增量（没有 ExtentHeight/OffsetY/ViewportHeight），
        /// 而增量算不出"离底还有多少" ⇒ 绝对读数只认 <see cref="ScrollViewer"/> 的
        /// <c>Extent/Offset/Viewport</c>。
        /// </summary>
        private ScrollViewer? _logScroll;

        /// <summary>
        /// 显示切片上限。旧值 10,000：实测一条 JXR 任务的路由日志就有 11,233~15,227 字符
        /// （2026-10-29 的 `tests/output/jxr_gui/{b2_full,cli_good,b5_after,route.log}` 三档读数）
        /// ⇒ 10K 会把开头那段"输入/编码器合法性"点名的话吃掉，而那正是本轮 B2 新加的诊断行。
        /// ⚠ 24K 不是拍脑袋，是让最大实测量留 1.6× 余量：**不能再抬**。整段重写 Text 的代价
        /// 实测约 12 ms/千字符（Avalonia 的 TextPresenter 不做行虚拟化；开不开 Wrap 只差 &lt;10%），
        /// 我先按 200K 试过 ⇒ 单次刷新 2,455 ms，比 500 ms 的刷新周期还长 5 倍 ⇒ UI 线程被吃死。
        /// 所以这里定 24K（≈300 ms/次）并配 <see cref="LogWriteIntervalMs"/> 节流。
        /// </summary>
        private const int LogDisplayLimit = 24 * 1024;

        /// <summary>日志文本两次整段重写之间的最小间隔（状态/进度行不受它影响，仍每 tick 更新）。</summary>
        private const long LogWriteIntervalMs = 1000;

        /// <summary>上一次整段重写日志文本的时刻（<see cref="Environment.TickCount64"/>）。</summary>
        private long _lastLogWriteMs;

        private static string TrimLogForDisplay(string log)
            => log.Length <= LogDisplayLimit
                ? log
                : $"...(前段已截断，仅显示最后 {LogDisplayLimit / 1024}K 字符)\n" + log.Substring(log.Length - LogDisplayLimit);

        public ProgressWindow()
        {
            InitializeComponent();
        }

        public ProgressWindow(QueueItem item, string command) : this()
        {
            _item = item;
            Title = $"编码详情 — {item.DisplayName}";

            TitleLabel = this.FindControl<TextBlock>("TitleLabel");
            StatusBadge = this.FindControl<TextBlock>("StatusBadge");
            StatusLabel = this.FindControl<TextBlock>("StatusLabel");
            ProgressLabel = this.FindControl<TextBlock>("ProgressLabel");
            QualityInfoLabel = this.FindControl<TextBlock>("QualityInfoLabel");
            CommandLabel = this.FindControl<TextBox>("CommandLabel");
            LogBox = this.FindControl<TextBox>("LogBox");
            AnalysisLabel = this.FindControl<TextBlock>("AnalysisLabel");
            FilePathLabel = this.FindControl<TextBlock>("FilePathLabel");
            MetadataEditorCtrl = this.FindControl<MetadataEditor>("MetadataEditorCtrl");
            MediaInfoPanel = this.FindControl<StackPanel>("MediaInfoPanel");

            if (TitleLabel != null)
                TitleLabel.Text = item.DisplayName;
            if (StatusBadge != null)
                StatusBadge.Text = item.Status;
            if (StatusLabel != null)
                StatusLabel.Text = item.Status;
            if (CommandLabel != null)
                CommandLabel.Text = command;
            if (FilePathLabel != null)
                FilePathLabel.Text = $"📁 输出: {item.OutputPath}";
            if (MetadataEditorCtrl != null)
                MetadataEditorCtrl.FilePath = item.OutputPath;

            // 定时刷新
            var timer = new System.Timers.Timer(500);
            timer.Elapsed += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => Refresh());
            };
            timer.Start();
            Closed += (_, _) => timer.Stop();

            // 视图离末尾 <24px 视为"在跟末尾"；用户往上拖出去 ⇒ 置假，刷新时不再把光标打到末尾。
            // 内部 ScrollViewer 要等模板落地才存在，构造期取不到 ⇒ 交给 Refresh() 惰性认领
            // （每 500 ms 试一次，取到就订事件并停止尝试）。

            // 异步加载媒体技术信息
            _ = LoadMediaInfoAsync();
        }

        private async System.Threading.Tasks.Task LoadMediaInfoAsync()
        {
            if (_item == null || MediaInfoPanel == null) return;
            try
            {
                var info = await Services.MediaInfoParser.ParseAsync(_item.OutputPath, _item);
                var cards = BuildMediaInfoCards(info);
                Dispatcher.UIThread.Post(() =>
                {
                    if (MediaInfoPanel != null)
                    {
                        MediaInfoPanel.Children.Clear();
                        foreach (var card in cards) MediaInfoPanel.Children.Add(card);
                    }
                });
            }
            catch { }
        }

        private static List<Border> BuildMediaInfoCards(Services.MediaInfoModel m)
        {
            var cards = new List<Border>();

            // File overview card
            cards.Add(MakeCard("📁 输出文件",
                ("文件名", m.FileName),
                ("格式", m.Format.ToUpper()),
                ("文件大小", Services.MediaInfoParser.FormatSize(m.FileSize))));

            // Image properties card
            if (m.Width > 0)
                cards.Add(MakeCard("🖼 图像属性",
                    ("分辨率", $"{m.Width} × {m.Height} ({(m.Width * m.Height) / 1_000_000.0:F1} MP)"),
                    ("位深", m.BitDepth > 0 ? $"{m.BitDepth} bits" : "—"),
                    ("像素格式", string.IsNullOrWhiteSpace(m.PixelFormat) ? "—" : m.PixelFormat),
                    ("色彩空间", Services.MediaInfoParser.FormatColorInfo(m))));

            // ICC card
            var iccText = string.IsNullOrWhiteSpace(m.IccDescription)
                ? "（未内嵌 ICC Profile）"
                : $"📎 {m.IccDescription}" + (m.IccSize > 0 ? $" ({m.IccSize} bytes)" : "");
            cards.Add(MakeCard("🎨 色彩配置", ("ICC", iccText)));

            // Quality card
            if (m.Ssim.HasValue || m.Psnr.HasValue)
                cards.Add(MakeCard("📊 质量分析",
                    ("SSIM", m.Ssim.HasValue ? $"{m.Ssim:F4}" : "—"),
                    ("PSNR", m.Psnr.HasValue ? $"{m.Psnr:F2} dB" : "—")));

            return cards;
        }

        private static Border MakeCard(string title, params (string label, string value)[] rows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*") };
            for (int i = 0; i < rows.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var lbl = new TextBlock { Text = rows[i].label, FontSize = 11, Foreground = Avalonia.Media.Brushes.Gray, Margin = new Avalonia.Thickness(0, 1) };
                var val = new TextBlock { Text = rows[i].value, FontSize = 11, Margin = new Avalonia.Thickness(0, 1), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
                Grid.SetRow(lbl, i); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
                Grid.SetRow(val, i); Grid.SetColumn(val, 1); grid.Children.Add(val);
            }
            return new Border
            {
                CornerRadius = new Avalonia.CornerRadius(6),
                Padding = new Avalonia.Thickness(12, 8),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 12, Margin = new Avalonia.Thickness(0,0,0,2) },
                        grid
                    }
                }
            };
        }

        public void Refresh()
        {
            if (_item == null) return;
            if (StatusLabel != null)
                StatusLabel.Text = _item.Status;
            if (StatusBadge != null)
                StatusBadge.Text = _item.Status;

            if (CommandLabel != null && !string.IsNullOrEmpty(_item.Command))
                CommandLabel.Text = _item.Command;

            if (LogBox != null)
            {
                AttachLogScrollTracking();

                // ⚠ 旧写法每 500 ms **无条件**重写 Text 并把 CaretIndex 打到末尾 ⇒ 用户每拖一下，
                //   下一个 tick 就被拽回底部——这才是"拉不动"的主因（布局那半边见 ProgressWindow.xaml
                //   行定义处的注释）。现在：没变就一个字都不写；变了也只在视图本就停在末尾时跟到末尾；
                //   整段重写本身按 LogWriteIntervalMs 节流（代价实测 ≈12 ms/千字符，见上限处的读数）。
                var log = TrimLogForDisplay(_item.Log);
                if (log != _shownLog)
                {
                    // 终态之后日志不再变化 ⇒ 最后一版必须无条件落地，否则窗口会永远停在半截。
                    long nowMs = Environment.TickCount64;
                    if (_item.IsTerminal || nowMs - _lastLogWriteMs >= LogWriteIntervalMs)
                    {
                        LogBox.Text = log;
                        _shownLog = log;
                        _lastLogWriteMs = nowMs;
                        if (_followTail) LogBox.CaretIndex = log.Length;
                    }
                }
            }

            // 解析 ffmpeg stderr 中的进度和质量信息
            ParseProgress(_item.Log);
        }

        /// <summary>
        /// 惰性认领 <c>LogBox</c> 模板里的 ScrollViewer，并订它的 <c>ScrollChanged</c>。
        /// 走视觉树而不是路由事件：本仓对模板部件没有公开入口（<c>TemplatedControl.GetTemplateChild</c>
        /// 非公开），而 <c>TextBox</c> 也没有将 ScrollChanged 冒泡上来的公开承诺。
        /// </summary>
        private void AttachLogScrollTracking()
        {
            if (_logScroll != null || LogBox == null) return;
            _logScroll = LogBox.FindDescendantOfType<ScrollViewer>();
            if (_logScroll == null) return;
            _logScroll.ScrollChanged += (_, _) =>
                _followTail = _logScroll!.Extent.Height - _logScroll.Offset.Y - _logScroll.Viewport.Height < 24;
        }

        private void ParseProgress(string log)
        {
            if (ProgressLabel == null) return;

            // ★ 任务已完成/失败：不解析日志残留（日志中仍有编码阶段的旧消息），
            //    直接显示最终状态，避免出现 "djxl 解码中" 等误导信息
            if (_item != null && (_item.IsSuccessStatus || _item.IsFailure))
            {
                ProgressLabel.Text = _item.Status;
                QualityInfoLabel!.Text = "-";
                return;
            }

            // ── 空日志：尚未开始 ──
            if (string.IsNullOrWhiteSpace(log))
            {
                ProgressLabel.Text = (_item?.IsPending ?? false) ? "等待开始..." : "已启动，等待输出...";
                QualityInfoLabel!.Text = "-";
                return;
            }

            var lines = log.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

            // ── 1) 优先检测 ffmpeg 标准进度输出 ──
            // 格式: "frame=  123 fps= 30 q=28.0 size=    1024kB time=00:00:05.00 speed=..."
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                if ((l.Contains("frame=") || l.Contains("speed=")) && l.Contains("size="))
                {
                    ProgressLabel.Text = l.Trim();

                    var qIdx = l.IndexOf("q=");
                    if (qIdx >= 0)
                    {
                        var qPart = l.Substring(qIdx + 2).Trim();
                        var space2 = qPart.IndexOf(' ');
                        var qVal = space2 > 0 ? qPart.Substring(0, space2) : qPart;
                        QualityInfoLabel!.Text = $"q={qVal}";
                    }
                    else
                    {
                        QualityInfoLabel!.Text = "编码中...";
                    }
                    return;
                }
            }

            // ── 2) 检测 djxl 进度输出 ──
            // djxl 输出格式: "Decoded to pixels." / "Reading JXL..." / 百分比
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (l.StartsWith("[djxl]") && (l.Contains("Decoded") || l.Contains("Reading") || l.Contains("%")))
                {
                    ProgressLabel.Text = l.Replace("[djxl] ", "").Trim();
                    QualityInfoLabel!.Text = "djxl 解码中";
                    return;
                }
            }

            // ── 3) 检测 cjxl 进度输出 ──
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (l.StartsWith("[cjxl]") && (l.Contains("%") || l.Contains("MP") || l.Contains("Compressed")))
                {
                    ProgressLabel.Text = l.Replace("[cjxl] ", "").Trim();
                    QualityInfoLabel!.Text = "cjxl 编码中";
                    return;
                }
            }

            // ── 4) 检测当前执行阶段（从日志末尾向前扫描阶段标记）──
            var phaseMarkers = new[] { "[cjxl]", "[cjpegli]", "[djxl]", "[pipe]", "[pipeline]",
                "[preconv]", "[avif→", "[gif→", "[exiftool]", "[cmd]" };
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var l = lines[i];
                foreach (var marker in phaseMarkers)
                {
                    if (l.StartsWith(marker))
                    {
                        ProgressLabel.Text = l.Trim();
                        QualityInfoLabel!.Text = "处理中...";
                        return;
                    }
                }
            }

            // ── 5) 回退：显示任务状态 ──
            if (_item != null)
            {
                if (_item.IsSuccessStatus || _item.IsFailure)
                {
                    ProgressLabel.Text = _item.Status;
                    QualityInfoLabel!.Text = "-";
                }
                else if (_item.IsProcessing)
                {
                    ProgressLabel.Text = "处理中...";
                    QualityInfoLabel!.Text = "等待输出";
                }
                else
                {
                    ProgressLabel.Text = _item.Status;
                    QualityInfoLabel!.Text = "-";
                }
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
                            // 域标注 (2026-08-15: 按目标格式编码域 — RGB 系格式用 RGB 域, YUV 系用 YUV 域)
                            psnrStr += result.PsnrIsRgb ? " (RGB)" : " (YUV)";
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
