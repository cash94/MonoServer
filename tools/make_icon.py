"""
Иконка TorrServer для Tizen (512×423, как у TorrStream-Tizen): тот же неон на
тёмном фоне, но вместо «плей» — стопка серверных блоков со стрелкой загрузки,
янтарно-оранжевая, чтобы на телевизоре два значка не путались.

  python tools/make_icon.py   → app/icon.png и service/shared/res/icon.png
"""
import os
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W, H = 512, 423
BG = (10, 10, 16, 255)
HOT = (255, 140, 0)       # оранжевый «Stream» из логотипа
GOLD = (255, 200, 90)
CORE = (255, 236, 200)


def layer():
    return Image.new('RGBA', (W, H), (0, 0, 0, 0))


def glow(draw_fn, color, width, blur):
    """Линия с ореолом: размытая копия под чёткой"""
    halo = layer()
    draw_fn(ImageDraw.Draw(halo), color + (255,), width * 3)
    halo = halo.filter(ImageFilter.GaussianBlur(blur))
    sharp = layer()
    draw_fn(ImageDraw.Draw(sharp), color + (255,), width)
    return Image.alpha_composite(halo, sharp)


def main():
    img = Image.new('RGBA', (W, H), BG)

    # Мягкое пятно света за знаком
    spot = layer()
    ImageDraw.Draw(spot).ellipse((136, 92, 376, 332), fill=HOT + (70,))
    img = Image.alpha_composite(img, spot.filter(ImageFilter.GaussianBlur(60)))

    # Три серверных блока
    x0, x1 = 166, 346
    rows = [(118, 168), (186, 236), (254, 304)]
    for i, (y0, y1) in enumerate(rows):
        col = GOLD if i == 1 else HOT

        def box(d, c, w, y0=y0, y1=y1):
            d.rounded_rectangle((x0, y0, x1, y1), radius=14, outline=c, width=w)
        img = Image.alpha_composite(img, glow(box, col, 5, 9))

        # Огоньки и прорези — как на передней панели сервера
        lights = layer()
        d = ImageDraw.Draw(lights)
        cy = (y0 + y1) // 2
        for k, c in enumerate((CORE, GOLD)):
            cx = x0 + 26 + k * 22
            d.ellipse((cx - 6, cy - 6, cx + 6, cy + 6), fill=c + (255,))
        for k in range(3):
            lx = x1 - 82 + k * 22
            d.rounded_rectangle((lx, cy - 4, lx + 14, cy + 4), radius=3, fill=col + (200,))
        halo = lights.filter(ImageFilter.GaussianBlur(5))
        img = Image.alpha_composite(img, Image.alpha_composite(halo, lights))

    # Стрелка загрузки поверх правого нижнего угла
    def arrow(d, c, w):
        ax, top, tip = 376, 250, 352
        d.line((ax, top, ax, tip - 10), fill=c, width=w)
        d.line((ax - 26, tip - 30, ax, tip), fill=c, width=w, joint='curve')
        d.line((ax + 26, tip - 30, ax, tip), fill=c, width=w, joint='curve')
    plate = layer()
    ImageDraw.Draw(plate).ellipse((340, 232, 412, 368), fill=BG)
    img = Image.alpha_composite(img, plate.filter(ImageFilter.GaussianBlur(10)))
    img = Image.alpha_composite(img, glow(arrow, CORE, 7, 9))

    # Искры вокруг, как у значка TorrStream
    sparks = layer()
    d = ImageDraw.Draw(sparks)
    for x, y, r in ((150, 104, 3), (372, 112, 2), (140, 300, 2), (392, 278, 3), (178, 330, 2), (330, 96, 2), (120, 200, 2)):
        d.ellipse((x - r, y - r, x + r, y + r), fill=GOLD + (230,))
    img = Image.alpha_composite(img, Image.alpha_composite(sparks.filter(ImageFilter.GaussianBlur(3)), sparks))

    for rel in ('app/icon.png', 'service/shared/res/icon.png'):
        path = os.path.join(ROOT, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        img.save(path)
        print('==', path)


if __name__ == '__main__':
    main()
