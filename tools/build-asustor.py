"""
Пакет App Central для NAS ASUSTOR (ADM 4+/5): MonoServer-ASUSTOR-x64.apk и
MonoServer-ASUSTOR-arm64.apk (.apk — формат ASUSTOR, не Android).

  python tools/build-asustor.py [--version 1.0.38] [--revision 1] [--dist dist]

Берёт готовые архивы Linux из --dist (MonoServer-linux-x64.tar.gz и
-arm64.tar.gz — их делает tools/build.py --linux или они скачаны из релиза):
программа MonoServer с .NET внутри едет в пакете, NAS при установке ничего не
качает. Нет архива под архитектуру — пакет для неё пропускается.

Устройство пакета (APKG 2.0, как у apkg-tools.py от ASUSTOR) — zip без сжатия:
  apkg-version     — «2.0»
  control.tar.gz   — asustor/CONTROL: config.json (версия, архитектура), icon.png
                     (90×90), описание и скрипты App Central
  data.tar.gz      — то, что ляжет в /usr/local/AppCentral/monoserver:
                     bin/monoserverctl.sh, runtime/MonoServer, runtime/libclrgc.so
Данные службы — /volume1/MonoServer, порт 8099 (asustor/README.md).
"""
import argparse, io, json, os, re, shutil, tarfile, tempfile, time, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'asustor')
PACKAGE = 'monoserver'
# Архив Linux → архитектура ASUSTOR и имя пакета
ARCHES = (('linux-x64', 'x86-64', 'x64'), ('linux-arm64', 'arm64', 'arm64'))


def version_from_config():
    text = open(os.path.join(ROOT, 'app', 'config.xml'), encoding='utf-8').read()
    m = re.search(r'<widget[^>]*\sversion="([\d.]+)"', text, re.S)
    if not m:
        raise SystemExit('версии нет в app/config.xml — укажите --version')
    return m.group(1)


class Tar:
    """tar.gz с владельцем root и постоянным временем — как собирает apkg-tools.py, но без chown на диске"""

    def __init__(self, path):
        self.tar = tarfile.open(path, 'w:gz', format=tarfile.GNU_FORMAT)
        self.dirs = set()
        self.mtime = int(time.time())
        self._dir('.')

    def _info(self, name, mode):
        ti = tarfile.TarInfo(name)
        ti.mode = mode
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = 'root'
        ti.mtime = self.mtime
        return ti

    def _dir(self, name):
        if name in self.dirs:
            return
        parent = os.path.dirname(name)
        if parent and parent != name:
            self._dir(parent)
        ti = self._info(name, 0o755)
        ti.type = tarfile.DIRTYPE
        self.tar.addfile(ti)
        self.dirs.add(name)

    def add(self, name, data, mode=0o644):
        name = './' + name
        self._dir(os.path.dirname(name))
        ti = self._info(name, mode)
        ti.size = len(data)
        self.tar.addfile(ti, io.BytesIO(data))

    def close(self):
        self.tar.close()


def unix_text(path):
    # Скрипты App Central должны быть с LF (apkg-tools.py гоняет их через dos2unix)
    return open(path, 'rb').read().replace(b'\r\n', b'\n')


def build(version, revision, arch, linux_tar, out, work):
    apkg_version = '%s.r%02d' % (version, revision)
    with tarfile.open(linux_tar) as t:
        names = t.getnames()
        if 'MonoServer' not in names:
            raise SystemExit('%s: нет программы MonoServer' % linux_tar)
        binary = t.extractfile('MonoServer').read()
        gc = t.extractfile('libclrgc.so').read() if 'libclrgc.so' in names else None
        notices = t.extractfile('THIRD-PARTY-NOTICES.txt').read() if 'THIRD-PARTY-NOTICES.txt' in names else None

    tmp = tempfile.mkdtemp(dir=work)
    # control.tar.gz
    control = Tar(os.path.join(tmp, 'control.tar.gz'))
    cfg = open(os.path.join(SRC, 'CONTROL', 'config.json'), encoding='utf-8').read()
    cfg = cfg.replace('@VERSION@', apkg_version).replace('@ARCH@', arch)
    info = json.loads(cfg)   # битый JSON App Central не покажет внятно — ловим здесь
    for key in ('package', 'version', 'architecture', 'firmware'):
        if not info['general'].get(key):
            raise SystemExit('config.json: пусто general.' + key)
    control.add('config.json', cfg.encode('utf-8'))
    for name in sorted(os.listdir(os.path.join(SRC, 'CONTROL'))):
        path = os.path.join(SRC, 'CONTROL', name)
        if name == 'config.json' or not os.path.isfile(path):
            continue
        if name.endswith('.sh'):
            control.add(name, unix_text(path), 0o755)
        else:
            control.add(name, open(path, 'rb').read(), 0o644)
    control.add('changelog.txt', ('MonoServer %s (пакет %s): https://github.com/cash94/MonoServer/releases/tag/v%s\n'
                                  % (version, apkg_version, version)).encode('utf-8'))
    control.close()

    # data.tar.gz
    data = Tar(os.path.join(tmp, 'data.tar.gz'))
    data.add('bin/monoserverctl.sh', unix_text(os.path.join(SRC, 'bin', 'monoserverctl.sh')), 0o755)
    data.add('runtime/MonoServer', binary, 0o755)
    if gc:
        data.add('runtime/libclrgc.so', gc, 0o644)
    if notices:
        data.add('THIRD-PARTY-NOTICES.txt', notices, 0o644)
    data.add('README.md', open(os.path.join(SRC, 'README.md'), 'rb').read(), 0o644)
    data.close()

    with open(os.path.join(tmp, 'apkg-version'), 'w') as f:
        f.write('2.0\n')

    base = 'MonoServer-ASUSTOR-' + dict((a, n) for _, a, n in ARCHES)[arch]
    target = os.path.join(out, '%s_%s.apk' % (base, apkg_version))
    # Порядок и без сжатия — как zipfile.ZipFile(apk, 'w') в apkg-tools.py
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_STORED) as z:
        for name in ('apkg-version', 'control.tar.gz', 'data.tar.gz'):
            z.write(os.path.join(tmp, name), name)
    shutil.copyfile(target, os.path.join(out, base + '.apk'))
    print('==', target)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--version', help='версия MonoServer x.y.z (по умолчанию из app/config.xml)')
    ap.add_argument('--revision', type=int, default=1, help='ревизия пакета: 1.0.38.r01, .r02…')
    ap.add_argument('--dist', default=os.path.join(ROOT, 'dist'), help='где архивы Linux и куда класть .apk')
    args = ap.parse_args()
    version = args.version or version_from_config()
    if not re.match(r'^\d+\.\d+\.\d+$', version):
        raise SystemExit('версия должна быть вида 1.0.38: ' + version)
    work = tempfile.mkdtemp()
    built = 0
    try:
        for rid, arch, _ in ARCHES:
            src = os.path.join(args.dist, 'MonoServer-%s.tar.gz' % rid)
            if not os.path.exists(src):
                print('-- нет %s — пакет %s пропущен' % (src, arch))
                continue
            build(version, args.revision, arch, src, args.dist, work)
            built += 1
    finally:
        shutil.rmtree(work, ignore_errors=True)
    if not built:
        raise SystemExit('нет ни одного архива MonoServer-linux-*.tar.gz в ' + args.dist)


if __name__ == '__main__':
    main()
