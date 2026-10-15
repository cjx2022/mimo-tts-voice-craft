# -*- coding: utf-8 -*-
"""标题栏命中测试探针：验证无边框窗口上每个按钮真的可点。

背景：WindowStyle=None 的窗口靠 WM_NCHITTEST 决定某点算「标题栏（可拖动）」还是「客户区（可点击）」。
按钮只要没被注册进可点集合，就会被当成拖动区 → 表现为「按钮点不动」。
本脚本用 SendMessage(WM_NCHITTEST) 直接问系统，返回码即答案：
  1 = HTCLIENT（可点击）  2 = HTCAPTION（拖动区）  其它 = 边框缩放等

用法:
  1) 起一个离屏窗口： mimo-tts-voice-craft.exe --ghost --opaque
  2) python tools/hittest_probe.py
"""
import ctypes
import sys
import time
from ctypes import wintypes

user32 = ctypes.windll.user32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TITLE = "MiMo TTS Voice Craft"
WM_NCHITTEST = 0x0084
HTCLIENT, HTCAPTION = 1, 2

hwnd = 0
for _ in range(40):
    hwnd = user32.FindWindowW(None, TITLE)
    if hwnd:
        break
    time.sleep(0.25)
if not hwnd:
    print("ERR: 未找到窗口", TITLE, "（先用 --ghost --opaque 启动）")
    sys.exit(1)

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
w = rect.right - rect.left
try:
    dpi = user32.GetDpiForWindow(hwnd) / 96.0
except Exception:
    dpi = 1.0

# 期望：标题栏按钮 -> 1（HTCLIENT），标题栏空白 -> 2（HTCAPTION），客户区 -> 1
# 按钮自右向左各宽 44 DIP，垂直居中于 48 DIP 的标题栏
cases = [
    ("关于 ⓘ",      w / dpi - 198, 24, HTCLIENT),
    ("设置 ⚙",      w / dpi - 154, 24, HTCLIENT),
    ("最小化 −",    w / dpi - 110, 24, HTCLIENT),
    ("最大化 □",    w / dpi - 66,  24, HTCLIENT),
    ("关闭 ×",      w / dpi - 22,  24, HTCLIENT),
    ("标题栏空白",  w / dpi / 2,   24, HTCAPTION),
    ("客户区",      w / dpi / 2,   200, HTCLIENT),
]

fails = 0
for name, dx, dy, want in cases:
    sx = rect.left + int(dx * dpi)
    sy = rect.top + int(dy * dpi)
    lp = ((sy & 0xFFFF) << 16) | (sx & 0xFFFF)
    res = user32.SendMessageW(hwnd, WM_NCHITTEST, 0, lp)
    code = ctypes.c_short(res & 0xFFFF).value
    ok = code == want
    fails += 0 if ok else 1
    print(f"{'OK  ' if ok else 'FAIL'} {name:12s} 期望={want} 实际={code}  (屏幕坐标 {sx},{sy})")

print(f"\n结果: {len(cases) - fails}/{len(cases)} 通过")
sys.exit(0 if fails == 0 else 2)
