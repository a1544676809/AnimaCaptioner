"""生成图像工具验证用的探针图片。

两类，各自解决一个"肉眼看不出"的问题：

1. **四象限纯色图**（一张正常、一张带 EXIF 方向 6）——用来判定"裁的是不是那一块"、
   "EXIF 方向有没有被应用"。四块颜色取输出图中心那一个像素就能判，不必肉眼比。
   标签写在每块的**左上角**：压在正中央的话取中心像素取到的是白字与底色的混合。
   EXIF 6 的期望值（显示坐标系）：左上=蓝、右上=红、左下=黄、右下=绿。
   **忽略方向**的实现会在"显示右上"取到绿色，这正是要抓的错。

2. **带圆的图**（竖图 + EXIF 6、大图 + EXIF 6）——用来判定"有没有被拉伸"。
   纯色象限判不出拉伸：四个象限在两种长宽比下相对位置完全一样。
   正圆却骗不了人——任何正确的旋转都还是正圆，被拉伸就成椭圆。
   大图那张还兼着验证"缩到长边 1024"那一步。

用法:  python make_probe_images.py [输出目录]     默认 %TEMP%/ac-imgprobe
"""
import os
import sys
import tempfile

from PIL import Image, ImageDraw, ImageFont

RED, GREEN, BLUE, YELLOW = (220, 30, 30), (30, 200, 60), (40, 60, 220), (240, 210, 30)

DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(tempfile.gettempdir(), "ac-imgprobe")


def quadrants(w=600, h=400):
    im = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, w // 2, h // 2], fill=RED)
    d.rectangle([w // 2, 0, w, h // 2], fill=GREEN)
    d.rectangle([0, h // 2, w // 2, h], fill=BLUE)
    d.rectangle([w // 2, h // 2, w, h], fill=YELLOW)
    try:
        font = ImageFont.truetype("arial.ttf", int(h * 0.12))
    except Exception:
        font = ImageFont.load_default()
    for text, (cx, cy) in (("TL", (w * 0.06, h * 0.04)), ("TR", (w * 0.56, h * 0.04)),
                           ("BL", (w * 0.06, h * 0.54)), ("BR", (w * 0.56, h * 0.54))):
        d.text((cx, cy), text, fill=(255, 255, 255), font=font)
    return im


def circle(w, h):
    """黑底白圆。半径取短边的三分之一，保证裁 1/4 区域时也还能看出圆。"""
    im = Image.new("RGB", (w, h), (0, 0, 0))
    r = min(w, h) // 3
    cx, cy = w // 2, h // 2
    ImageDraw.Draw(im).ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255))
    return im


def secret(text, w=1500, h=400):
    """白底大黑字。用来判定**模型到底看没看见这张图**（corecheck 的 region 探针）。"""
    im = Image.new("RGB", (w, h), (255, 255, 255))
    d = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype("arialbd.ttf", int(h * 0.40))
    except Exception:
        font = ImageFont.load_default()
    d.text((w // 2, h // 2), text, fill=(0, 0, 0), font=font, anchor="mm")
    return im


def save(im, name, orientation=1):
    path = os.path.join(DIR, name)
    exif = im.getexif()
    if orientation != 1:
        exif[0x0112] = orientation
    im.save(path, quality=95, exif=exif)
    return path


def main():
    os.makedirs(DIR, exist_ok=True)

    save(quadrants(), "quad.jpg")
    save(quadrants(), "quad-exif6.jpg", orientation=6)

    # 竖图（400x800）+ EXIF 6：显示时是 800x400 的横图。被拉伸的话圆会变成椭圆。
    save(circle(400, 800), "circle-tall-exif6.jpg", orientation=6)
    # 大图（2400x1200）+ EXIF 6：显示时 1200x2400，长边超过 1024，缩那一步也要验
    save(circle(2400, 1200), "circle-big-exif6.jpg", orientation=6)

    save(secret("PANGOLIN-42"), "secret1.jpg")
    save(secret("WOMBAT-77"), "secret2.jpg")

    # 端到端那条路（corecheck region 的第 4 个场景）用这两张：
    # blank 是"模型看不到任何字"的原图，secret2-crop 是**假装裁出来**的那块
    # （尺寸按真实裁剪的目标给：长边 1024）。模型如果答出了 WOMBAT-77，
    # 它只可能来自那条后插的图文消息。
    save(Image.new("RGB", (1200, 800), (255, 255, 255)), "blank.jpg")
    save(secret("WOMBAT-77", w=1024, h=273), "secret2-crop.jpg")

    # 大图 + 角落里一行**极小**的字。字号是按"缩到长边 1024 之后还剩几个像素"挑的：
    # 4000 宽缩到 1024 是 3.9 倍，22px 的字在那里只剩约 5-6px 高——认不出来；
    # 把左上角 1/4 裁出来是 1000x750（不放大，`Fit` 不放大），那行字回到 22px，清楚。
    # 所以模型只能靠 view_region 才能答对，答案里的字符串就是"这条路真的通了"的证据。
    tiny = Image.new("RGB", (4000, 3000), (255, 255, 255))
    try:
        tfont = ImageFont.truetype("arialbd.ttf", 22)
    except Exception:
        tfont = ImageFont.load_default()
    ImageDraw.Draw(tiny).text((50, 50), "PANGOLIN-42", fill=(0, 0, 0), font=tfont)
    save(tiny, "tiny-text.jpg")

    print("探针图片已生成到 " + DIR)
    for name in sorted(os.listdir(DIR)):
        if not name.endswith(".jpg"):
            continue
        with Image.open(os.path.join(DIR, name)) as im:
            print(f"  {name:24} 存储 {im.size}  EXIF方向 {im.getexif().get(0x0112, 1)}")
    return 0


sys.exit(main())
