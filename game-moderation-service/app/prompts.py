from __future__ import annotations

from .schemas import Game

DEFAULT_REJECTION_REASON = "The content did not meet KuSaFe safety rules."


def build_prompt(game: Game) -> str:
    lines: list[str] = []
    lines.append("You are moderating a user-created educational game.")
    lines.append("Return exactly YES: followed by one short sentence if this content is safe for a public educational platform.")
    lines.append("Return exactly NO: followed by one short sentence if it contains prohibited, hateful, sexual, violent, illegal, or otherwise unsafe content.")
    lines.append("")
    lines.append(f"Title: {game.title}")
    lines.append(f"Description: {game.description or ''}")
    lines.append("Tasks:")

    for task in sorted(game.tasks, key=lambda t: t.order):
        lines.append(f"- Type: {task.type}; Text: {task.text}")
        options_text = "; ".join(
            o.text for o in sorted([o for o in task.options if o.is_active], key=lambda o: o.sort_order)
        )
        lines.append(f"  Options: {options_text}")

    return "\n".join(lines) + "\n"


def extract_reason(response: str) -> str:
    trimmed = response.strip()
    colon = trimmed.find(":")
    reason = trimmed[colon + 1 :].strip() if colon >= 0 else trimmed
    if reason[:2].upper() == "NO":
        reason = reason[2:].strip()
    if not reason:
        return DEFAULT_REJECTION_REASON

    for i, ch in enumerate(reason):
        if ch in ".!?":
            return reason[: i + 1].strip()
    return reason.strip()


def first_non_empty_reason(reasons: list[str]) -> str:
    for r in reasons:
        if r and r.strip():
            return r
    return DEFAULT_REJECTION_REASON
