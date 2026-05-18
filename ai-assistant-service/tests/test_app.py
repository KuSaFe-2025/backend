from __future__ import annotations

import os
import json
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient

os.environ["PROVIDER"] = "deterministic"

from app.main import app  
from app.prompts import (  
    build_explain_answer_prompt,
    build_rewrite_prompt,
    build_suggest_option_prompt,
    build_suggest_task_prompt,
    normalize_rewrite_mode,
)
from app.providers import _extract_json, _is_valid_task_suggestion  
from app.schemas import (  
    AnswerDto,
    FullGame,
    FullOption,
    FullTask,
    GameSnapshot,
    GameTaskType,
    SuggestOptionRequest,
    SuggestTaskRequest,
    TaskSnapshot,
    TaskSummary,
)


@pytest.fixture(scope="module")
def client() -> TestClient:
    with TestClient(app) as c:
        yield c


class TestHealth:
    def test_health(self, client: TestClient) -> None:
        r = client.get("/health")
        assert r.status_code == 200
        assert r.json() == {"status": "ok"}


class TestRewriteEndpoint:
    def test_rewrite_professional(self, client: TestClient) -> None:
        r = client.post("/v1/rewrite", json={
            "field": "title",
            "mode": "professional",
            "text": "Это тест",
        })
        assert r.status_code == 200
        assert r.json()["text"] == "Профессионально: Это тест"

    def test_rewrite_simple(self, client: TestClient) -> None:
        r = client.post("/v1/rewrite", json={"field": "title", "mode": "simple", "text": "Foo"})
        assert r.json()["text"] == "Проще: Foo"

    def test_rewrite_hard(self, client: TestClient) -> None:
        r = client.post("/v1/rewrite", json={"field": "title", "mode": "hard", "text": "Foo"})
        assert r.json()["text"] == "Сложнее: Foo"

    def test_rewrite_unknown_mode(self, client: TestClient) -> None:
        r = client.post("/v1/rewrite", json={"field": "title", "mode": "weird", "text": "Foo"})
        assert r.json()["text"] == "AI: Foo"


class TestSuggestOptionEndpoint:
    def test_suggest_option(self, client: TestClient) -> None:
        r = client.post("/v1/suggest-option", json={
            "game": {"title": "Game", "description": "Desc"},
            "task": {"text": "2+2", "type": 0, "options": ["A", "B"]},
        })
        assert r.status_code == 200
        assert r.json()["text"] == "Неправильный вариант 3"

    def test_suggest_option_empty_options(self, client: TestClient) -> None:
        r = client.post("/v1/suggest-option", json={
            "game": {"title": "Game", "description": ""},
            "task": {"text": "?", "type": 0, "options": []},
        })
        assert r.json()["text"] == "Неправильный вариант 1"


class TestSuggestTaskEndpoint:
    def test_suggest_task(self, client: TestClient) -> None:
        r = client.post("/v1/suggest-task", json={
            "game": {"title": "Game", "description": "Desc"},
            "tasks": [{"type": 0, "text": "Existing"}],
        })
        assert r.status_code == 200
        body = r.json()
        assert body["type"] == GameTaskType.QUIZ.value
        assert body["text"] == "AI-задача 2"
        assert body["points"] == 100
        assert body["timeLimitMs"] == 60000
        assert body["correctOptionIndexes"] == [0]
        assert body["options"] == ["Верный ответ", "Неверный ответ"]


class TestExplainAnswerEndpoint:
    def test_explain_answer_quiz_correct(self, client: TestClient) -> None:
        option1, option2 = uuid4(), uuid4()
        task_id = uuid4()
        payload = {
            "game": {
                "title": "Math",
                "description": "Basic arithmetic",
                "tasks": [
                    {
                        "id": str(task_id),
                        "order": 0,
                        "type": GameTaskType.QUIZ.value,
                        "text": "2 + 2 = ?",
                        "correctOptionId": str(option1),
                        "options": [
                            {"id": str(option1), "text": "4", "isActive": True, "sortOrder": 0, "isCorrect": True},
                            {"id": str(option2), "text": "5", "isActive": True, "sortOrder": 1, "isCorrect": False},
                        ],
                    }
                ],
            },
            "taskId": str(task_id),
            "answer": {"selectedOptionId": str(option1)},
        }
        r = client.post("/v1/explain-answer", json=payload)
        assert r.status_code == 200
        assert "правильный" in r.json()["explanation"].lower()

    def test_explain_answer_unknown_task_returns_400(self, client: TestClient) -> None:
        payload = {
            "game": {"title": "G", "description": "", "tasks": []},
            "taskId": str(uuid4()),
            "answer": {},
        }
        r = client.post("/v1/explain-answer", json=payload)
        assert r.status_code == 400


