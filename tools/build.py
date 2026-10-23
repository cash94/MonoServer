"""
Сборка TorrServer для Samsung Tizen и Linux.

  python tools/build.py [--version 1.0.9] [--out dist] [--linux]

TorrServer-Tizen.wgt — гибрид, как TorrServer от no-cover/torr-streaming и
службы TorrStream-Tizen: страница app/ (запуск службы и её состояние) и
.NET-служба service/ (TorrServer на C#, MonoTorrent, API TorrServer MatriX на
8090). Раскладка пакета:
  config.xml, tizen-manifest.xml   — в корне (package службы = package страницы)
  bin/  shared/res/  res/          — .NET-служба (из её .tpk)
  res/wgt/                         — страница, её config.xml тоже здесь
Подписей внутри нет: пакет подписывает тот, кто ставит (Apps2Samsung, Tizen
Studio) своим сертификатом Samsung — он привязан к DUID телевизора.

--linux — ещё TorrServer-linux-x64.tar.gz и TorrServer-linux-arm64.tar.gz:
та же служба консольной программой (service/pc), с .NET внутри.

Результат — <имя>_<версия> и копия с постоянным именем. Нужен .NET SDK 8+
(Tizen.NET.Sdk и MonoTorrent dotnet скачает сам).
"""
import argparse, io, os, re, shutil, subprocess, tarfile, tempfile, zipfile
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SERVICE = os.path.join(ROOT, 'service')
APP = os.path.join(ROOT, 'app')
BASE = 'TorrServer-Tizen'


def check_xml(text, what):
    # Установщик Tizen с битым XML (хотя бы «--» в комментарии) отвечает только
    # «Invalid App. Could not read xml entry» — ловим это при сборке
    try:
        ET.fromstring(text.encode('utf-8'))
    except ET.ParseError as e:
        raise SystemExit('%s: неверный XML — %s' % (what, e))


def set_version(text, tag, version):
    return re.sub(r'(<%s\b[^>]*\bversion=")[^"]*(")' % tag, r'\g<1>' + version + r'\g<2>', text, count=1)


def build_tpk(version, work):
    """Собрать .tpk службы во временном каталоге; путь к нему"""
    proj = os.path.join(work, 'service')
    # pc/ — запуск на ПК и Linux, в пакет не идёт
    shutil.copytree(SERVICE, proj, ignore=shutil.ignore_patterns('bin', 'obj', 'pc'))
    if version:
        man_path = os.path.join(proj, 'tizen-manifest.xml')
        man = open(man_path, encoding='utf-8').read()
        open(man_path, 'w', encoding='utf-8').write(set_version(man, 'manifest', version))
    subprocess.check_call(['dotnet', 'build', '-c', 'Release', '-nologo', '-v', 'q'], cwd=proj)
    for folder, _, files in os.walk(os.path.join(proj, 'bin')):
        for f in files:
            if f.endswith('.tpk'):
                return os.path.join(folder, f)
    raise SystemExit('tpk не собрался')


def add_dir(z, folder, prefix):
    for cur, _, files in os.walk(folder):
        for f in sorted(files):
            full = os.path.join(cur, f)
            z.write(full, prefix + os.path.relpath(full, folder).replace(os.sep, '/'))


def build_wgt(version, out, work):
    app = os.path.join(work, 'app')
    shutil.copytree(APP, app)
    cfg_path = os.path.join(app, 'config.xml')
    cfg = open(cfg_path, encoding='utf-8').read()
    if version:
        cfg = set_version(cfg, 'widget', version)
        open(cfg_path, 'w', encoding='utf-8').write(cfg)
    check_xml(cfg, 'app/config.xml')
    version = re.search(r'<widget\b[^>]*\bversion="([^"]+)"', cfg).group(1)
    package = re.search(r'<tizen:application\b[^>]*\bpackage="([^"]+)"', cfg).group(1)

    tpk = zipfile.ZipFile(build_tpk(version, work))
    # Служба — в пакете страницы: тот же package, что в config.xml
    manifest = tpk.read('tizen-manifest.xml').decode('utf-8')
    manifest = re.sub(r'(<manifest\b[^>]*\bpackage=")[^"]*(")', r'\g<1>' + package + r'\g<2>', manifest, count=1)
    # Как у no-cover: в корневом config.xml — ещё и привилегии службы: права
    # пакета установщик, судя по их пакету, берёт оттуда
    extra = ''
    for priv in re.findall(r'<privilege>([^<]+)</privilege>', manifest):
        if ('name="%s"' % priv) not in cfg:
            extra += '    <tizen:privilege name="%s"/>\n' % priv
    root_cfg = cfg.replace('</widget>', extra + '</widget>') if extra else cfg
    check_xml(root_cfg, 'config.xml (корень)')
    check_xml(manifest, 'tizen-manifest.xml')

    target = os.path.join(out, '%s_%s.wgt' % (BASE, version))
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('config.xml', root_cfg)
        z.writestr('tizen-manifest.xml', manifest)
        for info in tpk.infolist():
            n = info.filename
            # Подписи Tizen.NET.Sdk — не наши: подпишет установщик весь wgt
            if n.endswith('/') or n in ('tizen-manifest.xml', 'author-signature.xml') or re.match(r'signature\d+\.xml$', n):
                continue
            z.writestr(info, tpk.read(n))
        add_dir(z, app, 'res/wgt/')
    shutil.copyfile(target, os.path.join(out, BASE + '.wgt'))
    print('==', target)
    return version


def build_linux(version, out, work):
    """Служба консольной программой с .NET внутри — архив с правом на запуск"""
    for rid in ('linux-x64', 'linux-arm64'):
        pub = os.path.join(work, 'linux-' + rid)
        subprocess.check_call(['dotnet', 'publish', os.path.join(SERVICE, 'pc'), '-c', 'Release', '-r', rid,
                               '--self-contained', 'true', '-p:PublishSingleFile=true',
                               '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None',
                               '-nologo', '-v', 'q', '-o', pub])
        name = 'TorrServer-' + rid
        target = os.path.join(out, '%s_%s.tar.gz' % (name, version))
        with tarfile.open(target, 'w:gz') as tar:
            info = tar.gettarinfo(os.path.join(pub, 'TorrServerPc'), arcname='TorrServer')
            info.mode = 0o755
            with open(os.path.join(pub, 'TorrServerPc'), 'rb') as f:
                tar.addfile(info, f)
            notices = open(os.path.join(SERVICE, 'res', 'THIRD-PARTY-NOTICES.txt'), 'rb').read()
            ti = tarfile.TarInfo('THIRD-PARTY-NOTICES.txt')
            ti.size = len(notices)
            ti.mode = 0o644
            tar.addfile(ti, io.BytesIO(notices))
        shutil.copyfile(target, os.path.join(out, name + '.tar.gz'))
        print('==', target)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--version', help='версия x.y.z (по умолчанию из app/config.xml)')
    ap.add_argument('--out', default=os.path.join(ROOT, 'dist'))
    ap.add_argument('--linux', action='store_true', help='ещё архивы для Linux (x64, arm64)')
    args = ap.parse_args()
    if args.version and not re.match(r'^\d+\.\d+\.\d+$', args.version):
        raise SystemExit('версия должна быть вида 1.0.9: ' + args.version)
    os.makedirs(args.out, exist_ok=True)
    work = tempfile.mkdtemp()
    try:
        version = build_wgt(args.version, args.out, work)
        if args.linux:
            build_linux(version, args.out, work)
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == '__main__':
    main()
