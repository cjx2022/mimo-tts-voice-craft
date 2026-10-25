using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using MimoTtsVoiceCraft.Helpers;
using MimoTtsVoiceCraft.Models;
using MimoTtsVoiceCraft.Services;
using Microsoft.Win32;

namespace MimoTtsVoiceCraft;

public partial class MainWindow : Window
{
    private static readonly string[] PresetVoices =
        { "mimo_default", "冰糖", "茉莉", "苏打", "白桦", "Mia", "Chloe", "Milo", "Dean" };

    private TtsModelKind _kind = TtsModelKind.Preset;

    /// <summary>
    /// 音色设计（voicedesign）模型的实用字数上限。
    /// 实测：该模型输出 token 恒定在 365~526，对应音频 58~84 秒，与输入字数无关；
    /// 300 字以内内容完整，超过后表现为「开头编造、中间跳跃、结尾断掉」。
    /// 预置 / 克隆不受此限（预置 2100 字实测 406~418 秒完整）。
    /// </summary>
    private const int DesignTextLimit = 300;

    // NAudio 播放（每次加载新建一对 output+stream，切换时先释放，避免多路叠加）
    private WaveOutEvent _out;
    private WaveStream _stream;
    private bool _loaded;            // 当前音频已加载可播放
    private bool _manualStop;        // 主动停止标志，用于区分"播完"与"被停止"
    private float _volume = 1f;      // 自检时置 0 静音
    private readonly ObservableCollection<GenRecord> _history = new();

    // 内嵌播放器状态
    private readonly System.Windows.Threading.DispatcherTimer _tick = new()
    { Interval = TimeSpan.FromMilliseconds(150) };
    private GenRecord _playing;
    private bool _suppressSeek;   // 计时器回写 Position 时抑制 Slider 回调
    private GenRecord _renaming;
    private GenRecord _deleting;

    // ---------------- 音色设计维度 ----------------

    private static readonly (string Label, string[] Options)[] DesignDims =
    {
        ("性别与年龄", new[] { "少女（14~18岁）", "青年女性（20~30岁）", "青年男性（20~30岁）", "中年女性（40~50岁）",
                               "中年男性（40~50岁）", "老年女性（60岁以上）", "老年男性（65岁以上）", "其它" }),
        ("音色质感",   new[] { "明亮清脆", "低沉磁性", "丝滑醇厚", "沙哑沧桑", "温柔绵软",
                               "清亮空灵", "甜美娇俏", "浑厚大气", "其它" }),
        ("情绪语气",   new[] { "平静沉稳", "活泼欢快", "温柔亲切", "高冷疏离", "疲惫慵懒",
                               "兴奋激动", "忧伤低回", "严肃干练", "其它" }),
        ("语速节奏",   new[] { "极慢，一字一顿", "缓慢从容", "适中自然", "稍快轻巧", "飞快如连珠", "其它" }),
        ("角色人设",   new[] { "无", "深夜电台DJ", "评书先生", "纪录片解说员", "邻家女孩",
                               "职场白领", "古装侠女", "童话讲述者", "其它" }),
        ("使用场景",   new[] { "无", "深夜电台独白", "自然纪录片旁白", "有声书朗读", "新闻播报",
                               "游戏角色配音", "广告带货口播", "其它" }),
    };
    private static readonly string[] DimDefaults =
        { "青年女性（20~30岁）", "明亮清脆", "活泼欢快", "适中自然", "无", "无" };

    private readonly List<ComboBox> _dimCombos = new();
    private readonly List<TextBox> _dimOthers = new();

    public MainWindow()
    {
        InitializeComponent();
        ChromeHelper.Attach(this, 48, // 必须在构造函数里，放 Loaded 里钩子永不触发
            new FrameworkElement[] { BtnAbout, BtnSettings, BtnMin, BtnMax, BtnClose });

        // 真实亚克力模糊（项目默认关闭；如需开启可在 settings.ini 置 GlassEffect=1）
        SourceInitialized += (s, e) => ApplyGlass();

        _tick.Tick += (s, e) =>
        {
            if (_playing == null || !_loaded || _stream == null) return;
            _suppressSeek = true;
            try
            {
                _playing.Position = _stream.CurrentTime.TotalSeconds;
                if (_stream.TotalTime.TotalSeconds > 0.1) _playing.Duration = _stream.TotalTime.TotalSeconds;
            }
            catch { }
            finally { _suppressSeek = false; }
        };

        LstHistory.ItemsSource = _history;
        foreach (var v in PresetVoices) CmbVoice.Items.Add(v);
        CmbVoice.SelectedIndex = 1; // 冰糖（中国集群默认女声）
        foreach (var f in new[] { "wav", "mp3", "pcm", "pcm16" }) CmbFormat.Items.Add(f);
        CmbAutoPlayInit();
        BuildDesignDims();
        ReloadHistory();

        if (App.GhostMode)
        {
            // 零干扰自检：不激活（不抢焦点）、不进任务栏、离屏
            ShowActivated = false;
            ShowInTaskbar = false;
            if (!App.OpaqueProbe) Opacity = 0.01; // --opaque 时保留不透明度，供 PrintWindow 采样
            Left = -2000; Top = 0;
        }

        UpdateChromeState();
        StateChanged += (s, e) => UpdateChromeState();

        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        TitleHint.Text = $"v{ver?.Major}.{ver?.Minor}.{ver?.Build} · 非官方客户端";
        StatusRight.Text = $"输出：{AppSettings.OutputDir}";
        Log("配置文件：%APPDATA%\\MimoTtsVoiceCraft\\settings.ini");
        Log(AppSettings.ApiKey.Length > 0
            ? $"API Key 已配置（{AppSettings.ApiKey.Length} 字符）"
            : "API Key 未配置 —— 请点右上角齿轮设置");

        Loaded += (_, _) =>
        {
            UpdateTextMetrics();               // XAML 里初始文本的字数统计
            _ = Task.Run(WarmUpAudioDevice);   // 后台预热音频端点（静音），避免首次播放丢首缓冲
            RunSelfCheckIfRequested();
            if (App.AudioTestPath != null) RunAudioSelfTest();
        };
    }

