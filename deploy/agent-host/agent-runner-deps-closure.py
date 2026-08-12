#!/usr/bin/python3
"""Validate that a staged agent-host publish contains its managed runtime closure."""

from __future__ import annotations

import json
import os
import stat
import sys
from pathlib import Path, PurePosixPath
from typing import Any


class DependencyClosureError(ValueError):
    """The deps.json document does not expose a usable runtime target."""


def _require_mapping(value: Any, description: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise DependencyClosureError(f"{description} must be a JSON object")
    return value


def runtime_assembly_names(document: dict[str, Any]) -> list[str]:
    runtime_target = _require_mapping(document.get("runtimeTarget"), "runtimeTarget")
    target_name = runtime_target.get("name")
    if not isinstance(target_name, str) or not target_name:
        raise DependencyClosureError("runtimeTarget.name must be a non-empty string")

    targets = _require_mapping(document.get("targets"), "targets")
    target = _require_mapping(
        targets.get(target_name),
        f"targets[{target_name!r}]",
    )

    assembly_names: set[str] = set()
    for library_name, untyped_library in target.items():
        library = _require_mapping(
            untyped_library,
            f"targets[{target_name!r}][{library_name!r}]",
        )

        runtime_assets = _require_mapping(
            library.get("runtime", {}),
            f"runtime assets for {library_name!r}",
        )
        for asset_path in runtime_assets:
            _add_managed_assembly_name(assembly_names, asset_path)

        runtime_targets = _require_mapping(
            library.get("runtimeTargets", {}),
            f"runtimeTargets for {library_name!r}",
        )
        for asset_path, untyped_metadata in runtime_targets.items():
            metadata = _require_mapping(
                untyped_metadata,
                f"runtime target metadata for {asset_path!r}",
            )
            if metadata.get("assetType") == "runtime":
                _add_managed_assembly_name(assembly_names, asset_path)

    return sorted(assembly_names)


def _add_managed_assembly_name(names: set[str], asset_path: Any) -> None:
    if not isinstance(asset_path, str):
        raise DependencyClosureError("runtime asset names must be strings")
    normalized_path = asset_path.replace("\\", "/")
    if normalized_path.lower().endswith(".dll"):
        names.add(PurePosixPath(normalized_path).name)


def find_missing_runtime_assemblies(
    document: dict[str, Any],
    staged_root: Path,
) -> list[str]:
    missing: list[str] = []
    for assembly_name in runtime_assembly_names(document):
        candidate = staged_root / assembly_name
        try:
            mode = os.stat(candidate, follow_symlinks=False).st_mode
        except FileNotFoundError:
            missing.append(assembly_name)
            continue
        if not stat.S_ISREG(mode):
            missing.append(assembly_name)
    return missing


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(
            "usage: agent-runner-deps-closure <agent-host.deps.json> <staged-root>",
            file=sys.stderr,
        )
        return 2

    deps_path = Path(argv[1])
    staged_root = Path(argv[2])
    try:
        with deps_path.open("r", encoding="utf-8") as deps_file:
            untyped_document = json.load(deps_file)
        document = _require_mapping(untyped_document, "deps.json root")
        missing = find_missing_runtime_assemblies(document, staged_root)
    except (OSError, UnicodeError, json.JSONDecodeError, DependencyClosureError) as error:
        print(f"invalid agent-host.deps.json: {error}", file=sys.stderr)
        return 2

    if missing:
        print(f"missing runtime assemblies: {', '.join(missing)}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
