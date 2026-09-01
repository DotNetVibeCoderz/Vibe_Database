#!/usr/bin/env python3
"""Documentation checks for FAISS.Net, run in CI and usable locally.

Two things are checked, both of which are easy to break in a hurry and tedious to notice by eye:

1. **Every relative link and image resolves.** A README that points at a moved file is worse than
   one that does not point at it at all.
2. **The English and Indonesian documentation sets stay in step.** `docs/en/` and `docs/id/` are
   parallel sets by policy; a page added to one and forgotten in the other is a silent regression.
   The filenames differ by design (the Indonesian pages carry Indonesian names), so parity is
   checked on count and on each page declaring a link to its counterpart.

Usage:
    python3 .github/scripts/check_docs.py [root]
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

LINK = re.compile(r'!?\[[^\]]*\]\(([^)\s]+?)(?:\s+"[^"]*")?\)')
SKIP_DIRS = {"bin", "obj", "artifacts", ".git", "node_modules", ".claude", ".vs"}


def markdown_files(root: Path) -> list[Path]:
    return [
        p for p in root.rglob("*.md")
        if not any(part in SKIP_DIRS for part in p.parts)
    ]


def check_links(root: Path) -> list[str]:
    problems: list[str] = []
    for path in markdown_files(root):
        text = path.read_text(encoding="utf-8")
        for match in LINK.finditer(text):
            target = match.group(1).split("#", 1)[0].strip()
            if not target or target.startswith(("http://", "https://", "mailto:", "#")):
                continue
            if not (path.parent / target).resolve().exists():
                problems.append(f"{path.relative_to(root)} -> {target}")
    return problems


def check_language_parity(root: Path) -> list[str]:
    problems: list[str] = []
    english = sorted((root / "docs" / "en").glob("*.md"))
    indonesian = sorted((root / "docs" / "id").glob("*.md"))

    if len(english) != len(indonesian):
        problems.append(
            f"docs/en has {len(english)} pages, docs/id has {len(indonesian)}. "
            "Both languages are updated together — see CLAUDE.md."
        )

    # Each page must link to its counterpart, which is what makes the pairing checkable at all
    # given that the filenames are intentionally different between languages.
    for page in english:
        if "../id/" not in page.read_text(encoding="utf-8"):
            problems.append(f"docs/en/{page.name} has no link to its Indonesian counterpart")
    for page in indonesian:
        if "../en/" not in page.read_text(encoding="utf-8"):
            problems.append(f"docs/id/{page.name} has no link to its English counterpart")

    return problems


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()

    broken = check_links(root)
    parity = check_language_parity(root)

    total = len(markdown_files(root))
    print(f"checked {total} markdown files under {root}")

    if broken:
        print(f"\n{len(broken)} broken link(s):")
        for item in broken:
            print(f"  {item}")

    if parity:
        print(f"\n{len(parity)} documentation parity problem(s):")
        for item in parity:
            print(f"  {item}")

    if broken or parity:
        return 1

    print("all links resolve; English and Indonesian sets are in step")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
