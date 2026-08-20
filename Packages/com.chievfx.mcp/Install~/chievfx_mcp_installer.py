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

from PyQt6.QtCore import QObject, Qt, QThread, pyqtSignal
from PyQt6.QtGui import QDragEnterEvent, QDropEvent, QFont, QPalette, QColor
from PyQt6.QtWidgets import (
    QAbstractItemView,
    QApplication,
    QCheckBox,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QListView,
    QListWidget,
    QListWidgetItem,
    QMainWindow,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QTreeView,
    QVBoxLayout,
    QWidget,
)


APP_TITLE = "ChievFX Unity MCP Installer"
APP_VERSION = "0.4.5"
SETTINGS_ROOT = Path.home() / ".chievfx_mcp_installer"
LEGACY_SETTINGS_PATH = Path.home() / ".chievfx_mcp_installer.json"
DEFAULT_PROFILE_CONTEXT = Path("__default__")

INSTALL_BUTTON_STYLE = """
QPushButton#InstallButton {
  background-color: #3a6df0;
  color: #ffffff;
  border: 1px solid #2f5ad0;
  border-radius: 6px;
  padding: 8px 18px;
  font-weight: 600;
}
QPushButton#InstallButton:hover:!disabled {
  background-color: #4b7cff;
}
QPushButton#InstallButton:pressed:!disabled {
  background-color: #1f45b0;
  padding-top: 9px;
  padding-bottom: 7px;
}
QPushButton#InstallButton:disabled {
  background-color: #2a2f38;
  color: #6b7078;
  border: 1px solid #3a404a;
}
"""

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


def _macos_bootstrap_foreground_app() -> None:
    """Promote this bare python process to a foreground GUI app on macOS.

    Unity launches ``python`` without an .app bundle. Those processes often show
    Qt windows but never get mouse press/release (buttons look dead — no pressed
    state). ``TransformProcessType`` is the reliable fix for non-bundled tools;
    ``setActivationPolicy`` alone usually fails outside a real .app.
    """
    if sys.platform != "darwin":
        return
    try:
        from ctypes import Structure, byref, c_bool, c_char_p, c_int32, c_uint32, c_void_p, cdll, util

        class ProcessSerialNumber(Structure):
            _fields_ = [("highLongOfPSN", c_uint32), ("lowLongOfPSN", c_uint32)]

        app_services = util.find_library("ApplicationServices")
        if app_services:
            lib = cdll.LoadLibrary(app_services)
            # kCurrentProcess = {0, 2}; kProcessTransformToForegroundApplication = 1
            psn = ProcessSerialNumber(0, 2)
            lib.TransformProcessType.argtypes = [c_void_p, c_int32]
            lib.TransformProcessType.restype = c_int32
            lib.TransformProcessType(byref(psn), 1)

        appkit_name = util.find_library("AppKit")
        objc_name = util.find_library("objc")
        if not appkit_name or not objc_name:
            return
        cdll.LoadLibrary(appkit_name)
        objc = cdll.LoadLibrary(objc_name)
        objc.objc_getClass.restype = c_void_p
        objc.objc_getClass.argtypes = [c_char_p]
        objc.sel_registerName.restype = c_void_p
        objc.sel_registerName.argtypes = [c_char_p]
        objc.objc_msgSend.restype = c_void_p
        objc.objc_msgSend.argtypes = [c_void_p, c_void_p]

        def _cls(name: str) -> int:
            return objc.objc_getClass(name.encode("utf-8"))

        def _sel(name: str) -> int:
            return objc.sel_registerName(name.encode("utf-8"))

        ns_app = objc.objc_msgSend(_cls("NSApplication"), _sel("sharedApplication"))
        if not ns_app:
            return

        # Best-effort; may return NO for non-bundled tools.
        set_policy = objc.objc_msgSend
        set_policy.restype = c_int32
        set_policy.argtypes = [c_void_p, c_void_p, c_int32]
        set_policy(ns_app, _sel("setActivationPolicy:"), 0)

        activate = objc.objc_msgSend
        activate.restype = None
        activate.argtypes = [c_void_p, c_void_p, c_bool]
        activate(ns_app, _sel("activateIgnoringOtherApps:"), True)
    except Exception:
        pass


def _macos_activate_process() -> None:
    """Bring this process frontmost on macOS.

    Unity launches the installer as a bare python subprocess. Without an app
    bundle, macOS often leaves Qt modal dialogs behind Unity, so Install looks
    like a no-op. Asking System Events to activate this PID fixes that.
    """
    if sys.platform != "darwin":
        return
    _macos_bootstrap_foreground_app()
    script = (
        'tell application "System Events" to set frontmost of '
        f"(first process whose unix id is {os.getpid()}) to true"
    )
    try:
        subprocess.run(
            ["osascript", "-e", script],
            check=False,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            timeout=2,
        )
    except Exception:
        # Activation is best-effort; install must still work without it.
        pass


