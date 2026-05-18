from __future__ import annotations

import logging
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from .providers import DeterministicProvider, ModerationProvider, OllamaProvider
from .schemas import ModerateRequest, ModerateResponse

logger = logging.getLogger(__name__)
logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO"))


def build_provider() -> ModerationProvider:
    provider_name = os.getenv("PROVIDER", "ollama").lower()
    if provider_name == "deterministic":
        logger.info("game-moderation-service: using DeterministicProvider")
        return DeterministicProvider()

    base_url = os.getenv("OLLAMA_BASE_URL", "http://ollama:11434")
    model = os.getenv("OLLAMA_MODEL", "llama3.1:8b")
    try:
        votes = max(1, int(os.getenv("MODERATION_VOTES", "5")))
    except ValueError:
        logger.warning("Invalid MODERATION_VOTES, falling back to 5")
        votes = 5
    logger.info(
        "game-moderation-service: using OllamaProvider (model=%s, base=%s, votes=%d)",
        model, base_url, votes,
    )
    return OllamaProvider(base_url=base_url, model=model, votes=votes)


@asynccontextmanager
async def lifespan(app: FastAPI):
    app.state.provider = build_provider()
    try:
        yield
    finally:
        provider = app.state.provider
        if hasattr(provider, "aclose"):
            await provider.aclose()


app = FastAPI(
    title="KuSaFe Game Moderation Service",
    version="1.0.0",
    description="Internal microservice. Moderates user-created games via an LLM with majority-vote consensus.",
    lifespan=lifespan,
)


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/v1/moderate", response_model=ModerateResponse)
async def moderate(req: ModerateRequest, request: Request) -> ModerateResponse:
    provider: ModerationProvider = request.app.state.provider
    return await provider.moderate(req.game)


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    logger.exception("Unhandled error processing %s %s: %s", request.method, request.url.path, exc)
    return JSONResponse(status_code=500, content={"detail": "Internal error in game-moderation-service."})
