"""
Значки Android из жетона MonoServer (tools/make_icon.py, badge()):

  python android/tools/make_icons.py

- адаптивный (Android 8+): фон — цвет, передний слой — жетон в безопасной зоне 66 dp из 108;
- обычный mipmap/ic_launcher.png (Android 7 и старше) — жетон на прозрачном фоне, как у TorrServer;
- баннер Android TV 320×180 (drawable-xhdpi/banner.png) — жетон и название.
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

ANDROID = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = os.path.join(ANDROID, 'app', 'src', 'main', 'res')
sys.path.insert(0, os.path.join(os.path.dirname(ANDROID), 'tools'))
import make_icon  # noqa: E402

BG = (10, 10, 16, 255)
DENSITY = {'mdpi': 1, 'hdpi': 1.5, 'xhdpi': 2, 'xxhdpi': 3, 'xxxhdpi': 4}


def save(img, folder, name):
    path = os.path.join(RES, folder)
    os.makedirs(path, exist_ok=True)
    img.save(os.path.join(path, name), optimize=True)


def main():
    s = make_icon.badge(512)
    for d, k in DENSITY.items():
        full, safe = round(108 * k), round(66 * k)
        fg = Image.new('RGBA', (full, full), (0, 0, 0, 0))
        off = (full - safe) // 2
        fg.alpha_composite(s.resize((safe, safe), Image.LANCZOS), (off, off))
        save(fg, 'drawable-' + d, 'ic_launcher_foreground.png')
        legacy = round(48 * k)
        save(s.resize((legacy, legacy), Image.LANCZOS), 'mipmap-' + d, 'ic_launcher.png')

    banner = Image.new('RGBA', (320, 180), BG)
    banner.alpha_composite(s.resize((160, 160), Image.LANCZOS), (4, 10))
    draw = ImageDraw.Draw(banner)
    font = make_icon.font(30, bold=True)
    draw.text((166, 62), 'Mono', fill=(255, 255, 255), font=font)
    draw.text((166, 96), 'Server', fill=make_icon.ICE, font=font)
    save(banner, 'drawable-xhdpi', 'banner.png')


if __name__ == '__main__':
    main()
