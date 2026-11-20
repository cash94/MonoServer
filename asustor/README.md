# MonoServer для ASUSTOR (ADM)

Пакет App Central с **MonoServer** для сетевых хранилищ ASUSTOR: ставится через
**App Central → Установка вручную**, без Docker и systemd. Собирается в CI вместе с
остальными сборками MonoServer — в каждом [релизе](https://github.com/cash94/MonoServer/releases/latest):

| Файл | NAS |
| --- | --- |
| `MonoServer-ASUSTOR-x64.apk` | x86-64 — Intel/AMD: серии 31xx, 32xx, 40xx, 50xx, 51xx, 52xx, **53xx** (AS5304T), 54xx, 6xxx, 7xxx, Flashstor… |
| `MonoServer-ASUSTOR-arm64.apk` | ARM64 — Drivestor 2/4 Pro (33xx), 11TL и др. |

`.apk` здесь — установочный пакет ASUSTOR (zip: `apkg-version`, `control.tar.gz`,
`data.tar.gz`), не Android APK. Для **AS5304T** и других Intel/AMD — только `-x64.apk`:
`-arm64.apk` на них не встанет.

> Неофициальный пакет того же устройства (сетевой установщик) открывал веб-интерфейс на
> **AS5304T** с **ADM 5.1**. Этот пакет на NAS пока не проверялся, ARM64 — тоже.

## Возможности

- Нативный запуск MonoServer на NAS через App Central.
- Веб-интерфейс и API TorrServer на порту **8099** — адрес для TorrStream, Lampa и
  других клиентов TorrServer.
- Ярлык приложения на рабочем столе ADM.
- Запуск и остановка из App Central; служба стартует вместе с NAS. Упала — через
  10 с запускается снова.
- Процесс ищется не только по PID-файлу, но и по `/proc`: по файлу программы
  пакета и каталогу данных в аргументах. Остановка срабатывает, даже если PID-файл
  потерян или службу запускал прежний пакет; другие экземпляры MonoServer (с другим
  каталогом данных) не трогаются.
- Программа MonoServer **внутри пакета** — при установке NAS ничего не скачивает.
- Настройки, база торрентов, кэш и журналы — в отдельной папке `/volume1/MonoServer`,
  при обновлении и удалении приложения не стираются.

## Совместимость

| Параметр | Значение |
| --- | --- |
| ADM | 4.0 и новее (проверено на 5.1) |
| Архитектура | x86-64 или arm64 — свой пакет |
| Версия пакета | `<версия MonoServer>.rNN`, например `1.0.39.r01` (для 1.0.38 — `1.0.38.r03`) |
| Идентификатор в ADM | `monoserver` |
| Веб-порт | **8099/TCP** |

## Установка

1. Скачайте `MonoServer-ASUSTOR-x64.apk` (или `-arm64.apk`) из
   [последнего релиза](https://github.com/cash94/MonoServer/releases/latest).
2. Войдите в веб-интерфейс NAS под учётной записью администратора.
3. Откройте **App Central → Управление → Установка вручную**.
4. Выберите `.apk` и подтвердите установку (App Central предупредит, что пакет не
   из каталога ASUSTOR).
5. Дождитесь окончания установки — служба запустится сама.
6. Откройте ярлык **MonoServer** на рабочем столе ADM или адрес:

   ```text
   http://IP_ВАШЕГО_NAS:8099/
   ```

   Например, если у NAS адрес `192.168.1.42` — `http://192.168.1.42:8099/`.

Обновление — так же: новый `.apk` через «Установка вручную» поверх прежнего;
данные остаются.

### Переход с неофициального пакета

Неофициальный `MonoServer_1.0.38.r0N_AS5304T_8099.apk` (сетевой установщик) — тот
же идентификатор `monoserver`, тот же порт 8099 и каталог `/volume1/MonoServer`.
Ставьте этот пакет **поверх**, прежний не удаляйте: база, настройки и пароль
сохранятся. Перед этим скопируйте `/volume1/MonoServer` в надёжное место. Версия
этого пакета для 1.0.38 — `1.0.38.r03`, выше неофициальных `r01`/`r02`, поэтому
App Central примет его как обновление. Службу, запущенную прежним пакетом, новый
найдёт и остановит сам (по `/proc`).

## Первый вход

Веб-интерфейс при первом входе просит код первоначальной настройки. Подключитесь
к NAS по SSH и выполните:

```sh
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh code
```

Затем задайте пароль пользователя `admin` в веб-интерфейсе.

## Управление из SSH

Команды выполняются на NAS от администратора (`sudo -i` или `root`).

```sh
# Состояние службы, версия и адрес
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh status

# Запустить / остановить / перезапустить
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh start
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh stop
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh restart

# Журнал в реальном времени (Ctrl+C — выход)
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh log
```

### Обновить только программу

Скачать последний релиз MonoServer для Linux (x64 или arm64 — по NAS) с GitHub и
перезапустить службу:

```sh
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh update
```

Заменяется только программа, версия пакета в App Central остаётся прежней.
Следующая установка `.apk` вернёт программу из пакета. Обычно проще поставить
новый `.apk`.

### Другой порт

Порт 8099 занят — запишите другой в файл и перезапустите службу:

```sh
echo 8098 > /volume1/MonoServer/monoserver.port
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh restart
```

Ярлык на рабочем столе ADM при этом ведёт по-прежнему на 8099 — открывайте
`http://IP_NAS:8098/` вручную.

## Где что лежит

| Путь | Назначение |
| --- | --- |
| `/usr/local/AppCentral/monoserver/` | Установленное приложение |
| `/usr/local/AppCentral/monoserver/runtime/MonoServer` | Программа MonoServer (.NET внутри) |
| `/usr/local/AppCentral/monoserver/bin/monoserverctl.sh` | Управление службой |
| `/volume1/MonoServer/` | Данные и настройки |
| `/volume1/MonoServer/monoserver.log` | Журнал (больше 5 МБ — в `monoserver.log.1`) |
| `/volume1/MonoServer/log/` | Журнал самой службы: этот и прошлый запуск |
| `/var/run/monoserver/` | PID-файлы сторожа и службы (пропадают при перезагрузке — не страшно) |

Для резервной копии достаточно папки `/volume1/MonoServer`. При удалении
приложения она не удаляется — сотрите вручную, если не нужна.

## Если веб-интерфейс не открывается

1. Проверьте, что приложение запущено в App Central.
2. Состояние службы:

   ```sh
   /usr/local/AppCentral/monoserver/bin/monoserverctl.sh status
   ```

3. Ошибки в журнале:

   ```sh
   tail -n 100 /volume1/MonoServer/monoserver.log
   ```

4. Порт **8099/TCP** не занят другим приложением и не закрыт брандмауэром ADM.

### В ADM выключено, но MonoServer работает

```sh
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh stop
ps -ef | grep '[M]onoServer'
```

`stop` находит процесс по `/proc` и без PID-файла. Если в `ps` что-то осталось —
это MonoServer с другим каталогом данных (не этот пакет). Не останавливайте всё
подряд через `killall`.

`status` пишет «без сторожа» — служба работает, но после падения сама не
поднимется (так бывает, если её запускал прежний пакет): `monoserverctl.sh restart`.

### ADM пишет: «в общей веб-папке уже есть папка monoserver»

Ошибка — про регистрацию веб-папки приложения в ADM, а не про данные. Своей
веб-папки этот пакет не создаёт (веб-интерфейс отдаёт сама служба на 8099), такая
папка обычно остаётся от другого пакета или сайта. **Не удаляйте
`/volume1/MonoServer`** — там база и настройки. Сначала посмотрите, что это:

```sh
ls -ld /share/Web/monoserver /volume1/Web/monoserver 2>/dev/null
readlink -f /share/Web/monoserver 2>/dev/null
/usr/local/AppCentral/monoserver/bin/monoserverctl.sh status
```

Не переименовывайте и не удаляйте папку `monoserver` в Web, пока не ясно, чья
она: часть другого сайта или ссылка, оставшаяся от прежнего пакета. Сохраните
вывод команд и проверьте запись приложения в App Central.

## Безопасность

Не открывайте порт `8099` в интернет напрямую. Для доступа извне — VPN или
обратный прокси с HTTPS и аутентификацией.

## Сборка

Пакет собирает `tools/build-asustor.py` из архивов Linux (`dist/MonoServer-linux-x64.tar.gz`,
`-arm64.tar.gz`), которые делает `tools/build.py --linux`:

```sh
python3 tools/build.py --linux
python3 tools/build-asustor.py            # → dist/MonoServer-ASUSTOR-x64.apk, -arm64.apk
```

Без .NET — из архивов готового релиза: положите их в `dist/` и запустите
`python3 tools/build-asustor.py --version 1.0.38`. Ревизия пакета без смены
MonoServer — `--revision 3` (→ `1.0.38.r03`).

В CI пакеты собираются сами:

- `.github/workflows/build.yml` — на каждый тег `v*` вместе с остальными сборками,
  `.apk` прикладываются к релизу;
- `.github/workflows/asustor.yml` — при изменениях `asustor/` или
  `tools/build-asustor.py` в `main`: пакеты из архивов последнего релиза, без
  сборки .NET (около минуты), в артефакт `MonoServer-ASUSTOR`. Ручной запуск
  (Actions → asustor → Run workflow) с флажком «Приложить пакеты к этому релизу»
  добавляет `.apk` к уже вышедшему релизу; при пересборке поднимайте «Ревизию пакета»,
  иначе App Central не примет её как обновление.

Файлы пакета — `asustor/`: `CONTROL/` (config.json, иконка 90×90, скрипты App
Central) и `bin/monoserverctl.sh`.
