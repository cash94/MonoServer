"""
Значок MonoServer — по мотивам значка TorrServer (круглый жетон: тёмное
кольцо, внутри «цифровой дождь», буквы T и S, по диагонали молния за края
кольца), но в цветах Sub-Zero: ледяной синий и белый на тёмно-синем, буквы
M и S, вместо молнии — ледяная единица.

  python tools/make_icon.py   → app/icon.png и service/shared/res/icon.png
                                (Tizen, 512×423: жетон на тёмном фоне)

badge(size) — сам жетон на прозрачном фоне; из него значки Android
(android/tools/make_icons.py).
"""
import math
import os
import random
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
W, H = 512, 423
BG = (10, 10, 16, 255)

# Sub-Zero: лёд, синий костюм, тёмно-синие тени
ICE_WHITE = (232, 250, 255)
ICE = (120, 214, 255)
BLUE = (0, 140, 230)
DEEP = (8, 60, 140)
NAVY = (10, 26, 52)
INK = (12, 30, 58)

S = 4                 # рисуем в 4 раза крупнее и уменьшаем — сглаживание
BASE = 512            # координаты ниже — для жетона 512×512


def font(size, bold=False):
    names = (('NotoSans-Bold.ttf', 'DejaVuSans-Bold.ttf', 'arialbd.ttf') if bold else
             ('NotoSans-Regular.ttf', 'DejaVuSans.ttf', 'arial.ttf'))
    dirs = ('/usr/share/fonts/truetype/noto/', '/usr/share/fonts/truetype/dejavu/', '')
    for n in names:
        for d in dirs:
            try:
                return ImageFont.truetype(d + n, size)
            except OSError:
                pass
    return ImageFont.load_default()


def layer(n):
    return Image.new('RGBA', (n, n), (0, 0, 0, 0))


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def radial(n, center, inner, outer, radius):
    """Радиальный градиент inner → outer"""
    img = Image.new('RGBA', (n, n))
    px = img.load()
    cx, cy = center
    for y in range(n):
        for x in range(n):
            t = min(1.0, math.hypot(x - cx, y - cy) / radius)
            px[x, y] = lerp(inner, outer, t * t) + (255,)
    return img


