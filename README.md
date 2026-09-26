# FileManager

Веб-доступ к файловой системе Linux: ASP.NET Core 10 Web API + Angular 22, раздаваемый тем же
Kestrel, состояние — во внешнем PostgreSQL.

Приложение работает на Linux-хосте как root-агент: аутентифицирует пользователей по их системным
паролям (PAM), выполняет файловые операции **от имени вошедшего пользователя** (имперсонация
`setresuid`/`setresgid`/`setgroups`) и хранит настройки доступа там, где это принято в Linux —
в группах, `/etc/sudoers.d` и POSIX ACL.

---

## 1. Возможности (по требованиям)

| # | Требование | Реализация |
|---|---|---|
| 1 | Вход + история навигации в PostgreSQL | `POST /api/auth/login` (PAM/`/etc/shadow`), HttpOnly cookie `fm_session`, сессии и `navigation_history` в PostgreSQL |
| 1a | Строго одна активная сессия на пользователя | Второй вход **отклоняется** (`409 already-signed-in`), пока пользователь не выполнит logout; одна сессия = одна вкладка (`409 tab-conflict`); закрытие страницы выполняет принудительный logout; администратор из root-группы может принудительно закрыть зависшую сессию |
| 2 | Просмотр ФС хоста с правами пользователя | Все операции ФС идут на выделенных потоках после `setgroups` → `setresgid` → `setresuid`; видимость определяет ядро |
| 3 | Список пользователей из хоста, пустой пароль игнорируется | `/etc/passwd` + `/etc/shadow` + `/etc/group`; отбрасываются пустой/заблокированный пароль (`*`, `!`, `!!`) и `nologin`-шеллы |
| 4 | Регистрация пользователя с настройками доступа к ФС | `useradd` + `chpasswd` + `usermod -aG` + `/etc/sudoers.d/<user>` (проверка `visudo -cf`) + `setfacl` на каталоги |
| 5 | Создание `a-admin`, если нет рабочего root-аккаунта | `AdminBootstrapService` при старте: если нет uid 0 / члена admin-группы с рабочим паролем — создаётся `a-admin` (пароль из `FileManager:Bootstrap:AdminPassword`, в Docker — из `.env`) |
| 6 | Загрузка/скачивание/удаление/копирование | upload (в т.ч. папками), download, `tar.gz` для каталогов, рекурсивное удаление, серверное копирование/перемещение, mkdir/rename |

Дополнительно: аудит действий в PostgreSQL, блокировка перебора пароля, страница администрирования
пользователей, история посещённых папок, локализация интерфейса ru/en, строго одна активная сессия и
одна вкладка на пользователя.

---

## 2. Состав репозитория

```
src/FileManager.Core/     домен: Linux (passwd/shadow/group, PAM, crypt), имперсонация, ФС,
                          useradd/sudoers/ACL, bootstrap, EF Core, PostgreSQL
src/FileManager.Api/      хост: Minimal API, cookie-аутентификация, endpoints, раздача SPA
tests/FileManager.Core.Tests/  юнит-тесты (SQLite, подделки внешних команд)
tests/FileManager.Api.Tests/   интеграционные HTTP-тесты (WebApplicationFactory + SQLite)
web/                      Angular 22 SPA (ru + en), сборка в src/FileManager.Api/wwwroot
Dockerfile                стадии base/web/build/publish/final (base использует Visual Studio)
docker-compose.yml        PostgreSQL + приложение (dev/прод запуск и отладка из VS)
docker-compose.dcproj     проект оркестрации Visual Studio («Docker Compose»)
.dockerignore, .env(.example)
```

---

## 3. Модель безопасности

* **Процесс работает как root.** Это осознанное следствие модели (управление пользователями,
  чтение `/etc/shadow`, смена учётных данных). Компенсации: bind на loopback либо TLS-прокси перед
  сервисом, HttpOnly+SameSite=Strict cookie, проверка `Origin`, обязательный заголовок
  `X-Requested-With` для мутаций, rate limit и журнал неудачных входов, аудит админ-действий.
* **Имперсонация.** Каждая файловая операция выполняется на отдельном выделенном потоке, который
  переключает **эффективные** учётные данные — именно их ядро использует для проверки прав на файлы
  (fsuid/fsgid + дополнительные группы). Реальный и сохранённый uid остаются 0, поэтому поток
  возвращается в root в `finally`; это механизм принудительного применения прав для доверенного кода
  приложения, а не песочница против исполнения произвольного кода. Смена кредов сбрасывает у процесса
  флаг `dumpable`, поэтому после каждого возврата в root он восстанавливается (`prctl(PR_SET_DUMPABLE)`)
  — иначе приложение нельзя ни привязать отладчиком, ни снять дампом.
