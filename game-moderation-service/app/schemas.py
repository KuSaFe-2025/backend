from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, ConfigDict, Field


class _Base(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="ignore")


class Option(_Base):
    text: str
    is_active: bool = Field(alias="isActive", default=True)
    sort_order: int = Field(alias="sortOrder", default=0)


class Task(_Base):
    order: int
    type: int
    text: str
    options: List[Option] = Field(default_factory=list)


class Game(_Base):
    title: str
    description: Optional[str] = None
    tasks: List[Task] = Field(default_factory=list)


class ModerateRequest(_Base):
    game: Game


class ModerateResponse(_Base):
    approved: bool
    yes_votes: int = Field(alias="yesVotes")
    no_votes: int = Field(alias="noVotes")
    decision: str
    model_config = ConfigDict(populate_by_name=True, extra="ignore", serialize_by_alias=True)
