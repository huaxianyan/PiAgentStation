"""用各字体的 π 生成对照表，挑一版做图标底稿。"""

from PIL import Image, ImageDraw, ImageFont

FONTS = [
    ("Segoe UI Semibold", r"C:\Windows\Fonts\segoeuib.ttf", 0),
    ("Segoe UI", r"C:\Windows\Fonts\segoeui.ttf", 0),
    ("Segoe UI Light", r"C:\Windows\Fonts\segoeuil.ttf", 0),
    ("Microsoft YaHei Bold", r"C:\Windows\Fonts\msyhbd.ttc", 0),
    ("Microsoft YaHei", r"C:\Windows\Fonts\msyh.ttc", 0),
    ("Tahoma Bold", r"C:\Windows\Fonts\tahomabd.ttf", 0),
    ("Cambria Bold（衬线）", r"C:\Windows\Fonts\cambriab.ttf", 0),
    ("Constantia Bold（衬线）", r"C:\Windows\Fonts\constanb.ttf", 0),
    ("Georgia Bold（衬线）", r"C:\Windows\Fonts\georgiab.ttf", 0),
    ("Arial Bold", r"C:\Windows\Fonts\arialbd.ttf", 0),
    ("Consolas Bold（等宽）", r"C:\Windows\Fonts\consolab.ttf", 0),
]

GLYPH = "π"
INK = (26, 26, 29)
LIGHT = (247, 247, 249)

ROW_HEIGHT = 168
WIDTH = 1180


def draw_glyph(target, box, font_path, index, color):
    """把 π 画进 box 正中，尽量撑满。"""
    x, y, size = box
    box_px = size
    # 字号先给个上限，再按实际包围盒缩放，保证各字体视觉大小一致
    probe = ImageFont.truetype(font_path, 200, index=index)
    left, top, right, bottom = probe.getbbox(GLYPH)
    glyph_w = right - left
    glyph_h = bottom - top
    scale = min(box_px / glyph_w, box_px / glyph_h)
    font_size = int(200 * scale)
    font = ImageFont.truetype(font_path, font_size, index=index)
    left, top, right, bottom = font.getbbox(GLYPH)
    dx = x + (box_px - (right - left)) / 2 - left
    dy = y + (box_px - (bottom - top)) / 2 - top
    target.text((dx, dy), GLYPH, font=font, fill=color)


def tile(canvas, origin, tile_size, font_path, index, dark):
    """画一枚圆角方块图标。"""
    x, y = origin
    block = Image.new("RGBA", (tile_size * 4, tile_size * 4), (0, 0, 0, 0))
    d = ImageDraw.Draw(block)
    radius = int(tile_size * 4 * 0.23)
    if dark:
        d.rounded_rectangle((0, 0, tile_size * 4 - 1, tile_size * 4 - 1), radius=radius, fill=(26, 26, 29, 255))
        draw_glyph(d, (int(tile_size * 4 * 0.2), int(tile_size * 4 * 0.2), int(tile_size * 4 * 0.6)), font_path, index, (255, 255, 255, 255))
    else:
        d.rounded_rectangle((0, 0, tile_size * 4 - 1, tile_size * 4 - 1), radius=radius, fill=(247, 247, 249, 255), outline=(26, 26, 29, 255), width=max(1, int(tile_size * 4 * 0.05)))
        draw_glyph(d, (int(tile_size * 4 * 0.2), int(tile_size * 4 * 0.2), int(tile_size * 4 * 0.6)), font_path, index, (26, 26, 29, 255))
    block = block.resize((tile_size, tile_size), Image.LANCZOS)
    canvas.paste(block, (int(x), int(y)), block)


def main():
    height = 92 + ROW_HEIGHT * len(FONTS) + 40
    canvas = Image.new("RGB", (WIDTH, height), (255, 255, 255))
    draw = ImageDraw.Draw(canvas)

    title_font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 20)
    label_font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 15)
    small_font = ImageFont.truetype(r"C:\Windows\Fonts\msyh.ttc", 13)

    draw.text((36, 28), "π 用哪套字体：同一枚图标，不同字体底稿", font=title_font, fill=INK)
    draw.text((36, 58), "每行左边是字体名与 96px 大样，右边是 128 / 32 / 24 / 16 四档图标", font=small_font, fill=(120, 120, 128))

    top = 92
    for row, (name, path, index) in enumerate(FONTS):
        y = top + row * ROW_HEIGHT
        if row % 2 == 0:
            draw.rectangle((24, y - 6, WIDTH - 24, y + ROW_HEIGHT - 18), fill=(250, 250, 252))

        draw.text((40, y + 14), name, font=label_font, fill=INK)
        draw.text((40, y + 40), path.split("\\")[-1], font=small_font, fill=(150, 150, 158))

        # 大样：浅底
        big = Image.new("RGB", (132, 132), (255, 255, 255))
        bd = ImageDraw.Draw(big)
        draw_glyph(bd, (10, 10, 112), path, index, INK)
        canvas.paste(big, (40, y + 66))

        # 四档图标
        x = 200
        for size in (128, 32, 24, 16):
            tile(canvas, (x, y + 60 + (128 - size) // 2), size, path, index, dark=True)
            x += size + 26

        # 浅底 128 + 32
        tile(canvas, (x + 10, y + 60), 128, path, index, dark=False)
        tile(canvas, (x + 158, y + 106), 32, path, index, dark=False)
        tile(canvas, (x + 202, y + 112), 24, path, index, dark=False)
        tile(canvas, (x + 238, y + 118), 16, path, index, dark=False)

    canvas.save(r"E:\Dev\SevenStation\design\shots\control-icon-fonts.png")
    print("生成 design/shots/control-icon-fonts.png", canvas.size)


if __name__ == "__main__":
    main()
