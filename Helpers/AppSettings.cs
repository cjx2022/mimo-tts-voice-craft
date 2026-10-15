using System.IO;

namespace MimoTtsVoiceCraft.Helpers;

/// <summary>
/// 极简配置：%APPDATA%\MimoTtsVoiceCraft\settings.ini
/// 首次运行自动生成一份带注释的默认配置。
/// </summary>
public static class AppSettings
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MimoTtsVoiceCraft");

    public static string Location => Path.Combine(Dir, "settings.ini");

    /// <summary>MiMo 开放平台 API Key（用户明确要求可在设置中配置并落盘）</summary>
    public static string ApiKey { get; set; } = "";

    /// <summary>合成音频输出目录</summary>
    public static string OutputDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MimoTtsVoiceCraft");

    /// <summary>true = 软件渲染（不创建 D3D 设备，省约 150MB 显卡驱动内存）</summary>
    public static bool SoftwareRendering { get; set; } = true;

    /// <summary>最小化时回收工作集</summary>
    public static bool TrimOnMinimize { get; private set; } = true;

    /// <summary>桌面亚克力模糊（默认关闭：界面已自绘炫彩渐变，开启不再有明显收益）</summary>
    public static bool GlassEffect { get; set; } = false;

    /// <summary>输出音频格式：wav / mp3 / pcm / pcm16（API 实测支持这四种）</summary>
    public static string OutputFormat { get; set; } = "wav";

    /// <summary>生成完成后自动播放</summary>
    public static bool AutoPlay { get; set; } = true;

    /// <summary>播放时在音频前插入的静音毫秒数（抵消声卡起播丢首个缓冲导致的"吞字"）</summary>
    public static int PlayLeadInMs { get; set; } = 120;

    public static void Load()
    {
        try
        {
            if (!File.Exists(Location))
            {
                Save();
                return;
            }

            foreach (var raw in File.ReadAllLines(Location))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                var i = line.IndexOf('=');
                if (i <= 0) continue;

                var key = line.Substring(0, i).Trim().ToLowerInvariant();
                var val = line.Substring(i + 1).Trim();

                switch (key)
                {
                    case "apikey":
                        ApiKey = val;
                        break;
                    case "outputdir":
                        if (val.Length > 0) OutputDir = val;
                        break;
                    case "rendermode":
                        SoftwareRendering = !val.Equals("hardware", StringComparison.OrdinalIgnoreCase)
                                            && !val.Equals("auto", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "trimonminimize":
                        TrimOnMinimize = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "glasseffect":
                        GlassEffect = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "outputformat":
                        if (val.Length > 0) OutputFormat = val.ToLowerInvariant();
                        break;
                    case "autoplay":
                        AutoPlay = val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "leadinms":
                        if (int.TryParse(val, out var ms) && ms >= 0 && ms <= 2000) PlayLeadInMs = ms;
                        break;
                }
            }
        }
        catch { /* 读不了就用默认值 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var text =
                "# MiMo TTS Voice Craft配置\r\n" +
                "#\r\n" +
                "# ApiKey: 小米 MiMo 开放平台 API Key（platform.xiaomimimo.com 控制台获取）\r\n" +
                "ApiKey=" + ApiKey + "\r\n" +
                "#\r\n" +
                "# OutputDir: 合成音频的输出目录\r\n" +
                "OutputDir=" + OutputDir + "\r\n" +
                "#\r\n" +
                "# RenderMode: Software = 软件渲染（默认，省约 150MB 显卡驱动内存）\r\n" +
                "#             Hardware = 硬件加速（动画更顺，但会加载显卡驱动约 150MB+）\r\n" +
                "RenderMode=" + (SoftwareRendering ? "Software" : "Hardware") + "\r\n" +
                "#\r\n" +
                "# TrimOnMinimize: 1 = 最小化时回收工作集（1/0）\r\n" +
                "TrimOnMinimize=" + (TrimOnMinimize ? "1" : "0") + "\r\n" +
                "#\r\n" +
                "# GlassEffect: 1 = 开启桌面亚克力模糊（默认 0；界面自带炫彩渐变，一般无需开）\r\n" +
                "GlassEffect=" + (GlassEffect ? "1" : "0") + "\r\n" +
                "#\r\n" +
                "# OutputFormat: 输出音频格式，可选 wav / mp3 / pcm / pcm16\r\n" +
                "#               pcm 与 pcm16 为无文件头的裸流（24kHz 单声道 16bit）\r\n" +
                "OutputFormat=" + OutputFormat + "\r\n" +
                "#\r\n" +
                "# AutoPlay: 1 = 生成完成后自动播放（1/0）\r\n" +
                "AutoPlay=" + (AutoPlay ? "1" : "0") + "\r\n" +
                "#\r\n" +
                "# LeadInMs: 播放时在音频前插入的静音毫秒数，默认 120\r\n" +
                "#           用于抵消部分声卡起播时丢弃首个缓冲造成的开头被截（吞字）\r\n" +
                "LeadInMs=" + PlayLeadInMs + "\r\n";
            File.WriteAllText(Location, text, new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
