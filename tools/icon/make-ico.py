#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
从 Assets/app.svg 生成 Assets/app.ico（多尺寸 Windows 图标）。

为什么要分尺寸出图
------------------
原设计是 256 画布上四条标签，条间空隙 10/256。缩到 16px 时那空隙只剩
0.62 像素——间隙消失，四条粘成一块，整个图标读起来是"彩色糊块"。

这不是设计缺陷，而是矢量图标缩放的固有限制，Windows 自己的图标也一样：
系统图标是分尺寸出图的，小尺寸用简化画法（Notepad 的 16px 就不是 256px 的等比缩小）。

所以这里：
    16px          用 3 条加粗版（空隙 1.06px）
    20 / 24px     用 4 条宽隙、去白点版（空隙 1.33 / 1.59px）
    32px 及以上   用 app.svg 原设计

保留的视觉身份完全一致：蓝色圆角底、白色画框、暖色画芯、彩色标签条。

依赖
----
    pip install Pillow
    Google Chrome（用于把 SVG 光栅化；Chrome 的 SVG 渲染质量比 cairosvg 好，
    且本机 cairosvg/svglib 都不可用）

用法
----
    python tools/icon/make-ico.py            # 默认从仓库根开始找
    python tools/icon/make-ico.py --repo <仓库根>
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image

# Windows 控制台默认是 GBK，脚本里的非 GBK 字符（如 ✓）会直接抛
# UnicodeEncodeError 把整个流程打断。这里降级成替换字符，保证任何代码页下都能跑完。
try:
    sys.stdout.reconfigure(errors="replace")   # type: ignore[union-attr]
except Exception:
    pass

# ---------------------------------------------------------------- 配色（与 app.svg 完全一致）
BG = [("#38BDF8", 0), ("#0EA5E9", 50), ("#0284C7", 100)]
ART = [("#FDE047", 0), ("#FB7185", 45), ("#818CF8", 100)]
PILLS = [
    ("#F59E0B", "#EA580C"),   # 质量 / 元信息 / 安全
    ("#10B981", "#059669"),   # 人物数量
    ("#FB7185", "#E11D48"),   # 画师
    ("#64748B", "#475569"),   # 一般标签
]

CHROME_CANDIDATES = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
]


# ---------------------------------------------------------------- 小尺寸简化版 SVG
def _grad(gid: str, stops, x2="100%", y2="0%") -> str:
    body = "".join(f'<stop offset="{o}%" stop-color="{c}"/>' for c, o in stops)
    return (f'<linearGradient id="{gid}" x1="0%" y1="0%" x2="{x2}" y2="{y2}">'
            f'{body}</linearGradient>')


def small_svg(nbars: int, bar_h: int, gap: int, card, bar_left: int,
              bar_right: int, bar_top: int, radius: int = 56) -> str:
    """小尺寸专用的简化画法（无白点、条更粗、间隙更大）。坐标空间仍是 256。"""
    cx, cy, cw, ch = card
    p = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" '
         'width="100%" height="100%">', '<defs>']
    p.append(_grad("bg", BG, "100%", "100%"))
    p.append(_grad("art", ART, "100%", "100%"))
    for i, (a, b) in enumerate(PILLS[:nbars]):
        p.append(_grad(f"p{i}", [(a, 0), (b, 100)]))
    p.append('</defs>')
    p.append(f'<rect width="256" height="256" rx="{radius}" fill="url(#bg)"/>')
    p.append(f'<rect width="254" height="254" x="1" y="1" rx="{radius-1}" fill="none" '
             'stroke="#FFFFFF" stroke-opacity="0.45" stroke-width="2"/>')
    p.append(f'<rect x="{cx}" y="{cy}" width="{cw}" height="{ch}" rx="20" fill="#FFFFFF"/>')
    iw, ih = cw - 24, ch - 46
    p.append(f'<rect x="{cx+12}" y="{cy+12}" width="{iw}" height="{ih}" rx="12" fill="url(#art)"/>')
    p.append(f'<rect x="{cx+12}" y="{cy+ch-26}" width="{int(iw*0.34)}" height="12" rx="6" fill="#E2E8F0"/>')
    y = bar_top
    for i in range(nbars):
        w = bar_right - bar_left - i * 12
        x = bar_left + i * 14
        p.append(f'<rect x="{x}" y="{y}" width="{w}" height="{bar_h}" rx="{bar_h//2}" fill="url(#p{i})"/>')
        y += bar_h + gap
    p.append('</svg>')
    return "\n".join(p)


# 16px：3 条（空隙 17/256 -> 16px 时 1.06px）
SMALL_3BAR = dict(nbars=3, bar_h=48, gap=17, card=(18, 26, 148, 200),
                  bar_left=96, bar_right=238, bar_top=44)
# 20/24px：4 条宽隙、去白点
SMALL_4BAR = dict(nbars=4, bar_h=36, gap=17, card=(20, 28, 146, 198),
                  bar_left=100, bar_right=236, bar_top=36)

