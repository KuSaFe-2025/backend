from __future__ import annotations

import httpx
import pytest

from app.providers import OllamaProvider
from app.schemas import Game, Task


def _make_provider(responses: list[str], votes: int = 5) -> OllamaProvider:
    idx = {"i": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        i = idx["i"]
        idx["i"] += 1
        body = responses[min(i, len(responses) - 1)]
        return httpx.Response(200, json={"response": body})

    provider = OllamaProvider(base_url="http://ollama-mock", model="m", votes=votes)
    provider._client = httpx.AsyncClient( 
        base_url="http://ollama-mock",
        transport=httpx.MockTransport(handler),
        timeout=5.0,
    )
    return provider


def _simple_game() -> Game:
    return Game(title="G", description="D", tasks=[Task(order=0, type=0, text="Q", options=[])])


@pytest.mark.asyncio
async def test_unanimous_yes() -> None:
    p = _make_provider(["YES: looks fine."] * 5)
    res = await p.moderate(_simple_game())
    assert res.approved is True
    assert res.yes_votes == 5
    assert res.no_votes == 0
    assert "5/5 YES" in res.decision
    await p.aclose()


@pytest.mark.asyncio
async def test_unanimous_no_includes_first_reason() -> None:
    p = _make_provider(["NO: bad content."] * 5)
    res = await p.moderate(_simple_game())
    assert res.approved is False
    assert res.yes_votes == 0
    assert res.no_votes == 5
    assert "bad content." in res.decision
    await p.aclose()


@pytest.mark.asyncio
async def test_majority_yes_approves() -> None:
    p = _make_provider([
        "YES: ok",
        "YES: ok",
        "YES: ok",
        "NO: bad",
        "NO: bad",
    ])
    res = await p.moderate(_simple_game())
    assert res.approved is True
    assert res.yes_votes == 3
    assert res.no_votes == 2
    await p.aclose()


@pytest.mark.asyncio
async def test_tie_rejects() -> None:
    p = _make_provider([
        "YES: ok", "YES: ok",
        "NO: bad", "NO: bad",
    ], votes=4)
    res = await p.moderate(_simple_game())
    assert res.approved is False
    assert res.yes_votes == 2
    assert res.no_votes == 2
    await p.aclose()


@pytest.mark.asyncio
async def test_non_yes_response_counts_as_no() -> None:
    p = _make_provider(["I think this is fine."] * 5)
    res = await p.moderate(_simple_game())
    assert res.approved is False
    assert res.no_votes == 5
    await p.aclose()


@pytest.mark.asyncio
async def test_ollama_http_failure_counts_as_no_each_time() -> None:

    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(500, text="boom")

    provider = OllamaProvider(base_url="http://ollama-mock", model="m", votes=3)
    provider._client = httpx.AsyncClient( 
        base_url="http://ollama-mock",
        transport=httpx.MockTransport(handler),
        timeout=5.0,
    )

    res = await provider.moderate(_simple_game())
    assert res.approved is False
    assert res.no_votes == 3
    assert res.yes_votes == 0
    await provider.aclose()