* **Почему прямые syscall-ы, а не `setresuid` из glibc.** Обёртки glibc для setxid-семейства
  реализуют POSIX-семантику: вызов `setresuid` из одного потока меняет креды **всему процессу**
  (проверено на glibc 2.36: поток-наблюдатель видел чужой uid и терял доступ к `/etc/shadow`).
  Поэтому переключение идёт через `syscall(SYS_setresuid/SYS_setresgid/SYS_setgroups)`, которые
  действуют только на вызывающий поток (номера syscall-ов заданы для x86-64 и aarch64). Это
  ключевое отличие от наивной реализации: иначе запрос одного пользователя мог бы выполняться с
  правами другого.
* **Проверка изоляции при старте.** Прежде чем обслуживать запросы, сервис запускает фоновый
  поток-наблюдатель и несколько раз переключает креды на рабочем потоке: если наблюдатель увидит
  чужой эффективный uid (или архитектура не поддерживается), сервис **отказывается запускаться**
  (fail closed) — иначе один пользователь мог бы получить доступ от имени другого. Значение uid
  читается тем же прямым syscall-ом (ядро), а не кэшируемым `geteuid` из glibc.
* **«Права root»** = uid 0 **или** членство в группе из `FileManager:AdminGroups` (по умолчанию
  `sudo`, `wheel`). Для таких пользователей операции идут от root, без имперсонации. При выдаче
  sudo-прав аккаунт дополнительно добавляется в admin-группу — иначе приложение не распознало бы его
  как администратора.
* **Пароли** никогда не логируются и не попадают в argv: `chpasswd` получает данные только через
  stdin; в API возвращается лишь флаг `hasUsablePassword`. В PostgreSQL хранится SHA-256 cookie-токена,
  сам токен — только в браузере.
* **Проверка пароля.** По умолчанию (`FileManager:Auth:Provider=shadow`) приложение само сравнивает
  пароль с записью в `/etc/shadow` через `crypt_r(3)` — поддерживаются SHA-512 (`$6$`), yescrypt
  (`$y$`, штатный метод `chpasswd` в Debian) и другие форматы libcrypt. Дополнительно проверяются
  поля старения: истёкшая учётная запись (`expire`), истёкший пароль (`lastchg + max`) и
  деактивация по неактивности. Провайдер `pam` включается явно (`Provider=pam`) — он нужен, если на
  хосте пароли проверяются не через `/etc/shadow` (LDAP/SSSD) или используются модули вроде
  `pam_faillock`; при недоступности PAM приложение возвращается к проверке по `/etc/shadow`.
* **Пути.** Все пути нормализуются (`Path.GetFullPath`) и обязаны лежать внутри
  `FileManager:BrowseRoots`; корневые каталоги защищены от удаления и переименования.
* **Строго одна сессия и одна вкладка на пользователя.** Одновременная работа одного аккаунта из
  разных браузеров запрещена: пока есть живая сессия, второй вход **отклоняется** с `409` и кодом
  `already-signed-in` (в ответе есть `lastSeenUtc` — когда другая сессия была активна). Аккаунт
  освобождается только явным `logout`, истечением сессии или закрытием страницы. Внутри одного
  браузера сессия принадлежит **одной вкладке**: SPA присылает `X-Tab-Id`, первая вкладка забирает
  аренду (`sessions.ActiveTabId`), остальные получают `409 tab-conflict` и блокирующий оверлей.
  Гарантия «одна живая сессия» дополнительно закреплена в БД частично-уникальным индексом
  `sessions(UserId) WHERE RevokedUtc IS NULL`, поэтому две конкурентные попытки входа не могут обе
  остаться активными.
* **Разблокировка администратором.** Если браузер упал или был закрыт без события `pagehide`, сессия
  остаётся живой и блокирует вход из другого браузера до истечения срока. Пользователь из root-группы
  (uid 0 или член `FileManager:AdminGroups`) видит такие сессии в разделе «Пользователи»
  (`GET /api/sessions` — пользователь, время входа, последняя активность, IP, браузер, признак
  «моя текущая сессия») и может принудительно завершить любую из них
  (`DELETE /api/sessions/{userName}`, причина отзыва `session-closed-by-admin`, аудит
  `session.admin-closed`). Владелец закрытой сессии получает `401` с кодом
  `session-closed-by-admin`, аккаунт сразу доступен для нового входа. Не-администраторам эти
  эндпоинты недоступны (`403`), закрытие собственной сессии администратора выводит его на страницу
  входа.
* **Принудительный logout при закрытии страницы.** Обработчик `pagehide` отправляет
  `navigator.sendBeacon('/api/auth/page-closed?tabId=...')`; сервер помечает сессию закрытой и после
  `PageCloseGraceSeconds` (по умолчанию 10 с) считает её завершённой — аккаунт снова доступен для
  входа из другого браузера. Перезагрузка страницы (F5) попадает в окно grace и отменяет закрытие,
  поэтому обычная работа не страдает. Смена пароля завершает сессии аккаунта (администратор,
  меняющий свой пароль, остаётся в текущей вкладке), выход помечает сессию `signed-out`. Все отказы
  входа (`login.refused`) и закрытия страниц (`session.page-closed`) попадают в аудит.

---

## 4. Конфигурация

Основные ключи (`appsettings.json`, переопределяются переменными окружения через `__`):