def choose_existing_directories(
    parent: QWidget,
    title: str,
    start_dir: str,
) -> list[Path]:
    """Pick one or more folders.

    Native macOS/Windows folder pickers only allow a single directory, so this
    uses Qt's non-native dialog with extended selection (Cmd/Ctrl-click).
    """
    dialog = QFileDialog(parent, title, start_dir)
    dialog.setFileMode(QFileDialog.FileMode.Directory)
    dialog.setOption(QFileDialog.Option.ShowDirsOnly, True)
    dialog.setOption(QFileDialog.Option.DontUseNativeDialog, True)
    dialog.setOption(QFileDialog.Option.ReadOnly, True)

    for view in dialog.findChildren(QListView):
        view.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)
    for view in dialog.findChildren(QTreeView):
        view.setSelectionMode(QAbstractItemView.SelectionMode.ExtendedSelection)

    if dialog.exec() != QFileDialog.DialogCode.Accepted:
        return []

    selected: list[Path] = []
    seen: set[str] = set()
    for raw in dialog.selectedFiles():
        if not raw:
            continue
        path = Path(raw)
        if not path.is_dir():
            continue
        try:
            resolved = path.resolve()
        except OSError:
            continue
        key = str(resolved)
        if key in seen:
            continue
        seen.add(key)
        selected.append(resolved)
    return selected


def _bring_window_forward(widget: QWidget) -> None:
    widget.show()
    widget.raise_()
    widget.activateWindow()
    _macos_activate_process()


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
    log("  2. Window > ChievFX > MCP -> Start Bridge -> Write Cursor Config.")
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
    log("  2. Window > ChievFX > MCP -> Start Bridge -> Write client config.")
    log("  3. Reload the MCP client tools or restart it.")


class _InstallWorker(QObject):
    log_line = pyqtSignal(str)
    finished_ok = pyqtSignal()
    finished_err = pyqtSignal(str)

    def __init__(
        self,
        from_root: Path,
        to_roots: Iterable[Path],
        install_as_tgz: bool = False,
        tgz_dest_folder: str = DEFAULT_TGZ_DEST_FOLDER,
    ) -> None:
        super().__init__()
        self._from_root = from_root
        self._to_roots = tuple(to_roots)
        self._install_as_tgz = install_as_tgz
        self._tgz_dest_folder = tgz_dest_folder

    def run(self) -> None:
        try:
            total = len(self._to_roots)
            # One shared build index for the whole run so all targets get the same suffix.
            f_index = (
                compute_next_f_index(
                    self._to_roots,
                    self._tgz_dest_folder,
                    _read_package_version(self._from_root),
                )
                if self._install_as_tgz
                else 0
            )
            for index, to_root in enumerate(self._to_roots, start=1):
                self.log_line.emit(f"=== Target {index}/{total}: {to_root} ===")
                if self._install_as_tgz:
                    perform_install_tgz(
                        self._from_root,
                        to_root,
                        self._tgz_dest_folder,
                        f_index,
                        self.log_line.emit,
                    )
                else:
                    perform_install(
                        self._from_root,
                        to_root,
                        self.log_line.emit,
                    )
                if index < total:
                    self.log_line.emit("")
            self.finished_ok.emit()
        except Exception as ex:
            self.log_line.emit("")
            self.log_line.emit(f"ERROR: {ex}")
            self.finished_err.emit(str(ex))


