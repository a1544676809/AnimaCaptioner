"""裁剪路径的实测核对：不看图，**取像素**。

为什么非要这样：裁剪这条路依赖两件文档说不清、且实测与直觉相反的事——
  1. `BitmapTransform.ScaledWidth/Height` 是先把**整幅**缩到那个尺寸，
     `Bounds` 再在**缩放后**的坐标系里裁（不是"先裁一块再放大"）。
  2. 请解码器帮忙转正（`RespectExifOrientation`）时，那两个缩放数字解释在
     转正**之前**的坐标系里；按转正后的宽高给，竖拍图会被横向拉伸。
这两条猜错都不会报错，症状是"模型答得和没给图一样"或"画面变形了"。
尺寸对不对可以看数字，但"裁的是不是那一块"只有取像素才认得出——
实测抓到过一次：`缩到 300x200 且裁 (0,0,300,200)` 给出的是**整幅的缩小版**，
尺寸恰好也是 300x200，看着完全正常。

用法:
  python make_probe_images.py          # 先生成探针图片（默认 %TEMP%/ac-imgprobe）
  python check.py [图片目录] [imgprobe.exe]
"""
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.join(tempfile.gettempdir(), "ac-imgprobe")
PROBE = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    HERE, "bin", "Release", "net10.0-windows10.0.19041.0", "imgprobe.exe")

RED, GREEN, BLUE, YELLOW = (220, 30, 30), (30, 200, 60), (40, 60, 220), (240, 210, 30)
TOL = 24

bad = 0
out_dir = tempfile.mkdtemp(prefix="ac-imgcheck-")


def run(*args):
    p = subprocess.run([PROBE, *args], capture_output=True, text=True, encoding="utf-8")
    return p.returncode, (p.stdout or "") + (p.stderr or "")


def check(label, path, rx, ry, expect, want_size=None):
    """取相对坐标 (rx, ry) 处的一个像素。都取在各块的中心附近，避开 JPEG 边缘振铃。"""
    global bad
    with Image.open(path) as im:
        im = im.convert("RGB")
        w, h = im.size
        px = im.getpixel((min(int(w * rx), w - 1), min(int(h * ry), h - 1)))

    ok = all(abs(px[i] - expect[i]) <= TOL for i in range(3))
    if want_size is not None and (w, h) != want_size:
        ok = False
    if not ok:
        bad += 1
    print(f"  {'ok  ' if ok else 'FAIL'} {label:<40} 像素={px} 尺寸={(w, h)}"
          + ("" if ok else f"   期望={expect} 尺寸={want_size}"))


def check_round(label, path, want_size):
    """白圆是不是还是圆的（宽高差不超过 6%）。拉伸会让它变成椭圆。"""
    global bad
    with Image.open(path) as im:
        im = im.convert("L")
        w, h = im.size
        px = im.load()
        xs, ys = [], []
        for y in range(0, h, 2):
            for x in range(0, w, 2):
                if px[x, y] > 160:
                    xs.append(x)
                    ys.append(y)

    cw = (max(xs) - min(xs)) if xs else 0
    ch = (max(ys) - min(ys)) if ys else 0
    ok = (w, h) == want_size and cw > 0 and abs(cw - ch) <= max(cw, ch) * 0.06
    if not ok:
        bad += 1
    print(f"  {'ok  ' if ok else 'FAIL'} {label:<40} 尺寸={(w, h)} 圆={cw}x{ch}"
          + ("" if ok else f"   期望尺寸={want_size}、圆接近正圆"))