| Ключ | По умолчанию | Назначение |
|---|---|---|
| `Database:Provider` | `postgres` | `postgres` или `sqlite` (последний — только для локальной отладки/тестов) |
| `Database:ConnectionString` | `Host=localhost;...` | Строка подключения |
| `Database:MigrateOnStartup` | `true` | Миграции EF Core при старте (для SQLite — `EnsureCreated`) |
| `FileManager:BrowseRoots` | не задано → `/` | Разрешённые корни навигации (в Docker — `/data`, `/home`). Пустой список означает `/` и пишет предупреждение в лог; в `appsettings.json` он намеренно пуст, потому что биндер конфигурации **дописывает** значения в существующие коллекции, и непустое значение по умолчанию пережило бы любой переопределённый массив |
| `FileManager:AdminGroups` | `["sudo","wheel"]` | Группы с root-правами |
| `FileManager:AllowNonRootDev` | `false` | Разрешить запуск без root (только отладка) |
| `FileManager:Impersonation:Enabled` | `true` | Имперсонация |
| `FileManager:Impersonation:Threads` | `0` (auto) | Число выделенных потоков |
| `FileManager:Auth:Provider` | `shadow` | Как проверяется пароль: `shadow` — сами через `crypt_r(3)` по `/etc/shadow` (по умолчанию), `pam` — через PAM-стек с fallback на `/etc/shadow` |
| `FileManager:Auth:PamService` | `filemanager` | Имя сервиса в `/etc/pam.d` |
| `FileManager:Auth:SessionIdleMinutes` | `480` | Скользящий срок жизни сессии |
| `FileManager:Auth:SessionAbsoluteHours` | `168` | Абсолютный предел сессии |
| `FileManager:Auth:PageCloseGraceSeconds` | `10` | Окно после закрытия страницы, в течение которого перезагрузка сохраняет сессию |
| `FileManager:Auth:ExposeUserList` | `false` | Показывать список логин-способных аккаунтов на странице входа |
| `FileManager:Auth:MinPasswordLength` | `8` | Минимальная длина пароля при регистрации |
| `FileManager:Auth:MinimumUid` | `1000` | Ниже — системные аккаунты |
| `FileManager:Bootstrap:Enabled` | `true` | Требование 5 |
| `FileManager:Bootstrap:AdminUser` / `AdminPassword` | `a-admin` / `passwordDemand` | Аварийный администратор |
| `FileManager:Acl:Enabled` | `true` | Использование `setfacl`/`getfacl` |
| `FileManager:Upload:MaxRequestSizeBytes` | 4 GiB | Лимит размера запроса |
| `FileManager:Linux:*` | `/etc/passwd`, `/etc/shadow`, `/etc/group`, `/etc/sudoers.d`, `useradd`… | Пути и имена утилит (используются тестами) |

Порт задаётся `ASPNETCORE_URLS` (Dockerfile и compose задают `http://0.0.0.0:8080`; локально —
`launchSettings.json`). Ключ `Urls` в `appsettings.json` намеренно отсутствует: он перекрывал бы
переменную окружения.

Секреты (пароль администратора, пароль PostgreSQL) задавайте переменными окружения или в `.env`
(файл в `.gitignore`).

---

## 5. Запуск в Docker

```bash
cp .env.example .env      # задайте POSTGRES_PASSWORD и FM_BOOTSTRAP_ADMIN_PASSWORD
docker compose up --build
```

Открыть <http://localhost:8080/> → вход `a-admin` / пароль из `.env` (`passwordDemand` по умолчанию).

Что происходит:

* `db` — PostgreSQL 17 с healthcheck, данные в томе `db-data`;
* `app` — контейнер сам является «хостом»: его `/etc/passwd`, `/etc/shadow`, `/etc/group`,
  `/etc/sudoers.d` и ACL управляются приложением; навигация ограничена томами `/data` и `/home`;
* entrypoint в `Dockerfile` создаёт `a-admin` (useradd + chpasswd + группа `sudo` + drop-in
  `/etc/sudoers.d/a-admin`) из переменных `FM_BOOTSTRAP_ADMIN_USER`/`FM_BOOTSTRAP_ADMIN_PASSWORD`;
  встроенный bootstrap приложения при этом видит рабочего администратора и ничего не делает.
  Образ ставит пакет `sudo` (он и создаёт `/etc/sudoers.d`) и `libgssapi-krb5-2` (иначе Npgsql
  печатает безвредное `Cannot load library libgssapi_krb5.so.2`).

Чтобы открыть реальные каталоги хоста, добавьте их в `volumes` сервиса `app` и в
`FileManager__BrowseRoots__*`.

### Если контейнер падает с `exec .../filemanager-entrypoint.sh: no such file or directory`

Это не отсутствующий файл, а **CRLF в переводе строк**. Windows-checkout (git `core.autocrlf=true`)
или копирование `Dockerfile` через редактор с CRLF превращает shebang в `#!/bin/sh\r`, ядро ищет
интерпретатор `/bin/sh\r` и возвращает `ENOENT` — docker печатает именно такое сообщение.

