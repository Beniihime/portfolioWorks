# PortfolioWorks

gRPC-сервис для загрузки студенческих работ: список дисциплин берётся из УМУ.

## Структура

| Проект | Назначение |
| --- | --- |
| `PortfolioWorks.Domain` | модель `UploadedWork` |
| `PortfolioWorks.Application` | интерфейсы, `WorkSubmissionService`, валидация загрузки |
| `PortfolioWorks.Infrastructure` | клиент УМУ, EF Core + PostgreSQL, хранилище MinIO |
| `PortfolioWorks.Api` | gRPC-сервис (`Protos/portfolio.proto`) и запуск |

## Конфигурация

| Ключ | Переменная окружения | Обязателен | Описание |
| --- | --- | --- | --- |
| `Umu:Address` | `Umu__Address` | да | абсолютный URI gRPC-сервиса УМУ |
| `Umu:Authorization` | `Umu__Authorization` | да | значение заголовка `authorization` для УМУ |
| `Storage:Minio:Endpoint` | `Storage__Minio__Endpoint` | да | адрес MinIO в виде `host:port`, без схемы |
| `Storage:Minio:AccessKey` | `Storage__Minio__AccessKey` | да | access key MinIO |
| `Storage:Minio:SecretKey` | `Storage__Minio__SecretKey` | да | secret key MinIO |
| `Storage:Minio:Bucket` | `Storage__Minio__Bucket` | да | бакет для файлов, создаётся при старте |
| `Storage:Minio:UseSsl` | `Storage__Minio__UseSsl` | нет | использовать HTTPS, по умолчанию `false` |
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
| `Storage__Minio__Endpoint` |
| `Storage__Minio__AccessKey` |
| `Storage__Minio__SecretKey` |
| `Storage__Minio__Bucket` |
| `Storage__Minio__UseSsl` |
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
| `GetDisciplines` | учебные планы студента из УМУ, разбитые по учебным группам (включая `electives`) |
| `UploadWork` | потоковая загрузка файла к курсовой, курсовому проекту или практике |
| `ListWorks` | ранее загруженные работы студента |
| `DownloadWork` | скачивание файла работы серверным стримингом |

`GetDisciplines` принимает `student_email` или `student_id` — достаточно одного поля.
Ответ содержит `groups`: по одной на запись студента. Сначала сервис берёт все
записи из `GetStudentProfile` (у человека могут быть бакалавриат, магистратура и
выпуск), затем для каждой записи запрашивает `GetStudentCurriculum` по её
`student_id`. В группе приходят `student_id`, `group_id`, `group_name`,
`specialty_name` и `level_name`; названия могут быть пустыми, если УМУ не отдал
расшифровки справочников. Группы без подходящих позиций не возвращаются.

Внутри группы остаются только позиции, к которым можно приложить работу: практики
(`object_type_id = 3` или `practice_type_id > 0`) и дисциплины с курсовой работой
или курсовым проектом. Модули, контроль, ГИА и обычные дисциплины без курсовой
отбрасываются.

У каждой дисциплины в `files` лежат работы студента с этим `discipline_id`:
`id`, `file_name`, `content_type`, `size_bytes`, `uploaded_at`. Это те же работы,
что возвращает `ListWorks`. Работы не привязаны к группе, поэтому если дисциплина
входит и в бакалавриат, и в магистратуру, её файлы будут показаны в обеих
группах. Если email студента не удалось определить, дисциплины вернутся без
файлов.

В `Discipline.grade` приходит последняя оценка по дисциплине: `value` (код шкалы),
`text` и `short_text`. Для `GetDisciplines` сервис дополнительно вызывает
`GetStudentGrades` отдельно по каждой записи студента; `UploadWork` и `ListWorks`
оценки не запрашивают. Если `GetStudentGrades` недоступен, дисциплины возвращаются
без `grade`, а запрос не падает.

В контракте УМУ у `Grade` нет `discipline_id`, поэтому оценка сопоставляется с
дисциплиной по нормализованному названию: регистр, лишние пробелы и разница
`ё`/`е` не важны. Если название в ведомости отличается от названия в учебном
плане, оценка не подставится. При нескольких оценках по одной дисциплине
выбирается самая поздняя — сначала по `exam_date`, затем по `semester`. Значение
берётся из `grade`, если оно пустое — из `exam_grade`, затем из `total`.

Поле `disciplines` верхнего уровня заменено на `groups` — это несовместимое
изменение ответа `GetDisciplines`.

В `Discipline` приходят `object_type_id`/`object_type_name` (2 — дисциплина, 3 — практика,
4 — контроль, 6 — ГИА и т.д.), `practice_type_id`/`practice_type_name` (6 — учебная,
7 — производственная, 8 — НИР, 72 — преддипломная) и флаги `has_course_work` /
`has_course_project`, собранные по всем семестрам дисциплины. Так клиент отличает
курсовую или курсовой проект от практики и понимает, какая именно это практика.

`UploadWork` и `ListWorks` принимают `student_email` или `student_id`. Работы в
PostgreSQL привязаны к email, поэтому при запросе только по `student_id` сервис
резолвит email через `GetStudentProfile` в УМУ: сначала берётся email записи с
этим `id`, затем первый валидный из остальных. Если валидного email нет — запрос
завершается `FailedPrecondition`. Загрузить файл можно только к позиции из
отфильтрованного списка (по любой группе студента) — остальные отклоняются
с ошибкой. Работы по-прежнему не привязаны к группе: `ListWorks` возвращает
плоский список.

`DownloadWork` принимает `work_id` и `student_email` или `student_id`. Сервис
проверяет, что работа принадлежит этому email, и отдаёт файл серверным
стримингом: в первом сообщении приходят `file_name`, `content_type` и `size_bytes`,
в следующих — только `content`. Если работы нет или она принадлежит другому
студенту, возвращается `NotFound`; если запись в БД есть, а объекта в MinIO нет —
`FailedPrecondition`. Авторизации в сервисе по-прежнему нет: email и `student_id`
не подтверждают право на доступ, поэтому выдачу файлов нельзя считать защищённой.

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