class DropZone(QFrame):
    """Folder drop target with a status badge."""

    path_changed = pyqtSignal(Path)

    def __init__(
        self,
        title: str,
        hint: str,
        validator: Callable[[Path], ValidationResult],
        parent: QWidget | None = None,
    ) -> None:
        super().__init__(parent)
        self._validator = validator
        self._path: Path | None = None

        self.setAcceptDrops(True)
        self.setFrameShape(QFrame.Shape.StyledPanel)
        self.setObjectName("DropZone")
        self.setMinimumHeight(150)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(6)

        self._title_label = QLabel(title)
        title_font = QFont()
        title_font.setBold(True)
        title_font.setPointSize(13)
        self._title_label.setFont(title_font)
        layout.addWidget(self._title_label)

        self._hint_label = QLabel(hint)
        self._hint_label.setWordWrap(True)
        self._hint_label.setStyleSheet("color: #9aa0a6;")
        layout.addWidget(self._hint_label)

        self._path_label = QLabel("Drop folder here, or click `Browse`.")
        self._path_label.setWordWrap(True)
        self._path_label.setStyleSheet("color: #cfd2d6; font-family: monospace;")
        layout.addWidget(self._path_label, 1)

        bottom = QHBoxLayout()
        self._status_label = QLabel("(empty)")
        self._status_label.setStyleSheet("color: #9aa0a6;")
        bottom.addWidget(self._status_label, 1)

        self._browse_button = QPushButton("Browse...")
        self._browse_button.clicked.connect(self._on_browse)
        bottom.addWidget(self._browse_button)

        self._clear_button = QPushButton("Clear")
        self._clear_button.clicked.connect(self._on_clear)
        bottom.addWidget(self._clear_button)

        layout.addLayout(bottom)

        self._apply_idle_style()

    def path(self) -> Path | None:
        return self._path

    def is_valid(self) -> bool:
        return self._path is not None and self._validator(self._path).ok

    def set_path(self, path: Path) -> None:
        self._path = path.resolve()
        self._path_label.setText(str(self._path))
        result = self._validator(self._path)
        self._status_label.setText(result.message)
        if result.ok:
            self._apply_ok_style()
        else:
            self._apply_err_style()
        self.path_changed.emit(self._path)

    def _on_browse(self) -> None:
        start_dir = str(self._path) if self._path else str(Path.home())
        chosen = QFileDialog.getExistingDirectory(self, "Choose folder", start_dir)
        if chosen:
            self.set_path(Path(chosen))

    def _on_clear(self) -> None:
        self._path = None
        self._path_label.setText("Drop folder here, or click `Browse`.")
        self._status_label.setText("(empty)")
        self._apply_idle_style()
        self.path_changed.emit(Path())

    def dragEnterEvent(self, event: QDragEnterEvent) -> None:
        mime = event.mimeData()
        if mime.hasUrls() and any(u.toLocalFile() for u in mime.urls()):
            event.acceptProposedAction()
            self._apply_hover_style()
        else:
            event.ignore()

    def dragLeaveEvent(self, event) -> None:
        if self._path is None:
            self._apply_idle_style()
        elif self._validator(self._path).ok:
            self._apply_ok_style()
        else:
            self._apply_err_style()
        super().dragLeaveEvent(event)

    def dropEvent(self, event: QDropEvent) -> None:
        for url in event.mimeData().urls():
            local = url.toLocalFile()
            if not local:
                continue
            candidate = Path(local)
            if candidate.is_dir():
                self.set_path(candidate)
                event.acceptProposedAction()
                return
        event.ignore()

    def _apply_idle_style(self) -> None:
        self.setStyleSheet(
            "QFrame#DropZone {"
            " border: 2px dashed #4a4f57;"
            " border-radius: 10px;"
            " background-color: #20232a;"
            "}"
        )

    def _apply_hover_style(self) -> None:
        self.setStyleSheet(
            "QFrame#DropZone {"
            " border: 2px dashed #6c8cff;"
            " border-radius: 10px;"
            " background-color: #2a2f3a;"
            "}"
        )

    def _apply_ok_style(self) -> None:
        self.setStyleSheet(
            "QFrame#DropZone {"
            " border: 2px solid #3fb950;"
            " border-radius: 10px;"
            " background-color: #1f2a22;"
            "}"
        )

    def _apply_err_style(self) -> None:
        self.setStyleSheet(
            "QFrame#DropZone {"
            " border: 2px solid #f85149;"
            " border-radius: 10px;"
            " background-color: #2a1f1f;"
            "}"
        )


