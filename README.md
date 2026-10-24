# TorrServer для Samsung Tizen

TorrServer прямо на телевизоре Samsung — служба на C# с API
[TorrServer](https://github.com/YouROK/TorrServer) MatriX на порту 8090.
[TorrStream](https://github.com/cash94/TorrStream-Tizen), Lampa и другие
клиенты видят её как обычный TorrServer.

Зачем своя: на **Tizen 5.5** службе телевизора нельзя запустить ни одну программу
и загрузить свою `.so` (и файлом, и из памяти — «Operation not permitted»,
«failed to map segment»), поэтому официальный TorrServer (Go) там не работает.
Управляемый код .NET — работает. На Tizen 6 и новее эта служба — альтернатива
[no-cover/torr-streaming](https://github.com/no-cover/torr-streaming): обе
занимают порт 8090, ставьте одну.

Та же служба есть и для Linux (x64, arm64) — без установки .NET.

## Установка на телевизор

Файл — `TorrServer-Tizen.wgt` из [релизов](../../releases/latest). Пакет без
подписи: подписывает тот, кто ставит, своим сертификатом Samsung (он привязан к
DUID телевизора).

1. На ТВ: Apps → набрать на пульте `12345` → Developer Mode **On**, Host PC IP —
   адрес компьютера, перезагрузить телевизор.
2. **Apps2Samsung** ([Jellyfin2Samsung](https://github.com/Jellyfin2Samsung/Samsung-Jellyfin-Installer)):
   «Custom WGT» → `TorrServer-Tizen.wgt`. Программа сама войдёт в учётную
   запись Samsung, выпустит сертификат под телевизор и подпишет пакет.
3. Или **Tizen Studio**: Certificate Manager → профиль Samsung (DUID
   телевизора), `sdb connect <IP>` → `tizen install -n TorrServer-Tizen.wgt -t <устройство>`.

Служба запускается вместе с телевизором. Значок «TorrStream TorrServer» — её
запуск вручную и состояние: адрес, версия, движок. В TorrStream: Настройки →
TorrServer → «Использовать TorrServer, установленный на этом устройстве» —
адрес (IP телевизора, порт 8090) подставится сам; «Об устройстве» → «TorrServer на ТВ».

Ставилась версия под именем `TorrStream-TorrServer.wgt` (до 1.0.9) — новая
ставится поверх: id пакета тот же (`TorrServr1.TorrServer`).

## Linux

Одной командой — служба systemd с автозапуском (спросит порт, Enter — 8090,
проверит, свободен ли; программу для x64 или arm64 скачает из релиза):

```bash
mkdir -p ~/torrserver && cd ~/torrserver && curl -fsSL https://raw.githubusercontent.com/cash94/TorrServer-Tizen/main/linux/torrserver.sh -o torrserver.sh && bash torrserver.sh install
```

Скрипт `linux/torrserver.sh` (он же в архивах релиза):

| Команда | Что делает |
|---|---|
| `./torrserver.sh install` | поставить службу и запустить; повторно — обновить или сменить порт |
| `./torrserver.sh status` | состояние и адрес для TorrStream, Lampa |
| `./torrserver.sh log` | журнал службы |
| `./torrserver.sh uninstall` | удалить службу (данные остаются) |
| `./torrserver.sh run` | запустить в терминале, без службы |

`-p ПОРТ` — без вопроса, `-d КАТАЛОГ` — данные (по умолчанию
`~/.local/share/torrserver-tizen`), `--system` — служба для всей системы через
sudo вместо пользовательской. Пользовательская служба стартует при загрузке
благодаря `loginctl enable-linger` (скрипт включает его сам).

Вручную, без скрипта:

```
tar xzf TorrServer-linux-x64.tar.gz
./TorrServer 8090 ~/ts-mono
```

Аргументы — HTTP-порт и каталог данных (база торрентов, настройки, кэш DHT и
метаданных); по умолчанию 8090 и временный каталог. Для Raspberry Pi и других
ARM — `TorrServer-linux-arm64.tar.gz` (64-битная система). Порт для пиров
случайный, он записан в `settings.json` (`PeersListenPort`) — откройте его в
брандмауэре, если нужны входящие соединения.

## Как устроено

`service/` — .NET-служба `online.torrstream.torrserver`:

- `TorrEngine.cs` — [MonoTorrent](https://github.com/alanmcgovern/monotorrent)
  3.0.2 (MIT, чистый C#, сборка netstandard2.0). Торренты по magnet, хэшу или
  ссылке на `.torrent`; база — `db.json`, метаданные — кэш MonoTorrent,
  настройки — `settings.json`. Без читателей `TorrentDisconnectTimeout` секунд —
  торрент закрывается, как у TorrServer (после предзагрузки — не раньше чем через 2 минуты).
  Каждый трекер — отдельным уровнем (tier): MonoTorrent иначе спрашивает
  трекеры magnet по очереди до первого ответившего, даже если тот пиров не знает.
- `TrackerList.cs` — ретрекеры из [ngosang/trackerslist](https://github.com/ngosang/trackerslist)
  (`trackers_best_ip.txt`, по IP): обновляются при старте службы в фоне, не
  дольше 8 с; GitHub недоступен — список из прошлого раза или встроенный.
  Добавляются к каждому торренту (`RetrackersMode` 1), в базу не пишутся.
- `DhtBootstrap.cs` — запуск DHT в обход MonoTorrent 3.0.2: тот ищет первые
  узлы только через `router.bittorrent.com` (не отвечает), а кэш узлов пишет
  не в том формате, в каком читает, — DHT стоял на 0 узлов. Здесь — несколько
  bootstrap-серверов, кэш в читаемом виде, повторный поиск узлов раз в 2 минуты.
- `MemoryStorage.cs` — куски только в памяти (`CacheSize`, по умолчанию 64 МБ
  на торрент), на флеш телевизора ничего не пишется. Места не хватает —
  вытесняется давно не нужный кусок вне окна читателей, и его бит в bitfield
  MonoTorrent снимается (через внутреннее свойство — `/version` → `evict`),
  чтобы при перемотке назад кусок скачался снова.
- `ResponsiveMode` (по умолчанию включён, как у TorrServer): плеер получает
  блоки куска по мере прихода, не дожидаясь проверки хэша всего куска
  (`MemoryStorage.ReadAvailable`). Без этого при кусках по 8 МБ и 0,7 МБ/с плеер
  по 12 с не получал ни байта и обрывал запрос.
- `PositionPicker.cs` — StandardPicker MonoTorrent, но блоки куска под
  читателем — с его позиции, а начало куска — когда читатель уйдёт дальше:
  перемотка в середину 8-мегабайтного куска не ждёт скачивания мегабайт до неё.
- `PeerRotation.cs` — замена медленных пиров, как у TorrServer: MonoTorrent
  подключает с запасом сверх `ConnectionsLimit` (в его лимит идут и
  незавершённые подключения), лишних и самых медленных (медленнее четверти
  медианы) служба раз в 15 с отключает — из запаса адресов остаются лучшие.
- `WindowRequester.cs` — переделка StreamingPieceRequester из MonoTorrent:
  качается только окно у читателей (`ReaderReadAHead` % кэша вперёд), по
  порядку от позиции; очередь запросов у пира — сколько он тянет; куски вне
  окна (allowed fast, suggested) не берутся.
- `PeerConnector.cs` — подключение к пирам асинхронно, с таймаутом 4 с. В
  netstandard2.0-сборке MonoTorrent оно блокирующее, и на Tizen попытки к
  недоступным пирам висели по 2 минуты — новых подключений не было. Попыток
  сразу — до 48 (`MaximumHalfOpenConnections`): большинство адресов с трекеров
  недоступны, и при 16 попытках по 8 с список перебирался минутами.
- `HttpApi.cs` — `/echo` (`MatriX.136 (TorrStream …)`), `/torrents`
  (add/get/set/rem/list/drop/wipe), `/stream?link=…&index=N&play|stat|m3u|preload`,
  `/play/<хэш>/<N>`, `/cache`, `/settings` (имена BTSets), `/download/<МБ>`;
  файлы — с Range, HEAD, keep-alive; CORS и Private Network Access. Служебное:
  `/version` (JSON), `/log` (журнал: сводка по пирам, трекерам и DHT, кто что
  читает; `?after=N` — новые строки JSON). `/cache` — как у TorrServer, плюс
  у читателя файл, позиция, сколько скачано подряд вперёд (`Ahead`) из окна
  (`Window`), откуда пиры, с `"peers":true` — список пиров.
- `FileOrder.cs` — файлы и их номера как у TorrServer (`server/torr/torrent.go`):
  по пути, числа как числа (`utils.CompareStrings`), id — место в этом списке
  с 1. До 1.0.10 был порядок из .torrent, и раздачи с сериями «по размеру» так и
  показывались в клиентах; номера файлов у таких раздач с 1.0.10 другие.
- `web/index.html` (`WebPage.cs`, вшита в сборку) — страница отладки на `/`:
  торренты и файлы, карта кусков с окнами читателей (позиция, что скачано
  вперёд, что качается), пиры и трекеры, журнал вживую с фильтром, настройки.
  Открывается с телефона или ПК: `http://<IP телевизора>:8090/`.
- `Program.cs` — обвязка Tizen, `Host.cs` — служба целиком (её же запускает `pc/`).

`app/` — страница пакета (запуск службы, состояние). `tools/make_icon.py` — значок.

Не сделано: `/ffp` (ffprobe TorrServer), `/viewed` (отвечает пустым списком),
пароль, кэш на диске, uTP (в MonoTorrent 3.0.2 только TCP — пиры за NAT,
доступные лишь по uTP, остаются «Unreachable»). Пиру, который просит уже вытесненный кусок, MonoTorrent отдаёт не
те данные (он не проверяет, есть ли кусок); пир отбросит их по хэшу.

## Сборка

```
python tools/build.py [--version 1.0.12] [--linux]
```

Нужен .NET SDK 8+ (Tizen.NET.Sdk и MonoTorrent скачаются сами). Результат — в
`dist/`. Проверка без телевизора: `cd service/pc && dotnet run -c Release -- 18090`.

Тег `v*` → workflow `build.yml`: версия из тега → `tools/build.py --linux` →
релиз с `TorrServer-Tizen.wgt` и `TorrServer-linux-*.tar.gz`.

Сторонний код и лицензии — `service/res/THIRD-PARTY-NOTICES.txt` (MonoTorrent, MIT).
