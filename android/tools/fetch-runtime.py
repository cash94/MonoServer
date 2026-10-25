"""
Всё, что едет в APK TorrServer для Android:

  python android/tools/fetch-runtime.py [--abi armeabi-v7a] [--abi arm64-v8a]

- служба: service/mono (те же исходники, что для Tizen, сборка net472) — dotnet build;
- Mono из пакетов Termux — собран под bionic, как всё на Android, и есть для
  armeabi-v7a, arm64-v8a и x86_64 (.NET от Microsoft для Android-процессов есть только
  arm64, в Termux .NET — тоже без 32-битного ARM). Из пакетов берутся сама программа
  mono и только те библиотеки, что ей и OpenSSL нужны по DT_NEEDED;
- библиотеки классов Mono (mono-libs, общие для всех процессоров) — только сборки, на
  которые служба ссылается прямо или через другие сборки.

Раскладка:
  app/src/main/jniLibs/<abi>/libmono.so          — программа mono (исполняемая);
  app/src/main/jniLibs/<abi>/libdep_<hex>.so     — библиотеки (имя закодировано: из APK
                                                   распаковываются только lib*.so, а у
                                                   части имя с версией — libssl.so.3);
  app/src/main/jniLibs/<abi>/libdeplink_<hex>_<hex>.so — пустые метки ссылок имя → цель.
  app/src/main/assets/torrserver.zip             — mono/lib/mono/4.5 (сборки), mono/etc
                                                   (config, machine.config), app/ (служба).
Ссылки с настоящими именами создаёт приложение (ServerService.prepareLibs) и указывает
их каталог в LD_LIBRARY_PATH; torrserver.zip распаковывает при первом запуске версии.
"""
import argparse
import io
import json
import lzma
import os
import posixpath
import shutil
import struct
import subprocess
import sys
import tarfile
import urllib.request
import zipfile

ANDROID = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ROOT = os.path.dirname(ANDROID)
JNI = os.path.join(ANDROID, 'app', 'src', 'main', 'jniLibs')
ASSETS = os.path.join(ANDROID, 'app', 'src', 'main', 'assets')
WORK = os.path.join(ANDROID, 'build', 'runtime')
SERVICE = os.path.join(ROOT, 'service', 'mono')
ASMREFS = os.path.join(ANDROID, 'tools', 'asmrefs')

MIRROR = 'https://packages.termux.dev/apt/termux-main'
TERMUX_ARCH = {'arm64-v8a': 'aarch64', 'armeabi-v7a': 'arm', 'x86_64': 'x86_64'}
PREFIX = 'data/data/com.termux/files/usr/'
# Программа и то, что Mono открывает сам (P/Invoke), а не по DT_NEEDED
ROOT_LIBS = ['libmono-native.so', 'libMonoPosixHelper.so', 'libssl.so.3', 'libcrypto.so.3']
# Есть в любом Android — из пакетов не берём
SYSTEM_LIBS = {'libc.so', 'libm.so', 'libdl.so', 'liblog.so', 'libandroid.so', 'libz.so'}


def fetch(url):
    with urllib.request.urlopen(url) as r:
        return r.read()


def cached(url):
    """Скачать один раз в build/runtime/cache"""
    path = os.path.join(WORK, 'cache', url.rsplit('/', 1)[1])
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        data = fetch(url)
        with open(path + '.part', 'wb') as f:
            f.write(data)
        os.replace(path + '.part', path)
    with open(path, 'rb') as f:
        return f.read()


def packages_index(arch):
    raw = fetch(f'{MIRROR}/dists/stable/main/binary-{arch}/Packages')
    pk, cur = {}, {}
    for line in raw.decode('utf-8').split('\n') + ['']:
        if not line:
            if 'Package' in cur:
                pk[cur['Package']] = cur
            cur = {}
        elif ':' in line and not line.startswith(' '):
            k, v = line.split(':', 1)
            cur[k] = v.strip()
    return pk


def resolve(pk, name, seen, skip=()):
    """Пакет и все его зависимости (по Depends, без версий и альтернатив)"""
    if name in seen or name in skip or name not in pk:
        return
    seen.add(name)
    for dep in pk[name].get('Depends', '').split(','):
        dep = dep.split('|')[0].split('(')[0].strip()
        if dep:
            resolve(pk, dep, seen, skip)


def deb_data(deb):
    """data.tar.* из ar-архива .deb"""
    assert deb[:8] == b'!<arch>\n'
    pos = 8
    while pos < len(deb):
        name = deb[pos:pos + 16].decode().strip().rstrip('/')
        size = int(deb[pos + 48:pos + 58].decode().strip())
        body = deb[pos + 60:pos + 60 + size]
        pos += 60 + size + (size & 1)
        if name.startswith('data.tar'):
            if name.endswith('.xz'):
                return tarfile.open(fileobj=io.BytesIO(lzma.decompress(body)))
            return tarfile.open(fileobj=io.BytesIO(body))
    raise RuntimeError('в .deb нет data.tar')


