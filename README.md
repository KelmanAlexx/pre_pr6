## Состав комплекта

- `SmartCampusSilentUpgrade.sln` — решение Visual Studio.
- `SmartCampusSilentUpgrade/` — исходный проект консольной утилиты C# для .NET 8.
- `docs/OPERATOR_MANUAL.md` — руководство оператора.
- `docs/UPDATE_GUIDE.md` — инструкция системного администратора.
- `docs/CHANGELOG.md` — журнал изменений по Keep a Changelog.

## Требования

- Visual Studio 2022 и выше с компонентом разработки классических приложений .NET 8 SDK и выше.
- Для сетевого сценария — доступ к `jsonplaceholder.typicode.com`.

В проекте не используются сторонние NuGet-пакеты. SQLite-файл в `sample-data/campus.db` — настоящая база данных SQLite.

## Запуск

Откройте `SmartCampusSilentUpgrade.sln` в Visual Studio. Для штатного сценария задайте в свойствах отладки аргументы:

```text
--scenario success --reset
```

Для имитации сбоя и отката:

```text
--scenario rollback --reset
```

Чтобы повторить сценарии без подключения к сети, добавьте `--offline`:

```text
--scenario success --reset --offline
--scenario rollback --reset --offline
```

Либо запустите из терминала, находясь в папке решения:

```powershell
dotnet run --project .\SmartCampusSilentUpgrade\SmartCampusSilentUpgrade.csproj -- --scenario success --reset
```

`--reset` удаляет только каталог в профиле текущего пользователя и создает исходную версию 1.0.0.

## Результаты работы

Файлы создаются в каталоге пользователя:

```text
%LOCALAPPDATA%\SmartCampusSilentUpgradeDemo\success\
%LOCALAPPDATA%\SmartCampusSilentUpgradeDemo\rollback\
```

Внутри находятся журналы `logs/upgrade.log`, отчет `test-report.json`, резервная копия в `backups/`, ZIP-пакет в `work/`, а также каталоги установленной и предыдущей версий.

Коды завершения процесса: `0` — успешно; `2` — ошибка без подтвержденного отката; `3` — обновление не прошло проверку, но откат завершен успешно.
