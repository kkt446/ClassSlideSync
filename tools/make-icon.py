# -*- coding: utf-8 -*-
"""生成「课件续页」插件图标（三个方案）。

用法: python make-icon.py [输出目录]
产出: icon-A.png / icon-B.png / icon-C.png（512x512）+ _overview.png（含 48px 小图对照）
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont, ImageFilter

SS = 4          # 超采样倍数
S = 512         # 最终边长
W = S * SS      # 工作画布边长

BLUE_DARK = (12, 58, 130, 255)
BLUE_MID = (0, 103, 192, 255)
AMBER = (245, 166, 35, 255)
WHITE = (255, 255, 255, 255)

FONTS = [
    r"C:\Windows\Fonts\segoeuib.ttf",
    r"C:\Windows\Fonts\arialbd.ttf",
    r"C:\Windows\Fonts\msyhbd.ttc",
]


def font(size):
    for path in FONTS:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                pass
    return ImageFont.load_default()


def s(v):
    return int(round(v * SS))


def vgrad(size, top, bottom):
    img = Image.new("RGB", (1, size))
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / max(1, size - 1)
        d.point((0, y), fill=tuple(int(round(top[i] + (bottom[i] - top[i]) * t)) for i in range(3)))
    return img.resize((size, size), Image.BILINEAR)


def base_tile():
    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    grad = vgrad(W, (41, 138, 226), (8, 55, 132)).convert("RGBA")
    mask = Image.new("L", (W, W), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, W - 1, W - 1], radius=s(115), fill=255)
    img.paste(grad, (0, 0), mask)
    return img


def add_card(img, box, radius=28, fill=WHITE):
    x0, y0, x1, y1 = [s(v) for v in box]
    r = s(radius)
    sh = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    ImageDraw.Draw(sh).rounded_rectangle([x0 + s(4), y0 + s(12), x1 + s(6), y1 + s(16)],
                                         radius=r, fill=(4, 22, 52, 120))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(s(13))))
    ImageDraw.Draw(img).rounded_rectangle([x0, y0, x1, y1], radius=r, fill=fill)


def variant_a():
    """A：页码 + 回跳箭头（回到上次停的那一页）"""
    img = base_tile()
    add_card(img, (92, 104, 384, 408))
    d = ImageDraw.Draw(img)
    d.text((s(222), s(264)), "4", font=font(s(198)), fill=BLUE_DARK, anchor="mm")
    # 圆弧：从右下往上绕一圈，箭头指向左下（回到卡片里）
    d.arc([s(314), s(196), s(458), s(340)], 265, 85, fill=AMBER, width=s(30))
    d.polygon([(s(330), s(196)), (s(384), s(164)), (s(384), s(228))], fill=AMBER)
    return img


def variant_b():
    """B：照用户草图——课件被划掉 + 跳跃箭头"""
    img = base_tile()
    add_card(img, (78, 96, 360, 400))
    d = ImageDraw.Draw(img)
    d.text((s(219), s(214)), "PPT", font=font(s(78)), fill=BLUE_DARK, anchor="mm")
    # 斜杠（划掉 PPT）
    d.line([(s(104), s(158)), (s(334), s(272))], fill=AMBER, width=s(26))
    # 跳跃箭头：卡片右下角起跳，冲向画面右上角
    d.line([(s(268), s(372)), (s(438), s(196))], fill=AMBER, width=s(30))
    d.polygon([(s(404), s(180)), (s(470), s(158)), (s(452), s(228))], fill=AMBER)
    return img


def variant_c():
    """C：大页码 + 放映三角（从第 N 页开始放映）"""
    img = base_tile()
    add_card(img, (92, 104, 384, 408))
    d = ImageDraw.Draw(img)
    d.text((s(206), s(264)), "4", font=font(s(198)), fill=BLUE_DARK, anchor="mm")
    d.polygon([(s(300), s(216)), (s(300), s(312)), (s(372), s(264))], fill=AMBER)
    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    os.makedirs(out, exist_ok=True)
    variants = [("A", variant_a()), ("B", variant_b()), ("C", variant_c())]
    finals = []
    for name, img in variants:
        small = img.resize((S, S), Image.LANCZOS)
        path = os.path.join(out, "icon-%s.png" % name)
        small.save(path, "PNG", optimize=True)
        finals.append((name, small))
        print("%s -> %s (%d bytes)" % (name, path, os.path.getsize(path)))

    # 对照图：512 原图 + 48/32 小图，浅灰底
    pad, cell = 24, 512
    ov = Image.new("RGB", (pad * 4 + cell + 160, pad * 2 + cell), (238, 240, 243))
    for i, (name, img) in enumerate(finals):
        if i == 0:
            ov.paste(img.convert("RGB"), (pad, pad))
        x = pad
        y = pad + cell + 12
        for sz in (96, 64, 48, 32):
            if y + sz > ov.height:
                break
            ov.paste(img.resize((sz, sz), Image.LANCZOS).convert("RGB"), (x, y))
            x += sz + 12
    ov_path = os.path.join(out, "_overview.png")
    ov.save(ov_path, "PNG", optimize=True)
    print("overview -> %s" % ov_path)

    # 每个方案单独一张 48/32 对照（便于放大看）
    for name, img in finals:
        strip = Image.new("RGB", (4 * 56 + 20, 72), (238, 240, 243))
        x = 10
        for sz in (64, 48, 32, 24):
            strip.paste(img.resize((sz, sz), Image.LANCZOS).convert("RGB"), (x, 10))
            x += sz + 8
        strip.save(os.path.join(out, "strip-%s.png" % name), "PNG", optimize=True)


if __name__ == "__main__":
    main()