class MultiTargetZone(QFrame):
    """Drop target that keeps multiple Unity project roots."""

    paths_changed = pyqtSignal()

    def __init__(self, parent: QWidget | None = None) -> None:
        super().__init__(parent)
        self._paths: list[Path] = []

        self.setAcceptDrops(True)
        self.setFrameShape(QFrame.Shape.StyledPanel)
        self.setObjectName("MultiTargetZone")
        self.setMinimumHeight(150)

        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 16, 16, 16)
        layout.setSpacing(6)

        title_label = QLabel("TO (target Unity projects)")
        title_font = QFont()
        title_font.setBold(True)
        title_font.setPointSize(13)
        title_label.setFont(title_font)
        layout.addWidget(title_label)

        hint_label = QLabel(
            "Drop or browse one or more Unity project roots. Each must contain "
            "`Assets/` and `Packages/manifest.json`. In Add…, Cmd/Ctrl-click to "
            "select multiple folders."
        )
        hint_label.setWordWrap(True)
        hint_label.setStyleSheet("color: #9aa0a6;")
        layout.addWidget(hint_label)

        self._list = QListWidget()
        self._list.setStyleSheet(
            "QListWidget {"
            " background-color: #15171c;"
            " color: #cfd2d6;"
            " font-family: monospace;"
            " border: 1px solid #4a4f57;"
            " border-radius: 6px;"
            "}"
        )
        layout.addWidget(self._list, 1)

        bottom = QHBoxLayout()
        self._status_label = QLabel("(empty)")
        self._status_label.setStyleSheet("color: #9aa0a6;")
        bottom.addWidget(self._status_label, 1)

        self._browse_button = QPushButton("Add...")
        self._browse_button.clicked.connect(self._on_browse)
        bottom.addWidget(self._browse_button)

        self._remove_button = QPushButton("Remove Selected")
        self._remove_button.clicked.connect(self._on_remove_selected)
        bottom.addWidget(self._remove_button)

        self._clear_button = QPushButton("Clear All")
        self._clear_button.clicked.connect(self._on_clear_all)
        bottom.addWidget(self._clear_button)

        layout.addLayout(bottom)
        self._apply_idle_style()
        self._refresh_status()

    def paths(self) -> tuple[Path, ...]:
        return tuple(self._paths)

    def has_valid_paths(self) -> bool:
        return bool(self._paths)

    def set_paths(self, paths: Iterable[Path]) -> None:
        self._paths = []
        self._list.clear()
        for path in paths:
            self.add_path(path, emit=False)
        self._refresh_status()
        self.paths_changed.emit()

    def add_path(self, path: Path, emit: bool = True, show_error: bool = True) -> bool:
        resolved = path.resolve()
        if any(existing == resolved for existing in self._paths):
            return False

        result = validate_to(resolved)
        if not result.ok:
            if show_error:
                _bring_window_forward(self.window() if self.window() is not None else self)
                QMessageBox.warning(self, APP_TITLE, f"{resolved}\n\n{result.message}")
            return False

        self._paths.append(resolved)
        self._list.addItem(QListWidgetItem(str(resolved)))
        self._refresh_status()
        if emit:
            self.paths_changed.emit()
        return True

    def _on_browse(self) -> None:
        start_dir = str(self._paths[-1]) if self._paths else str(Path.home())
        chosen = choose_existing_directories(
            self,
            "Choose target Unity project(s)",
            start_dir,
        )
        if not chosen:
            return

        added_any = False
        failures: list[str] = []
        for path in chosen:
            before = len(self._paths)
            if self.add_path(path, emit=False, show_error=False):
                added_any = True
            elif before == len(self._paths):
                # Failed validation or duplicate (duplicate is silent).
                result = validate_to(path.resolve())
                if not result.ok:
                    failures.append(f"{path.resolve()}\n  {result.message}")

        if added_any:
            self.paths_changed.emit()

        if failures:
            _bring_window_forward(self.window() if self.window() is not None else self)
            QMessageBox.warning(
                self,
                APP_TITLE,
                "Skipped invalid folders:\n\n" + "\n\n".join(failures),
            )

    def _on_remove_selected(self) -> None:
        rows = sorted(
            (index.row() for index in self._list.selectedIndexes()),
            reverse=True,
        )
        if not rows:
            return
        for row in rows:
            self._list.takeItem(row)
            del self._paths[row]
        self._refresh_status()
        self.paths_changed.emit()

    def _on_clear_all(self) -> None:
        if not self._paths:
            return
        self._paths = []
        self._list.clear()
        self._refresh_status()
        self.paths_changed.emit()

    def dragEnterEvent(self, event: QDragEnterEvent) -> None:
        mime = event.mimeData()
        if mime.hasUrls() and any(u.toLocalFile() for u in mime.urls()):
            event.acceptProposedAction()
            self._apply_hover_style()
        else:
            event.ignore()

    def dragLeaveEvent(self, event) -> None:
        self._apply_ok_style() if self._paths else self._apply_idle_style()
        super().dragLeaveEvent(event)

    def dropEvent(self, event: QDropEvent) -> None:
        added_any = False
        for url in event.mimeData().urls():
            local = url.toLocalFile()
            if not local:
                continue
            candidate = Path(local)
            if candidate.is_dir():
                added_any = self.add_path(candidate, emit=False) or added_any
        if added_any:
            self.paths_changed.emit()
            event.acceptProposedAction()
        else:
            self._refresh_status()
            event.ignore()

    def _refresh_status(self) -> None:
        count = len(self._paths)
        self._status_label.setText(f"{count} target{'s' if count != 1 else ''}")
        self._remove_button.setEnabled(count > 0)
        self._clear_button.setEnabled(count > 0)
        self._apply_ok_style() if count else self._apply_idle_style()

    def _apply_idle_style(self) -> None:
        self.setStyleSheet(
            "QFrame#MultiTargetZone {"
            " border: 2px dashed #4a4f57;"
            " border-radius: 10px;"
            " background-color: #20232a;"
            "}"
        )

    def _apply_hover_style(self) -> None:
        self.setStyleSheet(
            "QFrame#MultiTargetZone {"
            " border: 2px dashed #6c8cff;"
            " border-radius: 10px;"
            " background-color: #2a2f3a;"
            "}"
        )

    def _apply_ok_style(self) -> None:
        self.setStyleSheet(
            "QFrame#MultiTargetZone {"
            " border: 2px solid #3fb950;"
            " border-radius: 10px;"
            " background-color: #1f2a22;"
            "}"
        )


