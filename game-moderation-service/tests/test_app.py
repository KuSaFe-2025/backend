from __future__ import annotations

import os
from typing import Iterator

import pytest
from fastapi.testclient import TestClient

os.environ["PROVIDER"] = "deterministic"

from app.main import app 
from app.prompts import build_prompt, extract_reason, first_non_empty_reason 
from app.schemas import Game, Option, Task 


def _safe_game_payload() -> dict:
    return {
        "game": {
            "title": "Math Quiz",
            "description": "Learn basic arithmetic",
            "tasks": [
                {
                    "order": 0,
                    "type": 0,
                    "text": "What is 2+2?",
                    "options": [
                        {"text": "4", "isActive": True, "sortOrder": 0},
                        {"text": "5", "isActive": True, "sortOrder": 1},
                    ],
                }
            ],
        }
    }


@pytest.fixture(scope="module")
def client() -> Iterator[TestClient]:
    with TestClient(app) as c:
        yield c


class TestHealth:
    def test_health(self, client: TestClient) -> None:
        r = client.get("/health")
        assert r.status_code == 200


class TestModerateEndpoint:
    def test_safe_game_is_approved(self, client: TestClient) -> None:
        r = client.post("/v1/moderate", json=_safe_game_payload())
        assert r.status_code == 200
        body = r.json()
        assert body["approved"] is True
        assert body["yesVotes"] == 4
        assert body["noVotes"] == 1
        assert "Approved" in body["decision"]

    def test_banword_game_is_rejected(self, client: TestClient) -> None:
        payload = _safe_game_payload()
        payload["game"]["title"] = "Game about a banword topic"
        r = client.post("/v1/moderate", json=payload)
        body = r.json()
        assert body["approved"] is False
        assert body["yesVotes"] == 1
        assert body["noVotes"] == 4
        assert "blocked word" in body["decision"].lower()

    def test_forbidden_in_option_is_rejected(self, client: TestClient) -> None:
        payload = _safe_game_payload()
        payload["game"]["tasks"][0]["options"][0]["text"] = "this is a forbidden option"
        r = client.post("/v1/moderate", json=payload)
        body = r.json()
        assert body["approved"] is False

    def test_response_uses_camelcase(self, client: TestClient) -> None:
        r = client.post("/v1/moderate", json=_safe_game_payload())
        body = r.json()
        assert "yesVotes" in body
        assert "noVotes" in body
        assert "yes_votes" not in body


class TestPromptBuilder:
    def test_prompt_contains_title_and_description(self) -> None:
        game = Game(title="My Game", description="My description", tasks=[])
        prompt = build_prompt(game)
        assert "Title: My Game" in prompt
        assert "Description: My description" in prompt
        assert "YES:" in prompt and "NO:" in prompt

    def test_prompt_lists_active_options_in_order(self) -> None:
        game = Game(
            title="G",
            description="D",
            tasks=[
                Task(order=0, type=0, text="Q1", options=[
                    Option(text="B", is_active=True, sort_order=1),
                    Option(text="A", is_active=True, sort_order=0),
                    Option(text="HIDDEN", is_active=False, sort_order=2),
                ]),
            ],
        )
        prompt = build_prompt(game)
        a_pos = prompt.find("A")
        b_pos = prompt.find("B")
        hidden_pos = prompt.find("HIDDEN")
        assert 0 <= a_pos < b_pos
        assert hidden_pos == -1

    def test_tasks_are_ordered_by_order_field(self) -> None:
        game = Game(
            title="G",
            description="D",
            tasks=[
                Task(order=1, type=0, text="Second", options=[]),
                Task(order=0, type=0, text="First", options=[]),
            ],
        )
        prompt = build_prompt(game)
        assert prompt.find("First") < prompt.find("Second")


class TestReasonExtraction:
    def test_extract_reason_strips_yes_no_prefix(self) -> None:
        assert extract_reason("NO: contains hate speech.") == "contains hate speech."

    def test_extract_reason_strips_just_no(self) -> None:
        assert extract_reason("NO contains hate speech.") == "contains hate speech."

    def test_extract_reason_handles_no_colon(self) -> None:
        assert extract_reason("Just a sentence!") == "Just a sentence!"

    def test_extract_reason_default_when_empty(self) -> None:
        assert "did not meet" in extract_reason("NO:")

    def test_first_non_empty_reason_picks_first(self) -> None:
        assert first_non_empty_reason(["", "  ", "real reason", "another"]) == "real reason"

    def test_first_non_empty_reason_fallback(self) -> None:
        assert "did not meet" in first_non_empty_reason(["", "  "])