    /// <summary>
    /// 静音播放器自检（--audiotest=&lt;path&gt;）：真实合成一次，然后验证
    /// 自动播放 / 暂停 / 继续 / 拖动定位 / 播完再点播放。音量置 0，不发出声音。
    /// </summary>
    private async void RunAudioSelfTest()
    {
        var log = new List<string>();
        void W(string s) { log.Add(s); }

        try
        {
            _volume = 0f;   // 静音，避免打扰
            ReloadHistory();
            W($"静音自检开始（Volume=0，离屏窗口，NAudio 播放）｜输出目录载入记录 {_history.Count} 条");

            var job = new TtsJob
            {
                Kind = TtsModelKind.Preset,
                VoiceId = "冰糖",
                Text = "静音自检，验证自动播放与播放器控制。",
                Format = AppSettings.OutputFormat,
            };
            var path = await MimoTtsService.SynthesizeAsync(job, AppSettings.ApiKey, AppSettings.OutputDir,
                s => W("  " + s), CancellationToken.None);

            var rec = new GenRecord
            {
                HeaderText = "自检", DisplayName = "自检", TimeText = DateTime.Now.ToString("HH:mm:ss"),
                TextSnippet = "静音自检", FileName = Path.GetFileName(path), FilePath = path,
            };
            _history.Insert(0, rec);

            // 0) 静音前导校验：直接读流，确认前 N ms 全是 0（声卡丢首缓冲也只丢静音）
            {
                using var probe = OpenStream(path);
                if (probe is LeadInStream lead)
                {
                    var buf = new byte[Math.Min((int)lead.LeadInBytes, 65536)];
                    var n = probe.Read(buf, 0, buf.Length);
                    var nonzero = 0;
                    for (var i = 0; i < n; i++) if (buf[i] != 0) nonzero++;
                    W($"0) 静音前导: 前导={lead.LeadInBytes} 字节（{lead.LeadInBytes * 1000.0 / lead.WaveFormat.AverageBytesPerSecond:F0}ms），" +
                      $"实测读 {n} 字节，非零={nonzero} → {(nonzero == 0 && lead.LeadInBytes > 0 ? "OK" : "FAIL")}");
                }
                else W("0) 静音前导: 未启用（LeadInMs=0）");
            }

            // 1) 生成后自动播放（走与 BtnGen 相同的路径：预热 + 停顿 + 加长前导）
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            await AutoPlayAfterGenerateAsync(rec);
            await Task.Delay(900);
            sw1.Stop();
            var p0 = _stream?.CurrentTime.TotalSeconds ?? -1;
            var leadStream = _stream as LeadInStream;
            var lead2 = leadStream?.LeadInBytes ?? 0;
            var bps = leadStream?.WaveFormat.AverageBytesPerSecond ?? 1;
            var leadMs = lead2 * 1000.0 / bps;
            W($"   自动播放前导: {leadMs:F0}ms（应为 {AppSettings.PlayLeadInMs + 300}ms，格式 {leadStream?.WaveFormat.SampleRate}Hz " +
              $"{leadStream?.WaveFormat.Channels}ch {leadStream?.WaveFormat.BitsPerSample}bit / {bps} B/s）→ " +
              $"{(Math.Abs(leadMs - (AppSettings.PlayLeadInMs + 300)) < 2 ? "OK" : "FAIL")}");
            W($"1) 生成后自动播放: IsPlaying={rec.IsPlaying} 时长={rec.Duration:F2}s 播放到={p0:F2}s → " +
              $"{(rec.IsPlaying && p0 > 0.3 ? "OK" : "FAIL")}（起播到读数 {sw1.Elapsed.TotalSeconds:F2}s，占比 {p0 / Math.Max(0.01, sw1.Elapsed.TotalSeconds):P0}，接近 100% 说明开头未被吞）");

            // 2) 进度推进
            var pa = _stream.CurrentTime.TotalSeconds;
            await Task.Delay(800);
            var pb = _stream.CurrentTime.TotalSeconds;
            W($"2) 进度推进: {pa:F2}s → {pb:F2}s → {(pb > pa + 0.5 ? "OK" : "FAIL")}");

            // 3) 暂停
            PlayRecord(rec);
            var pc = _stream.CurrentTime.TotalSeconds;
            await Task.Delay(700);
            var pd = _stream.CurrentTime.TotalSeconds;
            W($"3) 暂停: IsPlaying={rec.IsPlaying} 位置 {pc:F2}s → {pd:F2}s → {(!rec.IsPlaying && Math.Abs(pd - pc) < 0.15 ? "OK" : "FAIL")}");

            // 4) 继续播放（从停点继续，不是从头）
            PlayRecord(rec);
            await Task.Delay(800);
            var pe = _stream.CurrentTime.TotalSeconds;
            W($"4) 继续播放: IsPlaying={rec.IsPlaying} 位置={pe:F2}s（停点 {pd:F2}s）→ {(rec.IsPlaying && pe > pd + 0.4 ? "OK" : "FAIL")}");

            // 5) 定位
            var target = Math.Min(1.0, Math.Max(0.2, rec.Duration / 2));
            _stream.CurrentTime = TimeSpan.FromSeconds(target);
            await Task.Delay(120);
            var pf = _stream.CurrentTime.TotalSeconds;
            W($"5) 定位到 {target:F2}s: 实测={pf:F2}s → {(Math.Abs(pf - target) < 0.2 ? "OK" : "FAIL")}");

            // 6) 播完：不得自动重播
            _stream.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, rec.Duration - 0.4));
            await Task.Delay(1600);
            var endedPos = _stream?.CurrentTime.TotalSeconds ?? -1;
            W($"6) 播完: IsPlaying={rec.IsPlaying} 位置={endedPos:F2}s → " +
              $"{(!rec.IsPlaying ? "OK(未自动重播)" : "FAIL")}");
            await Task.Delay(1200);   // 再等一会，确认没有循环重播
            var after = _stream?.CurrentTime.TotalSeconds ?? -1;
            W($"   播完 1.2s 后仍在停止态: IsPlaying={rec.IsPlaying} 位置={after:F2}s → " +
              $"{(!rec.IsPlaying && Math.Abs(after - endedPos) < 0.2 ? "OK" : "FAIL")}");