# 尺寸 -> (来源, 说明)
PLAN = [
    (16, "small3", "3 条加粗（原设计空隙仅 0.62px，必然粘连）"),
    (20, "small4", "4 条宽隙、去白点"),
    (24, "small4", "4 条宽隙、去白点"),
    (32, "orig", "app.svg 原设计"),
    (40, "orig", "app.svg 原设计"),
    (48, "orig", "app.svg 原设计"),
    (64, "orig", "app.svg 原设计"),
    (128, "orig", "app.svg 原设计"),
    (256, "orig", "app.svg 原设计"),
]

RENDER_PX = 1024   # 光栅化分辨率：先大后小，比直接按目标尺寸渲染更锐（实测拉普拉斯方差更高）


def find_chrome() -> str:
    for c in CHROME_CANDIDATES:
        if os.path.exists(c):
            return c
    found = shutil.which("chrome") or shutil.which("google-chrome") or shutil.which("chromium")
    if found:
        return found
    sys.exit("找不到 Chrome。装一个，或用 --chrome 指定路径。")


def rasterize(chrome: str, svg: Path, out_png: Path, px: int = RENDER_PX) -> None:
    """把 SVG 光栅化成 px×px 的透明 PNG。"""
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        shutil.copy(svg, td / "icon.svg")
        (td / "wrap.html").write_text(
            "<!doctype html><html><head><meta charset='utf-8'><style>"
            "html,body{margin:0;padding:0;background:transparent;"
            f"width:{px}px;height:{px}px;overflow:hidden}}"
            f"img{{display:block;width:{px}px;height:{px}px}}"
            "</style></head><body><img src='icon.svg'></body></html>",
            encoding="utf-8",
        )
        out_png.unlink(missing_ok=True)
        subprocess.run(
            [chrome, "--headless", "--disable-gpu", "--hide-scrollbars",
             "--force-device-scale-factor=1", "--default-background-color=00000000",
             f"--window-size={px},{px}", f"--screenshot={out_png}",
             (td / "wrap.html").as_uri()],
            check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
    if not out_png.exists():
        sys.exit(f"渲染失败：{out_png}")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=None, help="仓库根目录")
    ap.add_argument("--svg", default=None, help="源 SVG（默认 tools/icon/app.svg）")
    ap.add_argument("--chrome", default=None)
    args = ap.parse_args()

    repo = Path(args.repo).resolve() if args.repo else Path(__file__).resolve().parents[2]
    # app.svg 刻意放在 tools\icon\ 而不是 Assets\：WinUI SDK 对 Assets\** 有默认
    # 通配，放那里会被拷进输出目录（实测确认），而它只是构建输入、不是运行时资源。
    svg = Path(args.svg).resolve() if args.svg else Path(__file__).resolve().parent / "app.svg"
    ico = repo / "Assets" / "app.ico"
    if not svg.exists():
        sys.exit(f"找不到 {svg}")

    chrome = args.chrome or find_chrome()
    print(f"仓库    : {repo}")
    print(f"源文件  : {svg}")
    print(f"Chrome  : {chrome}\n")

    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        sources: dict[str, Image.Image] = {}

        print(f"[1/3] 光栅化 {RENDER_PX}px 母图")
        rasterize(chrome, svg, td / "orig.png")
        sources["orig"] = Image.open(td / "orig.png").convert("RGBA")
        if sources["orig"].size != (RENDER_PX, RENDER_PX):
            sys.exit(f"母图尺寸异常：{sources['orig'].size}")

        for key, kw in (("small3", SMALL_3BAR), ("small4", SMALL_4BAR)):
            v = td / f"{key}.svg"
            v.write_text(small_svg(**kw), encoding="utf-8")
            rasterize(chrome, v, td / f"{key}.png")
            sources[key] = Image.open(td / f"{key}.png").convert("RGBA")
            gaps = "  ".join(f"{s}px:{kw['gap']/256*s:.2f}" for s in (16, 20, 24))
            print(f"      {key}: 条间空隙 {gaps}  (>=1.0px 才分得开)")

        print(f"\n[2/3] 逐尺寸降采样（LANCZOS）")
        frames: dict[int, Image.Image] = {}
        for size, src_key, note in PLAN:
            frames[size] = sources[src_key].resize((size, size), Image.LANCZOS)
            print(f"      {size:>4}px  <- {note}")

        print(f"\n[3/3] 合成 ICO")
        sizes = [(s, s) for s, _, _ in PLAN]
        frames[256].save(
            ico, format="ICO", sizes=sizes,
            append_images=[frames[s] for s in (128, 64, 48, 40, 32, 24, 20, 16)],
        )
        print(f"      写入 {ico}  ({ico.stat().st_size:,} bytes)")

        # 回读校验
        with Image.open(ico) as im:
            got = sorted(im.ico.sizes())
            if got != sorted(sizes):
                sys.exit(f"ICO 尺寸不符：{got}")
            for s in (16, 20, 24, 32, 40, 48, 64, 128, 256):
                im.size = (s, s)
                if im.convert("RGBA").tobytes() != frames[s].tobytes():
                    sys.exit(f"{s}px 像素与预期不一致")
        print(f"      回读校验：{len(sizes)} 个尺寸像素逐个一致 ✓")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
