from __future__ import annotations

from enum import IntEnum
from typing import Optional, List
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field


class GameTaskType(IntEnum):
    QUIZ = 0
    TRUE_FALSE = 1
    PUZZLE = 2
    OPEN_ENDED = 3
    POLL = 4
    MULTICHOICE = 5


class _Base(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="ignore")


class RewriteRequest(_Base):
    field: str
    mode: str
    text: str


class RewriteResponse(_Base):
    text: str


class GameSnapshot(_Base):
    title: str
    description: Optional[str] = None


class TaskSnapshot(_Base):
    text: str
    type: int = 0
    options: List[str] = Field(default_factory=list)


class SuggestOptionRequest(_Base):
    game: GameSnapshot
    task: TaskSnapshot


class SuggestOptionResponse(_Base):
    text: str


class TaskSummary(_Base):
    type: int
    text: str


class SuggestTaskRequest(_Base):
    game: GameSnapshot
    tasks: List[TaskSummary] = Field(default_factory=list)


class SuggestTaskResponse(_Base):
    type: int
    text: str
    points: int = Field(alias="points")
    time_limit_ms: int = Field(alias="timeLimitMs")
    options: List[str] = Field(default_factory=list)
    correct_option_indexes: List[int] = Field(alias="correctOptionIndexes", default_factory=list)

    model_config = ConfigDict(populate_by_name=True, extra="ignore", serialize_by_alias=True)

class FullOption(_Base):
    id: UUID
    text: str
    is_active: bool = Field(alias="isActive", default=True)
    sort_order: int = Field(alias="sortOrder", default=0)
    is_correct: bool = Field(alias="isCorrect", default=False)


class FullTask(_Base):
    id: UUID
    order: int
    type: int
    text: str
    correct_option_id: Optional[UUID] = Field(alias="correctOptionId", default=None)
    open_ended_accepted_answer: Optional[str] = Field(alias="openEndedAcceptedAnswer", default=None)
    options: List[FullOption] = Field(default_factory=list)


class FullGame(_Base):
    title: str
    description: Optional[str] = None
    tasks: List[FullTask] = Field(default_factory=list)


class AnswerDto(_Base):
    selected_option_id: Optional[UUID] = Field(alias="selectedOptionId", default=None)
    text_answer: Optional[str] = Field(alias="textAnswer", default=None)
    submitted_order: Optional[str] = Field(alias="submittedOrder", default=None)


class ExplainAnswerRequest(_Base):
    game: FullGame
    task_id: UUID = Field(alias="taskId")
    answer: AnswerDto


class ExplainAnswerResponse(_Base):
    explanation: str