            // 7) 播完后再次点击播放
            PlayRecord(rec);
            await Task.Delay(800);
            var pg = _stream?.CurrentTime.TotalSeconds ?? -1;
            W($"7) 播完后再点播放: IsPlaying={rec.IsPlaying} 位置={pg:F2}s → {(rec.IsPlaying && pg > 0.2 ? "OK" : "FAIL")}");

            // 8) 切记录不叠加
            var rec2 = new GenRecord
            {
                HeaderText = "自检2", DisplayName = "自检2", TimeText = DateTime.Now.ToString("HH:mm:ss"),
                TextSnippet = "静音自检2", FileName = Path.GetFileName(path), FilePath = path,
            };
            _history.Insert(0, rec2);
            PlayRecord(rec2);
            await Task.Delay(600);
            W($"8) 切到另一条: 上一条 IsPlaying={rec.IsPlaying}（应 False）新一条 IsPlaying={rec2.IsPlaying} → " +
              $"{(!rec.IsPlaying && rec2.IsPlaying ? "OK" : "FAIL")}");

            // 9) 连点
            PlayRecord(rec2); PlayRecord(rec2); PlayRecord(rec2);
            await Task.Delay(250);
            W($"9) 连点三次(暂停/继续/暂停): IsPlaying={rec2.IsPlaying}（应 False）→ {(rec2.IsPlaying ? "FAIL" : "OK")}");
        }
        catch (Exception ex)
        {
            log.Add("异常: " + ex);
        }

        try { File.WriteAllText(App.AudioTestPath, string.Join("\r\n", log), new System.Text.UTF8Encoding(true)); }
        catch { }
        Application.Current.Shutdown();
    }

    // ---------------- 生成记录：从输出目录载入 ----------------

    private static readonly string[] AudioExts = { ".wav", ".mp3", ".pcm" };

    private static string LabelFromFileName(string name)
    {
        if (name.Contains("voiceclone")) return "克隆";
        if (name.Contains("voicedesign")) return "设计";
        if (name.Contains("mimo-v2.5-tts")) return "预置音色";
        return "音频";
    }

    /// <summary>重新扫描输出目录，按修改时间倒序列出音频文件。</summary>
    private void ReloadHistory(bool keepPlaying = false)
    {
        GenRecord playingRec = keepPlaying ? _playing : null;
        if (playingRec == null) StopPlayback();

        _history.Clear();
        var dir = AppSettings.OutputDir;
        int count = 0;
        try
        {
            if (Directory.Exists(dir))
            {
                var files = Directory.EnumerateFiles(dir)
                    .Select(f => new FileInfo(f))
                    .Where(fi => AudioExts.Contains(fi.Extension.ToLowerInvariant()))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .Take(300)
                    .ToList();

                foreach (var fi in files)
                {
                    var label = LabelFromFileName(fi.Name);
                    _history.Add(new GenRecord
                    {
                        HeaderText = label,
                        DisplayName = label,
                        TimeText = fi.LastWriteTime.ToString("MM-dd HH:mm"),
                        TextSnippet = "",
                        FileName = fi.Name,
                        FilePath = fi.FullName,
                    });
                }
                count = files.Count;
            }
        }
        catch (Exception ex)
        {
            Log("✗ 扫描输出目录失败：" + ex.Message);
        }

        if (HistoryHint != null) HistoryHint.Text = count > 0 ? $"{count} 个文件" : "";
        Log($"已从输出目录载入 {count} 条音频记录：{dir}");
        if (count > 0) _ = FillDurationsAsync();
    }

    /// <summary>后台读取每个文件的时长，回填进度条上限（不阻塞 UI）。</summary>
    private async Task FillDurationsAsync()
    {
        var snapshot = _history.ToList();
        await Task.Run(() =>
        {
            foreach (var rec in snapshot)
            {
                try
                {
                    using var s = OpenStream(rec.FilePath);
                    var d = s.TotalTime.TotalSeconds;
                    if (d > 0.1)
                    {
                        var target = rec;
                        var val = d;
                        Dispatcher.Invoke(() => target.Duration = val);
                    }
                }
                catch { }
            }
        });
    }

    private void BtnRefreshHistory_Click(object sender, RoutedEventArgs e)
    {
        LoadHistoryFromOutputDirEntry();
        StatusLeft.Text = "已刷新生成记录";
    }

    private void LoadHistoryFromOutputDirEntry() => ReloadHistory(keepPlaying: true);

    /// <summary>--screenshot=&lt;path&gt;：渲染自身为 PNG 后退出（零干扰自检，不抢焦点）。</summary>
    private void RunSelfCheckIfRequested()
    {
        var shot = App.ScreenshotPath;
        if (shot == null) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // --page= 直入目标界面后再渲染
                if (App.Page == "design") SegDesign.IsChecked = true;
                else if (App.Page == "settings") OpenSettings();
                if (App.Page == "rename")
                {
                    if (_history.Count > 0)
                    {
                        _renaming = _history[0];
                        TxtRename.Text = _renaming.DisplayName;
                        RenameOverlay.Visibility = Visibility.Visible;
                    }
                }
                if (App.Page == "about") BtnAbout_Click(null, null);
                if (App.Page == "delete" && _history.Count > 0)
                    RequestDelete(_history[0]);
                UpdateLayout();
                var restore = Opacity;
                Opacity = 1; // ghost 模式下临时恢复，否则 RTB 会连透明度一起渲染
                var rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(this);
                Opacity = restore;

                // 半透明玻璃层压平到白底，便于查看（否则 PNG 带 alpha 会显得发暗）
                var w = rtb.PixelWidth; var h = rtb.PixelHeight;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawImage(rtb, new Rect(0, 0, w, h));
                }
                var flat = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                flat.Render(dv);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(flat));
                using var fs = File.Create(shot);
                enc.Save(fs);
                Console.WriteLine("[shot] " + shot);
            }
            catch (Exception ex)
            {
                File.AppendAllText(shot + ".err.txt", ex.ToString());
            }
            finally
            {
                Application.Current.Shutdown();
            }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void UpdateChromeState()
    {
        if (BtnMax == null) return;
        BtnMax.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
    }

    /// <summary>应用/关闭炫彩毛玻璃（真实亚克力模糊）。</summary>
    private void ApplyGlass()
    {
        if (AppSettings.GlassEffect)
            AcrylicHelper.TryApply(this, 0x40, 0xF6, 0xF9, 0xFF);   // 浅蓝白轻着色
        else
            AcrylicHelper.TryApply(this, 0x00, 0xFF, 0xFF, 0xFF, acrylic: false); // 关掉模糊
    }

    // ---------------- 标题栏 ----------------

    private void Caption_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMax();
        else DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        if (AppSettings.TrimOnMinimize) MemoryTrimmer.Trim();
    }

    private void BtnMax_Click(object sender, RoutedEventArgs e) => ToggleMax();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMax() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // ---------------- 模型切换 ----------------

    private void Seg_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelPreset == null) return; // InitializeComponent 期间触发时元素未建全
        var tag = (sender as RadioButton)?.Tag as string;
        _kind = tag switch
        {
            "Clone" => TtsModelKind.Clone,
            "Design" => TtsModelKind.Design,
            _ => TtsModelKind.Preset,
        };
        PanelPreset.Visibility = _kind == TtsModelKind.Preset ? Visibility.Visible : Visibility.Collapsed;
        PanelClone.Visibility = _kind == TtsModelKind.Clone ? Visibility.Visible : Visibility.Collapsed;
        PanelDesign.Visibility = _kind == TtsModelKind.Design ? Visibility.Visible : Visibility.Collapsed;
        CardStyle.Visibility = _kind == TtsModelKind.Design ? Visibility.Collapsed : Visibility.Visible;
        ModelIdText.Text = ModelIds.Of(_kind);
        UpdateTextMetrics();
    }

    // ---------------- 字数统计 / 设计模型上限 ----------------

    private void TxtText_TextChanged(object sender, TextChangedEventArgs e) => UpdateTextMetrics();

    /// <summary>
    /// 刷新「合成文本」右上角字数统计：
    /// 预置 / 克隆只显示「N 字」；音色设计显示「N / 300 字」，接近与超限分别用警示色。
    /// 同时把文本框 MaxLength 与模式绑定，从输入源头挡住超限。
    /// </summary>
    private void UpdateTextMetrics()
    {
        if (TxtText == null || TxtTextCount == null || DesignLimitHint == null) return;

        int n = TxtText.Text?.Length ?? 0;
        bool design = _kind == TtsModelKind.Design;

        DesignLimitHint.Visibility = design ? Visibility.Visible : Visibility.Collapsed;

        if (design)
        {
            TxtTextCount.Text = $"{n} / {DesignTextLimit} 字";
            var near = n >= DesignTextLimit - 30;
            TxtTextCount.Foreground = n > DesignTextLimit
                ? (Brush)FindResource("ErrBrush")
                : near ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("MutedBrush");
            // 说明文字跟着状态变色：平时中性，接近/达到上限转警示，超限转错误
            DesignLimitHint.Foreground = n > DesignTextLimit
                ? (Brush)FindResource("ErrBrush")
                : near ? (Brush)FindResource("WarnBrush") : (Brush)FindResource("SubTextBrush");
            TxtText.MaxLength = DesignTextLimit;
        }
        else
        {
            TxtTextCount.Text = $"{n} 字";
            TxtTextCount.Foreground = (Brush)FindResource("MutedBrush");
            DesignLimitHint.Foreground = (Brush)FindResource("SubTextBrush");
            TxtText.MaxLength = 0;          // 0 = 不限制
        }
    }

    private void BuildDesignDims()
    {
        var subBrush = (Brush)FindResource("SubTextBrush");
        var boxStyle = (Style)FindResource("ModernTextBox");
        var comboStyle = (Style)FindResource("ModernComboBox");

        for (int i = 0; i < DesignDims.Length; i++)
        {
            var (label, options) = DesignDims[i];
            var row = i;

            var combo = new ComboBox { Style = comboStyle, MinHeight = 34, FontSize = 12.5 };
            foreach (var o in options) combo.Items.Add(o);
            combo.SelectedIndex = Array.IndexOf(options, DimDefaults[i]);

            var other = new TextBox
            {
                Style = boxStyle, MinHeight = 34, FontSize = 12.5,
                Visibility = Visibility.Collapsed,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(9, 0, 9, 0),
            };

            combo.SelectionChanged += (s, e) =>
            {
                var isOther = combo.SelectedItem as string == "其它";
                other.Visibility = isOther ? Visibility.Visible : Visibility.Collapsed;
                if (isOther) other.Focus();
                UpdateDesignPreview();
            };
            other.TextChanged += (s, e) => UpdateDesignPreview();

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });

            var lbl = new TextBlock
            {
                Text = label, FontSize = 12.5, Foreground = subBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(combo, 1);
            Grid.SetColumn(other, 3);
            grid.Children.Add(lbl);
            grid.Children.Add(combo);
            grid.Children.Add(other);

            DimsHost.Children.Add(grid);
            _dimCombos.Add(combo);
            _dimOthers.Add(other);
        }
        UpdateDesignPreview();
    }

    private string ComposeDesignDesc()
    {
        var parts = new List<string>();
        for (int i = 0; i < _dimCombos.Count; i++)
        {
            var sel = _dimCombos[i].SelectedItem as string;
            if (string.IsNullOrEmpty(sel) || sel == "无") continue;
            if (sel == "其它")
            {
                var t = _dimOthers[i].Text.Trim();
                if (t.Length > 0) parts.Add(t);
            }
            else parts.Add(sel);
        }
        var extra = TxtDesignExtra.Text.Trim();
        if (extra.Length > 0) parts.Add(extra);
        return string.Join("，", parts);
    }

    private void UpdateDesignPreview()
    {
        if (DesignPreview == null) return;
        var desc = ComposeDesignDesc();
        DesignPreview.Text = desc.Length == 0
            ? "合成时将按所选维度自动拼装音色描述。"
            : "音色描述预览：" + desc;
    }

    private void CmbVoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 仅同步显示，无需额外处理
    }

    private async void BtnPickRef_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择参考音频",
            Filter = "音频文件 (mp3/wav)|*.mp3;*.wav|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        TxtRefPath.Text = dlg.FileName;
        try
        {
            var fi = new FileInfo(dlg.FileName);
            var b64Estimate = fi.Length * 4 / 3;
            RefInfoText.Text = $"{Path.GetFileName(dlg.FileName)}｜{fi.Length / 1024.0:F0}KB｜" +
                               $"Base64 约 {b64Estimate / 1024.0:F0}KB" +
                               (b64Estimate > 10 * 1024 * 1024 ? "（⚠ 超过 10MB 上限）" : "（✓ 可用）");
        }
        catch (Exception ex)
        {
            RefInfoText.Text = "读取文件信息失败：" + ex.Message;
        }
        await Task.CompletedTask;
    }

    // ---------------- 生成 ----------------

    private async void BtnGen_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.ApiKey))
        {
            Log("API Key 未配置，请先在设置中填写。");
            OpenSettings();
            return;
        }

        var text = TxtText.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            Log("合成文本为空。");
            StatusLeft.Text = "请先输入要合成的文本";
            TxtText.Focus();
            return;
        }
        // 音色设计模型有硬输出上限，超限不会报错、只会静默丢字，故在发请求前挡住
        if (_kind == TtsModelKind.Design && text.Length > DesignTextLimit)
        {
            Log($"✗ 音色设计单次上限 {DesignTextLimit} 字，当前 {text.Length} 字（超出 {text.Length - DesignTextLimit} 字）。" +
                "该模型超出后会前后丢字，请删减，或切换到「预置音色」/「音色克隆」。");
            StatusLeft.Text = $"音色设计上限 {DesignTextLimit} 字，当前超出 {text.Length - DesignTextLimit} 字";
            TxtText.Focus();
            return;
        }

        var job = new TtsJob
        {
            Kind = _kind,
            Text = text,
            Instruction = TxtStyle.Text,
            VoiceId = CmbVoice.SelectedItem as string ?? "mimo_default",
            RefPath = TxtRefPath.Text,
            DesignDesc = ComposeDesignDesc(),
            Format = AppSettings.OutputFormat,
        };

        BtnGen.IsEnabled = false;
        StatusLeft.Text = "合成中…";
        try
        {
            var path = await MimoTtsService.SynthesizeAsync(job, AppSettings.ApiKey, AppSettings.OutputDir,
                s => Log(s), System.Threading.CancellationToken.None);


            var header = _kind switch
            {
                TtsModelKind.Clone => "克隆",
                TtsModelKind.Design => "设计",
                _ => "预置·" + job.VoiceId,
            };
            var record = new GenRecord
            {
                HeaderText = header,
                DisplayName = header,
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                TextSnippet = job.Text.Replace("\r", "").Replace("\n", " "),
                FileName = Path.GetFileName(path),
                FilePath = path,
            };
            _history.Insert(0, record);
            if (_history.Count > 60)
                _history.RemoveAt(_history.Count - 1);

            StatusLeft.Text = "完成：" + Path.GetFileName(path);
            if (ChkAutoPlay.IsChecked == true) await AutoPlayAfterGenerateAsync(record);
        }
        catch (Exception ex)
        {
            Log("✗ " + ex.Message);
            StatusLeft.Text = "合成失败：" + ex.Message.Split('\n')[0];
        }
        finally
        {
            BtnGen.IsEnabled = true;
        }
    }

    // ---------------- 播放（NAudio：WAV/MP3/PCM 精确控制，起播不裁切） ----------------

    /// <summary>
    /// 在音频数据前插入一段数字静音后再送声卡。
    /// 部分声卡/驱动在起播时会丢弃首个缓冲，导致开头一两个字被吞；
    /// 把静音放在最前面，被丢弃的就只是静音，台词不受影响。文件本身不做任何改动。
    /// </summary>
    private sealed class LeadInStream : WaveStream
    {
        private readonly WaveStream _src;
        private readonly long _leadBytes;
        private long _pos;

        public LeadInStream(WaveStream src, int leadMs)
        {
            _src = src;
            var bytes = src.WaveFormat.AverageBytesPerSecond * (long)leadMs / 1000L;
            var align = src.WaveFormat.BlockAlign;
            _leadBytes = bytes - bytes % Math.Max(1, align);
            _pos = 0;
        }

        public long LeadInBytes => _leadBytes;
        public override WaveFormat WaveFormat => _src.WaveFormat;
        public override long Length => _leadBytes + _src.Length;

        public override long Position
        {
            get => _pos;
            set
            {
                _pos = Math.Max(0, Math.Min(value, Length));
                try { _src.Position = Math.Max(0, _pos - _leadBytes); } catch { }
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            if (_pos < _leadBytes)
            {
                var n = (int)Math.Min(count, _leadBytes - _pos);
                Array.Clear(buffer, offset, n);
                _pos += n; total += n; offset += n; count -= n;
            }
            if (count > 0)
            {
                var n = _src.Read(buffer, offset, count);
                _pos += n; total += n;
            }
            return total;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _src.Dispose();
            base.Dispose(disposing);
        }
    }

    private WaveStream OpenStream(string path, int? leadMs = null)
    {
        WaveStream raw = Path.GetExtension(path).Equals(".pcm", StringComparison.OrdinalIgnoreCase)
            ? new RawSourceWaveStream(File.OpenRead(path), new WaveFormat(24000, 16, 1))  // 裸流：24kHz 单声道 16bit
            : new AudioFileReader(path);                                                  // wav / mp3
        var lead = leadMs ?? AppSettings.PlayLeadInMs;
        return lead > 0 ? new LeadInStream(raw, lead) : raw;
    }

    /// <summary>预热音频设备：首次使用音频端点时驱动初始化很慢，容易丢掉起播的首个缓冲。</summary>
    private void WarmUpAudioDevice()
    {
        try
        {
            var silence = new SilenceProvider(new WaveFormat(24000, 16, 1));
            using var wo = new WaveOutEvent { Volume = 0f, DesiredLatency = 120, NumberOfBuffers = 3 };
            wo.Init(silence);
            wo.Play();
            System.Threading.Thread.Sleep(250);
            wo.Stop();
        }
        catch { }
    }

    /// <summary>生成完成后的自动播放：设备预热 + 稍作停顿 + 更长静音前导（冷启动丢缓冲更多）。</summary>
    private async Task AutoPlayAfterGenerateAsync(GenRecord rec)
    {
        await Task.Delay(150);                       // 让新文件与系统状态落定
        PlayRecord(rec, AppSettings.PlayLeadInMs + 300);
    }

    private void PlayRecord(GenRecord rec, int? leadMs = null)
    {
        if (rec == null) return;
        if (!File.Exists(rec.FilePath)) { Log("文件不存在：" + rec.FileName); return; }

        // 带自定义前导（自动播放路径）时强制重新加载，确保前导生效
        if (rec == _playing && _loaded && _out != null && leadMs == null)
        {
            if (rec.IsPlaying) { _out.Pause(); rec.IsPlaying = false; _tick.Stop(); }
            else { _out.Play(); rec.IsPlaying = true; _tick.Start(); }
            return;
        }
        LoadAndPlay(rec, leadMs);
    }

    private void LoadAndPlay(GenRecord rec, int? leadMs = null)
    {
        StopPlayback();
        try
        {
            _stream = OpenStream(rec.FilePath, leadMs);
            _out = new WaveOutEvent { Volume = _volume, DesiredLatency = 120, NumberOfBuffers = 3 };
            _out.PlaybackStopped += OnPlaybackStopped;
            _out.Init(_stream);
            _playing = rec;
            rec.Position = 0;
            if (_stream.TotalTime.TotalSeconds > 0.1) rec.Duration = _stream.TotalTime.TotalSeconds;
            _out.Play();               // NAudio 直接起播，无需等 Open 事件，开头不会被吞
            rec.IsPlaying = true;
            _loaded = true;
            _tick.Start();
            StatusLeft.Text = "播放：" + rec.FileName;
        }
        catch (Exception ex)
        {
            _loaded = false;
            Log("✗ 打开音频失败：" + ex.Message);
            StatusLeft.Text = "播放失败：" + ex.Message;
        }
    }

    private void OnPlaybackStopped(object sender, StoppedEventArgs e)
    {
        // NAudio 的 Stop() 是异步的：切换记录时旧输出的 PlaybackStopped 会延迟到达。
        // 必须先按 sender 识别是否当前输出，否则会把新记录的播放状态误清掉（状态错乱/看似重复播放）。
        if (!ReferenceEquals(sender, _out)) return;
        if (_manualStop) return;        // 主动停止时由 StopPlayback 处理
        _tick.Stop();
        _loaded = false;                // 已播完：下次点击播放需重新加载
        if (_playing != null)
        {
            _playing.IsPlaying = false;
            _playing.Position = _playing.Duration;   // 停在末尾，不自动重播
        }
        StatusLeft.Text = "播放结束";
    }

    private void StopPlayback()
    {
        _tick.Stop();
        _manualStop = true;
        var oldOut = _out;
        var oldStream = _stream;
        _out = null;                    // 先置空：之后到达的旧事件会被 OnPlaybackStopped 忽略
        _stream = null;
        if (oldOut != null)
        {
            oldOut.PlaybackStopped -= OnPlaybackStopped;   // 先解除订阅，避免旧事件污染新状态
            try { oldOut.Stop(); } catch { }
            try { oldOut.Dispose(); } catch { }
        }
        try { oldStream?.Dispose(); } catch { }
        _manualStop = false;
        _loaded = false;
        if (_playing != null) { _playing.IsPlaying = false; _playing.Position = 0; }
        _playing = null;
    }

    private void RowPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is GenRecord r) PlayRecord(r);
    }

    private void RowSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || !_loaded || _stream == null || _playing == null) return;
        if ((sender as Slider)?.DataContext != _playing) return;
        try { _stream.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, e.NewValue)); } catch { }
    }

    private void RowSeek_DragCompleted(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _stream == null || _playing == null) return;
        if ((sender as Thumb)?.DataContext is GenRecord r && r == _playing)
        {
            try { _stream.CurrentTime = TimeSpan.FromSeconds(Math.Max(0, r.Position)); } catch { }
        }
    }

    // ---------------- 删除 ----------------

    /// <summary>从按钮 / 右键菜单里取出对应的记录（ContextMenu 不在可视树内，需走 PlacementTarget）。</summary>
    private static GenRecord ResolveRecord(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is GenRecord r) return r;
            if (sender is MenuItem mi && mi.Parent is ContextMenu cm &&
                cm.PlacementTarget is FrameworkElement pt && pt.DataContext is GenRecord r2)
                return r2;
        }
        return null;
    }

    private void RequestDelete(GenRecord rec)
    {
        if (rec == null) return;
        _deleting = rec;
        DeleteInfo.Text = $"{rec.DisplayName}　·　{rec.FileName}";
        DeleteOverlay.Visibility = Visibility.Visible;
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e) => RequestDelete(ResolveRecord(sender));

    private void MenuDelete_Click(object sender, RoutedEventArgs e) => RequestDelete(ResolveRecord(sender));

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        _renaming = ResolveRecord(sender);
        if (_renaming == null) return;
        TxtRename.Text = _renaming.DisplayName;
        RenameOverlay.Visibility = Visibility.Visible;
        TxtRename.Focus();
        TxtRename.SelectAll();
    }

    private void BtnDeleteCancel_Click(object sender, RoutedEventArgs e)
    {
        DeleteOverlay.Visibility = Visibility.Collapsed;
        _deleting = null;
    }

    private void BtnDeleteRecordOnly_Click(object sender, RoutedEventArgs e) => RemoveRecord(_deleting, false);

    private void BtnDeleteRecordAndFile_Click(object sender, RoutedEventArgs e) => RemoveRecord(_deleting, true);

    private void RemoveRecord(GenRecord rec, bool deleteFile)
    {
        try
        {
            if (rec != null)
            {
                if (rec == _playing) StopPlayback();

                if (deleteFile && File.Exists(rec.FilePath))
                {
                    if (RecycleBin.DeleteToRecycleBin(rec.FilePath))
                        Log("已移入回收站：" + rec.FileName);
                    else
                    {
                        File.Delete(rec.FilePath);
                        Log("已删除文件（回收站不可用，已直接删除）：" + rec.FileName);
                    }
                }

                _history.Remove(rec);
                if (HistoryHint != null) HistoryHint.Text = _history.Count > 0 ? $"{_history.Count} 个文件" : "";
                StatusLeft.Text = deleteFile ? "已删除记录与文件" : "已移除记录（文件保留）";
            }
        }
        catch (Exception ex)
        {
            Log("✗ 删除失败：" + ex.Message);
        }
        finally
        {
            DeleteOverlay.Visibility = Visibility.Collapsed;
            _deleting = null;
        }
    }

    // ---------------- 重命名 ----------------

    private void RowRename_Click(object sender, RoutedEventArgs e)
    {
        _renaming = (sender as Button)?.Tag as GenRecord;
        if (_renaming == null) return;
        TxtRename.Text = _renaming.DisplayName;
        RenameOverlay.Visibility = Visibility.Visible;
        TxtRename.Focus();
        TxtRename.SelectAll();
    }

    private void BtnRenameSave_Click(object sender, RoutedEventArgs e)
    {
        if (_renaming != null)
        {
            var t = TxtRename.Text.Trim();
            _renaming.DisplayName = t.Length > 0 ? t : _renaming.HeaderText;
        }
        RenameOverlay.Visibility = Visibility.Collapsed;
        _renaming = null;
    }

    private void BtnRenameCancel_Click(object sender, RoutedEventArgs e)
    {
        RenameOverlay.Visibility = Visibility.Collapsed;
        _renaming = null;
    }

    /// <summary>按配置初始化"自动播放"勾选（放在 ctor 里，避免 XAML 属性触发事件时元素未建全）。</summary>
    private void CmbAutoPlayInit()
    {
        ChkAutoPlay.IsChecked = AppSettings.AutoPlay;
    }

    private void ChkAutoPlay_Changed(object sender, RoutedEventArgs e)
    {
        if (ChkAutoPlay == null) return;
        AppSettings.AutoPlay = ChkAutoPlay.IsChecked == true;
        AppSettings.Save();
    }

    private void LstHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is GenRecord r && File.Exists(r.FilePath))
        {
            PlayRecord(r);
            StatusLeft.Text = "播放：" + r.FileName;
        }
    }

    private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.OutputDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.OutputDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log("✗ 打开目录失败：" + ex.Message); }
    }

    // ---------------- 设置 ----------------

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ---------------- 关于 / 版权申明 ----------------

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        AboutVersion.Text = $"版本 {v?.Major}.{v?.Minor}.{v?.Build}　·　MIT License　·　© 2026 BillChen";
        AboutOverlay.Visibility = Visibility.Visible;
    }

    private void BtnAboutClose_Click(object sender, RoutedEventArgs e) =>
        AboutOverlay.Visibility = Visibility.Collapsed;

    private void AboutOverlay_Backdrop_Click(object sender, MouseButtonEventArgs e) =>
        AboutOverlay.Visibility = Visibility.Collapsed;

    private void DeleteOverlay_Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        DeleteOverlay.Visibility = Visibility.Collapsed;
        _deleting = null;
    }

    private void OpenSettings()
    {
        PwdApiKey.Password = AppSettings.ApiKey;
        TxtApiKey.Text = AppSettings.ApiKey;
        TxtOutDir.Text = AppSettings.OutputDir;
        ChkShowKey.IsChecked = false; // 默认密文显示
        CmbFormat.SelectedItem = AppSettings.OutputFormat;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log("✗ 打开链接失败：" + ex.Message); }
        e.Handled = true;
    }

    private void ChkShowKey_Changed(object sender, RoutedEventArgs e)
    {
        if (PwdApiKey == null || TxtApiKey == null || ChkShowKey == null) return;

        if (ChkShowKey.IsChecked == true)
        {
            TxtApiKey.Text = PwdApiKey.Password;
            TxtApiKey.Visibility = Visibility.Visible;
            PwdApiKey.Visibility = Visibility.Collapsed;
        }
        else
        {
            PwdApiKey.Password = TxtApiKey.Text; // 明文改回时反向同步
            PwdApiKey.Visibility = Visibility.Visible;
            TxtApiKey.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnSettingsCancel_Click(object sender, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    private void BtnSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var key = (ChkShowKey.IsChecked == true ? TxtApiKey.Text : PwdApiKey.Password).Trim();
        AppSettings.ApiKey = key;
        AppSettings.OutputDir = string.IsNullOrWhiteSpace(TxtOutDir.Text)
            ? AppSettings.OutputDir : TxtOutDir.Text.Trim();
        if (CmbFormat.SelectedItem is string fmtSel) AppSettings.OutputFormat = fmtSel;
        AppSettings.Save();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        TitleHint.Text = $"v{ver?.Major}.{ver?.Minor}.{ver?.Build} · 非官方客户端";
        StatusRight.Text = $"输出：{AppSettings.OutputDir}";
        Log(AppSettings.ApiKey.Length > 0
            ? $"设置已保存（API Key {AppSettings.ApiKey.Length} 字符，输出 {AppSettings.OutputDir}）"
            : "设置已保存（API Key 为空）");
    }

    private void BtnPickOutDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录" };
        if (dlg.ShowDialog(this) == true) TxtOutDir.Text = dlg.FolderName;
    }

    private void SettingsOverlay_Backdrop_Click(object sender, MouseButtonEventArgs e)
    {
        // 点遮罩不关闭，避免误触丢输入；只允许按钮关闭
    }

    // ---------------- 日志 ----------------

    private void Log(string msg)
    {
        if (TxtLog == null) return;
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}\r\n");
        TxtLog.ScrollToEnd();
    }
}
