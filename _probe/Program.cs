using MimoTtsVoiceCraft.Services;

// 服务层探针：真实调用 MiMo API 验证三模型通路
// 用法: dotnet run --project _probe -- <apikey> [参考音频路径]
var key = args.Length > 0 ? args[0] : "";
var refAudio = args.Length > 1 ? args[1] : "voice.mp3";   // 克隆用的参考音频（mp3/wav，自备）
if (key.Length == 0) { Console.WriteLine("用法: probe <apikey> [参考音频路径]"); return 1; }

var outDir = Path.Combine(Path.GetTempPath(), "mimo_probe");
var jobs = new List<TtsJob>
{
    new() { Kind = TtsModelKind.Preset, VoiceId = "冰糖",
            Text = "探针测试，预置音色，普通话合成。" },
    new() { Kind = TtsModelKind.Clone, RefPath = refAudio,
            Text = "探针测试，音色克隆。" },
    new() { Kind = TtsModelKind.Design, DesignDesc = "一位二十多岁的年轻女性，嗓音明亮清脆，语速轻快，元气活泼。",
            Text = "探针测试，音色设计。" },
};

int ok = 0;
foreach (var job in jobs)
{
    try
    {
        var path = await MimoTtsService.SynthesizeAsync(job, key, outDir,
            s => Console.WriteLine("  " + s), CancellationToken.None);
        var size = new FileInfo(path).Length;
        Console.WriteLine($"[OK] {ModelIds.Of(job.Kind)} -> {Path.GetFileName(path)} ({size} bytes)");
        ok++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FAIL] {ModelIds.Of(job.Kind)}\n{ex.Message}");
    }
}
Console.WriteLine($"通过 {ok}/3");
return ok == 3 ? 0 : 2;