Что уже сделано в образе: скрипт entrypoint нормализуется на этапе сборки (`tr -d '\r'`), сборка
**падает с понятной ошибкой**, если проверки shebang/содержимого/отсутствия `\r` не проходят, а
`ENTRYPOINT ["/bin/sh", "..."]` не зависит от shebang. Дополнительно `.gitattributes` фиксирует LF в
рабочем дереве.

Что сделать у себя, если ошибка всё же появилась:

```bash
# 1. Проверить причину (в репозитории)
git config --get core.autocrlf        # true -> checkout с CRLF
file Dockerfile                       # "... with CRLF line terminators" подтверждает диагноз

# 2. Пересоздать файлы с LF (после обновления .gitattributes)
git config core.autocrlf false
git rm --cached -r . >/dev/null && git reset --hard

# 3. Пересобрать без кэша и запустить
docker compose build --no-cache app && docker compose up

# 4. Убедиться, что внутри образа скрипт без CR
docker compose run --rm --entrypoint /bin/sh app -c \
  "head -1 /usr/local/bin/filemanager-entrypoint.sh | od -c | head -1"   # ожидается: # ! / b i n / s h  \n
```

### Если в логах `cannot create /etc/sudoers.d/...: Directory nonexistent`

В образе нет каталога `/etc/sudoers.d` — его создаёт пакет `sudo`. Актуальный `Dockerfile` ставит
`sudo` и дополнительно делает `install -d -m 0755 /etc/sudoers.d` (и в образе, и в самом entrypoint
перед записью drop-in). Если вы удаляли пакеты из образа, верните `sudo` или добавьте эту строку.

### Если сервис не стартует с «credential switching …» в логе

Сервис самопроверяется при старте и отказывается работать, если переключение кредов задевает чужие
потоки (см. §3): это защита от ситуации, когда запрос одного пользователя выполнился бы от имени
другого. Актуальный код использует прямые syscall-ы и проходит проверку; ошибка означает, что вы
запускаете сборку со старым кодом (обрывки кэша) или архитектуру вне x86-64/aarch64. Проверьте
`docker compose build --no-cache app` и `uname -m`.

### Если docker падает с `ports are not available … forbidden by its access permissions`

Это Windows: выбранный порт хоста попал в диапазон, зарезервированный Hyper-V/WSL (обычно
49152–65535) или уже занят. Проверить и выбрать порт вне диапазона:

```powershell
netsh interface ipv4 show excludedportrange protocol=tcp
# вариант: освободить резервирование на время
net stop winnat ; docker compose up ; net start winnat
```

В поставляемом `docker-compose.yml` наружу публикуется только порт приложения (`8080`, ниже
зарезервированного диапазона), база данных — нет, поэтому ошибка возможна лишь для ваших
собственных пробросов портов.

### Если VS не может привязаться к процессу: «vsdbg has insufficient privileges … must be running with root permissions»

Две независимые причины, обе уже закрыты в проекте:

1. **Процесс приложения становился non-dumpable.** Любая смена эффективных учётных данных — а её
   делает имперсонация на каждом файловом запросе — сбрасывает у процесса флаг `dumpable`, и ядро
   само его **не** возвращает:

   ```
   старт:                            PR_GET_DUMPABLE = 1
   после setresuid(65534):                                        0
   после возврата в root:                                         0   ← ядро не восстанавливает
   после prctl(PR_SET_DUMPABLE, 1):                               1   ← делает наш Restore()
   ```

   Non-dumpable процесс нельзя ни attach-ить отладчиком, ни снять `dotnet-dump`/дампом. Поэтому
   `ImpersonationExecutor.Restore()` теперь вызывает `Libc.SetDumpable()`.

2. **Docker по умолчанию запрещает `ptrace`.** Дефолтный seccomp-профиль разрешает `ptrace` только с
   `CAP_SYS_PTRACE`, поэтому attach к **уже запущенному** процессу в контейнере не проходит. В
   `docker-compose.yml` сервису `app` добавлено `cap_add: [SYS_PTRACE]`, а в профиле «Docker»
   (`Properties/launchSettings.json`) — `"containerRunArguments": "--cap-add=SYS_PTRACE"`.
   Для продакшена строку `cap_add` можно убрать.

Важное про сам сценарий: **F5 на профиле «Docker Compose» attach не требует** — VS сам запускает
приложение под `vsdbg`, поэтому достаточно этой возможности. «Attach to Process» нужен только когда
контейнер уже работает; тогда нужны оба пункта выше. Если приложение поднято вручную
(`docker compose up`), перед F5 лучше сделать `docker compose down`: иначе VS будет привязываться к
процессу, запущенному entrypoint'ом.

### Отладка в Docker из Visual Studio 2026

Проект подготовлен так, чтобы отлаживать API прямо внутри Linux-контейнера (точки останова, шаг с
заходом, локальные переменные), а PostgreSQL поднимался рядом сервисом.

Что для этого уже есть в репозитории:

