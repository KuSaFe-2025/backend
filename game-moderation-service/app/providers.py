from __future__ import annotations

import abc
import asyncio
import logging

import httpx

from .prompts import build_prompt, extract_reason, first_non_empty_reason, DEFAULT_REJECTION_REASON
from .schemas import Game, ModerateResponse

logger = logging.getLogger(__name__)


class ModerationProvider(abc.ABC):
    @abc.abstractmethod
    async def moderate(self, game: Game) -> ModerateResponse: ...


class OllamaProvider(ModerationProvider):
    def __init__(self, base_url: str, model: str, votes: int = 5, request_timeout: float = 60.0):
        self._base_url = base_url.rstrip("/")
        self._model = model
        self._votes = max(1, votes)
        self._client = httpx.AsyncClient(base_url=self._base_url, timeout=request_timeout)

    async def aclose(self) -> None:
        await self._client.aclose()

    async def _ask(self, prompt: str) -> str:
        try:
            resp = await self._client.post(
                "/api/generate",
                json={"model": self._model, "prompt": prompt, "stream": False},
            )
            resp.raise_for_status()
        except httpx.HTTPError as e:
            logger.error("Ollama request failed during moderation: %s", e)
            return "NO: Moderation backend error."
        return resp.json().get("response", "NO") or "NO"

    async def moderate(self, game: Game) -> ModerateResponse:
        prompt = build_prompt(game)
        yes = 0
        no = 0
        rejection_reasons: list[str] = []

        for _ in range(self._votes):
            response = await self._ask(prompt)
            if response.strip().upper().startswith("YES"):
                yes += 1
            else:
                no += 1
                rejection_reasons.append(extract_reason(response))

        approved = yes > no
        decision = (
            f"Approved by local AI moderation ({yes}/{self._votes} YES)."
            if approved
            else f"Rejected by local AI moderation ({no}/{self._votes} NO). Reason: {first_non_empty_reason(rejection_reasons)}"
        )
        return ModerateResponse(approved=approved, yes_votes=yes, no_votes=no, decision=decision)


class DeterministicProvider(ModerationProvider):

    BANNED = ("forbidden", "banword")

    async def moderate(self, game: Game) -> ModerateResponse:
        parts: list[str] = [game.title, game.description or ""]
        for t in game.tasks:
            parts.append(t.text)
            for o in t.options:
                parts.append(o.text)
        text = " ".join(parts).lower()

        rejected = any(word in text for word in self.BANNED)
        await asyncio.sleep(0)
        if rejected:
            return ModerateResponse(
                approved=False,
                yes_votes=1,
                no_votes=4,
                decision="Rejected by deterministic E2E moderation (4/5 NO). Reason: Content contains a blocked word.",
            )
        return ModerateResponse(
            approved=True,
            yes_votes=4,
            no_votes=1,
            decision="Approved by deterministic E2E moderation (4/5 YES).",
        )


__all__ = [
    "DEFAULT_REJECTION_REASON",
    "DeterministicProvider",
    "ModerationProvider",
    "OllamaProvider",
]
