using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MimoTtsVoiceCraft.Helpers;

/// <summary>
/// 无边框窗口的拖拽 / 缩放 / 最大化修正。
/// WindowStyle=None + ResizeMode=CanResize 时 WPF 不会自己处理边缘缩放，
/// 必须自己回 WM_NCHITTEST；最大化还要回 WM_GETMINMAXINFO 否则会盖住任务栏。
/// </summary>
public static class ChromeHelper
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 给窗口装上自绘外壳。
    /// </summary>
    /// <param name="w">目标窗口（需 WindowStyle=None, ResizeMode=CanResize）</param>
    /// <param name="captionHeight">标题栏高度（DIP），此区域内按标题栏处理</param>
    /// <param name="interactive">标题栏里需要可点击的元素（按钮等）</param>
    /// <param name="resizeBorder">边缘缩放命中宽度（DIP）</param>
    public static void Attach(Window w, double captionHeight, IEnumerable<FrameworkElement> interactive,
                              double resizeBorder = 6)
    {
        var list = new List<FrameworkElement>(interactive ?? Array.Empty<FrameworkElement>());

        w.SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 系统圆角（Win11），并跟浅色主题
            int round = 2, dark = 0;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == WM_GETMINMAXINFO)
                {
                    FixMaximizedBounds(w, h, lp);
                    handled = true;
                    return IntPtr.Zero;
                }
                if (msg == WM_NCHITTEST)
                {
                    handled = true;
                    return new IntPtr(HitTest(w, lp, captionHeight, list, resizeBorder));
                }
                return IntPtr.Zero;
            });
        };
    }

    private static int HitTest(Window w, IntPtr lParam, double captionHeight,
                               List<FrameworkElement> interactive, double border)
    {
        long v = lParam.ToInt64();
        int sx = unchecked((short)(v & 0xFFFF));
        int sy = unchecked((short)((v >> 16) & 0xFFFF));

        // 物理像素 → DIP
        var src = PresentationSource.FromVisual(w);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var tl = w.PointToScreen(new Point(0, 0));
        var br = w.PointToScreen(new Point(w.ActualWidth, w.ActualHeight));
        double winW = br.X - tl.X;
        double winH = br.Y - tl.Y;
        double px = sx - tl.X;
        double py = sy - tl.Y;

        bool atLeft = px <= border * dpiX;
        bool atRight = px >= winW - border * dpiX;
        bool atTop = py <= border * dpiY;
        bool atBottom = py >= winH - border * dpiY;

        if (w.WindowState != WindowState.Maximized)
        {
            if (atTop && atLeft) return HTTOPLEFT;
            if (atTop && atRight) return HTTOPRIGHT;
            if (atBottom && atLeft) return HTBOTTOMLEFT;
            if (atBottom && atRight) return HTBOTTOMRIGHT;
            if (atLeft) return HTLEFT;
            if (atRight) return HTRIGHT;
            if (atTop) return HTTOP;
            if (atBottom) return HTBOTTOM;
        }

        // 标题栏区域
        if (py <= captionHeight * dpiY + 0.5)
        {
            // 落在可点元素上 → 交给 WPF
            foreach (var el in interactive)
            {
                if (el == null || !el.IsVisible) continue;
                var a = el.PointToScreen(new Point(0, 0));
                var b = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                if (sx >= a.X && sx <= b.X && sy >= a.Y && sy <= b.Y) return HTCLIENT;
            }
            return HTCAPTION;
        }

        return HTCLIENT;
    }

    private static void FixMaximizedBounds(Window w, IntPtr hwnd, IntPtr lParam)
    {
        var mi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var moni = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref moni)) return;

        int workW = moni.rcWork.right - moni.rcWork.left;
        int workH = moni.rcWork.bottom - moni.rcWork.top;

        var src = PresentationSource.FromVisual(w);
        double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        // 最小尺寸必须 clamp 到工作区，否则高 DPI 下最大化会被 MinWidth 撑爆
        int minW = (int)Math.Min(w.MinWidth * dpiX, workW);
        int minH = (int)Math.Min(w.MinHeight * dpiY, workH);
        mi.ptMinTrackSize.x = Math.Max(minW, 64);
        mi.ptMinTrackSize.y = Math.Max(minH, 64);

        // 最大化时对齐到工作区（否则无边框窗口会盖住任务栏）
        mi.ptMaxPosition.x = moni.rcWork.left - moni.rcMonitor.left;
        mi.ptMaxPosition.y = moni.rcWork.top - moni.rcMonitor.top;
        mi.ptMaxSize.x = workW;
        mi.ptMaxSize.y = workH;

        Marshal.StructureToPtr(mi, lParam, false);
    }
}