| Файл | Роль |
|---|---|
| `Dockerfile` | стадии в конвенции Visual Studio: `base` (runtime + PAM/acl/passwd/sudo) — на её основе VS собирает отладочный контейнер, `web`/`build`/`publish`/`final` — продакшен-образ. `final` намеренно последняя: `docker compose build` собирает её, а VS передаёт `--target base` |
| `docker-compose.yml` | `db` + `app` (образ `filemanager-app`, контекст — корень репозитория; порты БД наружу не публикуются) |
| `docker-compose.dcproj` | проект оркестрации VS («Docker Compose»); добавлен в `FileManager.slnx` — CLI помечает его как *not selected for building*, поэтому `dotnet build`/`dotnet test` не ломаются |
| `src/FileManager.Api/FileManager.Api.csproj` | `DockerfileFile=..\..\Dockerfile`, `DockerComposeProjectPath=..\..\docker-compose.dcproj`, `DockerDefaultTargetOS=Linux` |
| `Properties/launchSettings.json` | профили **Docker Compose** (рекомендуется) и **Docker** (одиночный проект) |
| `.dockerignore` | контекст сборки без `bin`/`obj`/`node_modules`/`.env`/`wwwroot` |

Порядок действий:

```bash
cp .env.example .env          # один раз: пароли Postgres и a-admin
```

1. Открыть `FileManager.slnx` в Visual Studio 2026 (нужен рабочий процесс «ASP.NET и веб-разработка»;
   Docker Desktop настроен на **Linux containers**).
2. В выпадающем списке запуска выбрать **Docker Compose** и нажать F5.

Что происходит при этом: VS собирает стадию `base`, монтирует свежую сборку
(`src/FileManager.Api/bin/Debug/net10.0`) в `/app` отладочного контейнера, запускает `dotnet
FileManager.Api.dll` под `vsdbg` и поднимает `db` без отладки. Точки останова работают в
`FileManager.Api` и `FileManager.Core`.

Особенности отладочного контейнера:

* приложение видит **свои** `/etc/passwd`, `/etc/shadow`, `/etc/group`, `/etc/sudoers.d` (это и есть
  «хост» для контейнера), тома `/data` и `/home`, а также сервис `db` в общей сети compose;
* имперсонация работает и здесь (переключение кредов прямыми syscall-ами), поэтому файлы,
  созданные через API, принадлежат uid вошедшего пользователя;
* `a-admin` создаёт встроенный bootstrap приложения (в compose включены
  `FileManager__Bootstrap__*`), пароль — `FM_BOOTSTRAP_ADMIN_PASSWORD` из `.env`;
* для подробных ошибок задайте в `.env` `ASPNETCORE_ENVIRONMENT=Development` (в этом режиме
  доступен OpenAPI: `/openapi/v1.json`). Провайдер БД и строка подключения заданы в compose
  явно, поэтому Development не переключит приложение на SQLite;
* горячая перезагрузка: после правок снова F5 (образ в режиме отладки ставит
  `DOTNET_USE_POLLING_FILE_WATCHER=1`).

Про SPA в режиме отладки: `web`-стадия (Angular) попадает только в продакшен-стадию `final`, а в
отладочный контейнер монтируется лишь вывод `dotnet build`, поэтому `wwwroot` там отсутствует —
API отлаживается напрямую, а интерфейс удобнее запускать на хосте:

```bash
cd web && npm start          # dev-server на :4200 проксирует /api на http://localhost:8080
```

Это даёт полноценный UI поверх API, работающего в контейнере.

Профиль **Docker** (одиночный проект) — альтернатива, если нужен только API-контейнер без compose.
Он работает автономно на SQLite (файл `/tmp/filemanager-debug.db` внутри контейнера), поэтому не
требует ни запущенного `db`, ни публикации портов на хост:

```jsonc
// Properties/launchSettings.json, профиль "Docker"
"Database__Provider": "sqlite",
"Database__ConnectionString": "Data Source=/tmp/filemanager-debug.db"
```

Если в этом профиле всё же нужен PostgreSQL из compose, опубликуйте его порт на хост, взяв порт
**ниже 49152** (диапазон 49152–65535 на Windows зарезервирован Hyper-V/WSL, и docker падает с
`An attempt was made to access a socket in a way forbidden by its access permissions`):

```bash
# docker-compose.local-db.yml (создаётся только при необходимости)
# services:
#   db:
#     ports: [ "127.0.0.1:5433:5432" ]
docker compose -f docker-compose.yml -f docker-compose.local-db.yml up -d db
```

…и укажите в профиле `"Database__ConnectionString": "Host=host.docker.internal;Port=5433;Database=filemanager;Username=filemanager;Password=<POSTGRES_PASSWORD>"`.

Профиль берёт `DockerfileFile` из корня репозитория. Если VS выберет контекст сборки не от корня,
сборка упадёт на `COPY global.json ...` — тогда используйте профиль «Docker Compose».

---

## 6. Развёртывание на самом хосте (без Docker)

