# -*- coding: utf-8 -*-
"""PNG 采样器：正确反滤波后做接缝检测（背景是否为一整块连续渐变）。
用法: python png_sample.py <png> [x0f,x1f 垂直扫描带]
"""
import sys, zlib, struct

def load(path):
    data = open(path, "rb").read()
    pos, idat, w, h, ct = 8, b"", None, None, None
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + ln]
        if tag == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", chunk[:10])
        elif tag == b"IDAT":
            idat += chunk
        elif tag == b"IEND":
            break
        pos += 12 + ln
    raw = zlib.decompress(idat)
    bpp = 4 if ct == 6 else 3
    stride = w * bpp
    out = bytearray(stride * h)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        ft = raw[p]; p += 1
        line = bytearray(raw[p:p + stride]); p += stride
        if ft == 1:      # Sub
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif ft == 2:    # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif ft == 3:    # Average
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif ft == 4:    # Paeth
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return w, h, bpp, bytes(out)


def main():
    path = sys.argv[1]
    w, h, bpp, buf = load(path)
    stride = w * bpp

    def px(x, y):
        i = y * stride + x * bpp
        return buf[i], buf[i + 1], buf[i + 2]

    def band_avg_rows(x0f, x1f):
        vals = []
        a, b = int(w * x0f), int(w * x1f)
        for y in range(h):
            s = n = 0
            for x in range(a, b, 2):
                r, g, bl = px(x, y)
                s += (r + g + bl) / 3
                n += 1
            vals.append(s / n)
        return vals

    def band_avg_cols(y0f, y1f):
        vals = []
        a, b = int(h * y0f), int(h * y1f)
        for x in range(w):
            s = n = 0
            for y in range(a, b, 2):
                r, g, bl = px(x, y)
                s += (r + g + bl) / 3
                n += 1
            vals.append(s / n)
        return vals

    print(f"尺寸 {w}x{h}  通道 {bpp}")
    v = band_avg_rows(0.02, 0.05)
    jv = [(y, round(v[y] - v[y - 1], 2)) for y in range(1, h) if abs(v[y] - v[y - 1]) > 1.5]
    print(f"垂直扫描(左侧边距 2%~5%) 突变点 {len(jv)}: {jv[:8]}")
    print("  标题栏边界 y=40..56:", [round(x, 1) for x in v[40:57:2]])
    hz = band_avg_cols(0.88, 0.94)
    jh = [(x, round(hz[x] - hz[x - 1], 2)) for x in range(1, w) if abs(hz[x] - hz[x - 1]) > 1.5]
    print(f"横向扫描(底部空白带 88%~94%) 突变点 {len(jh)}: {jh[:8]}")
    print(f"  全窗亮度范围 {min(v):.0f} ~ {max(v):.0f}")
    for name, (fx, fy) in {"左上": (0.03, 0.05), "右上": (0.97, 0.05), "中部": (0.6, 0.5),
                           "左下": (0.03, 0.95), "右下": (0.97, 0.95)}.items():
        print(f"  {name} {px(int(w*fx), int(h*fy))}")


main()
