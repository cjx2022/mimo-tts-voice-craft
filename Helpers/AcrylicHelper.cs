using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MimoTtsVoiceCraft.Helpers;

/// <summary>
/// 真实亚克力毛玻璃（Win10 1803+ / Win11 通用）。
/// 注意：WPF 下 DWM 的 DWMWA_SYSTEMBACKDROP_TYPE(Mica/Acrylic) 材质透不出来
/// （根容器设 Transparent 会变纯黑），所以走 SetWindowCompositionAttribute 这条路。
/// 本方法失败时窗口只是失去"外部模糊"，自绘的炫彩渐变底仍在，不影响可用性。
/// </summary>
public static class AcrylicHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    /// <summary>尝试为窗口启用亚克力模糊。返回是否成功（不抛异常）。</summary>
    public static bool TryApply(Window w, byte alpha, byte r, byte g, byte b, bool acrylic = true)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // ABGR
            var gradient = (alpha << 24) | (b << 16) | (g << 8) | r;
            var accent = new AccentPolicy
            {
                AccentState = acrylic ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND,
                AccentFlags = 2,
                GradientColor = gradient,
                AnimationId = 0,
            };

            var size = Marshal.SizeOf(accent);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false; // 失败即降级为"自绘炫彩渐变"，不影响使用
        }
    }
}
