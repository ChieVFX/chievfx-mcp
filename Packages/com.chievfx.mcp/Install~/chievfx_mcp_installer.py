#!/usr/bin/env python3
"""ChievFX Unity MCP installer.

Drag-and-drop two folders:
- FROM: root of this repo (the one shipping `Tools~/ChievfxMcp/` and
  `Editor/ChievfxMcp/` under `Packages/com.chievfx.mcp/`).
- TO: root of another Unity project where the MCP should be installed.

Click `Install`. Old MCP sources in TO are removed, fresh copies from FROM
replace them at the same relative paths.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable


APP_TITLE = "ChievFX Unity MCP Installer"
APP_VERSION = "0.4.5"
SETTINGS_ROOT = Path.home() / ".chievfx_mcp_installer"
LEGACY_SETTINGS_PATH = Path.home() / ".chievfx_mcp_installer.json"
DEFAULT_PROFILE_CONTEXT = Path("__default__")


PACKAGE_NAME = "com.chievfx.mcp"
# Project-root folder, outside Assets/, so Unity never imports the .tgz as an asset (no .meta churn,
# no reimport on every install). manifest.json references it as file:../PackagesSource/<name>.tgz.
DEFAULT_TGZ_DEST_FOLDER = "PackagesSource"
# Prior installer versions dropped the tarball inside Assets/; profiles saved with that default are
# migrated to the new one on load (a deliberately customized folder is kept).
LEGACY_TGZ_DEST_FOLDER = "Assets/Editor"

MCP_PATHS: tuple[str, ...] = (
    # New MCP lives as a Unity package inside `Packages/com.chievfx.mcp/`.
    "Packages/com.chievfx.mcp",
)
MCP_META_PATHS: tuple[str, ...] = (
    # `Packages/com.chievfx.mcp/` contains its own package root meta in Unity.
    # Keep explicit list for any edge cases where repo ships meta files separately.
    "Packages/com.chievfx.mcp.meta",
)
MCP_TEST_PATHS: tuple[str, ...] = ()
# .venv holds the installer's own PyQt6 environment (tens of MB, machine-specific). It is created on
# demand next to this script, so it must never be copied into a target project or packed into a .tgz.
COPY_IGNORE_DIRS: frozenset[str] = frozenset({"__pycache__", "tests", ".venv"})
COPY_IGNORE_SUFFIXES: tuple[str, ...] = (".pyc", ".pyo")


@dataclass(frozen=True)
class ValidationResult:
    ok: bool
    message: str


def _package_server_script(path: Path) -> Path | None:
    """Return the MCP server entry script under a Unity project root, if present."""
    for tools_dir in ("Tools~", "Tools"):
        candidate = (
            path
            / "Packages"
            / "com.chievfx.mcp"
            / tools_dir
            / "ChievfxMcp"
            / "chievfx_mcp_server.py"
        )
        if candidate.is_file():
            return candidate
    return None


def _package_bridge_host(path: Path) -> Path | None:
    candidate = (
        path
        / "Packages"
        / "com.chievfx.mcp"
        / "Editor"
        / "ChievfxMcp"
        / "Bridge"
        / "ChievfxMcpBridgeHost.cs"
    )
    return candidate if candidate.is_file() else None


def validate_from(path: Path) -> ValidationResult:
    """FROM must contain MCP sources."""
    if not path.is_dir():
        return ValidationResult(False, "Not a folder.")
    missing: list[str] = []
    if _package_server_script(path) is None:
        missing.append(
            "Packages/com.chievfx.mcp/Tools~/ChievfxMcp/chievfx_mcp_server.py"
        )
    if _package_bridge_host(path) is None:
        missing.append(
            "Packages/com.chievfx.mcp/Editor/ChievfxMcp/Bridge/ChievfxMcpBridgeHost.cs"
        )
    if missing:
        return ValidationResult(False, "Missing: " + ", ".join(missing))
    return ValidationResult(True, "MCP sources detected.")


def validate_to(path: Path) -> ValidationResult:
    """TO must look like a Unity project root."""
    if not path.is_dir():
        return ValidationResult(False, "Not a folder.")
    if not (path / "Assets").is_dir():
        return ValidationResult(False, "No `Assets/` folder. Pick a Unity project root.")
    if not (path / "Packages" / "manifest.json").is_file():
        return ValidationResult(False, "No `Packages/manifest.json`. Pick a Unity project root.")
    return ValidationResult(True, "Looks like a Unity project.")


@dataclass(frozen=True)
class InstallerProfile:
    context_path: Path
    last_from_path: Path | None
    to_paths: list[Path]
    install_as_tgz: bool = False
    tgz_dest_folder: str = DEFAULT_TGZ_DEST_FOLDER


def detect_from_root(
    start: Path | None = None,
    extra_candidates: Iterable[Path] | None = None,
) -> Path | None:
    """Walk up from the installer folder until a valid MCP source root is found."""
    seen: set[str] = set()
    ordered: list[Path] = []

    def consider(path: Path | None) -> None:
        if path is None:
            return
        try:
            resolved = path.expanduser().resolve()
        except OSError:
            return
        key = str(resolved)
        if key in seen:
            return
        seen.add(key)
        ordered.append(resolved)

    for path in extra_candidates or ():
        consider(path)

    current = start or Path(__file__).resolve().parent
    consider(current)
    for parent in current.parents:
        consider(parent)

    for candidate in ordered:
        if validate_from(candidate).ok:
            return candidate
    return None


def detect_host_unity_project(start: Path | None = None) -> Path | None:
    """Walk up from the installer folder until a Unity project root is found."""
    current = start or Path(__file__).resolve().parent
    for candidate in (current, *current.parents):
        if validate_to(candidate).ok:
            return candidate.resolve()
    return None


def resolve_profile_context(launcher_project: str | None = None) -> Path:
    if launcher_project:
        candidate = Path(launcher_project).expanduser()
        if validate_to(candidate).ok:
            return candidate.resolve()

    host = detect_host_unity_project()
    if host is not None:
        return host

    return DEFAULT_PROFILE_CONTEXT


def _profile_context_key(context_path: Path) -> str:
    if context_path == DEFAULT_PROFILE_CONTEXT:
        return str(DEFAULT_PROFILE_CONTEXT)
    return str(context_path.resolve())


def _profile_settings_path(context_path: Path) -> Path:
    key = hashlib.sha1(_profile_context_key(context_path).encode("utf-8")).hexdigest()[:16]
    return SETTINGS_ROOT / "profiles" / key / "settings.json"


def _normalize_to_paths(raw_paths: Iterable[object]) -> list[Path]:
    paths: list[Path] = []
    seen: set[str] = set()
    for raw_path in raw_paths:
        if not isinstance(raw_path, str) or not raw_path.strip():
            continue
        path = Path(raw_path).expanduser()
        if not validate_to(path).ok:
            continue
        resolved = path.resolve()
        key = str(resolved)
        if key in seen:
            continue
        seen.add(key)
        paths.append(resolved)
    return paths


def _load_legacy_to_paths() -> list[Path]:
    try:
        data = json.loads(LEGACY_SETTINGS_PATH.read_text(encoding="utf-8"))
    except Exception:
        return []
    if not isinstance(data, dict):
        return []

    raw_paths = data.get("toPaths")
    if not isinstance(raw_paths, list):
        raw_paths = [data.get("lastToPath")]
    return _normalize_to_paths(raw_paths)


def load_profile(context_path: Path) -> InstallerProfile:
    settings_path = _profile_settings_path(context_path)
    last_from_path: Path | None = None
    to_paths: list[Path] = []

    try:
        data = json.loads(settings_path.read_text(encoding="utf-8"))
    except Exception:
        data = None

    install_as_tgz = False
    tgz_dest_folder = DEFAULT_TGZ_DEST_FOLDER
    if isinstance(data, dict):
        raw_from = data.get("lastFromPath")
        if isinstance(raw_from, str) and raw_from.strip():
            candidate = Path(raw_from).expanduser()
            if validate_from(candidate).ok:
                last_from_path = candidate.resolve()
        to_paths = _normalize_to_paths(data.get("toPaths", []))
        install_as_tgz = bool(data.get("installAsTgz", False))
        raw_dest = data.get("tgzDestFolder")
        if isinstance(raw_dest, str) and raw_dest.strip():
            tgz_dest_folder = raw_dest.strip()
            if _normalize_dest_folder(tgz_dest_folder) == LEGACY_TGZ_DEST_FOLDER:
                tgz_dest_folder = DEFAULT_TGZ_DEST_FOLDER
    elif context_path == DEFAULT_PROFILE_CONTEXT:
        to_paths = _load_legacy_to_paths()

    return InstallerProfile(
        context_path=context_path,
        last_from_path=last_from_path,
        to_paths=to_paths,
        install_as_tgz=install_as_tgz,
        tgz_dest_folder=tgz_dest_folder,
    )


def save_profile(profile: InstallerProfile) -> None:
    try:
        settings_path = _profile_settings_path(profile.context_path)
        settings_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "contextPath": _profile_context_key(profile.context_path),
            "lastFromPath": (
                str(profile.last_from_path.resolve()) if profile.last_from_path is not None else None
            ),
            "toPaths": [str(path.resolve()) for path in profile.to_paths],
            "installAsTgz": profile.install_as_tgz,
            "tgzDestFolder": profile.tgz_dest_folder,
        }
        settings_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    except Exception:
        # Remembering paths is convenience only; never fail install for it.
        pass


def _ignore_python_cache(_: str, names: list[str]) -> list[str]:
    skip: list[str] = []
    for name in names:
        if name in COPY_IGNORE_DIRS:
            skip.append(name)
        elif name.endswith(COPY_IGNORE_SUFFIXES):
            skip.append(name)
    return skip


def _running_installer_root() -> Path:
    """Package root that owns this running installer script."""
    return Path(__file__).resolve().parents[1]


def _to_contains_running_installer(to_root: Path) -> bool:
    """True when install would delete the live installer package under TO."""
    try:
        running = _running_installer_root().resolve()
        target_package = (to_root / "Packages" / "com.chievfx.mcp").resolve()
    except OSError:
        return False
    return running == target_package


def _remove_path(target: Path, log: Callable[[str], None]) -> None:
    if target.is_symlink() or target.is_file():
        target.unlink()
        log(f"  removed file {target}")
    elif target.is_dir():
        shutil.rmtree(target)
        log(f"  removed dir  {target}")


def _copy_tree(src: Path, dst: Path, log: Callable[[str], None]) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.is_file():
        shutil.copy2(src, dst)
        log(f"  copied file  {src} -> {dst}")
    elif src.is_dir():
        shutil.copytree(src, dst, ignore=_ignore_python_cache)
        log(f"  copied dir   {src} -> {dst}")
    else:
        raise FileNotFoundError(f"Source missing: {src}")


def _iter_install_paths() -> Iterable[str]:
    yield from MCP_PATHS
    yield from MCP_META_PATHS


def _iter_cleanup_paths() -> Iterable[str]:
    yield from _iter_install_paths()
    yield from MCP_TEST_PATHS


def _remove_manifest_dependency(to_root: Path, log: Callable[[str], None]) -> None:
    """Drop dependencies[com.chievfx.mcp] from Packages/manifest.json — whether it is a git url, a
    file: tarball, or a registry version — so the chosen install mode is the only definition left."""
    manifest_path = to_root / "Packages" / "manifest.json"
    if not manifest_path.is_file():
        log(f"  skipped (no manifest) {manifest_path}")
        return
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception as exc:
        log(f"  WARN: could not parse {manifest_path}: {exc}")
        return
    dependencies = manifest.get("dependencies") if isinstance(manifest, dict) else None
    if not isinstance(dependencies, dict) or PACKAGE_NAME not in dependencies:
        log(f"  skipped (no {PACKAGE_NAME} dependency)")
        return
    removed = dependencies.pop(PACKAGE_NAME)
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    log(f"  removed manifest dependency {PACKAGE_NAME} -> {removed}")


def _insert_dependency_alphabetically(dependencies: dict, name: str, value: str) -> dict:
    """A new dependencies dict with name:value placed before the first existing key that sorts after it,
    leaving the existing key order otherwise intact."""
    result: dict = {}
    inserted = False
    for key, existing in dependencies.items():
        if not inserted and key > name:
            result[name] = value
            inserted = True
        result[key] = existing
    if not inserted:
        result[name] = value
    return result


def _set_manifest_dependency(to_root: Path, dependency: str, log: Callable[[str], None]) -> None:
    """Write dependencies[com.chievfx.mcp] in Packages/manifest.json. If the key already exists (any
    prior form — git url, file:, registry version), substitute the value IN PLACE, keeping its position;
    otherwise insert it alphabetically among the existing dependencies."""
    manifest_path = to_root / "Packages" / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict):
        raise ValueError(f"{manifest_path} is not a JSON object.")
    dependencies = manifest.get("dependencies")
    if not isinstance(dependencies, dict):
        dependencies = {}
        manifest["dependencies"] = dependencies

    if PACKAGE_NAME in dependencies:
        previous = dependencies[PACKAGE_NAME]
        dependencies[PACKAGE_NAME] = dependency  # dict keeps the existing key position
        log(f"  substituted {PACKAGE_NAME}: {previous} -> {dependency}")
    else:
        manifest["dependencies"] = _insert_dependency_alphabetically(dependencies, PACKAGE_NAME, dependency)
        log(f"  inserted {PACKAGE_NAME} -> {dependency} (alphabetical)")

    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def _remove_all_tarballs(
    to_root: Path,
    log: Callable[[str], None],
    extra_dirs: Iterable[str] = (),
) -> None:
    """Remove every com.chievfx.mcp-*.tgz (and its .meta) under PackagesSource/, Assets/, or Packages/,
    wherever a prior tarball install dropped it (the dest folder is user-configurable, and older
    installer versions defaulted to Assets/Editor). extra_dirs adds the currently configured dest
    folder when it is outside those bases."""
    found = False
    seen_bases: set[Path] = set()
    for base in (DEFAULT_TGZ_DEST_FOLDER, "Assets", "Packages", *extra_dirs):
        base_dir = to_root / base
        if not base_dir.is_dir() or base_dir in seen_bases:
            continue
        seen_bases.add(base_dir)
        for tgz in sorted(base_dir.rglob(f"{PACKAGE_NAME}-*.tgz")):
            _remove_path(tgz, log)
            meta = tgz.with_name(tgz.name + ".meta")
            if meta.exists():
                _remove_path(meta, log)
            found = True
    if not found:
        log(f"  skipped (no {PACKAGE_NAME}-*.tgz found)")


def clean_all_installations(
    to_root: Path,
    log: Callable[[str], None],
    drop_manifest_dependency: bool = True,
    extra_tarball_dirs: Iterable[str] = (),
) -> None:
    """Remove EVERY form of a prior com.chievfx.mcp install from TO so only the mode being installed
    remains: embedded/copied sources (+ tests) and .tgz tarballs, plus the manifest dependency (git url,
    file: tarball, or registry version) when drop_manifest_dependency is True. The tarball install keeps
    the manifest key so it can substitute the new file: dependency in place (see _set_manifest_dependency)."""
    log("  embedded/copied sources:")
    for rel in _iter_cleanup_paths():
        target = to_root / rel
        if target.exists() or target.is_symlink():
            _remove_path(target, log)
        else:
            log(f"  skipped (absent) {target}")
    log("  tarball files:")
    _remove_all_tarballs(to_root, log, extra_dirs=extra_tarball_dirs)
    if drop_manifest_dependency:
        log("  manifest dependency:")
        _remove_manifest_dependency(to_root, log)


def perform_install(
    from_root: Path,
    to_root: Path,
    log: Callable[[str], None],
) -> None:
    log(f"FROM: {from_root}")
    log(f"TO:   {to_root}")
    log("")
    log("[1/2] Removing every existing MCP install in TO (sources, tarballs, manifest dependency) ...")
    clean_all_installations(to_root, log)

    log("")
    log("[2/2] Copying fresh MCP sources from FROM ...")
    for rel in _iter_install_paths():
        src = from_root / rel
        dst = to_root / rel
        if not src.exists():
            log(f"  WARN: source missing, skipping {src}")
            continue
        _copy_tree(src, dst, log)

    log("")
    log(
        "Package dependencies (newtonsoft-json, test-framework) are declared in "
        "com.chievfx.mcp/package.json; Unity resolves them automatically."
    )
    log("")
    log("Done.")
    log("Next steps in target Unity project:")
    log("  1. Open the project, wait for compile + domain reload.")
    log("  2. Window > ChievFX MCP -> Start Bridge -> Write Cursor Config.")
    log("  3. Reload Cursor MCP tools or restart Cursor.")


def _read_package_version(from_root: Path) -> str:
    package_json = from_root / "Packages" / PACKAGE_NAME / "package.json"
    try:
        data = json.loads(package_json.read_text(encoding="utf-8"))
        version = data.get("version")
        if isinstance(version, str) and version.strip():
            return version.strip()
    except Exception:
        pass
    return "0.0.0"


def _tarball_members(package_dir: Path) -> Iterable[tuple[Path, str]]:
    """(absolute file, arcname) pairs under a top-level ``package/`` root (npm/Unity tarball layout),
    applying the same ignores as the copy install. .meta files are kept — an immutable tarball package
    drops any .cs that lacks its .meta."""
    ignored_dir_metas = {d + ".meta" for d in COPY_IGNORE_DIRS}
    for current, dirnames, filenames in os.walk(package_dir):
        dirnames[:] = sorted(d for d in dirnames if d not in COPY_IGNORE_DIRS)
        for filename in sorted(filenames):
            if filename.endswith(COPY_IGNORE_SUFFIXES) or filename in ignored_dir_metas:
                continue
            abs_path = Path(current) / filename
            arcname = "package/" + abs_path.relative_to(package_dir).as_posix()
            yield abs_path, arcname


def build_package_tarball(from_root: Path, tgz_path: Path, log: Callable[[str], None]) -> None:
    package_dir = from_root / "Packages" / PACKAGE_NAME
    if not (package_dir / "package.json").is_file():
        raise FileNotFoundError(f"Missing {package_dir / 'package.json'}; cannot build a Unity tarball.")
    tgz_path.parent.mkdir(parents=True, exist_ok=True)
    if tgz_path.exists():
        tgz_path.unlink()
    count = 0
    with tarfile.open(tgz_path, "w:gz") as tar:
        for abs_path, arcname in _tarball_members(package_dir):
            tar.add(abs_path, arcname=arcname, recursive=False)
            count += 1
    log(f"  packed {count} files into {tgz_path.name}")


def _relative_file_dependency(tgz_path: Path, packages_dir: Path) -> str:
    # Unity resolves file: tarball paths relative to the project's Packages folder; use posix separators.
    return "file:" + os.path.relpath(tgz_path, packages_dir).replace(os.sep, "/")


def _normalize_dest_folder(dest_folder: str) -> str:
    return (dest_folder or "").strip().strip("/\\") or DEFAULT_TGZ_DEST_FOLDER


# The build suffix makes every install produce a new tarball filename, so the manifest file:
# reference changes and pulling copies re-resolve without a manual package.json version bump. It is
# per-version: the FIRST tarball of a version has no suffix, then .f1, .f2, ... on same-version
# rebuilds; a version bump starts fresh (no suffix again).
#   index -1 -> no tarball for this version yet   (next filename: no suffix)
#   index  0 -> com.chievfx.mcp-<version>.tgz      (next filename: .f1)
#   index  N -> com.chievfx.mcp-<version>.fN.tgz   (next filename: .f(N+1))


def _tgz_filename(version: str, index: int) -> str:
    suffix = f".f{index}" if index >= 1 else ""
    return f"{PACKAGE_NAME}-{version}{suffix}.tgz"


def _tarball_index_for_version(dest_dir: Path, version: str) -> int:
    if not dest_dir.is_dir():
        return -1
    prefix = f"{PACKAGE_NAME}-{version}"
    suffix_re = re.compile(r"^" + re.escape(prefix) + r"\.f(\d+)\.tgz$")
    plain_name = f"{prefix}.tgz"
    best = -1
    for tgz in dest_dir.glob(f"{PACKAGE_NAME}-*.tgz"):
        match = suffix_re.match(tgz.name)
        if match:
            best = max(best, int(match.group(1)))
        elif tgz.name == plain_name:
            best = max(best, 0)
    return best


def compute_next_f_index(to_roots: Iterable[Path], dest_folder: str, version: str) -> int:
    # Shared across all TO folders in one run (take the biggest current index for this version + 1) so
    # the suffix stays identical between projects installed together. -1 (version not present anywhere)
    # -> 0, which _tgz_filename renders suffix-less: a version bump drops the suffix.
    dest_rel = _normalize_dest_folder(dest_folder)
    current = max(
        (_tarball_index_for_version(to_root / Path(dest_rel), version) for to_root in to_roots),
        default=-1,
    )
    return current + 1


def perform_install_tgz(
    from_root: Path,
    to_root: Path,
    dest_folder: str,
    f_index: int,
    log: Callable[[str], None],
) -> None:
    log(f"FROM: {from_root}")
    log(f"TO:   {to_root}")
    log("MODE: tarball (.tgz) dependency")
    log("")

    version = _read_package_version(from_root)
    dest_rel = _normalize_dest_folder(dest_folder)
    dest_dir = to_root / Path(dest_rel)
    tgz_path = dest_dir / _tgz_filename(version, f_index)

    log("[1/3] Removing other existing MCP installs in TO (embedded sources, tarballs) ...")
    clean_all_installations(to_root, log, drop_manifest_dependency=False, extra_tarball_dirs=(dest_rel,))

    log("")
    log(f"[2/3] Building {tgz_path.name} in {dest_rel} ...")
    build_package_tarball(from_root, tgz_path, log)

    log("")
    log("[3/3] Setting the file: dependency in Packages/manifest.json (substitute in place, else insert alphabetically) ...")
    dependency = _relative_file_dependency(tgz_path, to_root / "Packages")
    _set_manifest_dependency(to_root, dependency, log)

    log("")
    log("Done (tarball install).")
    log("Next steps in target Unity project:")
    log("  1. Open the project; Unity imports the tarball as an immutable package.")
    log("  2. Window > ChievFX MCP -> Start Bridge -> Write client config.")
    log("  3. Reload the MCP client tools or restart it.")


def main() -> int:
    # The window needs PyQt6; the install logic above does not, so scripts import it without a GUI stack.
    from chievfx_mcp_installer_gui import main as gui_main

    return gui_main()


if __name__ == "__main__":
    sys.exit(main())
