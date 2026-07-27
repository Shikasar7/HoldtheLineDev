#!/usr/bin/env python3
"""Apply the project's established card-description typography to every card.

Manual entries (``generated`` absent/false) are never overwritten. Entries made by this tool carry
``generated: true``; editing one in the in-game card editor turns it into a manual override.
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CARDS_DIR = ROOT / "game" / "data" / "cards"
OUTPUT = ROOT / "game" / "data" / "card_text_formatting.json"

SERIF = "res://assets/fonts/SourceHanSerifSC-Bold.otf"
YELLOW = "#e0b24a"
RED = "#df7467"
TEAL = "#55c7b8"

# Terms are ordered by length by the matcher, so e.g. 免疫薪炎 wins over 薪炎.
KEYWORDS = {
    "法术护体", "免疫薪炎伤害", "免疫薪炎", "熔岩巨剑", "薪火回响", "不焚",
    "战吼", "亡语", "暗牌", "成长", "归魂", "蓄能", "引导", "加深",
    "冲锋", "突袭", "嘲讽", "坚守", "践踏", "偷袭", "持盾", "驻防",
    "跃障", "围猎", "潜行", "架设", "贯穿", "福泽", "守护", "定身",
    "疾行", "射程",
}

DAMAGE_TERMS = {
    "薪炎灼蚀伤害", "薪炎伤害", "灼蚀伤害", "薪炎灼蚀", "薪炎",
}


def tag(text: str, *, color: str | None = None, serif: bool = True) -> str:
    result = text
    if serif:
        result = f"[font={SERIF}]{result}[/font]"
    if color:
        result = f"[color={color}]{result}[/color]"
    return result


def semantic_lines(text: str) -> str:
    text = text.replace("\r\n", "\n").strip()
    # Match the client renderer's BodyText convention for a single trailing sentence mark.
    if text.endswith("。") and text.count("。") == 1:
        text = text[:-1]
    text = re.sub(r"。(?=\S)", "。\n", text)
    text = re.sub(r"[;；](?=\S)", "；\n", text)
    # The user's edited 教团 cards consistently separate a recurring condition from its result.
    text = re.sub(r"^(你每[^,，\n]{1,32}(?:后|时))[,，]", r"\1\n", text)
    return text


def make_matcher() -> re.Pattern[str]:
    keyword = "|".join(re.escape(x) for x in sorted(KEYWORDS, key=len, reverse=True))
    damage = "|".join(re.escape(x) for x in sorted(DAMAGE_TERMS, key=len, reverse=True))
    return re.compile(
        rf"(?P<damage>{damage})"
        rf"|(?P<keyword>{keyword})"
        rf"|(?P<stat>[+-]\d+(?:\s*/\s*[+-]?\d+)?)"
        rf"|(?P<number>\d+)"
    )


MATCHER = make_matcher()


def format_text(text: str) -> str:
    text = semantic_lines(text)

    def replace(match: re.Match[str]) -> str:
        value = match.group(0)
        if match.lastgroup == "damage":
            return tag(value, color=RED)
        if match.lastgroup == "keyword":
            return tag(value, color=YELLOW)
        if match.lastgroup == "stat":
            return tag(value, color=TEAL)
        return tag(value)

    return MATCHER.sub(replace, text)


def load_cards() -> list[dict]:
    cards: list[dict] = []
    for path in sorted(CARDS_DIR.glob("*.json")):
        cards.extend(json.loads(path.read_text(encoding="utf-8")))
    return cards


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true", help="write the generated configuration")
    args = parser.parse_args()

    existing = json.loads(OUTPUT.read_text(encoding="utf-8")) if OUTPUT.exists() else {}
    manual = {card_id: value for card_id, value in existing.items() if not value.get("generated", False)}
    result: dict[str, dict] = {}
    generated = 0
    empty = 0

    cards = load_cards()
    for card in cards:
        card_id = card["id"]
        if card_id in manual:
            result[card_id] = manual[card_id]
            continue
        text = card.get("text", "").strip()
        if not text:
            empty += 1
            continue
        result[card_id] = {"bbcode": format_text(text), "generated": True}
        generated += 1

    expected = sum(bool(card.get("text", "").strip()) for card in cards)
    if len(result) != expected:
        raise RuntimeError(f"coverage mismatch: formatted={len(result)} nonempty_cards={expected}")
    for card_id, value in result.items():
        bbcode = value["bbcode"]
        for name in ("font", "color", "b", "i", "u"):
            opens = len(re.findall(rf"\[{name}(?:=[^]]+)?\]", bbcode))
            closes = bbcode.count(f"[/{name}]")
            if opens != closes:
                raise RuntimeError(f"unbalanced [{name}] tags in {card_id}: {opens} open, {closes} close")

    print(f"manual={len(manual)} generated={generated} empty={empty} total={len(result)}")
    if args.write:
        OUTPUT.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"wrote {OUTPUT}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
