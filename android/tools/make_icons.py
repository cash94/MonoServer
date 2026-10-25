"""
Значки Android из значка Tizen (app/icon.png — tools/make_icon.py):

  python android/tools/make_icons.py

- адаптивный (Android 8+): фон — цвет, передний слой — знак в безопасной зоне 66 dp из 108;
- обычный mipmap/ic_launcher.png (Android 7 и старше) — знак на тёмном квадрате;
- баннер Android TV 320×180 (drawable-xhdpi/banner.png) — знак и название.
"""
import os
from PIL import Image, ImageDraw, ImageFont

ANDROID = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RES = os.path.join(ANDROID, 'app', 'src', 'main', 'res')
ICON = os.path.join(os.path.dirname(ANDROID), 'app', 'icon.png')
BG = (10, 10, 16, 255)
DENSITY = {'mdpi': 1, 'hdpi': 1.5, 'xhdpi': 2, 'xxhdpi': 3, 'xxxhdpi': 4}


def sign():
    """Знак без полей: квадрат вокруг серверов и стрелки"""
    img = Image.open(ICON).convert('RGBA')
    cx, cy, half = 284, 228, 150
    return img.crop((cx - half, cy - half, cx + half, cy + half))


def save(img, folder, name):
    path = os.path.join(RES, folder)
    os.makedirs(path, exist_ok=True)
    img.save(os.path.join(path, name), optimize=True)


def main():
    s = sign()
    for d, k in DENSITY.items():
        full, safe = round(108 * k), round(66 * k)
        fg = Image.new('RGBA', (full, full), BG)
        off = (full - safe) // 2
        fg.paste(s.resize((safe, safe), Image.LANCZOS), (off, off))
        save(fg, 'drawable-' + d, 'ic_launcher_foreground.png')
        legacy = round(48 * k)
        save(s.resize((legacy, legacy), Image.LANCZOS), 'mipmap-' + d, 'ic_launcher.png')

    banner = Image.new('RGBA', (320, 180), BG)
    banner.paste(s.resize((150, 150), Image.LANCZOS), (8, 15))
    draw = ImageDraw.Draw(banner)
    font = None
    for f in ('/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf', 'DejaVuSans-Bold.ttf', 'arial.ttf'):
        try:
            font = ImageFont.truetype(f, 30)
            break
        except OSError:
            pass
    draw.text((158, 62), 'Torr', fill=(255, 255, 255), font=font)
    draw.text((158, 96), 'Server', fill=(255, 140, 0), font=font)
    save(banner, 'drawable-xhdpi', 'banner.png')


if __name__ == '__main__':
    main()
