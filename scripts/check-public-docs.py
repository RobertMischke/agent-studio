#!/usr/bin/env python3
"""Fail when public Agent Studio documentation leaks retired names or local paths."""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path


FORBIDDEN_PATTERNS = {
    "retired personal repository owner": re.compile(r"(?:github\.com/)?RobertMischke/", re.IGNORECASE),
    "personal Windows profile path": re.compile(r"C:\\\\Users\\\\rmisc\\\\", re.IGNORECASE),
    "retired Runner product name": re.compile(r"\b(?:Coding )?Agent Runner\b"),
}

# These are explicit public release claims recorded in
# docs/operations/public-documentation-truth-check.md. They are deliberately
# checked against their registries instead of trusting copied version strings.
REGISTRY_CLAIMS = (
    ("NuGet", "TokenEconomy", "0.2.0", "https://api.nuget.org/v3-flatcontainer/tokeneconomy/index.json"),
    ("NuGet", "CodingAgentRunner", "0.6.0", "https://api.nuget.org/v3-flatcontainer/codingagentrunner/index.json"),
    ("npm", "coding-agent-chat", "0.3.2", "https://registry.npmjs.org/coding-agent-chat/0.3.2"),
)


def public_documentation_files(root: Path) -> list[Path]:
    files = [root / "README.md"]
    docs = root / "docs"
    if docs.exists():
        files.extend(
            path
            for path in docs.rglob("*")
            if path.is_file()
            and (
                path.suffix == ".md"
                or path.suffix == ".html"
                or path.name.endswith(".html.meta.json")
            )
            and "project-map-history" not in path.parts
        )
    return [path for path in files if path.is_file()]


def check_documentation(root: Path) -> list[str]:
    findings: list[str] = []
    for path in public_documentation_files(root):
        text = path.read_text(encoding="utf-8")
        for description, pattern in FORBIDDEN_PATTERNS.items():
            for match in pattern.finditer(text):
                line = text.count("\n", 0, match.start()) + 1
                findings.append(f"{path.relative_to(root)}:{line}: {description}")
    return findings


def get_json(url: str) -> object:
    request = urllib.request.Request(url, headers={"User-Agent": "agent-orc-public-docs-check"})
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.load(response)


def check_registry_claims() -> list[str]:
    findings: list[str] = []
    for registry, package, version, url in REGISTRY_CLAIMS:
        try:
            response = get_json(url)
        except (OSError, urllib.error.URLError, urllib.error.HTTPError, json.JSONDecodeError) as error:
            findings.append(f"{registry} {package} {version}: registry lookup failed: {error}")
            continue

        if registry == "NuGet":
            versions = response.get("versions", []) if isinstance(response, dict) else []
            if version not in versions:
                findings.append(f"NuGet {package} {version}: version is not published")
        elif not isinstance(response, dict) or response.get("version") != version:
            findings.append(f"npm {package} {version}: version is not published")
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--verify-registry", action="store_true")
    args = parser.parse_args()

    findings = check_documentation(args.root.resolve())
    if args.verify_registry:
        findings.extend(check_registry_claims())

    if findings:
        print("Public documentation truth check failed:", file=sys.stderr)
        print("\n".join(f"- {finding}" for finding in findings), file=sys.stderr)
        return 1

    print("Public documentation truth check passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
