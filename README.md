# KuSaFe Backend

ASP.NET Core 10 backend for the **KuSaFe** educational-game platform, plus two
Python microservices that own the AI features.

## Architecture

```
                    ┌──────────────────────┐
                    │  Frontend (React)    │
                    └──────────┬───────────┘
                               │ HTTP — contracts unchanged
                               ▼
                    ┌──────────────────────┐
                    │ C# Backend  :5000    │
                    │ Controllers / Auth   │
                    │ Postgres / Stream    │
                    └────┬───────────┬─────┘
                         │           │
            HTTP (internal docker network)
                         │           │
        ┌────────────────▼──┐   ┌────▼──────────────────┐
        │ ai-assistant-     │   │ game-moderation-      │
        │ service           │   │ service               │
        │ FastAPI :8000     │   │ FastAPI :8000         │
        └────────────────┬──┘   └────┬──────────────────┘
                         │           │
                         └─────┬─────┘
                               ▼
                    ┌──────────────────────┐
                    │  Ollama :11434       │
                    └──────────────────────┘
```

### What moved

The AI features that used to live in C# (`Services/AiAssistantService.cs`,
`Services/GameModerationService.cs`) are now implemented in Python and run as
independent microservices. The C# backend keeps the same public HTTP contract
toward the frontend; internally, it proxies AI calls to the Python services.

**Frontend wasn't touched. JWT/auth/CORS/E2E tests all keep working.**

| Capability                  | Owner                        | Endpoint exposed to FE                                                |
| --------------------------- | ---------------------------- | --------------------------------------------------------------------- |
| Game moderation (vote-based)| `game-moderation-service`    | `POST /v1/my/games/{id}/submit-for-verification` (on C#)             |
| Rewrite text (RU)           | `ai-assistant-service`       | `POST /v1/my/games/{id}/ai/rewrite/stream` (streaming on C#)         |
| Suggest a wrong answer      | `ai-assistant-service`       | `POST /v1/my/games/{id}/ai/suggest-option`                            |
| Suggest a new task          | `ai-assistant-service`       | `POST /v1/my/games/{id}/ai/suggest-task`                              |
| Explain the correct answer  | `ai-assistant-service`       | `POST /v1/games/{id}/attempts/{aid}/answers/{ansid}/explain`         |

The C# service classes still exist as thin proxies:
`Services/AiAssistantService.cs::RemoteAiAssistantService` and
`Services/GameModerationService.cs::RemoteGameModerationService`. The
`Deterministic*` variants are kept for the existing E2E tests so CI without
Ollama still works.

## Repository layout

```
.
├── KuSaFeBackend.csproj            # ASP.NET Core project
├── Program.cs                       # DI wiring (registers Remote* by default)
├── Controllers/                     # unchanged
├── Services/
│   ├── AiAssistantService.cs        # interface + RemoteAiAssistantService + Deterministic
│   └── GameModerationService.cs     # interface + RemoteGameModerationService + Deterministic
├── Tests/                           # C# tests (unchanged)
│
├── ai-assistant-service/            # NEW — Python microservice
│   ├── app/{main,schemas,prompts,providers}.py
│   ├── tests/                       # pytest suite
│   ├── Dockerfile
│   ├── requirements.txt
│   └── README.md
│
├── game-moderation-service/         # NEW — Python microservice
│   ├── app/{main,schemas,prompts,providers}.py
│   ├── tests/                       # pytest suite
│   ├── Dockerfile
│   ├── requirements.txt
│   └── README.md
│
├── deploy/
│   ├── docker-compose.prod.yml      # updated: 6 services now
│   └── .env.example
│
├── docker-compose.local.yml         # NEW — builds everything from sources
└── .github/workflows/ci-cd.yml      # 3 test jobs + 3 image builds + deploy
```

## Run the full stack locally

The fastest path uses Docker Compose and rebuilds everything from sources:

```bash
docker compose -f docker-compose.local.yml up --build
```

This brings up Postgres, Ollama, both Python microservices, and the C# backend.
Once it's up, point your frontend dev server at `http://localhost:5000`
(set `VITE_API_BASE_URL=http://localhost:5000` in `frontend/.env`).

> **Ollama models are not bundled.** After the first start, pull the model:
> ```bash
> docker exec kusafe-ollama ollama pull llama3.1:8b
> ```
> If you don't want to download ~5 GB of weights, run the Python services in
> deterministic mode instead — see the snippet under "Run microservices alone".

## Run microservices alone (no Docker)

```bash
# Terminal 1
cd ai-assistant-service
pip install -r requirements.txt
PROVIDER=deterministic uvicorn app.main:app --port 8001 --reload

# Terminal 2
cd game-moderation-service
pip install -r requirements.txt
PROVIDER=deterministic uvicorn app.main:app --port 8002 --reload
```

Then run the C# backend with these env vars so it points to your local Python
services:

```bash
export Ai__BaseUrl=http://localhost:8001
export Moderation__BaseUrl=http://localhost:8002
dotnet run --project KuSaFeBackend.csproj
```

## Tests

### Python microservices

```bash
cd ai-assistant-service && PYTHONPATH=. pytest -q          # 29 tests
cd game-moderation-service && PYTHONPATH=. pytest -q       # 20 tests
```

Both suites are hermetic — no Ollama, no network. CI runs them on every push
and PR (see `.github/workflows/ci-cd.yml`).

### C# backend

```bash
dotnet test Tests/KuSaFeBackend.Tests/KuSaFeBackend.Tests.csproj
```

The existing `ModerationTests` / `AnalyticsTests` mock out the moderation
service via DI, so they don't care about the new architecture.

## Configuration

All AI- and moderation-related settings live behind two new C# config keys:

| Key                                           | Default                                  |
| --------------------------------------------- | ---------------------------------------- |
| `Ai:BaseUrl`                                  | `http://ai-assistant-service:8000`       |
| `Moderation:BaseUrl`                          | `http://game-moderation-service:8000`    |
| `Ai:Provider` = `Deterministic`               | Skip the network, use offline fallback   |
| `Moderation:Provider` = `Deterministic`       | Skip the network, use offline fallback   |

The Python services in turn take their own env vars (see each service's
README) — typically `PROVIDER`, `OLLAMA_BASE_URL`, `OLLAMA_MODEL`,
`MODERATION_VOTES`.

## How to push a branch and trigger CI/CD

See [`PUSHING-TO-GIT.md`](./PUSHING-TO-GIT.md) for the step-by-step guide.
