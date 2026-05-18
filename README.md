# KuSaFe Backend

ASP.NET Core 10 backend для платформы KuSaFe: создание образовательных игр, прохождение заданий, лидерборды, отзывы, статистика, AI-модерация и AI-инструменты автора. Разработка Николая Кушнаренко, Андрея Самохвалова и Арсения Фёдорова.

AI-функционал вынесен в два независимых Python-микросервиса (FastAPI), C# backend ходит в них по внутренней сети. Внешний контракт для фронтенда не изменился.

## Возможности

- JWT-аутентификация с access/refresh токенами.
- Роли пользователей и админ-доступ через claim `isAdmin`.
- CRUD игр и заданий для автора.
- Типы заданий: викторина, верно/неверно, порядок, открытый ответ, опрос, множественный выбор.
- Приватные игры по ссылке, лимит попыток на пользователя, окна доступности по датам.
- Проверка игры локальной AI-модерацией через Python-микросервис `game-moderation-service` (Ollama-провайдер или deterministic provider для E2E).
- AI-инструменты автора через Python-микросервис `ai-assistant-service`: переписывание текста, генерация неправильного варианта ответа, генерация новой задачи.
- AI-объяснение правильного ответа после прохождения игры (через тот же `ai-assistant-service`).
- Публичный каталог, рекомендуемые игры, рейтинг и количество прохождений.
- Лидерборд идеальных попыток.
- Отзывы к платформе и играм, скрытие отзывов приватных игр от публичного списка.
- Статистика автора: средний балл, время, точность, CSV export, пагинация открытых ответов.

## Архитектура

```
Frontend → C# backend (5000) → ai-assistant-service (8001, Python)     → Ollama (11434)
                              → game-moderation-service (8002, Python) → Ollama (11434)
```

C# backend сериализует доменные модели в DTO и отправляет HTTP-запросы в микросервисы. Промпты, голосование, retry на невалидный JSON живут в Python. Интерфейсы `IAiAssistantService` и `IGameModerationService` сохранены — поэтому контроллеры и существующие тесты не изменились.

## Структура репозитория

```
.
├── KuSaFeBackend.csproj             # ASP.NET Core проект
├── Program.cs                        # DI: регистрирует Remote-клиенты или Deterministic
├── Controllers/                      # без изменений
├── Services/
│   ├── AiAssistantService.cs         # интерфейс + RemoteAiAssistantService + Deterministic
│   └── GameModerationService.cs      # интерфейс + RemoteGameModerationService + Deterministic
├── Tests/                            # C# тесты (без изменений)
│
├── ai-assistant-service/             # Python микросервис
├── game-moderation-service/          # Python микросервис
│
├── deploy/
│   ├── docker-compose.prod.yml       # 6 сервисов: postgres, ollama, ai-assistant, game-moderation, backend, frontend
│   └── .env.example
│
├── docker-compose.local.yml          # локальная сборка всего стека из исходников
└── .github/workflows/ci-cd.yml       # 3 джобы тестов + 3 сборки образов + деплой
```

## Локальный запуск

### Вариант 1 — всё через Docker Compose

```bash
docker compose -f docker-compose.local.yml up --build
```

Поднимет Postgres, Ollama, оба Python-микросервиса и C# backend. После первого старта надо стянуть модель для Ollama:

```bash
docker exec kusafe-ollama ollama pull llama3.1:8b
```

### Вариант 2 — отдельно

```bash
# Python-микросервисы (в двух терминалах)
cd ai-assistant-service && pip install -r requirements.txt
PROVIDER=deterministic uvicorn app.main:app --port 8001 --reload

cd game-moderation-service && pip install -r requirements.txt
PROVIDER=deterministic uvicorn app.main:app --port 8002 --reload

# C# backend
dotnet restore KuSaFeBackend.sln
dotnet build KuSaFeBackend.sln
dotnet run --project KuSaFeBackend.csproj --urls http://127.0.0.1:5267
```

По умолчанию C# использует PostgreSQL из `appsettings.json`.

Важные переменные:

- `ConnectionStrings__DefaultConnection`
- `Jwt__Key`
- `Jwt__Issuer`
- `Jwt__Audience`
- `Moderation__Provider`: `Remote` (по умолчанию) или `Deterministic`
- `Moderation__BaseUrl`: адрес `game-moderation-service` (по умолчанию `http://game-moderation-service:8000`)
- `Ai__Provider`: `Remote` (по умолчанию) или `Deterministic`
- `Ai__BaseUrl`: адрес `ai-assistant-service` (по умолчанию `http://ai-assistant-service:8000`)

Для каждого Python-микросервиса:

- `PROVIDER`: `ollama` или `deterministic`
- `OLLAMA_BASE_URL`
- `OLLAMA_MODEL`
- `MODERATION_VOTES` (только для `game-moderation-service`)

## Тесты

```bash
# C# backend
dotnet test Tests/KuSaFeBackend.Tests/KuSaFeBackend.Tests.csproj

# Python-микросервисы
cd ai-assistant-service && PYTHONPATH=. pytest -q          # 29 тестов
cd game-moderation-service && PYTHONPATH=. pytest -q       # 20 тестов
```

Тестовый backend может работать с SQLite и deterministic AI/модерацией, чтобы проверки не зависели от реального Ollama. Python-тесты герметичные — не ходят в сеть, используют `httpx.MockTransport` для имитации Ollama.

## Docker

Каждый сервис собирается из своего Dockerfile:

```bash
# C# backend
docker build -t kusafe-backend .

# Python-микросервисы
docker build -t kusafe-ai-assistant ./ai-assistant-service
docker build -t kusafe-game-moderation ./game-moderation-service
```

Запуск C# backend отдельно (для отладки):

```bash
docker run --rm -p 5000:5000 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ASPNETCORE_URLS=http://+:5000 \
  -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Port=5432;Database=kusafe_db;Username=postgres;Password=postgres" \
  -e Jwt__Key="replace-with-long-secret" \
  -e Ai__BaseUrl="http://host.docker.internal:8001" \
  -e Moderation__BaseUrl="http://host.docker.internal:8002" \
  kusafe-backend
```

Production compose и nginx-конфиг лежат во frontend repository в `deploy/`, потому что там расположен edge nginx для всего сайта.

## CI/CD

GitHub Actions workflow `.github/workflows/ci-cd.yml` делает:

- параллельный прогон трёх джоб тестов:
  - `test-backend`: `dotnet restore`, `dotnet build`, `dotnet test`;
  - `test-ai-assistant-service`: `pytest` для AI-микросервиса;
  - `test-game-moderation-service`: `pytest` для модерации;
- сборку трёх Docker-образов:
  - `ghcr.io/kusafe-2025/backend:<branch>` и `<sha>`;
  - `ghcr.io/kusafe-2025/ai-assistant-service:<branch>` и `<sha>`;
  - `ghcr.io/kusafe-2025/game-moderation-service:<branch>` и `<sha>`;
- SSH deploy на Ubuntu 22.04 в `/opt/kusafe`;
- авторизацию сервера в private GHCR через `GHCR_READ_USER` и `GHCR_READ_TOKEN`;
- bootstrap `/opt/kusafe`, `.env` и `docker-compose.prod.yml` для первого деплоя;
- `docker compose pull backend ai-assistant-service game-moderation-service`;
- `docker compose up -d postgres ollama ai-assistant-service game-moderation-service backend`;
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

`GHCR_READ_TOKEN` должен иметь минимум `read:packages`. Для push workflow использует стандартный `GITHUB_TOKEN`. Для образов AI-сервисов нужны те же права (`packages: write` уже включено в workflow).

## Production

Ожидаемая схема:

- `nginx` принимает `80/443`;
- системный nginx на Ubuntu проксирует `/api/` на `127.0.0.1:5000`;
- frontend container доступен системному nginx на `127.0.0.1:5549`;
- PostgreSQL работает в Docker volume;
- Ollama работает в Docker volume и доступна backend по `http://ollama:11434` (через Python-микросервисы);
- `ai-assistant-service` доступен backend по `http://ai-assistant-service:8000` (внутренняя docker-сеть, наружу не торчит);
- `game-moderation-service` доступен backend по `http://game-moderation-service:8000` (внутренняя docker-сеть, наружу не торчит).