def elf_needed(data):
    """DT_NEEDED из ELF (32/64 бита, little-endian)"""
    if data[:4] != b'\x7fELF':
        return []
    is64 = data[4] == 2
    if is64:
        phoff, = struct.unpack_from('<Q', data, 0x20)
        phentsize, phnum = struct.unpack_from('<HH', data, 0x36)
    else:
        phoff, = struct.unpack_from('<I', data, 0x1C)
        phentsize, phnum = struct.unpack_from('<HH', data, 0x2A)
    loads, dynamic = [], None
    for i in range(phnum):
        off = phoff + i * phentsize
        if is64:
            p_type, _, p_offset, p_vaddr, _, p_filesz, _, _ = struct.unpack_from('<IIQQQQQQ', data, off)
        else:
            p_type, p_offset, p_vaddr, _, p_filesz, _, _, _ = struct.unpack_from('<IIIIIIII', data, off)
        if p_type == 1:
            loads.append((p_vaddr, p_offset, p_filesz))
        elif p_type == 2:
            dynamic = (p_offset, p_filesz)
    if not dynamic:
        return []

    def to_off(vaddr):
        for v, o, sz in loads:
            if v <= vaddr < v + sz:
                return vaddr - v + o
        raise ValueError('адрес вне сегментов')

    fmt, step = ('<qQ', 16) if is64 else ('<iI', 8)
    needed, strtab = [], None
    for off in range(dynamic[0], dynamic[0] + dynamic[1], step):
        tag, val = struct.unpack_from(fmt, data, off)
        if tag == 0:
            break
        if tag == 1:
            needed.append(val)
        elif tag == 5:
            strtab = to_off(val)
    out = []
    for n in needed:
        end = data.index(b'\0', strtab + n)
        out.append(data[strtab + n:end].decode())
    return out


def native(abi, pk):
    """Программа mono и нужные ей библиотеки из пакетов Termux → jniLibs/<abi>"""
    names = set()
    resolve(pk, 'mono', names, skip={'mono-libs'})
    resolve(pk, 'openssl', names)
    files, links = {}, {}    # имя → байты; ссылка → цель
    exe = None
    for name in sorted(names):
        tar = deb_data(cached(f'{MIRROR}/{pk[name]["Filename"]}'))
        for m in tar.getmembers():
            path = m.name.lstrip('./')
            if not path.startswith(PREFIX):
                continue
            rel = path[len(PREFIX):]
            if rel == 'bin/mono-sgen' and m.isfile():
                exe = tar.extractfile(m).read()
            elif rel.startswith('lib/') and '/' not in rel[4:] and '.so' in rel:
                base = rel[4:]
                if m.issym():
                    links[base] = posixpath.basename(m.linkname)
                elif m.isfile():
                    files[base] = tar.extractfile(m).read()
    if exe is None:
        sys.exit(f'{abi}: в пакетах нет bin/mono-sgen')

    def real(name):
        seen = set()
        while name in links and name not in seen:
            seen.add(name)
            name = links[name]
        return name if name in files else None

    # Замыкание по DT_NEEDED: только то, что действительно загрузится
    want, queue, system = set(), list(elf_needed(exe)) + ROOT_LIBS, set()
    while queue:
        lib = queue.pop()
        if lib in want or lib in system:
            continue
        target = real(lib)
        if target is None:
            if lib not in SYSTEM_LIBS:
                print(f'   {abi}: {lib} — нет в пакетах, считаю системной')
            system.add(lib)
            continue
        want.add(lib)
        want.add(target)
        queue.extend(elf_needed(files[target]))

    dest = os.path.join(JNI, abi)
    os.makedirs(dest, exist_ok=True)
    for f in os.listdir(dest):
        if f.startswith('libdep') or f == 'libmono.so':
            os.remove(os.path.join(dest, f))
    with open(os.path.join(dest, 'libmono.so'), 'wb') as f:
        f.write(exe)
    size = len(exe)
    for name in sorted(want):
        if name in files:
            with open(os.path.join(dest, 'libdep_' + name.encode().hex() + '.so'), 'wb') as f:
                f.write(files[name])
            size += len(files[name])
        else:
            # Ссылка (libssl.so.3 → …): копия удвоила бы APK — пустая метка, ссылку создаст приложение
            target = real(name)
            open(os.path.join(dest, 'libdeplink_' + name.encode().hex() + '_' + target.encode().hex() + '.so'), 'wb').close()
    print(f'== {abi}: mono {pk["mono"]["Version"]}, {len([n for n in want if n in files])} библиотек, '
          f'{size / 1048576:.1f} МБ: {", ".join(sorted(n for n in want if n in files))}')
    return pk['mono']['Version']


def build_service():
    out = os.path.join(WORK, 'service')
    shutil.rmtree(out, ignore_errors=True)
    subprocess.check_call(['dotnet', 'build', SERVICE, '-c', 'Release', '-nologo', '-v', 'q', '-o', out])
    return out


