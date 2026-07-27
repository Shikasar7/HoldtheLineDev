"""提示词生成器:读卡牌/领袖 JSON + 风格圣经,拼装每张卡的完整出图提示词。

输出:
  out/prompts.json         全量提示词(id -> {prompt, negative, type, faction, name})
  out/exploration_pack.md  风格探索包(ChatGPT 手动出图用,可直接复制粘贴)

用法:
  python generate_prompts.py                 # 全量 + 探索包
  python generate_prompts.py --ids wp_pup    # 只生成指定卡
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

TOOLS_DIR = Path(__file__).parent
REPO_ROOT = TOOLS_DIR.parent.parent
CARDS_DIR = REPO_ROOT / "game" / "data" / "cards"
LEADERS_FILE = REPO_ROOT / "game" / "data" / "leaders" / "leaders.json"
STYLE_BIBLE = TOOLS_DIR / "style_bible.json"
ART_ENTRIES_FILE = TOOLS_DIR / "art_entries.json"
OUT_DIR = TOOLS_DIR / "out"


def load_entries() -> list[dict]:
    """收集所有需要出图的条目:卡牌 + 领袖 + 风格圣经里的额外资产(棋盘等)。"""
    entries = []
    for f in sorted(CARDS_DIR.glob("*.json")):
        entries.extend(json.loads(f.read_text(encoding="utf-8")))
    for leader in json.loads(LEADERS_FILE.read_text(encoding="utf-8")):
        leader = {**leader, "type": "leader"}
        entries.append(leader)
    # 允许美术先于玩法数据施工。正式卡牌 JSON 中已有的 id 始终优先，
    # 这样卡牌入库后无需同步删除美术侧条目，也不会产生重复提示词。
    if ART_ENTRIES_FILE.exists():
        known_ids = {entry["id"] for entry in entries}
        entries.extend(
            entry
            for entry in json.loads(ART_ENTRIES_FILE.read_text(encoding="utf-8"))
            if entry["id"] not in known_ids
        )
    bible = json.loads(STYLE_BIBLE.read_text(encoding="utf-8"))
    entries.extend(bible.get("extra_assets", []))
    return entries


def build_prompt(entry: dict, bible: dict) -> dict:
    kind = entry["type"] if entry["type"] in bible["composition"] else "unit"
    parts = [
        bible["global_style"],
        bible["composition"][kind],
    ]
    faction = entry.get("faction")
    if faction:
        parts.append(bible["factions"][faction]["block"])
    parts.append("Subject: " + entry["art_prompt"])
    return {
        "name": entry.get("name", entry["id"]),
        "type": entry["type"],
        "faction": faction,
        "prompt": ". ".join(parts),
        "negative": bible["negative_prompt"],
    }


def write_exploration_pack(prompts: dict[str, dict], bible: dict) -> Path:
    lines = [
        "# 风格探索包(ChatGPT 手动出图)",
        "",
        f"风格圣经版本:{bible['version']}",
        "",
        "每个条目把 Prompt 整段贴给 ChatGPT 生成(负面提示词 gpt-image 不直接支持,"
        "已在正向提示词中以约束句式表达;本文件仅列正向)。",
        "每张出 2~4 个变体,挑风格方向,不追求单张完美。验收标准:并排看 5 张图像同一个游戏。",
        "",
    ]
    for pid in bible["exploration_pack"]:
        p = prompts.get(pid)
        if p is None:
            lines.append(f"## {pid}\n\n**缺失:卡牌数据里没有这个 id**\n")
            continue
        lines += [
            f"## {p['name']}({pid},{p['type']})",
            "",
            "```",
            p["prompt"],
            "```",
            "",
        ]
    out = OUT_DIR / "exploration_pack.md"
    out.write_text("\n".join(lines), encoding="utf-8")
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ids", nargs="*", help="只生成这些 id(默认全量)")
    args = parser.parse_args()

    bible = json.loads(STYLE_BIBLE.read_text(encoding="utf-8"))
    entries = load_entries()
    if args.ids:
        entries = [e for e in entries if e["id"] in args.ids]

    prompts = {e["id"]: build_prompt(e, bible) for e in entries}

    OUT_DIR.mkdir(exist_ok=True)
    out_json = OUT_DIR / "prompts.json"
    out_json.write_text(
        json.dumps(prompts, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"{len(prompts)} prompts -> {out_json}")

    if not args.ids:
        pack = write_exploration_pack(prompts, bible)
        print(f"exploration pack -> {pack}")


if __name__ == "__main__":
    main()
