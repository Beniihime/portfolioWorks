# PortfolioWorks

gRPC-сервис для загрузки студенческих работ: список дисциплин берётся из УМУ.

## Структура

| Проект | Назначение |
| --- | --- |
| `PortfolioWorks.Domain` | модель `UploadedWork` |
| `PortfolioWorks.Application` | интерфейсы, `WorkSubmissionService`, валидация загрузки |
| `PortfolioWorks.Infrastructure` | клиент УМУ, EF Core + PostgreSQL, файловое хранилище |
| `PortfolioWorks.Api` | gRPC-сервис (`Protos/portfolio.proto`) и запуск |

## Конфигурация

| Ключ | Переменная окружения | Обязателен | Описание |
| --- | --- | --- | --- |
| `Umu:Address` | `Umu__Address` | да | абсолютный URI gRPC-сервиса УМУ |
| `Umu:Authorization` | `Umu__Authorization` | да | значение заголовка `authorization` для УМУ |
| `Storage:RootPath` | `Storage__RootPath` | нет | каталог файлов, по умолчанию `data/uploads` |
| `Storage:MaximumFileSizeBytes` | `Storage__MaximumFileSizeBytes` | нет | лимит размера файла, по умолчанию 25 МБ |
| `Storage:AllowedExtensions` | — | нет | список расширений, по умолчанию `.pdf`, `.doc`, `.docx` |

## Запуск через Docker Compose

```bash
docker compose up --build
```

Compose переопределяет значения из `appsettings.json` через переменные окружения:

| Переменная |
| --- |
| `ConnectionStrings__PortfolioWorks` |
| `Umu__Address` |
| `Umu__Authorization` |

Переменные окружения всегда важнее `appsettings.json`, поэтому локальные значения
из файла на запуск в Docker не влияют.

## Миграции

Схема БД описана EF Core миграциями в `src/PortfolioWorks.Infrastructure/Migrations`.
Приложение применяет их при старте и повторяет попытку, пока PostgreSQL не поднимется.

Применить вручную:

```bash
dotnet tool restore
dotnet ef database update --project src/PortfolioWorks.Infrastructure --startup-project src/PortfolioWorks.Api
```

Добавить миграцию после изменения модели:

```bash
dotnet ef migrations add <Name> --project src/PortfolioWorks.Infrastructure --startup-project src/PortfolioWorks.Api --output-dir Migrations
```

## RPC

| Метод | Назначение |
| --- | --- |
| `GetDisciplines` | дисциплины студента из УМУ, включая предметы по выбору (`electives`) |
| `UploadWork` | потоковая загрузка файла к дисциплине |
| `ListWorks` | ранее загруженные работы студента |

`UploadWork` — клиентский стриминг: один RPC, внутри которого файл приходит
несколькими сообщениями. Метаданные (`student_email`, `discipline_id`,
`file_name`, `content_type`) задаются в первом сообщении; в следующих достаточно
`content`, а если метаданные переданы повторно — они должны совпадать.
Рекомендуемый размер сообщения — 1–2 МБ. Серверный лимит на одно сообщение
поднимается до `Storage:MaximumFileSizeBytes + 1 МБ`, а суммарный размер файла
проверяется отдельно.

Client streaming поддерживают gRPC-клиенты для .NET, Go, Java, Python и т.п.
Браузерные gRPC-Web и Connect — нет (только unary и server streaming), поэтому
фронт должен обращаться к сервису через свой бэкенд/прокси, а не напрямую из SPA.
