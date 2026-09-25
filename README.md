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
Dockerfile, docker-compose.yml, .env(.example)
```

---

## 3. Модель безопасности

* **Процесс работает как root.** Это осознанное следствие модели (управление пользователями,
  чтение `/etc/shadow`, смена учётных данных). Компенсации: bind на loopback либо TLS-прокси перед
  сервисом, HttpOnly+SameSite=Strict cookie, проверка `Origin`, обязательный заголовок
  `X-Requested-With` для мутаций, rate limit и журнал неудачных входов, аудит админ-действий.
* **Имперсонация.** Каждая файловая операция выполняется на отдельном выделенном потоке, который
  переключает **эффективные** учётные данные (`setgroups`, `setresgid(-1, gid, -1)`,
  `setresuid(-1, uid, -1)`) — именно их ядро использует для проверки прав на файлы (fsuid/fsgid +
  дополнительные группы). Реальный и сохранённый uid остаются 0, поэтому поток возвращается в root в
  `finally`; это механизм принудительного применения прав для доверенного кода приложения, а не
  песочница против исполнения произвольного кода.
* **Проверка изоляции при старте.** Имперсонация корректна только если ядро/glibc меняют креды
  *только у вызывающего потока* (Linux + glibc ≥ 2.24, стандарт Debian 12). При старте сервис
  запускает фоновый поток-наблюдатель: если тот увидит чужой эффективный uid, значит платформа
  применяет смену кредов ко всему процессу — сервис **отказывается запускаться** (fail closed), потому
  что иначе один пользователь мог бы получить доступ от имени другого. Практический пример: DSH-песочница
  (`landlock-run`), в которой разрабатывался проект, ведёт себя именно так — там имперсонация
  проверена юнит-тестами механики, но приложение запускается только с `Impersonation:Enabled=false`
  (dev-режим без реального применения прав).
* **«Права root»** = uid 0 **или** членство в группе из `FileManager:AdminGroups` (по умолчанию
  `sudo`, `wheel`). Для таких пользователей операции идут от root, без имперсонации. При выдаче
  sudo-прав аккаунт дополнительно добавляется в admin-группу — иначе приложение не распознало бы его
  как администратора.
* **Пароли** никогда не логируются и не попадают в argv: `chpasswd` получает данные только через
  stdin; в API возвращается лишь флаг `hasUsablePassword`. В PostgreSQL хранится SHA-256 cookie-токена,
  сам токен — только в браузере.
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
| `FileManager:Auth:Provider` | `pam` | `pam` (fallback на `/etc/shadow` при недоступности) или `shadow` |
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

Чтобы открыть реальные каталоги хоста, добавьте их в `volumes` сервиса `app` и в
`FileManager__BrowseRoots__*`.

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

* **Юнит-тесты** (`tests/FileManager.Core.Tests`, 108 тестов): разбор `passwd/group/shadow` и фильтр
  «пустой/заблокированный пароль + nologin + uid < 1000»; проверка пароля по реальному SHA-512
  `crypt(3)`-хэшу и отказ для запертых аккаунтов; значения по умолчанию для коллекций опций
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
    закрыть и свою сессию.
* **Требует настоящего хоста и прав root** (в этой среде не выполнялось): фактическое создание
  системных пользователей в `/etc`, запись в `/etc/sudoers.d`, `setfacl` (пакет `acl`), реальный
  `pam_start` с `/etc/pam.d/filemanager`. Логика этих вызовов покрыта тестами с подстановкой
  исполнителя команд; Docker-сборка не запускалась (docker CLI в среде отсутствует).

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
  `docker compose up --build` у себя. Проверено без Docker: сборка, 152 автотеста, `dotnet publish`
  (обе локали попадают в `wwwroot`) и три сквозных smoke-теста (33 + 22 + 18 проверок) против
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
* Имперсонация требует per-thread семантики смены кредов (Linux + glibc ≥ 2.24). Если платформа
  применяет смену кредов ко всему процессу (например, песочницы, перехватывающие `setxid`), сервис
  не стартует с включённой имперсонацией — это осознанно: иначе запрос одного пользователя мог бы
  выполниться от имени другого.
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