def asm_refs(paths):
    tool = os.path.join(WORK, 'asmrefs')
    if not os.path.exists(os.path.join(tool, 'asmrefs.dll')):
        subprocess.check_call(['dotnet', 'build', ASMREFS, '-c', 'Release', '-nologo', '-v', 'q', '-o', tool])
    out = subprocess.run(['dotnet', os.path.join(tool, 'asmrefs.dll')] + paths, check=True,
                         capture_output=True, text=True).stdout
    refs = {}
    for line in out.splitlines():
        path, _, names = line.partition('\t')
        refs[path] = [n for n in names.split(',') if n]
    return refs


def bcl(pk, service):
    """Сборки Mono, нужные службе, и конфиги — в torrserver.zip вместе со службой"""
    libs = deb_data(cached(f'{MIRROR}/{pk["mono-libs"]["Filename"]}'))
    mono = deb_data(cached(f'{MIRROR}/{pk["mono"]["Filename"]}'))
    stage = os.path.join(WORK, 'bcl')
    shutil.rmtree(stage, ignore_errors=True)
    # 4.5 и 4.5/Facades — ссылки в gac: берём настоящие файлы
    members = {m.name.lstrip('./'): m for m in libs.getmembers()}
    base = PREFIX + 'lib/mono/4.5/'
    available = {}
    for name, m in members.items():
        if not name.startswith(base) or not name.endswith(('.dll', '.exe')):
            continue
        rel = name[len(base):]
        if '/' in rel and not rel.startswith('Facades/'):
            continue
        target = m
        while target.issym():
            target = members[posixpath.normpath(posixpath.join(posixpath.dirname(target.name.lstrip('./')), target.linkname))]
        available[posixpath.basename(rel)[:-4].lower()] = (rel, target)

    os.makedirs(stage)
    app_files = [os.path.join(service, f) for f in os.listdir(service) if f.endswith(('.dll', '.exe'))]
    local = {os.path.basename(f)[:-4].lower() for f in app_files}
    taken, queue = {}, list(app_files)
    while queue:
        refs = asm_refs(queue)
        queue = []
        for names in refs.values():
            for n in names:
                key = n.lower()
                if key in local or key in taken:
                    continue
                if key not in available:
                    print(f'   сборки {n} нет в mono-libs — пропускаю')
                    taken[key] = None
                    continue
                rel, m = available[key]
                path = os.path.join(stage, 'mono', 'lib', 'mono', '4.5', rel)
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, 'wb') as f:
                    f.write(libs.extractfile(m).read())
                taken[key] = path
                queue.append(path)
    # mscorlib грузится раньше всего и сам ни на что не ссылается — но нужен всегда
    for must in ('mscorlib',):
        if must not in taken:
            rel, m = available[must]
            path = os.path.join(stage, 'mono', 'lib', 'mono', '4.5', rel)
            with open(path, 'wb') as f:
                f.write(libs.extractfile(m).read())
            taken[must] = path

    # Конфиги Mono: config (dllmap) — без $mono_libdir: библиотеки ищутся по
    # LD_LIBRARY_PATH, а зашитый каталог Termux у приложения недоступен
    for m in mono.getmembers():
        rel = m.name.lstrip('./')[len(PREFIX):] if m.name.lstrip('./').startswith(PREFIX) else None
        if rel in ('etc/mono/config', 'etc/mono/4.5/machine.config') and m.isfile():
            data = mono.extractfile(m).read()
            if rel == 'etc/mono/config':
                data = data.replace(b'$mono_libdir/', b'')
            path = os.path.join(stage, 'mono', rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, 'wb') as f:
                f.write(data)
    count = len([p for p in taken.values() if p])
    size = sum(os.path.getsize(p) for p in taken.values() if p)
    print(f'== сборки Mono: {count} шт., {size / 1048576:.1f} МБ')
    return stage


def pack(stage, service, mono_version):
    os.makedirs(ASSETS, exist_ok=True)
    target = os.path.join(ASSETS, 'torrserver.zip')
    version = open(os.path.join(ROOT, 'service', 'TorrEngine.cs'), encoding='utf-8').read()
    version = version.split('ServiceVersion = "', 1)[1].split('"', 1)[0]
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as z:
        for cur, _, files in os.walk(stage):
            for f in sorted(files):
                full = os.path.join(cur, f)
                z.write(full, os.path.relpath(full, stage).replace(os.sep, '/'))
        for f in sorted(os.listdir(service)):
            if f.endswith(('.dll', '.exe', '.config')):
                z.write(os.path.join(service, f), 'app/' + f)
        z.write(os.path.join(ROOT, 'service', 'res', 'THIRD-PARTY-NOTICES.txt'), 'app/THIRD-PARTY-NOTICES.txt')
        z.writestr('version.json', json.dumps({'torrserver': version, 'mono': mono_version}))
    print(f'== {target}: {os.path.getsize(target) / 1048576:.1f} МБ, служба {version}')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--abi', choices=list(TERMUX_ARCH), action='append')
    args = ap.parse_args()
    os.makedirs(WORK, exist_ok=True)
    mono_version, pk = None, None
    for abi in args.abi or list(TERMUX_ARCH):
        pk = packages_index(TERMUX_ARCH[abi])
        mono_version = native(abi, pk)
    service = build_service()
    pack(bcl(pk, service), service, mono_version)


if __name__ == '__main__':
    main()
