from __future__ import annotations

import json
from typing import List
from uuid import UUID

from .schemas import (
    AnswerDto,
    FullGame,
    FullTask,
    GameSnapshot,
    GameTaskType,
    SuggestOptionRequest,
    SuggestTaskRequest,
    TaskSnapshot,
)


def normalize_rewrite_mode(mode: str) -> str:
    return {
        "professional": "сделать профессиональнее",
        "simple": "упростить",
        "hard": "усложнить",
    }.get(mode, mode)


def build_rewrite_prompt(field: str, mode: str, text: str) -> str:
    return (
        "Ты помогаешь автору образовательной игры KuSaFe.\n"
        "Верни только переписанный русский текст без пояснений и Markdown-оберток.\n"
        f"Поле: {field}\n"
        f"Действие: {normalize_rewrite_mode(mode)}\n"
        "Исходный текст:\n"
        f"{text}\n"
    )


def build_suggest_option_prompt(req: SuggestOptionRequest) -> str:
    existing = "; ".join(req.task.options or [])
    return (
        "Ты придумываешь вариант ответа для образовательной игры KuSaFe.\n"
        'Верни JSON строго вида {"text":"..."}. Никаких пояснений.\n'
        "Пиши на русском языке. Не повторяй существующие варианты.\n"
        "Важно: предложи именно НЕПРАВИЛЬНЫЙ, но правдоподобный вариант ответа. "
        "Не возвращай правильный ответ.\n"
        f"Название игры: {req.game.title}\n"
        f"Описание игры: {req.game.description or ''}\n"
        f"Текст задачи: {req.task.text}\n"
        f"Тип задачи: {req.task.type}\n"
        f"Текущие варианты: {existing}\n"
    )


def build_suggest_task_prompt(req: SuggestTaskRequest) -> str:
    existing = "\n".join(f"- {t.type}: {t.text}" for t in req.tasks)
    return (
        "Ты придумываешь новую задачу для образовательной игры KuSaFe.\n"
        'Верни JSON строго вида {"type":0,"text":"...","points":100,"timeLimitMs":60000,'
        '"options":["...","..."],"correctOptionIndexes":[0]}.\n'
        "type: 0 викторина, 1 верно/неверно, 2 порядок, 3 открытый ответ, 4 опрос, 5 множественный выбор.\n"
        "Если вопрос ожидает один правильный вариант ответа, используй type 0 (викторина), а не type 5. "
        "Используй type 5 только когда нужно выбрать несколько правильных вариантов одновременно.\n"
        "Пиши на русском языке. Никаких пояснений вне JSON.\n"
        f"Название игры: {req.game.title}\n"
        f"Описание игры: {req.game.description or ''}\n"
        "Уже существующие задачи:\n"
        f"{existing}\n"
    )


def build_explain_answer_prompt(game: FullGame, task: FullTask, answer: AnswerDto) -> str:
    submitted = _describe_submitted_answer(task, answer)
    correct = _describe_correct_answer(task)

    return (
        "Ты объясняешь результат прохождения образовательной игры KuSaFe.\n"
        "Ответь на русском языке кратко: 1-3 предложения.\n"
        "Объясни именно почему правильный ответ является правильным. "
        "Не оценивай пользователя и не используй Markdown.\n"
        "\n"
        "Полное содержимое игры:\n"
        f"Название: {game.title}\n"
        f"Описание: {game.description or ''}\n"
        "Задания:\n"
        f"{_build_game_snapshot(game)}\n"
        "\n"
        "Текущее задание:\n"
        f"{_build_task_snapshot(task)}\n"
        f"Ответ пользователя: {submitted}\n"
        f"Правильный ответ: {correct}\n"
    )



def _is_correct_option(task: FullTask, option) -> bool:
    t = task.type
    if t in (GameTaskType.QUIZ, GameTaskType.TRUE_FALSE):
        return option.id == task.correct_option_id
    if t == GameTaskType.MULTICHOICE:
        return option.is_correct
    if t == GameTaskType.PUZZLE:
        return True
    return False


def _build_task_snapshot(task: FullTask) -> str:
    active_opts = sorted([o for o in task.options if o.is_active], key=lambda o: o.sort_order)
    option_lines = [
        f"- {o.text}{' (правильный)' if _is_correct_option(task, o) else ''}"
        for o in active_opts
    ]
    return (
        f"{task.order + 1}. Тип: {task.type}. Текст: {task.text}\n"
        f"Варианты:\n" + "\n".join(option_lines)
    ).strip()


def _build_game_snapshot(game: FullGame) -> str:
    return "\n".join(_build_task_snapshot(t) for t in sorted(game.tasks, key=lambda t: t.order))


def _describe_submitted_answer(task: FullTask, answer: AnswerDto) -> str:
    option_map = {o.id: o.text for o in task.options}

    if answer.selected_option_id and answer.selected_option_id in option_map:
        return option_map[answer.selected_option_id]

    if answer.text_answer and answer.text_answer.strip():
        return answer.text_answer

    ordered = _parse_guid_list(answer.submitted_order)
    if ordered:
        return " -> ".join(option_map.get(oid, str(oid)) for oid in ordered)

    return "не указан"


def _describe_correct_answer(task: FullTask) -> str:
    active = [o for o in task.options if o.is_active and _is_correct_option(task, o)]
    if task.type == GameTaskType.PUZZLE:
        correct = sorted(active, key=lambda o: o.sort_order)
    else:
        correct = sorted(active, key=lambda o: o.text)
    correct_texts = [o.text for o in correct]

    if task.type == GameTaskType.PUZZLE and correct_texts:
        return " -> ".join(correct_texts)
    if correct_texts:
        return ", ".join(correct_texts)
    if task.open_ended_accepted_answer and task.open_ended_accepted_answer.strip():
        return task.open_ended_accepted_answer
    return "у опроса нет правильного ответа" if task.type == GameTaskType.POLL else "не задан"


def _parse_guid_list(raw: str | None) -> List[UUID]:
    if not raw or not raw.strip():
        return []
    try:
        parsed = json.loads(raw)
        if not isinstance(parsed, list):
            return []
        return [UUID(str(v)) for v in parsed]
    except (json.JSONDecodeError, ValueError, TypeError):
        return []