class TestPromptBuilders:
    def test_normalize_rewrite_mode(self) -> None:
        assert normalize_rewrite_mode("professional") == "сделать профессиональнее"
        assert normalize_rewrite_mode("simple") == "упростить"
        assert normalize_rewrite_mode("hard") == "усложнить"
        assert normalize_rewrite_mode("custom") == "custom"

    def test_build_rewrite_prompt_contains_inputs(self) -> None:
        prompt = build_rewrite_prompt("title", "professional", "Hi")
        assert "сделать профессиональнее" in prompt
        assert "Hi" in prompt
        assert "Поле: title" in prompt

    def test_build_suggest_option_prompt_lists_existing(self) -> None:
        req = SuggestOptionRequest(
            game=GameSnapshot(title="G", description="D"),
            task=TaskSnapshot(text="Q?", type=0, options=["X", "Y"]),
        )
        prompt = build_suggest_option_prompt(req)
        assert "НЕПРАВИЛЬНЫЙ" in prompt
        assert "X; Y" in prompt
        assert '{"text":"..."}' in prompt

    def test_build_suggest_task_prompt_lists_existing_tasks(self) -> None:
        req = SuggestTaskRequest(
            game=GameSnapshot(title="G", description="D"),
            tasks=[TaskSummary(type=0, text="A"), TaskSummary(type=1, text="B")],
        )
        prompt = build_suggest_task_prompt(req)
        assert "- 0: A" in prompt
        assert "- 1: B" in prompt

    def test_explain_prompt_marks_correct_option(self) -> None:
        opt_id = uuid4()
        opt_other = uuid4()
        task = FullTask(
            id=uuid4(),
            order=0,
            type=GameTaskType.QUIZ.value,
            text="Q",
            correct_option_id=opt_id,
            options=[
                FullOption(id=opt_id, text="Right", is_active=True, sort_order=0, is_correct=True),
                FullOption(id=opt_other, text="Wrong", is_active=True, sort_order=1, is_correct=False),
            ],
        )
        game = FullGame(title="G", description="D", tasks=[task])
        prompt = build_explain_answer_prompt(game, task, AnswerDto(selected_option_id=opt_other))
        assert "Right (правильный)" in prompt
        assert "Ответ пользователя: Wrong" in prompt
        assert "Правильный ответ: Right" in prompt


class TestJsonHelpers:
    def test_extract_json_strips_prose(self) -> None:
        raw = 'Here is the JSON: {"text": "hi"} ok'
        assert _extract_json(raw) == '{"text": "hi"}'

    def test_extract_json_balanced_braces(self) -> None:
        raw = 'Nested: {"a": {"b": 1}, "c": 2}'
        assert _extract_json(raw) == '{"a": {"b": 1}, "c": 2}'

    def test_extract_json_falls_back_to_trimmed(self) -> None:
        assert _extract_json("  no json here  ") == "no json here"

    def test_valid_task_quiz(self) -> None:
        assert _is_valid_task_suggestion({
            "type": 0, "text": "Q", "points": 100, "timeLimitMs": 60000,
            "options": ["A", "B"], "correctOptionIndexes": [0],
        })

    def test_valid_task_open_ended_no_options(self) -> None:
        assert _is_valid_task_suggestion({
            "type": GameTaskType.OPEN_ENDED.value,
            "text": "Open Q", "points": 100, "timeLimitMs": 60000,
            "options": [], "correctOptionIndexes": [],
        })

    def test_invalid_task_quiz_without_correct(self) -> None:
        assert not _is_valid_task_suggestion({
            "type": 0, "text": "Q", "points": 100, "timeLimitMs": 60000,
            "options": ["A", "B"], "correctOptionIndexes": [],
        })

    def test_invalid_task_wrong_type(self) -> None:
        assert not _is_valid_task_suggestion({
            "type": 99, "text": "Q", "points": 100, "timeLimitMs": 60000,
            "options": ["A", "B"], "correctOptionIndexes": [0],
        })

    def test_invalid_task_negative_points(self) -> None:
        assert not _is_valid_task_suggestion({
            "type": 0, "text": "Q", "points": -1, "timeLimitMs": 60000,
            "options": ["A", "B"], "correctOptionIndexes": [0],
        })