```bash
# зависимости: acl (setfacl), passwd (useradd/chpasswd), libpam-modules, PostgreSQL
sudo apt-get install -y acl passwd libpam-modules postgresql

sudo install -m 0644 /dev/stdin /etc/pam.d/filemanager <<'EOF'
auth      required   pam_unix.so
account   required   pam_unix.so
password  required   pam_unix.so
session   required   pam_unix.so
EOF

dotnet publish src/FileManager.Api/FileManager.Api.csproj -c Release -o /opt/filemanager
sudo cp -r src/FileManager.Api/wwwroot /opt/filemanager/wwwroot   # результат сборки web/
```

Юнит systemd (`/etc/systemd/system/filemanager.service`):

```ini
[Unit]
Description=FileManager web file access
After=network-online.target postgresql.service

[Service]
Type=simple
User=root
WorkingDirectory=/opt/filemanager
Environment=ASPNETCORE_URLS=http://127.0.0.1:8080
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Database__ConnectionString=Host=localhost;Database=filemanager;Username=filemanager;Password=CHANGE_ME
Environment=FileManager__Bootstrap__AdminPassword=CHANGE_ME
ExecStart=/usr/bin/dotnet /opt/filemanager/FileManager.Api.dll
Restart=on-failure
NoNewPrivileges=no

[Install]
WantedBy=multi-user.target
```

Перед публичным доступом поставьте TLS-прокси (nginx/Caddy) и включите
`FileManager__Auth__RequireSecureCookie=true`.

**Важно:** при первом запуске на хосте, где нет ни одного root-аккаунта с паролем, сервис создаст
`a-admin`. Пароль по умолчанию `passwordDemand` — смените его сразу после первого входа.

---

## 7. Разработка и тестирование без Docker

```bash
export NUGET_PACKAGES=$PWD/.nuget/packages DOTNET_CLI_HOME=$PWD/.dotnet-home
export npm_config_cache=$PWD/.npm-cache

dotnet build FileManager.slnx
dotnet test                      # 81 юнит-тест + 35 интеграционных HTTP-тестов

cd web && npm ci && npm run build # сборка SPA для ru и en
```

