using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MimoTtsVoiceCraft.Services;

public enum TtsModelKind { Preset, Clone, Design }

public static class ModelIds
{
    public const string Preset = "mimo-v2.5-tts";
    public const string Clone = "mimo-v2.5-tts-voiceclone";
    public const string Design = "mimo-v2.5-tts-voicedesign";

    public static string Of(TtsModelKind kind) => kind switch
    {
        TtsModelKind.Clone => Clone,
        TtsModelKind.Design => Design,
        _ => Preset,
    };
}

/// <summary>一次合成请求的全部输入。</summary>
public class TtsJob
{
    public TtsModelKind Kind { get; set; }
    public string Text { get; set; } = "";          // assistant 消息：要合成的文本（可含 (风格) 标签）
    public string Instruction { get; set; } = "";   // user 消息：风格指令（Preset/Clone 可用）
    public string VoiceId { get; set; } = "";       // Preset：预置音色 ID
    public string RefPath { get; set; } = "";       // Clone：参考音频 mp3/wav
    public string DesignDesc { get; set; } = "";    // Design：音色描述（user 消息）
    public string Format { get; set; } = "wav";     // 输出格式：wav / mp3 / pcm / pcm16
}

/// <summary>MiMo TTS 三模型的 HTTP 客户端封装（OpenAI 兼容协议）。</summary>
public class MimoTtsService
{
    private const string BaseUrl = "https://api.xiaomimimo.com/v1/chat/completions";
    private const long MaxBase64Len = 10 * 1024 * 1024; // 文档：Base64 串不超过 10MB

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>合成并保存 wav，返回输出文件路径。异常抛给调用方。</summary>
    public static async Task<string> SynthesizeAsync(TtsJob job, string apiKey, string outDir,
        Action<string> log, CancellationToken ct)
    {
        // 组 messages：user = 风格指令 / 音色描述；assistant = 合成文本
        string userContent = job.Kind switch
        {
            TtsModelKind.Design => job.DesignDesc.Trim(),
            _ => job.Instruction.Trim(),
        };
        if (string.IsNullOrWhiteSpace(userContent))
            userContent = job.Kind == TtsModelKind.Design
                ? throw new ArgumentException("音色设计必须填写音色描述（user 消息为必填）")
                : "";

        if (string.IsNullOrWhiteSpace(job.Text))
            throw new ArgumentException("合成文本不能为空");

        object audio;
        var fmt = string.IsNullOrWhiteSpace(job.Format) ? "wav" : job.Format.ToLowerInvariant();
        if (job.Kind == TtsModelKind.Preset)
        {
            if (string.IsNullOrWhiteSpace(job.VoiceId))
                throw new ArgumentException("请选择预置音色");
            audio = new { format = fmt, voice = job.VoiceId };
        }
        else if (job.Kind == TtsModelKind.Clone)
        {
            if (string.IsNullOrWhiteSpace(job.RefPath) || !File.Exists(job.RefPath))
                throw new ArgumentException("请选择有效的参考音频文件（mp3/wav）");
            var ext = Path.GetExtension(job.RefPath).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".wav")
                throw new ArgumentException("参考音频仅支持 mp3 / wav 格式，当前：" + ext);

            var bytes = await File.ReadAllBytesAsync(job.RefPath, ct);
            var b64 = Convert.ToBase64String(bytes);
            if (b64.Length > MaxBase64Len)
                throw new ArgumentException($"参考音频 Base64 后 {b64.Length / 1024.0 / 1024.0:F1}MB，超过 10MB 上限");
            var mime = ext == ".mp3" ? "audio/mpeg" : "audio/wav";
            log($"参考音频 {Path.GetFileName(job.RefPath)}：{bytes.Length / 1024.0:F0}KB → Base64 {b64.Length / 1024.0:F0}KB");
            audio = new { format = fmt, voice = $"data:{mime};base64,{b64}" };
        }
        else
        {
            audio = new { format = fmt };
        }

        var payload = new
        {
            model = ModelIds.Of(job.Kind),
            messages = new object[]
            {
                new { role = "user", content = userContent },
                new { role = "assistant", content = job.Text },
            },
            audio,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        log($"POST {ModelIds.Of(job.Kind)} …");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}\n{Truncate(body, 800)}");

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("audio")
            .GetProperty("data").GetString();
        if (string.IsNullOrEmpty(data))
            throw new InvalidDataException("响应中没有 audio.data 字段\n" + Truncate(body, 800));

        var raw = Convert.FromBase64String(data);
        Directory.CreateDirectory(outDir);
        var extOut = fmt switch
        {
            "mp3" => ".mp3",
            "pcm" or "pcm16" => ".pcm",
            _ => ".wav",
        };
        var outPath = Path.Combine(outDir,
            $"{DateTime.Now:yyyyMMdd_HHmmss}_{ModelIds.Of(job.Kind)}{extOut}");
        await File.WriteAllBytesAsync(outPath, raw, ct);
        log($"[{sw.ElapsedMilliseconds}ms] {Path.GetFileName(outPath)}（{raw.Length / 1024.0:F0}KB，{fmt}）已保存");
        return outPath;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
