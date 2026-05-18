from __future__ import annotations

import logging
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse

from .providers import AiProvider, DeterministicProvider, OllamaProvider
from .schemas import (
    ExplainAnswerRequest,
    ExplainAnswerResponse,
    RewriteRequest,
    RewriteResponse,
    SuggestOptionRequest,
    SuggestOptionResponse,
    SuggestTaskRequest,
    SuggestTaskResponse,
)

logger = logging.getLogger(__name__)
logging.basicConfig(level=os.getenv("LOG_LEVEL", "INFO"))


def build_provider() -> AiProvider:
    provider_name = os.getenv("PROVIDER", "ollama").lower()
    if provider_name == "deterministic":
        logger.info("ai-assistant-service: using DeterministicProvider")
        return DeterministicProvider()

    base_url = os.getenv("OLLAMA_BASE_URL", "http://ollama:11434")
    model = os.getenv("OLLAMA_MODEL", "llama3.1:8b")
    logger.info("ai-assistant-service: using OllamaProvider (model=%s, base=%s)", model, base_url)
    return OllamaProvider(base_url=base_url, model=model)


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
    title="KuSaFe AI Assistant Service",
    version="1.0.0",
    description=(
        "Internal microservice. Provides AI features (rewrite, suggest option, "
        "suggest task, explain answer) for the KuSaFe C# backend."
    ),
    lifespan=lifespan,
)


def get_provider(request: Request) -> AiProvider:
    return request.app.state.provider


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/v1/rewrite", response_model=RewriteResponse)
async def rewrite(req: RewriteRequest, request: Request) -> RewriteResponse:
    provider = get_provider(request)
    text = await provider.rewrite(req)
    return RewriteResponse(text=text)


@app.post("/v1/suggest-option", response_model=SuggestOptionResponse)
async def suggest_option(req: SuggestOptionRequest, request: Request) -> SuggestOptionResponse:
    provider = get_provider(request)
    try:
        return await provider.suggest_option(req)
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/v1/suggest-task", response_model=SuggestTaskResponse)
async def suggest_task(req: SuggestTaskRequest, request: Request) -> SuggestTaskResponse:
    provider = get_provider(request)
    try:
        return await provider.suggest_task(req)
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.post("/v1/explain-answer", response_model=ExplainAnswerResponse)
async def explain_answer(req: ExplainAnswerRequest, request: Request) -> ExplainAnswerResponse:
    provider = get_provider(request)
    try:
        explanation = await provider.explain_answer(req)
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e))
    return ExplainAnswerResponse(explanation=explanation)


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    logger.exception("Unhandled error processing %s %s: %s", request.method, request.url.path, exc)
    return JSONResponse(status_code=500, content={"detail": "Internal error in ai-assistant-service."})
