using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MimoTtsVoiceCraft.Models;

/// <summary>生成记录列表项：支持重命名与内嵌播放状态。</summary>
public class GenRecord : INotifyPropertyChanged
{
    public const string PlayGlyphData = "M8,5.14V19.14L19,12.14L8,5.14Z";
    public const string PauseGlyphData = "M14,19H18V5H14M6,19H10V5H6V19Z";

    public string HeaderText { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string TextSnippet { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";

    private string _displayName = "";
    /// <summary>显示名（可重命名；默认 = 生成时的模型/音色标签）</summary>
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropertyChanged(); }
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        set { _duration = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
    }

    private double _position;
    public double Position
    {
        get => _position;
        set { _position = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(PlayGlyph)); }
    }

    public string PlayGlyph => IsPlaying ? PauseGlyphData : PlayGlyphData;

    public string ProgressText => $"{Fmt(Position)} / {Fmt(Duration)}";

    private static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
