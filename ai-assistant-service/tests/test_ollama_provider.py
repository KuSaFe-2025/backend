from __future__ import annotations

import json
from uuid import uuid4

import httpx
import pytest

from app.providers import OllamaProvider
from app.schemas import (
    AnswerDto,
    ExplainAnswerRequest,
    FullGame,
    FullOption,
    FullTask,
    GameSnapshot,
    GameTaskType,
    RewriteRequest,
    SuggestOptionRequest,
    SuggestTaskRequest,
    TaskSnapshot,
    TaskSummary,
)


def _make_provider_with_responses(responses: list[str]) -> OllamaProvider:
    idx = {"i": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        i = idx["i"]
        idx["i"] += 1
        body = responses[min(i, len(responses) - 1)]
        return httpx.Response(200, json={"response": body})

    provider = OllamaProvider(base_url="http://ollama-mock", model="test-model")
    provider._client = httpx.AsyncClient(
        base_url="http://ollama-mock",
        transport=httpx.MockTransport(handler),
        timeout=5.0,
    )
    return provider


@pytest.mark.asyncio
async def test_rewrite_returns_trimmed_response() -> None:
    provider = _make_provider_with_responses(["  hello world  \n"])
    text = await provider.rewrite(RewriteRequest(field="title", mode="professional", text="hi"))
    assert text == "hello world"
    await provider.aclose()


@pytest.mark.asyncio
async def test_suggest_option_parses_well_formed_json() -> None:
    provider = _make_provider_with_responses(['{"text": "Wrong answer"}'])
    req = SuggestOptionRequest(
        game=GameSnapshot(title="G", description="D"),
        task=TaskSnapshot(text="Q", type=0, options=["A"]),
    )
    resp = await provider.suggest_option(req)
    assert resp.text == "Wrong answer"
    await provider.aclose()


@pytest.mark.asyncio
async def test_suggest_option_retries_on_invalid_then_succeeds() -> None:
    provider = _make_provider_with_responses([
        "this is not json at all",
        'Sure, here: {"text":"Plausible wrong"}',
    ])
    req = SuggestOptionRequest(
        game=GameSnapshot(title="G", description="D"),
        task=TaskSnapshot(text="Q", type=0, options=[]),
    )
    resp = await provider.suggest_option(req)
    assert resp.text == "Plausible wrong"
    await provider.aclose()


@pytest.mark.asyncio
async def test_suggest_option_raises_after_exhausted_retries() -> None:
    provider = _make_provider_with_responses(["bad", "still bad"])
    req = SuggestOptionRequest(
        game=GameSnapshot(title="G", description="D"),
        task=TaskSnapshot(text="Q", type=0, options=[]),
    )
    with pytest.raises(ValueError):
        await provider.suggest_option(req)
    await provider.aclose()


@pytest.mark.asyncio
async def test_suggest_task_parses_well_formed_json() -> None:
    payload = {
        "type": 0,
        "text": "Generated quiz",
        "points": 50,
        "timeLimitMs": 30000,
        "options": ["A", "B"],
        "correctOptionIndexes": [1],
    }
    provider = _make_provider_with_responses([json.dumps(payload)])
    req = SuggestTaskRequest(game=GameSnapshot(title="G", description="D"), tasks=[])
    resp = await provider.suggest_task(req)
    assert resp.type == 0
    assert resp.text == "Generated quiz"
    assert resp.points == 50
    assert resp.time_limit_ms == 30000
    assert resp.correct_option_indexes == [1]
    await provider.aclose()


@pytest.mark.asyncio
async def test_explain_answer_includes_task_id_in_prompt_and_returns_response() -> None:
    opt_correct = uuid4()
    opt_wrong = uuid4()
    task_id = uuid4()
    task = FullTask(
        id=task_id,
        order=0,
        type=GameTaskType.QUIZ.value,
        text="Q",
        correct_option_id=opt_correct,
        options=[
            FullOption(id=opt_correct, text="Right", is_active=True, sort_order=0, is_correct=True),
            FullOption(id=opt_wrong, text="Wrong", is_active=True, sort_order=1, is_correct=False),
        ],
    )

    captured: dict[str, str] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["prompt"] = json.loads(request.content)["prompt"]
        return httpx.Response(200, json={"response": "  Because 4 follows 2+2.  "})

    provider = OllamaProvider(base_url="http://ollama-mock", model="test-model")
    provider._client = httpx.AsyncClient( 
        base_url="http://ollama-mock",
        transport=httpx.MockTransport(handler),
        timeout=5.0,
    )

    explanation = await provider.explain_answer(ExplainAnswerRequest(
        game=FullGame(title="G", description="D", tasks=[task]),
        task_id=task_id,
        answer=AnswerDto(selected_option_id=opt_wrong),
    ))

    assert explanation == "Because 4 follows 2+2."
    assert "Right (правильный)" in captured["prompt"]
    assert "Ответ пользователя: Wrong" in captured["prompt"]
    await provider.aclose()