class InstallerWindow(QMainWindow):
    def __init__(self, context_path: Path) -> None:
        super().__init__()
        self._context_path = (
            context_path
            if context_path == DEFAULT_PROFILE_CONTEXT
            else context_path.resolve()
        )
        self.setWindowTitle(f"{APP_TITLE} v{APP_VERSION}")
        self.resize(900, 700)

        central = QWidget(self)
        self.setCentralWidget(central)

        root_layout = QVBoxLayout(central)
        root_layout.setContentsMargins(16, 16, 16, 16)
        root_layout.setSpacing(12)

        header = QLabel(
            "Drag the source Unity project into FROM and one or more target Unity projects into TO. "
            "Install replaces the ChievFX MCP package in every TO project. "
            "FROM/TO choices are remembered per launcher Unity project."
        )
        header.setWordWrap(True)
        header.setStyleSheet("color: #cfd2d6;")
        root_layout.addWidget(header)

        zones = QHBoxLayout()
        zones.setSpacing(12)

        self._from_zone = DropZone(
            "FROM (source)",
            "Unity project root containing `Packages/com.chievfx.mcp/`.",
            validate_from,
        )
        self._to_zone = MultiTargetZone()

        zones.addWidget(self._from_zone, 1)
        zones.addWidget(self._to_zone, 1)
        root_layout.addLayout(zones)

        options = QHBoxLayout()
        self._tgz_checkbox = QCheckBox("Install as tarball (.tgz)")
        self._tgz_checkbox.setToolTip(
            "Instead of copying sources into Packages/, build a .tgz and reference it from "
            "Packages/manifest.json via a file: dependency. The .tgz is written into the TO project."
        )
        self._tgz_checkbox.toggled.connect(self._on_tgz_toggled)
        options.addWidget(self._tgz_checkbox)

        self._tgz_folder_label = QLabel("Destination folder:")
        options.addWidget(self._tgz_folder_label)
        self._tgz_folder_edit = QLineEdit(DEFAULT_TGZ_DEST_FOLDER)
        self._tgz_folder_edit.setPlaceholderText(DEFAULT_TGZ_DEST_FOLDER)
        self._tgz_folder_edit.setToolTip(
            "Project-relative folder in each TO project where the .tgz is written (default PackagesSource, "
            "outside Assets/ so Unity does not import the tarball as an asset)."
        )
        self._tgz_folder_edit.textChanged.connect(self._on_tgz_folder_changed)
        options.addWidget(self._tgz_folder_edit, 1)
        root_layout.addLayout(options)

        self._tgz_folder_label.setEnabled(False)
        self._tgz_folder_edit.setEnabled(False)

        controls = QHBoxLayout()
        self._autodetect_button = QPushButton("Auto-detect FROM (walk up from installer)")
        self._autodetect_button.clicked.connect(self._on_autodetect_from)
        controls.addWidget(self._autodetect_button)

        controls.addStretch(1)

        self._install_button = QPushButton("Install")
        self._install_button.setObjectName("InstallButton")
        self._install_button.setStyleSheet(INSTALL_BUTTON_STYLE)
        self._install_button.setMinimumHeight(36)
        self._install_button.setMinimumWidth(120)
        self._install_button.setCursor(Qt.CursorShape.PointingHandCursor)
        self._install_button.pressed.connect(self._on_install_pressed)
        self._install_button.clicked.connect(self._on_install_clicked)
        controls.addWidget(self._install_button)

        root_layout.addLayout(controls)

        self._install_hint = QLabel()
        self._install_hint.setWordWrap(True)
        root_layout.addWidget(self._install_hint)

        log_label = QLabel("Log")
        log_label.setStyleSheet("color: #9aa0a6;")
        root_layout.addWidget(log_label)

        self._log_view = QPlainTextEdit()
        self._log_view.setReadOnly(True)
        self._log_view.setStyleSheet(
            "QPlainTextEdit {"
            " background-color: #0e1015;"
            " color: #cfd2d6;"
            " font-family: monospace;"
            " border-radius: 6px;"
            " padding: 8px;"
            "}"
        )
        root_layout.addWidget(self._log_view, 1)

        self._from_zone.path_changed.connect(self._refresh_install_button)
        self._from_zone.path_changed.connect(self._remember_profile)
        self._to_zone.paths_changed.connect(self._refresh_install_button)
        self._to_zone.paths_changed.connect(self._remember_profile)

        self._worker_thread: QThread | None = None
        self._worker: _InstallWorker | None = None

        self._restore_profile_silently()
        if self._from_zone.path() is None:
            self._try_autodetect_from_silently()
        self._refresh_install_button()

    def _refresh_install_button(self) -> None:
        from_ok = self._from_zone.is_valid()
        to_ok = self._to_zone.has_valid_paths()
        ready = from_ok and to_ok
        # Keep Install enabled so macOS/Fusion always shows a pressed visual and
        # clicked() fires; readiness is enforced inside the click handler.
        self._install_button.setEnabled(True)
        if ready:
            count = len(self._to_zone.paths())
            self._install_hint.setText(
                f"Ready. Install will copy into {count} TO project{'s' if count != 1 else ''}."
            )
            self._install_hint.setStyleSheet("color: #3fb950;")
            self._install_button.setToolTip("Install package into all TO projects.")
        else:
            reasons: list[str] = []
            if not from_ok:
                if self._from_zone.path() is None:
                    reasons.append("set FROM (source Unity project)")
                else:
                    reasons.append("FROM is invalid (needs Packages/com.chievfx.mcp with Tools~/ server)")
            if not to_ok:
                reasons.append("add at least one TO Unity project")
            text = "Not ready: " + "; ".join(reasons) + ". Click Install for details."
            self._install_hint.setText(text)
            self._install_hint.setStyleSheet("color: #f0a030;")
            self._install_button.setToolTip(text)

    def _install_not_ready_reason(self) -> str | None:
        from_ok = self._from_zone.is_valid()
        to_ok = self._to_zone.has_valid_paths()
        if from_ok and to_ok:
            return None
        reasons: list[str] = []
        if not from_ok:
            if self._from_zone.path() is None:
                reasons.append("Set FROM to a Unity project that contains Packages/com.chievfx.mcp.")
            else:
                reasons.append(
                    f"FROM is invalid:\n{self._from_zone.path()}\n"
                    "It must contain Packages/com.chievfx.mcp MCP sources."
                )
        if not to_ok:
            reasons.append("Add at least one TO Unity project (Assets/ + Packages/manifest.json).")
        return "\n\n".join(reasons)

    def _on_install_pressed(self) -> None:
        # Visible proof that mouse press reached the widget (helps debug macOS focus).
        self._append_log("Install button pressed.")

    def _on_autodetect_from(self) -> None:
        self._try_autodetect_from_silently(force=True)

    def _from_detect_extra_candidates(self) -> list[Path]:
        extras: list[Path] = []
        if self._context_path != DEFAULT_PROFILE_CONTEXT:
            extras.append(self._context_path)
        return extras

    def _try_autodetect_from_silently(self, force: bool = False) -> None:
        candidate = detect_from_root(extra_candidates=self._from_detect_extra_candidates())
        if candidate is not None and (force or self._from_zone.path() is None):
            self._from_zone.set_path(candidate)
            if force:
                self._append_log(f"Auto-detected FROM: {candidate}")
            return

        if force:
            searched_from = Path(__file__).resolve().parent
            self._append_log(
                "Auto-detect FROM failed. Walked up from "
                f"{searched_from} looking for "
                "Packages/com.chievfx.mcp/Tools~/ChievfxMcp/chievfx_mcp_server.py."
            )
            _bring_window_forward(self)
            QMessageBox.warning(
                self,
                APP_TITLE,
                (
                    "Could not auto-detect FROM.\n\n"
                    "Expected a Unity project root containing:\n"
                    "  Packages/com.chievfx.mcp/Tools~/ChievfxMcp/chievfx_mcp_server.py\n"
                    "  Packages/com.chievfx.mcp/Editor/ChievfxMcp/Bridge/ChievfxMcpBridgeHost.cs\n\n"
                    f"Searched upward from:\n{searched_from}\n\n"
                    "Browse to the source Unity project root manually."
                ),
            )

    def _restore_profile_silently(self) -> None:
        profile = load_profile(self._context_path)
        if profile.last_from_path is not None:
            self._from_zone.set_path(profile.last_from_path)
        if profile.to_paths:
            self._to_zone.set_paths(profile.to_paths)
        self._tgz_folder_edit.blockSignals(True)
        self._tgz_folder_edit.setText(profile.tgz_dest_folder or DEFAULT_TGZ_DEST_FOLDER)
        self._tgz_folder_edit.blockSignals(False)
        self._tgz_checkbox.setChecked(profile.install_as_tgz)
        self._on_tgz_toggled(profile.install_as_tgz)

    def _on_tgz_toggled(self, checked: bool) -> None:
        self._tgz_folder_label.setEnabled(checked)
        self._tgz_folder_edit.setEnabled(checked)
        self._remember_profile()
        self._refresh_install_button()

    def _on_tgz_folder_changed(self, _text: str) -> None:
        self._remember_profile()

    def _remember_profile(self) -> None:
        save_profile(
            InstallerProfile(
                context_path=self._context_path,
                last_from_path=self._from_zone.path(),
                to_paths=self._to_zone.paths(),
                install_as_tgz=self._tgz_checkbox.isChecked(),
                tgz_dest_folder=self._tgz_folder_edit.text().strip() or DEFAULT_TGZ_DEST_FOLDER,
            )
        )

    def _append_log(self, line: str) -> None:
        self._log_view.appendPlainText(line)

    def _on_install_clicked(self) -> None:
        not_ready = self._install_not_ready_reason()
        if not_ready is not None:
            self._append_log("Install blocked: prerequisites missing.")
            _bring_window_forward(self)
            QMessageBox.warning(self, APP_TITLE, not_ready)
            return

        from_root = self._from_zone.path()
        to_roots = self._to_zone.paths()
        if from_root is None or not to_roots:
            # Defensive — _install_not_ready_reason should already catch this.
            self._append_log("Install blocked: set a valid FROM folder and at least one TO project.")
            _bring_window_forward(self)
            QMessageBox.warning(
                self,
                APP_TITLE,
                "Set a valid FROM folder and at least one TO Unity project before installing.",
            )
            return

        matching_roots = [to_root for to_root in to_roots if from_root == to_root]
        if matching_roots:
            _bring_window_forward(self)
            QMessageBox.warning(
                self,
                APP_TITLE,
                "FROM cannot also be a TO folder:\n" + "\n".join(str(path) for path in matching_roots),
            )
            return

        live_installer_targets = [
            to_root for to_root in to_roots if _to_contains_running_installer(to_root)
        ]
        if live_installer_targets:
            _bring_window_forward(self)
            QMessageBox.warning(
                self,
                APP_TITLE,
                (
                    "Cannot install into a TO project that owns this running installer:\n"
                    + "\n".join(str(path) for path in live_installer_targets)
                    + "\n\nThat would delete the live Install~ package mid-run. "
                    "Launch the installer from a different Unity project, or copy FROM "
                    "into a different TO."
                ),
            )
            return

        install_as_tgz = self._tgz_checkbox.isChecked()
        tgz_dest_folder = self._tgz_folder_edit.text().strip() or DEFAULT_TGZ_DEST_FOLDER
        target_lines = "\n".join(f"  - {path}" for path in to_roots)
        _bring_window_forward(self)
        box = QMessageBox(self)
        box.setIcon(QMessageBox.Icon.Question)
        box.setWindowTitle(APP_TITLE)
        box.setText("Install ChievFX MCP into the selected TO folders?")
        if install_as_tgz:
            informative = (
                "This will install to these TO folders:\n"
                f"{target_lines}\n\n"
                "In each TO folder, this will first REMOVE other existing MCP installs:\n"
                "  - embedded Packages/com.chievfx.mcp (and its .meta)\n"
                "  - any com.chievfx.mcp-*.tgz under PackagesSource/, Assets/, or Packages/\n\n"
                "Then it will:\n"
                f"  - write the tarball into {tgz_dest_folder}/ (no suffix on a new version, then .f1/.f2/... per rebuild)\n"
                "  - set the com.chievfx.mcp file: dependency in Packages/manifest.json (replace the existing line in place, or insert alphabetically)"
            )
        else:
            informative = (
                "This will install to these TO folders:\n"
                f"{target_lines}\n\n"
                "In each TO folder, this will first REMOVE every existing MCP install:\n"
                "  - embedded Packages/com.chievfx.mcp (and its .meta)\n"
                "  - any com.chievfx.mcp-*.tgz under PackagesSource/, Assets/, or Packages/\n"
                "  - the com.chievfx.mcp dependency in Packages/manifest.json (git url, file:, or version)\n\n"
                "Then copy a fresh MCP package from FROM."
            )
        box.setInformativeText(informative)
        install_button = box.addButton("Install", QMessageBox.ButtonRole.AcceptRole)
        box.addButton("Cancel", QMessageBox.ButtonRole.RejectRole)
        box.setDefaultButton(install_button)
        box.exec()
        if box.clickedButton() is not install_button:
            self._append_log("Install cancelled.")
            return

        save_profile(
            InstallerProfile(
                context_path=self._context_path,
                last_from_path=from_root,
                to_paths=to_roots,
                install_as_tgz=install_as_tgz,
                tgz_dest_folder=tgz_dest_folder,
            )
        )
        self._install_button.setEnabled(False)
        self._autodetect_button.setEnabled(False)
        self._log_view.clear()
        self._append_log(f"Starting install ({len(to_roots)} target{'s' if len(to_roots) != 1 else ''})...")

        self._worker_thread = QThread(self)
        self._worker = _InstallWorker(from_root, to_roots, install_as_tgz, tgz_dest_folder)
        self._worker.moveToThread(self._worker_thread)

        self._worker_thread.started.connect(self._worker.run)
        self._worker.log_line.connect(self._append_log)
        self._worker.finished_ok.connect(self._on_install_finished_ok)
        self._worker.finished_err.connect(self._on_install_finished_err)
        self._worker.finished_ok.connect(self._worker_thread.quit)
        self._worker.finished_err.connect(self._worker_thread.quit)
        self._worker_thread.finished.connect(self._cleanup_worker)

        self._worker_thread.start()

    def _on_install_finished_ok(self) -> None:
        _bring_window_forward(self)
        QMessageBox.information(self, APP_TITLE, "Install complete.")
        self._refresh_buttons_after_run()

    def _on_install_finished_err(self, message: str) -> None:
        _bring_window_forward(self)
        QMessageBox.critical(self, APP_TITLE, f"Install failed:\n{message}")
        self._refresh_buttons_after_run()

    def _refresh_buttons_after_run(self) -> None:
        self._autodetect_button.setEnabled(True)
        self._refresh_install_button()

    def _cleanup_worker(self) -> None:
        if self._worker is not None:
            self._worker.deleteLater()
            self._worker = None
        if self._worker_thread is not None:
            self._worker_thread.deleteLater()
            self._worker_thread = None


