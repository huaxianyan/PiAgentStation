"""生成安装程序用的两张位图（WiX 约定：BMP，非压缩 24 位）。

- banner.bmp 493×58：安装向导顶栏背景，页面标题由向导自己画在左边，所以这块只放极淡的品牌痕迹。
- dialog.bmp 493×312：欢迎/安装/完成页的背景，左侧 164px 是图，右侧留给向导摆文案。

跑法：python design/make-installer-art.py
产物：installer/assets/banner.bmp、installer/assets/dialog.bmp，外加 design/shots 里的 png 预览。
"""

from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "installer" / "assets"
SHOTS = ROOT / "design" / "shots"
ICONS = ROOT / "src" / "SevenStation.Control" / "icons"

INK = (26, 26, 29)
PAPER = (255, 255, 255)
PAGE = (247, 247, 249)
HAIR = (232, 232, 237)
MUTE = (201, 201, 208)


def icon(size: int, light: bool) -> Image.Image:
    """直接用控制台那枚图标，别在这里重画 π。"""
    source = Image.open(ICONS / ("icon.ico" if light else "tray.png"))
    source = source.convert("RGBA")
    return source.resize((size, size), Image.LANCZOS)


def banner() -> Image.Image:
    image = Image.new("RGB", (493, 58), PAPER)
    draw = ImageDraw.Draw(image)

    # 底边一条细线，把顶栏和内容分开
    draw.line([(0, 56), (493, 56)], fill=HAIR)

    # 右端放个极淡的 mark，标题文字在左边，不会打架
    mark = icon(20, light=False)
    faded = mark.copy()
    faded.putalpha(mark.getchannel("A").point(lambda value: int(value * 0.35)))
    image.paste(faded, (455, 18), faded)

    return image


def dialog() -> Image.Image:
    image = Image.new("RGB", (493, 312), PAPER)
    draw = ImageDraw.Draw(image)

    # 左侧 164px 的深色块，右侧留给向导写文案
    draw.rectangle([0, 0, 163, 312], fill=INK)
    draw.line([(164, 0), (164, 312)], fill=HAIR)

    # 深色块里放浅底那枚图标（自带描边，暗底上也立得住）
    mark = icon(84, light=True)
    image.paste(mark, (40, 114), mark)

    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    SHOTS.mkdir(parents=True, exist_ok=True)

    pieces = {"banner": banner(), "dialog": dialog()}
    for name, image in pieces.items():
        # WiX 只吃 BMP，24 位不压缩最稳
        image.save(ASSETS / f"{name}.bmp", "BMP")
        image.save(SHOTS / f"installer-{name}.png", "PNG")
        print(f"{name}: {image.width}×{image.height} → installer/assets/{name}.bmp")


if __name__ == "__main__":
    main()
