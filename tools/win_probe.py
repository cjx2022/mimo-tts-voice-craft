# -*- coding: utf-8 -*-
"""PrintWindow 像素探针：离屏窗口的合成结果采样（不动鼠标、不抢焦点）。
用法: python win_probe.py <out.png>
"""
import ctypes, struct, sys, zlib, time
from ctypes import wintypes

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TITLE = "MiMo TTS Voice Craft"
out_png = sys.argv[1] if len(sys.argv) > 1 else "_win_probe.png"

hwnd = user32.FindWindowW(None, TITLE)
if not hwnd:
    print("ERR: 未找到窗口", TITLE)
    sys.exit(1)

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
w, h = rect.right - rect.left, rect.bottom - rect.top
if w < 600 or h < 400:
    print(f"ERR: 窗口尺寸可疑 {w}x{h}（可能是同名 MessageBox）")
    sys.exit(2)

hdc = user32.GetDC(0)
mem = gdi32.CreateCompatibleDC(hdc)
bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
gdi32.SelectObject(mem, bmp)

ok = user32.PrintWindow(hwnd, mem, 0x00000002)  # PW_RENDERFULLCONTENT
print(f"PrintWindow ok={ok}  size={w}x{h}")

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG), ("biHeight", wintypes.LONG),
                ("biPlanes", wintypes.WORD), ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
                ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
                ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD), ("biClrImportant", wintypes.DWORD)]

bih = BITMAPINFOHEADER()
bih.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bih.biWidth = w
bih.biHeight = -h          # 自上而下
bih.biPlanes = 1
bih.biBitCount = 32
bih.biCompression = 0      # BI_RGB

buf = ctypes.create_string_buffer(w * h * 4)
got = gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bih), 0)
print(f"GetDIBits rows={got}")
raw = buf.raw

def px(x, y):
    i = (y * w + x) * 4
    b, g, r = raw[i], raw[i + 1], raw[i + 2]
    return (r, g, b)

# 统计
black = colorful = 0
total = 0
sumv = 0
for y in range(0, h, 4):
    for x in range(0, w, 4):
        r, g, b = px(x, y)
        total += 1
        sumv += r + g + b
        if r + g + b < 60:
            black += 1
        if max(r, g, b) - min(r, g, b) > 25:
            colorful += 1
print(f"平均亮度={sumv/total/3:.1f}  纯黑占比={black/total*100:.2f}%  彩色像素占比={colorful/total*100:.1f}%")

# 相对坐标采样：背景带（两列之间）、卡片内部、标题栏
pts = {"标题栏": (0.5, 0.03), "左卡片内": (0.28, 0.45), "两列间隙(背景)": (0.588, 0.55),
       "右下光晕区": (0.8, 0.62), "右上角": (0.97, 0.06)}
for name, (fx, fy) in pts.items():
    print(f"  {name:16s} {px(int(w*fx), int(h*fy))}")

# 写 PNG（手写编码，避免依赖 Pillow）
rows = []
for y in range(h):
    line = bytearray([0])
    base = y * w * 4
    for x in range(w):
        i = base + x * 4
        line += bytes((raw[i + 2], raw[i + 1], raw[i], 255))
    rows.append(bytes(line))
idat = zlib.compress(b"".join(rows), 6)

def chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data +
            struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

png = (b"\x89PNG\r\n\x1a\n" +
       chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)) +
       chunk(b"IDAT", idat) + chunk(b"IEND", b""))
with open(out_png, "wb") as f:
    f.write(png)
print("saved", out_png)
