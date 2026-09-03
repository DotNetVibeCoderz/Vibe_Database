#!/usr/bin/env python3
"""Check the documentation for broken links and English/Indonesian drift.

Two failure modes this catches, both of which are invisible in review:

* A relative link or image that points at a file which was renamed or never existed. Markdown does
  not complain, so the link simply does nothing.
* An `en/` page with no `id/` counterpart, or the reverse. The README is bilingual and the docs are
  mirrored, so a page added on one side and forgotten on the other leaves half the audience without
  it.

Run from the MemSharp directory:

    python .github/scripts/check_docs.py .
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# Markdown links and images, excluding reference-style definitions.
LINK = re.compile(r"!?\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")

problems: list[str] = []


def report(path: Path, message: str) -> None:
    problems.append(f"{path}: {message}")


def check_links(root: Path, markdown: Path) -> None:
    """Verify every relative link and image in one file resolves to something on disk."""
    text = markdown.read_text(encoding="utf-8")

    for target in LINK.findall(text):
        # External and in-page links are out of scope: resolving them would make the check depend on
        # the network, and a flaky docs job gets ignored rather than fixed.
        if target.startswith(("http://", "https://", "mailto:", "#")):
            continue

        # Strip any anchor before resolving the path.
        path_part = target.split("#", 1)[0]
        if not path_part:
            continue

        resolved = (markdown.parent / path_part).resolve()
        if not resolved.exists():
            report(markdown.relative_to(root), f"link target does not exist: {target}")


def check_parity(root: Path) -> None:
    """Verify docs/en and docs/id hold the same set of pages."""
    english = root / "docs" / "en"
    indonesian = root / "docs" / "id"

    if not english.is_dir() or not indonesian.is_dir():
        return

    en_pages = {p.name for p in english.glob("*.md")}
    id_pages = {p.name for p in indonesian.glob("*.md")}

    for name in sorted(en_pages - id_pages):
        problems.append(f"docs/id/{name}: missing - docs/en/{name} exists")
    for name in sorted(id_pages - en_pages):
        problems.append(f"docs/en/{name}: missing - docs/id/{name} exists")


def check_images_referenced(root: Path) -> None:
    """Warn about screenshots nothing links to - usually a rename that left the old file behind."""
    images = root / "docs" / "images"
    if not images.is_dir():
        return

    referenced: set[str] = set()
    for markdown in root.rglob("*.md"):
        if any(part in {"bin", "obj", "node_modules", ".git"} for part in markdown.parts):
            continue
        for target in LINK.findall(markdown.read_text(encoding="utf-8")):
            referenced.add(Path(target.split("#", 1)[0]).name)

    for image in sorted(images.glob("*.png")):
        if image.name not in referenced:
            problems.append(f"docs/images/{image.name}: not referenced by any page")


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()

    markdown_files = [
        path
        for path in root.rglob("*.md")
        if not any(part in {"bin", "obj", "node_modules", ".git", "artifacts"} for part in path.parts)
    ]

    for markdown in markdown_files:
        check_links(root, markdown)

    check_parity(root)
    check_images_referenced(root)

    print(f"checked {len(markdown_files)} markdown files under {root}")

    if problems:
        print(f"\n{len(problems)} problem(s):")
        for problem in problems:
            print(f"  {problem}")
        return 1

    print("no problems found")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