Локальный запуск API в режиме разработки (SQLite, имперсонация и bootstrap отключены —
`appsettings.Development.json`):

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/FileManager.Api
```

### Что именно проверяется

* **Юнит-тесты** (`tests/FileManager.Core.Tests`, 114 тестов): разбор `passwd/group/shadow` и фильтр
  «пустой/заблокированный пароль + nologin + uid < 1000»; проверка пароля по реальным хэшам
  SHA-512 (`$6$`) и **yescrypt** (`$y$`), отказ для запертых аккаунтов, истёкшего пароля и истёкшей
  (или деактивированной) учётной записи, отказ при отсутствии записи в `/etc/shadow`; значения по
  умолчанию для коллекций опций
  (защита от «прилипания» дефолта при переопределении массива); `PathPolicy` (traversal, несколько
  корней, пустая конфигурация); `SudoersService` (содержимое, режим 0440, отклонение `visudo`, откат);
  `HostUserAdminService` (последовательность `useradd`/`chpasswd`/`usermod`/`groupadd`, пароль только
  через stdin, ACL, полный откат при сбое, `userdel`, отказ для root); `AdminBootstrapService`
  (создание, «ремонт», отказ при пустом пароле); `FileSystemService` (листинг, конфликтные политики
  копирования, копирование каталога внутрь себя, перемещение, рекурсивное удаление, staged-upload,
  `tar.gz`, защита корней); **сессии в реальном SQLite** (`SessionService`: второй вход отклоняется,
  а не вытесняет первый; ровно одна живая сессия на пользователя и частично-уникальный индекс,
  отвергающий вторую живую строку; аренда вкладки — первая вкладка владеет сессией, вторая получает
  конфликт, клиенты без `X-Tab-Id` не ограничены; закрытие страницы завершает сессию после окна
  grace и освобождает аккаунт, перезагрузка внутри окна отменяет закрытие, а чужая вкладка не может
  завершить сессию; отзыв при смене пароля сохраняет текущий клиент; коды `expired`/`signed-out`);
  **реальная имперсонация** (`setresuid`/`setgroups` на выделенных потоках,
  файл внутри scope принадлежит имперсонированному пользователю, восстановление root после исключения
  и проверка изоляции платформы).
* **Интеграционные тесты** (`tests/FileManager.Api.Tests`, 44 теста): поднимается настоящий хост с
  SQLite и файлами-фикстурами вместо `/etc/*` — проверяются 401/403/404/409, cookie-сессия, отзыв
  сессии при исчезновении пароля, **строгое ограничение «один браузер»** (второй вход получает `409
  already-signed-in` с `lastSeenUtc`, сессии и аудит `login.refused` проверяются в БД напрямую; после
  logout аккаунт снова доступен), **одна вкладка на браузер** (`409 tab-conflict` для второй вкладки,
  первая продолжает работать), **принудительный logout при закрытии страницы** (beacon без
  `X-Requested-With`, перезагрузка внутри grace сохраняет сессию, после окна grace аккаунт свободен),
  вход разных пользователей одновременно, история навигации,
  подъём/скачивание/копирование/переименование/удаление, `tar.gz`, обязательный
  `X-Requested-With`, отклонение чужого `Origin`, защита browse-root, admin-only доступ к управлению
  пользователями и отсутствие утечки паролей в ответах.
* **Сквозные smoke-тесты** против реально запущенного `dotnet publish`-артефакта, без Docker:
  * 33 проверки базового сценария: редирект `/` → `/ru/`, отдача русской и английской сборок,
    SPA-fallback для deep-link, 404 для отсутствующих ассетов, вход, листинг с метаданными,
    mkdir/upload/download/`tar.gz`/copy (+409 на конфликт)/rename/рекурсивное удаление, 403 вне
    browse-root, 403 без `X-Requested-With`, 403 с чужого `Origin`, запрет удаления browse-root,
    запись истории в БД, 403 для не-админа и 200 для админа в `/api/users`, отсутствие хэшей в
    ответах, отзыв сессии после logout;
  * 22 проверки строгого режима: вход из браузера A, затем из B — B получает `409
    already-signed-in` с временем активности другой сессии и без cookie; A продолжает работать (в том
    числе файловые операции); после logout A браузер B входит; вторая вкладка того же браузера
    получает `409 tab-conflict`, а владелец — нет; beacon `page-closed` без `X-Requested-With`
    принимается, перезагрузка внутри grace сохраняет сессию, после окна grace сессия завершается
    (`session-page-closed`) и аккаунт свободен; другой аккаунт входит независимо и его второй браузер
    тоже отклоняется;
  * 18 проверок разблокировки администратором: «упавший» браузер оставляет сессию, новый вход
    отклоняется; администратор видит её в `GET /api/sessions` (последняя активность, IP, признак
    чужой сессии) и закрывает через `DELETE /api/sessions/{userName}`; старый cookie получает `401
    session-closed-by-admin`; аккаунт сразу доступен для входа; обычному пользователю оба эндпоинта
    запрещены (`403`); повторное закрытие и неизвестный пользователь дают `404`; администратор может
    закрыть и свою сессию;
  * 9 проверок имперсонации с **включённым** `FileManager:Impersonation:Enabled` против реально
    запущенного процесса: сервис стартует (проверка изоляции пройдена), вход пользователя с uid
    65534, листинг и загрузка работают, созданные ядром файл и каталог принадлежат `65534:65534`, а
    root-only файл (0600) скачать нельзя — `403` от ядра, при этом `effectiveAccess` для него
    показан как `---`.
* **Проверка реального входа на настоящем системном пользователе** (выполнялась вручную в этой
  среде): создавался пользователь через `useradd`/`chpasswd`, затем проверялся полный путь
  `HostAuthenticator`. Результат: `crypt_r` успешно проверяет и SHA-512 (`$6$`), и **yescrypt**
  (`$y$` — именно такой хэш пишет `chpasswd` в Debian), а `pam_start`/`pam_authenticate` для того же
  пользователя с тем же верным паролем возвращал `PAM_AUTH_ERR` (7) и через свой сервис, и через
  стандартный `login`. Поэтому провайдер по умолчанию — `shadow`, а не `pam`.
* **Требует настоящего хоста и прав root** (в этой среде не выполнялось): фактическое создание
  системных пользователей через API (`useradd`/`usermod`/`groupdel` вызываются настоящие, но
  проверялись тестами с подстановкой команд), запись в `/etc/sudoers.d`, `setfacl` (в среде нет
  пакета `acl`). Docker-сборка не запускалась (docker CLI в среде отсутствует).

---

## 8. HTTP API (кратко)

| Метод | Путь | Назначение |
|---|---|---|
| POST | `/api/auth/login` | Вход (`{userName, password}`); ставит cookie `fm_session` или отклоняет вход, если аккаунт уже занят (`409 already-signed-in`) |
| POST | `/api/auth/logout` | Выход, отзыв сессии (код `signed-out`) |
| POST | `/api/auth/page-closed?tabId=` | Сигнал от `navigator.sendBeacon` при закрытии страницы; без заголовка `X-Requested-With` |
| GET | `/api/auth/me` | Профиль текущего пользователя |
| GET | `/api/auth/login-options` | Список логин-способных имён (если включено) |
| GET | `/api/system/capabilities` | ОС, root, PAM, shadow, ACL, доступность БД, browse-корни |
| GET | `/api/fs/list?path=` | Листинг каталога (пишется в историю) |
| GET | `/api/fs/download?path=` | Скачать файл |
| GET | `/api/fs/archive?path=` | Скачать каталог как `tar.gz` |
| POST | `/api/fs/upload?path=&overwrite=` | Загрузка файла (multipart `file`, опционально `relativePath`) |
| POST | `/api/fs/mkdir` \| `/rename` \| `/copy` \| `/move` | Операции с записями |
| DELETE | `/api/fs/entry?path=&recursive=` | Удаление |
| GET/POST/DELETE | `/api/history` | История навигации текущего пользователя |
| GET | `/api/users/`, `/api/users/groups` | Список host-пользователей и групп (только админ) |
| GET/POST/PUT/DELETE | `/api/users/{name}` | Просмотр/создание/изменение/удаление пользователя с правами и ACL (только админ) |
| GET | `/api/sessions` | Активные сессии: пользователь, вход, последняя активность, IP, браузер, «моя сессия» (только админ) |
| DELETE | `/api/sessions/{userName}` | Принудительно завершить сессию пользователя (только админ; `404`, если сессии нет) |

Ошибки возвращаются как `ProblemDetails` (`title`, `status`, `detail`). Ответы `401` дополнительно
содержат стабильный `code` (`session-page-closed`, `session-expired`, `signed-out`,
`session-password-changed`, `session-account-unusable`, `unauthenticated`), а конфликты сессии —
`409` с кодом `already-signed-in` (+ `lastSeenUtc`) или `tab-conflict`; по этим кодам SPA показывает
локализованное объяснение. Мутирующие запросы требуют заголовок `X-Requested-With: XMLHttpRequest`
и совпадающий `Origin`; исключение сделано только для двух «beacon»-эндпоинтов
(`/api/auth/logout`, `/api/auth/page-closed`), потому что `sendBeacon` не умеет задавать заголовки, —
проверка `Origin` для них сохраняется.

---

## 9. Миграции EF Core

```bash
dotnet tool restore
dotnet dotnet-ef migrations add <Name> -p src/FileManager.Core -s src/FileManager.Api -o Data/Migrations
```

---

## 10. Известные ограничения

* Docker-сборка в репозитории **не проверялась** в среде разработки (нет docker CLI) — проверьте
  `docker compose up --build` у себя. Проверено без Docker: сборка, 158 автотестов, `dotnet publish`
  (обе локали попадают в `wwwroot`) и четыре сквозных smoke-теста (33 + 22 + 18 + 9 проверок) против
  опубликованного артефакта.
* Строгий режим сессий означает, что при «зависшей» сессии войти из другого браузера нельзя, пока она
  не завершится. Аккаунт освобождают: logout, закрытие страницы (через `PageCloseGraceSeconds`),
  **принудительное закрытие администратором** (раздел «Пользователи» → «Завершить сессию»),
  истечение `SessionIdleMinutes`/`SessionAbsoluteHours` или смена пароля. Если браузер упал или
  закрылся без события `pagehide` (краш, выгрузка ОС, мобильный фон), сессия остаётся живой до
  истечения срока — поэтому либо уменьшайте `SessionIdleMinutes`, либо закрывайте такие сессии вручную
  как администратор.
* Аренда вкладки действует для клиентов, присылающих `X-Tab-Id` (SPA делает это всегда); «сырые»
  API-клиенты без этого заголовка арендой не ограничиваются — ограничение одной сессии при этом
  сохраняется.
* Плавающая проверка `Origin` для двух beacon-эндпоинтов (`/api/auth/logout`, `/api/auth/page-closed`)
  допускает POST без `X-Requested-With` (иначе `sendBeacon` не сработает). Это позволяет стороннему
  сайту инициировать выход — безвредный, но заметный эффект; при необходимости эндпоинт
  `page-closed` можно закрыть заголовком, пожертвовав автоматическим logout при закрытии страницы.
* Переносы сессий между узлами не поддерживаются: приложение рассчитано на один экземпляр на хост,
  а частично-уникальный индекс `sessions(UserId) WHERE RevokedUtc IS NULL` требует общей БД (в
  docker-compose она одна).
* Имперсонация использует прямые syscall-ы и номера вызовов заданы для **x86-64 и aarch64**. На других
  архитектурах происходит откат на обёртки glibc, которые меняют креды всему процессу — проверка
  изоляции при старте это обнаружит и сервис не запустится с включённой имперсонацией (осознанный
  fail closed: иначе запрос одного пользователя мог бы выполниться от имени другого).
* Для управления ACL нужен пакет `acl`; если `setfacl`/`getfacl` отсутствуют, `GET /api/system/capabilities`
  вернёт `aclAvailable: false`, а выдача прав по путям — ошибку 501 **без частичного применения**.
* ACL — это атрибут конкретной файловой системы: если целевой каталог смонтирован без поддержки ACL,
  `setfacl` вернёт ошибку, и она будет показана пользователю.
* Удаление пользователя снимает ACL только с тех путей, которые переданы в `revokePaths`
  (ограничение модели «источник истины — ОС», без дублирования прав в БД). Осиротевшие ACL-записи
  безвредны: они ссылаются на несуществующий uid.
* Один экземпляр приложения на хост: параллельные записи в `/etc/*` не координируются между узлами.
* Массивы в конфигурации (`BrowseRoots`, `AdminGroups`) нельзя «дополнять» через переменные
  окружения без учёта того, что биндер дописывает элементы в существующую коллекцию; поэтому значения
  по умолчанию применяются после биндинга (`FileManagerOptionsDefaults`), а базовый `appsettings.json`
  держит эти массивы пустыми.
