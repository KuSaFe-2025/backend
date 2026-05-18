from __future__ import annotations

import abc
import asyncio
import json
import logging
from typing import Any, Callable, Optional

import httpx

from .prompts import (
    build_explain_answer_prompt,
    build_rewrite_prompt,
    build_suggest_option_prompt,
    build_suggest_task_prompt,
)
from .schemas import (
    AnswerDto,
    ExplainAnswerRequest,
    FullGame,
    FullTask,
    GameTaskType,
    RewriteRequest,
    SuggestOptionRequest,
    SuggestOptionResponse,
    SuggestTaskRequest,
    SuggestTaskResponse,
)

logger = logging.getLogger(__name__)


class AiProvider(abc.ABC):
    @abc.abstractmethod
    async def rewrite(self, req: RewriteRequest) -> str: ...

    @abc.abstractmethod
    async def suggest_option(self, req: SuggestOptionRequest) -> SuggestOptionResponse: ...

    @abc.abstractmethod
    async def suggest_task(self, req: SuggestTaskRequest) -> SuggestTaskResponse: ...

    @abc.abstractmethod
    async def explain_answer(self, req: ExplainAnswerRequest) -> str: ...


def _extract_json(raw: str) -> str:
    trimmed = raw.strip()
    start = trimmed.find("{")
    end = trimmed.rfind("}")
    if start >= 0 and end > start:
        return trimmed[start : end + 1]
    return trimmed


def _is_valid_task_suggestion(payload: dict[str, Any]) -> bool:
    if not isinstance(payload, dict):
        return False
    text = payload.get("text")
    if not isinstance(text, str) or not text.strip():
        return False
    points = payload.get("points")
    time_limit = payload.get("timeLimitMs")
    if not isinstance(points, int) or points < 0:
        return False
    if not isinstance(time_limit, int) or time_limit <= 0:
        return False
    type_val = payload.get("type")
    if type_val not in (member.value for member in GameTaskType):
        return False
    if type_val == GameTaskType.OPEN_ENDED.value:
        return True
    options = payload.get("options") or []
    if not isinstance(options, list) or len(options) < 2:
        return False
    if type_val in (GameTaskType.QUIZ.value, GameTaskType.TRUE_FALSE.value, GameTaskType.MULTICHOICE.value):
        idx = payload.get("correctOptionIndexes") or []
        if not isinstance(idx, list) or len(idx) == 0:
            return False
    return True


class OllamaProvider(AiProvider):
    def __init__(self, base_url: str, model: str, request_timeout: float = 90.0):
        self._base_url = base_url.rstrip("/")
        self._model = model
        self._client = httpx.AsyncClient(base_url=self._base_url, timeout=request_timeout)

    async def aclose(self) -> None:
        await self._client.aclose()

    async def _generate(self, prompt: str) -> str:
        try:
            resp = await self._client.post(
                "/api/generate",
                json={"model": self._model, "prompt": prompt, "stream": False},
            )
            resp.raise_for_status()
        except httpx.HTTPError as e:
            logger.error("Ollama request failed: %s", e)
            raise
        body = resp.json()
        return body.get("response", "") or ""

    async def rewrite(self, req: RewriteRequest) -> str:
        prompt = build_rewrite_prompt(req.field, req.mode, req.text)
        raw = await self._generate(prompt)
        return raw.strip()

    async def suggest_option(self, req: SuggestOptionRequest) -> SuggestOptionResponse:
        prompt = build_suggest_option_prompt(req)

        def validate(payload: dict[str, Any]) -> bool:
            return isinstance(payload, dict) and isinstance(payload.get("text"), str) and bool(payload["text"].strip())

        payload = await self._parse_json_with_retries(prompt, validate)
        if payload is None:
            raise ValueError("Не удалось разобрать ответ AI для нового варианта.")
        return SuggestOptionResponse(text=payload["text"])

    async def suggest_task(self, req: SuggestTaskRequest) -> SuggestTaskResponse:
        prompt = build_suggest_task_prompt(req)
        payload = await self._parse_json_with_retries(prompt, _is_valid_task_suggestion)
        if payload is None:
            raise ValueError("Не удалось разобрать ответ AI для новой задачи.")
        return SuggestTaskResponse(
            type=payload["type"],
            text=payload["text"],
            points=payload["points"],
            time_limit_ms=payload["timeLimitMs"],
            options=payload.get("options", []) or [],
            correct_option_indexes=payload.get("correctOptionIndexes", []) or [],
        )

    async def explain_answer(self, req: ExplainAnswerRequest) -> str:
        task: Optional[FullTask] = next((t for t in req.game.tasks if t.id == req.task_id), None)
        if task is None:
            raise ValueError(f"Task {req.task_id} is not present in the supplied game.")
        prompt = build_explain_answer_prompt(req.game, task, req.answer)
        raw = await self._generate(prompt)
        return raw.strip()

    async def _parse_json_with_retries(
        self,
        prompt: str,
        validate: Callable[[dict[str, Any]], bool],
        attempts: int = 2,
    ) -> Optional[dict[str, Any]]:
        for attempt in range(attempts):
            raw = await self._generate(prompt)
            try:
                parsed = json.loads(_extract_json(raw))
            except json.JSONDecodeError:
                logger.warning("LLM returned non-JSON on attempt %d", attempt + 1)
                continue
            if validate(parsed):
                return parsed
            logger.warning("LLM returned JSON failing validation on attempt %d: %s", attempt + 1, parsed)
        return None


class DeterministicProvider(AiProvider):
    async def rewrite(self, req: RewriteRequest) -> str:
        prefix = {
            "professional": "Профессионально: ",
            "simple": "Проще: ",
            "hard": "Сложнее: ",
        }.get(req.mode, "AI: ")
        await asyncio.sleep(0)
        return prefix + req.text.strip()

    async def suggest_option(self, req: SuggestOptionRequest) -> SuggestOptionResponse:
        count = len(req.task.options or []) + 1
        await asyncio.sleep(0)
        return SuggestOptionResponse(text=f"Неправильный вариант {count}")

    async def suggest_task(self, req: SuggestTaskRequest) -> SuggestTaskResponse:
        number = len(req.tasks or []) + 1
        await asyncio.sleep(0)
        return SuggestTaskResponse(
            type=GameTaskType.QUIZ.value,
            text=f"AI-задача {number}",
            points=100,
            time_limit_ms=60000,
            options=["Верный ответ", "Неверный ответ"],
            correct_option_indexes=[0],
        )

    async def explain_answer(self, req: ExplainAnswerRequest) -> str:
        if not any(t.id == req.task_id for t in req.game.tasks):
            raise ValueError(f"Task {req.task_id} is not present in the supplied game.")
        await asyncio.sleep(0)
        return "Правильный ответ выбран потому, что он соответствует условию задания."