def main():
    global bad

    if not os.path.exists(PROBE):
        print("找不到 imgprobe.exe：" + PROBE)
        return 2
    needed = ["quad.jpg", "quad-exif6.jpg", "circle-tall-exif6.jpg",
              "circle-big-exif6.jpg"]
    missing = [n for n in needed if not os.path.exists(os.path.join(DIR, n))]
    if missing:
        print("缺探针图片：" + ", ".join(missing) + "（先跑 make_probe_images.py）")
        return 2

    quad = os.path.join(DIR, "quad.jpg")
    quad6 = os.path.join(DIR, "quad-exif6.jpg")
    tall6 = os.path.join(DIR, "circle-tall-exif6.jpg")
    big6 = os.path.join(DIR, "circle-big-exif6.jpg")

    print("=== 1. 文件事实：尺寸与 EXIF 方向 ===")
    for path, want in ((quad, "displayed   = 600x400"), (quad6, "displayed   = 400x600")):
        code, out = run("facts", path)
        line = next((l for l in out.splitlines() if l.startswith("displayed")), "")
        ok = code == 0 and want in out
        bad += 0 if ok else 1
        print(f"  {'ok  ' if ok else 'FAIL'} {os.path.basename(path):<24} {line}")

    print("\n=== 2. 不带 EXIF 的图：四个象限各裁一次 ===")
    for name, x, y, expect in (("左上", 0.0, 0.0, RED), ("右上", 0.5, 0.0, GREEN),
                               ("左下", 0.0, 0.5, BLUE), ("右下", 0.5, 0.5, YELLOW)):
        out = os.path.join(out_dir, f"plain-{name}.jpg")
        code, log = run("crop", quad, str(x), str(y), "0.5", "0.5", out)
        if code != 0:
            bad += 1
            print(f"  FAIL 裁剪失败({name}): {log.strip()}")
            continue
        check(f"quad.jpg 裁{name}", out, 0.5, 0.5, expect, want_size=(300, 200))

    print("\n=== 3. EXIF 6：模型给的坐标是**转正之后**的坐标系 ===")
    # 期望值见 make_probe_images.py 的文件头：显示左上=蓝、右上=红、左下=黄、右下=绿。
    # 忽略 EXIF 的实现会在"右上"取到绿色——这就是这一节存在的理由。
    for name, x, y, expect in (("左上", 0.0, 0.0, BLUE), ("右上", 0.5, 0.0, RED),
                               ("左下", 0.0, 0.5, YELLOW), ("右下", 0.5, 0.5, GREEN)):
        out = os.path.join(out_dir, f"exif-{name}.jpg")
        code, log = run("crop", quad6, str(x), str(y), "0.5", "0.5", out)
        if code != 0:
            bad += 1
            print(f"  FAIL 裁剪失败({name}): {log.strip()}")
            continue
        # 转正之后是 400x600 的竖图，一半就是 200x300
        check(f"quad-exif6.jpg 裁{name}", out, 0.5, 0.5, expect, want_size=(200, 300))

    print("\n=== 4. tiles：阅读顺序 = 左到右、上到下 ===")
    code, log = run("tiles", quad, "2", "2", out_dir)
    if code != 0:
        bad += 1
        print("  FAIL tiles 失败: " + log.strip())
    else:
        for i, (expect, name) in enumerate(zip((RED, GREEN, BLUE, YELLOW),
                                               ("左上", "右上", "左下", "右下"))):
            check(f"tile[{i}] = {name}", os.path.join(out_dir, f"tile-{i:02}.jpg"),
                  0.5, 0.5, expect, want_size=(300, 200))

    print("\n=== 5. 长宽比：正圆不许变椭圆（拉伸才最难发现）===")
    # 竖图 400x800 + EXIF6 → 显示 800x400；大图 2400x1200 + EXIF6 → 显示 1200x2400，
    # 长边超 1024，所以这一张还兼验"缩到长边 1024"。
    for label, path, size in (("整图 payload：竖图 EXIF6", tall6, (800, 400)),
                              ("整图 payload：大图 EXIF6 缩到 1024", big6, (512, 1024))):
        out = os.path.join(out_dir, "aspect-" + os.path.basename(path))
        code, log = run("payload", path, out)
        if code != 0:
            bad += 1
            print(f"  FAIL {label}: {log.strip()}")
            continue
        check_round(label, out, size)

        # crop 走的是另一条路（裁完托管旋转 + 编码器缩放），同一张图要给出同样的结果
        out2 = os.path.join(out_dir, "aspect-crop-" + os.path.basename(path))
        code, log = run("crop", path, "0", "0", "1", "1", out2)
        if code != 0:
            bad += 1
            print(f"  FAIL {label}（crop 整幅）: {log.strip()}")
            continue
        check_round(label + "（crop 整幅）", out2, size)

    print("\n=== 6. 放大一块：区域比例与目标尺寸 ===")
    # 大图 1200x2400（显示）里取右上 1/4 = 600x1200 → 缩到长边 1024 = 512x1024
    out = os.path.join(out_dir, "zoom.jpg")
    code, log = run("crop", big6, "0.5", "0", "0.5", "0.5", out)
    if code != 0:
        bad += 1
        print("  FAIL 放大失败: " + log.strip())
    else:
        with Image.open(out) as im:
            size = im.size
        ok = size == (512, 1024)
        bad += 0 if ok else 1
        print(f"  {'ok  ' if ok else 'FAIL'} 大图 EXIF6 裁右上 1/4 后缩放"
              f"{'':<12} 尺寸={size}   期望=(512, 1024)")

    print(f"\n=== imgcheck: {'OK' if bad == 0 else str(bad) + ' FAILED'} ===")
    return 0 if bad == 0 else 1


try:
    code = main()
finally:
    shutil.rmtree(out_dir, ignore_errors=True)
sys.exit(code)
