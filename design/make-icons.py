"""用 Segoe UI Semibold 的 π 生成图标：控制台浅底、客户端深底、托盘单独一张。

改字体或改留白，重跑这个脚本就行，不要手改 png/ico。
"""

from PIL import Image, ImageDraw, ImageFont

GLYPH = "π"
FONT_PATH = r"C:\Windows\Fonts\segoeuib.ttf"   # Segoe UI Semibold
FONT_INDEX = 0

INK = (26, 26, 29)
PAPER = (247, 247, 249)

CANVAS = 256
INSET = 8
RADIUS = 0.22          # 圆角占边长比例
OUTLINE = 0.048        # 浅底那圈的描边粗细占边长比例
GLYPH_RATIO = 0.60     # 字形最长边占方块内边的比例


def glyph_layer(size, color):
    """把 π 画成一张透明的 layer，按包围盒居中。"""
    probe = ImageFont.truetype(FONT_PATH, 400, index=FONT_INDEX)
    left, top, right, bottom = probe.getbbox(GLYPH)
    scale = size / max(right - left, bottom - top)
    font = ImageFont.truetype(FONT_PATH, int(400 * scale), index=FONT_INDEX)

    left, top, right, bottom = font.getbbox(GLYPH)
    width, height = right - left, bottom - top

    layer = Image.new("RGBA", (int(width) + 8, int(height) + 8), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    draw.text((4 - left, 4 - top), GLYPH, font=font, fill=color)
    return layer


def build(dark):
    """dark=True 给客户端（深底白 π），False 给控制台（浅底深 π）。"""
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    box = (INSET, INSET, CANVAS - INSET, CANVAS - INSET)
    radius = int(CANVAS * RADIUS)

    if dark:
        draw.rounded_rectangle(box, radius=radius, fill=(*INK, 255))
        ink = (255, 255, 255, 255)
    else:
        draw.rounded_rectangle(
            box,
            radius=radius,
            fill=(*PAPER, 255),
            outline=(*INK, 255),
            width=int(CANVAS * OUTLINE),
        )
        ink = (*INK, 255)

    inner = int((CANVAS - INSET * 2) * GLYPH_RATIO)
    layer = glyph_layer(inner, ink)
    image.alpha_composite(
        layer,
        (
            (CANVAS - layer.width) // 2,
            (CANVAS - layer.height) // 2,
        ),
    )
    return image


def main():
    console = build(dark=False)
    client = build(dark=True)

    console.save(r"E:\Dev\SevenStation\src\SevenStation.Control\icons\icon.ico",
                 sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])

    # 托盘单独一张：64 起步，任务栏缩放时不会糊
    console.resize((64, 64), Image.LANCZOS).save(
        r"E:\Dev\SevenStation\src\SevenStation.Control\icons\tray.png")

    client.save(r"E:\Dev\SevenStation\src\SevenStation.Desktop\icons\icon.ico",
                sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])

    # 预览：几个尺寸拼一张，改完顺手看一眼
    sizes = (128, 64, 48, 32, 24, 16)
    row_height = 150
    preview = Image.new("RGB", (620, row_height * 2 + 20), (255, 255, 255))
    for row, image in enumerate((console, client)):
        x = 20
        for size in sizes:
            scaled = image.resize((size, size), Image.LANCZOS)
            preview.paste(scaled, (x, 20 + row * row_height + (128 - size) // 2), scaled)
            x += size + 16
    preview.save(r"E:\Dev\SevenStation\design\shots\control-icons-final.png")
    print("生成控制台 icon.ico / tray.png、客户端 icon.ico、预览图")


if __name__ == "__main__":
    main()