def _apply_dark_palette(app: QApplication) -> None:
    palette = QPalette()
    palette.setColor(QPalette.ColorRole.Window, QColor("#15171c"))
    palette.setColor(QPalette.ColorRole.WindowText, QColor("#e6e8eb"))
    palette.setColor(QPalette.ColorRole.Base, QColor("#1b1e24"))
    palette.setColor(QPalette.ColorRole.AlternateBase, QColor("#20232a"))
    palette.setColor(QPalette.ColorRole.Text, QColor("#e6e8eb"))
    palette.setColor(QPalette.ColorRole.Button, QColor("#262a32"))
    palette.setColor(QPalette.ColorRole.ButtonText, QColor("#e6e8eb"))
    palette.setColor(QPalette.ColorRole.Highlight, QColor("#3a6df0"))
    palette.setColor(QPalette.ColorRole.HighlightedText, QColor("#ffffff"))
    # Without Disabled roles, Fusion keeps Active button colors — Install looks
    # clickable while setEnabled(False) blocks press visuals and clicked().
    palette.setColor(QPalette.ColorGroup.Disabled, QPalette.ColorRole.WindowText, QColor("#6b7078"))
    palette.setColor(QPalette.ColorGroup.Disabled, QPalette.ColorRole.Text, QColor("#6b7078"))
    palette.setColor(QPalette.ColorGroup.Disabled, QPalette.ColorRole.Button, QColor("#1a1c20"))
    palette.setColor(QPalette.ColorGroup.Disabled, QPalette.ColorRole.ButtonText, QColor("#6b7078"))
    app.setPalette(palette)


def main() -> int:
    parser = argparse.ArgumentParser(description=APP_TITLE)
    parser.add_argument(
        "--launcher-project",
        help="Unity project root that launched this installer; FROM/TO are remembered per launcher project.",
    )
    args = parser.parse_args()

    # Must run before QApplication so Cocoa treats this subprocess as a real GUI app.
    _macos_bootstrap_foreground_app()

    app = QApplication(sys.argv)
    app.setApplicationName(APP_TITLE)
    app.setStyle("Fusion")
    _apply_dark_palette(app)
    _macos_bootstrap_foreground_app()

    context_path = resolve_profile_context(args.launcher_project)
    window = InstallerWindow(context_path)
    window.show()
    _bring_window_forward(window)
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
