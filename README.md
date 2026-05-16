# KuSaFe Backend

ASP.NET Core 10 backend для платформы KuSaFe: создание образовательных игр, прохождение заданий, лидерборды, отзывы, статистика, AI-модерация и AI-инструменты автора. Разработка Николая Кушнаренко, Андрея Самохвалова и Арсения Фёдорова.

## Возможности

- JWT-аутентификация с access/refresh токенами.
- Роли пользователей и админ-доступ через claim `isAdmin`.
- CRUD игр и заданий для автора.
- Типы заданий: викторина, верно/неверно, порядок, открытый ответ, опрос, множественный выбор.
- Приватные игры по ссылке, лимит попыток на пользователя, окна доступности по датам.
- Проверка игры локальной AI-модерацией через Ollama или deterministic provider для E2E.
- AI-инструменты автора: переписывание текста, генерация неправильного варианта ответа, генерация новой задачи.
- AI-объяснение правильного ответа после прохождения игры.
- Публичный каталог, рекомендуемые игры, рейтинг и количество прохождений.
- Лидерборд идеальных попыток.
- Отзывы к платформе и играм, скрытие отзывов приватных игр от публичного списка.
- Статистика автора: средний балл, время, точность, CSV export, пагинация открытых ответов.

## Локальный запуск

```bash
dotnet restore KuSaFeBackend.sln
dotnet build KuSaFeBackend.sln
dotnet run --project KuSaFeBackend.csproj --urls http://127.0.0.1:5267
```

По умолчанию используется PostgreSQL из `appsettings.json`.

Важные переменные:

- `ConnectionStrings__DefaultConnection`
- `Jwt__Key`
- `Jwt__Issuer`
- `Jwt__Audience`
- `Moderation__Provider`: `Ollama` или `Deterministic`
- `Moderation__OllamaBaseUrl`
- `Ai__Provider`: `Ollama` или `Deterministic`
- `Ai__OllamaBaseUrl`

## Тесты

```bash
dotnet test Tests/KuSaFeBackend.Tests/KuSaFeBackend.Tests.csproj
```

Тестовый backend может работать с SQLite и deterministic AI, чтобы проверки не зависели от реального Ollama.

## Docker

```bash
docker build -t kusafe-backend .
docker run --rm -p 5000:5000 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:5000 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Port=5432;Database=kusafe_db;Username=postgres;Password=postgres" \
  -e Jwt__Key="replace-with-long-secret" \
  kusafe-backend
```

Production compose и nginx-конфиг лежат во frontend repository в `deploy/`, потому что там расположен edge nginx для всего сайта.

## CI/CD

GitHub Actions workflow `.github/workflows/ci-cd.yml` делает:

- `dotnet restore`;
- `dotnet build`;
- `dotnet test`;
- сборку Docker image;
- push в GHCR как `ghcr.io/kusafe-2025/backend:<branch>` и `<sha>`;
- SSH deploy на Ubuntu 22.04 в `/opt/kusafe`;
- авторизацию сервера в private GHCR через `GHCR_READ_USER` и `GHCR_READ_TOKEN`;
- bootstrap `/opt/kusafe`, `.env` и `docker-compose.prod.yml` для первого деплоя;
- `docker compose pull backend`;
- `docker compose up -d postgres ollama backend`;
- healthcheck `/v1/health`.

Нужные secrets:

- `DEPLOY_HOST`
- `DEPLOY_USER`
- `DEPLOY_SSH_KEY`
- `DEPLOY_PORT`
- `PROD_POSTGRES_PASSWORD`
- `PROD_JWT_KEY`
- `GHCR_READ_USER`
- `GHCR_READ_TOKEN`

`GHCR_READ_TOKEN` должен иметь минимум `read:packages`. Для push workflow использует стандартный `GITHUB_TOKEN`.

## Production

Ожидаемая схема:

- `nginx` принимает `80/443`;
- системный nginx на Ubuntu проксирует `/api/` на `127.0.0.1:5000`;
- frontend container доступен системному nginx на `127.0.0.1:5549`;
- PostgreSQL работает в Docker volume;
- Ollama работает в Docker volume и доступна backend по `http://ollama:11434`.