def face(n):
    """Лёд внутри кольца: светлый центр, лучи и ледяной «цифровой дождь»"""
    small = n // 8
    img = radial(small, (small * 0.45, small * 0.42), (176, 204, 226), (40, 64, 98), small * 0.6)
    img = img.resize((n, n), Image.BICUBIC)

    # Лучи от центра, как вспышка за знаком TorrServer
    rays = layer(n)
    d = ImageDraw.Draw(rays)
    rnd = random.Random(7)
    c = n / 2
    for i in range(140):
        a = rnd.uniform(0, 2 * math.pi)
        r0, r1 = n * rnd.uniform(0.05, 0.15), n * rnd.uniform(0.35, 0.5)
        d.line((c + r0 * math.cos(a), c + r0 * math.sin(a), c + r1 * math.cos(a), c + r1 * math.sin(a)),
               fill=ICE_WHITE + (rnd.randint(18, 45),), width=max(1, n // 400))
    img = Image.alpha_composite(img, rays.filter(ImageFilter.GaussianBlur(n / 300)))

    # Столбцы символов — ярче к низу каждого «ручья»
    rain = layer(n)
    d = ImageDraw.Draw(rain)
    step = n // 40
    f = font(int(step * 0.95), bold=True)
    glyphs = '01ΛΞ<>=+*:7Z'
    for col in range(84):
        x = (col % 42) * step * 0.97 + rnd.uniform(-2, 2) * S
        top = rnd.uniform(-0.3, 0.8) * n
        length = rnd.randint(6, 22)
        for k in range(length):
            y = top + k * step * 1.05
            t = k / max(1, length - 1)
            alpha = int(90 + 165 * t * rnd.uniform(0.7, 1.0))
            colr = ICE_WHITE if t > 0.9 else lerp((40, 170, 255), ICE, t)
            d.text((x, y), rnd.choice(glyphs), fill=colr + (alpha,), font=f)
    glow = rain.filter(ImageFilter.GaussianBlur(n / 220))
    img = Image.alpha_composite(img, glow)
    img = Image.alpha_composite(img, rain)
    return img


def one_polygon(n):
    """
    Единица вместо молнии: длинная наклонная ножка из-за кольца внизу слева
    за кольцо вверху справа, вверху — флажок «1», концы — острые, как льдинка
    """
    k = n / BASE
    bottom, top = (128 * k, 488 * k), (382 * k, 30 * k)
    ux, uy = top[0] - bottom[0], top[1] - bottom[1]
    length = math.hypot(ux, uy)
    ux, uy = ux / length, uy / length          # вдоль ножки, вверх
    vx, vy = uy, -ux                            # поперёк, влево-вверх → флажок туда
    w = 30 * k                                  # полуширина ножки

    def p(u, v):
        return (bottom[0] + ux * u + vx * v, bottom[1] + uy * u + vy * v)

    L = length
    return [
        p(0, 0),                     # остриё внизу
        p(70 * k, -w),
        p(L - 92 * k, -w),           # правый край ножки до верха
        p(L - 8 * k, -w * 0.55),
        p(L, w * 0.2),               # острая макушка
        p(L - 22 * k, w),            # начало флажка
        p(L - 104 * k, w + 62 * k),  # кончик флажка — влево-вниз
        p(L - 128 * k, w + 34 * k),
        p(L - 84 * k, w),            # флажок вернулся к ножке
        p(70 * k, w),
    ]


def badge(size=512):
    n = size * S
    k = n / BASE
    img = layer(n)

    # Тень под жетоном
    shadow = layer(n)
    ImageDraw.Draw(shadow).ellipse((34 * k, 40 * k, 486 * k, 492 * k), fill=(0, 0, 0, 150))
    img = Image.alpha_composite(img, shadow.filter(ImageFilter.GaussianBlur(10 * k)))

    # Кольцо: тёмно-синее, светлее сверху, ледяная кромка
    ring = radial(n // 8, (n / 16, n / 40), (52, 84, 128), NAVY, n / 9).resize((n, n), Image.BICUBIC)
    mask = layer(n)
    ImageDraw.Draw(mask).ellipse((26 * k, 26 * k, 486 * k, 486 * k), fill=(255, 255, 255, 255))
    img.paste(ring, (0, 0), mask)
    edge = layer(n)
    ImageDraw.Draw(edge).ellipse((28 * k, 28 * k, 484 * k, 484 * k), outline=ICE + (110,), width=int(3 * k))
    img = Image.alpha_composite(img, edge.filter(ImageFilter.GaussianBlur(1.5 * k)))

    # Лёд внутри
    inner = (72 * k, 72 * k, 440 * k, 440 * k)
    m = layer(n)
    ImageDraw.Draw(m).ellipse(inner, fill=(255, 255, 255, 255))
    img.paste(face(n), (0, 0), m)
    rim = layer(n)
    d = ImageDraw.Draw(rim)
    d.ellipse(inner, outline=(0, 0, 0, 140), width=int(9 * k))           # тень кольца на льду
    d.ellipse((66 * k, 66 * k, 446 * k, 446 * k), outline=ICE + (200,), width=int(3 * k))
    img = Image.alpha_composite(img, rim.filter(ImageFilter.GaussianBlur(3 * k)))

    # Буквы M и S — как T и S у TorrServer
    f = font(int(158 * k))
    spots = (((196 * k, 205 * k), 'M'), ((316 * k, 322 * k), 'S'))
    halo = layer(n)
    hd = ImageDraw.Draw(halo)
    letters = layer(n)
    d = ImageDraw.Draw(letters)
    for xy, ch in spots:
        hd.text(xy, ch, font=f, fill=ICE_WHITE + (170,), anchor='mm', stroke_width=int(5 * k), stroke_fill=ICE_WHITE + (170,))
        d.text(xy, ch, font=f, fill=INK + (255,), anchor='mm')
    img = Image.alpha_composite(img, halo.filter(ImageFilter.GaussianBlur(6 * k)))
    img = Image.alpha_composite(img, letters)

    # Единица: ореол, тень, лёд с градиентом, блик и тёмный контур
    poly = one_polygon(n)
    glow = layer(n)
    ImageDraw.Draw(glow).polygon(poly, fill=ICE + (230,))
    img = Image.alpha_composite(img, glow.filter(ImageFilter.GaussianBlur(14 * k)))

    sh = layer(n)
    ImageDraw.Draw(sh).polygon([(x + 6 * k, y + 8 * k) for x, y in poly], fill=(0, 10, 30, 160))
    img = Image.alpha_composite(img, sh.filter(ImageFilter.GaussianBlur(6 * k)))

    one_mask = layer(n)
    ImageDraw.Draw(one_mask).polygon(poly, fill=(255, 255, 255, 255))
    grad = Image.new('RGBA', (n, n))
    gd = ImageDraw.Draw(grad)
    for y in range(0, n, 2):
        t = y / n
        col = lerp(ICE_WHITE, ICE, min(1, t * 1.6)) if t < 0.6 else lerp(ICE, BLUE, (t - 0.6) / 0.4)
        gd.rectangle((0, y, n, y + 2), fill=col + (255,))
    img.paste(grad, (0, 0), one_mask)

    facets = layer(n)
    b, t_ = poly[0], poly[4]
    ImageDraw.Draw(facets).line((b[0] + (t_[0] - b[0]) * 0.08, b[1] + (t_[1] - b[1]) * 0.08,
                                 b[0] + (t_[0] - b[0]) * 0.92, b[1] + (t_[1] - b[1]) * 0.92),
                                fill=(255, 255, 255, 200), width=int(7 * k))
    facets = facets.filter(ImageFilter.GaussianBlur(3 * k))
    clip = layer(n)
    clip.paste(facets, (0, 0), one_mask)
    img = Image.alpha_composite(img, clip)

    outline = layer(n)
    ImageDraw.Draw(outline).line(poly + [poly[0]], fill=DEEP + (255,), width=int(5 * k), joint='curve')
    img = Image.alpha_composite(img, outline)

    # Искры инея у концов
    sparks = layer(n)
    d = ImageDraw.Draw(sparks)
    for x, y, r in ((392, 36, 5), (118, 482, 4), (410, 70, 3), (100, 450, 3), (360, 18, 2)):
        d.ellipse(((x - r) * k, (y - r) * k, (x + r) * k, (y + r) * k), fill=ICE_WHITE + (255,))
    img = Image.alpha_composite(img, Image.alpha_composite(sparks.filter(ImageFilter.GaussianBlur(4 * k)), sparks))

    return img.resize((size, size), Image.LANCZOS)


def main():
    img = Image.new('RGBA', (W, H), BG)
    b = badge(H - 10)
    img.alpha_composite(b, ((W - b.width) // 2, 5))
    for rel in ('app/icon.png', 'service/shared/res/icon.png'):
        path = os.path.join(ROOT, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        img.save(path, optimize=True)
        print('==', path)


if __name__ == '__main__':
    main()
