#!/usr/bin/env python3
"""ChievFX Unity MCP installer.

Drag-and-drop two folders:
- FROM: root of this repo (the one shipping `Tools/ChievfxMcp/` and
  `Assets/Editor/ChievfxMcp/`).
- TO: root of another Unity project where the MCP should be installed.

Click `Install`. Old MCP sources in TO are removed, fresh copies from FROM
replace them at the same relative paths.
"""

from __future__ import annotations

import json
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Iterable

from PyQt6.QtCore import QObject, Qt, QThread, pyqtSignal
from PyQt6.QtGui import QDragEnterEvent, QDropEvent, QFont, QPalette, QColor
from PyQt6.QtWidgets import (
    QApplication,
    QFileDialog,
    QFrame,
    QHBoxLayout,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QMainWindow,
    QMessageBox,
    QPlainTextEdit,
    QPushButton,
    QVBoxLayout,
    QWidget,
)


APP_TITLE = "ChievFX Unity MCP Installer"
APP_VERSION = "0.2.2"
SETTINGS_PATH = Path.home() / ".chievfx_mcp_installer.json"

MCP_PATHS: tuple[str, ...] = (
    "Tools/ChievfxMcp",
    "Assets/Editor/ChievfxMcp",
    "Assets/Editor/ChievfxMcpExtensions",
    "Assets/Plugins/NuGet",
)
MCP_META_PATHS: tuple[str, ...] = (
    "Assets/Editor/ChievfxMcp.meta",
    "Assets/Editor/ChievfxMcpExtensions.meta",
    "Assets/Plugins/NuGet.meta",
)
MCP_TEST_PATHS: tuple[str, ...] = (
    "Tools/ChievfxMcp/tests",
    "Assets/Editor/ChievfxMcpTests",
    "Assets/Editor/ChievfxMcpTests.meta",
    "Assets/Tests/PlayMode/ChievfxMcpInput",
    "Assets/Tests/PlayMode/ChievfxMcpInput.meta",
)
COPY_IGNORE_DIRS: frozenset[str] = frozenset({"__pycache__", "tests"})
COPY_IGNORE_SUFFIXES: tuple[str, ...] = (".pyc", ".pyo")
REQUIRED_UNITY_PACKAGE = "com.unity.test-framework"
REQUIRED_NEWTONSOFT_PACKAGE = "com.unity.nuget.newtonsoft-json"
REQUIRED_NEWTONSOFT_VERSION = "3.2.2"


@dataclass(frozen=True)
class ValidationResult:
    ok: bool
    message: str


def validate_from(path: Path) -> ValidationResult:
    """FROM must contain MCP sources."""
    if not path.is_dir():
        return ValidationResult(False, "Not a folder.")
    missing: list[str] = []
    if not (path / "Tools" / "ChievfxMcp" / "chievfx_mcp_server.py").is_file():
        missing.append("Tools/ChievfxMcp/chievfx_mcp_server.py")
    if not (path / "Assets" / "Editor" / "ChievfxMcp" / "Bridge" / "ChievfxMcpBridge.cs").is_file():
        missing.append("Assets/Editor/ChievfxMcp/Bridge/ChievfxMcpBridge.cs")
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


def load_last_to_paths() -> list[Path]:
    try:
        data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
    except Exception:
        return []
    if not isinstance(data, dict):
        return []

    raw_paths = data.get("toPaths")
    if not isinstance(raw_paths, list):
        # Backward compatibility with installer settings from v0.2.2 and older.
        raw_paths = [data.get("lastToPath")]

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


def save_last_to_paths(paths: Iterable[Path]) -> None:
    try:
        resolved_paths = [str(path.resolve()) for path in paths]
        SETTINGS_PATH.write_text(
            json.dumps({"toPaths": resolved_paths}, indent=2) + "\n",
            encoding="utf-8",
        )
    except Exception:
        # Remembering targets is convenience only; never fail install for it.
        pass


def _ignore_python_cache(_: str, names: list[str]) -> list[str]:
    skip: list[str] = []
    for name in names:
        if name in COPY_IGNORE_DIRS:
            skip.append(name)
        elif name.endswith(COPY_IGNORE_SUFFIXES):
            skip.append(name)
    return skip


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


def manifest_has_package(manifest_path: Path, package: str) -> tuple[bool, str | None]:
    """Return (present, version) for `package` in `<manifest>.dependencies`."""
    if not manifest_path.is_file():
        return (False, None)
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except Exception:
        return (False, None)
    deps = manifest.get("dependencies", {}) or {}
    if package in deps:
        return (True, str(deps[package]))
    return (False, None)


def add_package_to_manifest(
    manifest_path: Path,
    package: str,
    version: str,
) -> None:
    """Add `package: version` to `dependencies`, preserving alphabetical order.

    Idempotent: caller is expected to have checked presence first.
    """
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    deps = dict(manifest.get("dependencies", {}) or {})
    deps[package] = version
    sorted_deps = dict(sorted(deps.items(), key=lambda kv: kv[0]))
    manifest["dependencies"] = sorted_deps
    manifest_path.write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )


def perform_install(
    from_root: Path,
    to_root: Path,
    log: Callable[[str], None],
    add_newtonsoft: bool,
) -> None:
    log(f"FROM: {from_root}")
    log(f"TO:   {to_root}")
    log("")
    log("[1/4] Removing existing MCP sources and tests in TO ...")
    for rel in _iter_cleanup_paths():
        target = to_root / rel
        if target.exists() or target.is_symlink():
            _remove_path(target, log)
        else:
            log(f"  skipped (absent) {target}")

    log("")
    log("[2/4] Copying fresh MCP sources from FROM ...")
    for rel in _iter_install_paths():
        src = from_root / rel
        dst = to_root / rel
        if not src.exists():
            log(f"  WARN: source missing, skipping {src}")
            continue
        _copy_tree(src, dst, log)

    log("")
    log("[3/4] Patching Packages/manifest.json ...")
    manifest_path = to_root / "Packages" / "manifest.json"
    newtonsoft_present, newtonsoft_version = manifest_has_package(
        manifest_path, REQUIRED_NEWTONSOFT_PACKAGE
    )
    if newtonsoft_present:
        log(
            f"  OK: {REQUIRED_NEWTONSOFT_PACKAGE} {newtonsoft_version} already present, "
            "leaving as-is."
        )
    elif add_newtonsoft:
        try:
            add_package_to_manifest(
                manifest_path,
                REQUIRED_NEWTONSOFT_PACKAGE,
                REQUIRED_NEWTONSOFT_VERSION,
            )
            log(
                f"  added: {REQUIRED_NEWTONSOFT_PACKAGE}@{REQUIRED_NEWTONSOFT_VERSION} "
                f"-> {manifest_path}"
            )
        except Exception as ex:
            log(f"  WARN: could not patch manifest: {ex}")
    else:
        log(
            f"  WARN: `{REQUIRED_NEWTONSOFT_PACKAGE}` missing and not auto-added. "
            "Bridge will not compile until you add it via Unity Package Manager."
        )

    log("")
    log("[4/4] Sanity checks ...")
    test_present, test_version = manifest_has_package(manifest_path, REQUIRED_UNITY_PACKAGE)
    if test_present:
        log(f"  OK: {REQUIRED_UNITY_PACKAGE} {test_version} present.")
    else:
        log(
            f"  WARN: `{REQUIRED_UNITY_PACKAGE}` not found in Packages/manifest.json. "
            "Bridge `tests-run` will not compile without it. "
            "Add it via Unity Package Manager."
        )

    log("")
    log("Done.")
    log("Next steps in target Unity project:")
    log("  1. Open the project, wait for compile + domain reload.")
    log("  2. Window > ChievFX > MCP -> Start Bridge -> Write Cursor Config.")
    log("  3. Reload Cursor MCP tools or restart Cursor.")


class _InstallWorker(QObject):
    log_line = pyqtSignal(str)
    finished_ok = pyqtSignal()
    finished_err = pyqtSignal(str)

    def __init__(
        self,
        from_root: Path,
        to_roots: Iterable[Path],
        add_newtonsoft: bool,
    ) -> None:
        super().__init__()
        self._from_root = from_root
        self._to_roots = tuple(to_roots)
        self._add_newtonsoft = add_newtonsoft

    def run(self) -> None:
        try:
            total = len(self._to_roots)
            for index, to_root in enumerate(self._to_roots, start=1):
                self.log_line.emit(f"=== Target {index}/{total}: {to_root} ===")
                perform_install(
                    self._from_root,
                    to_root,
                    self.log_line.emit,
                    add_newtonsoft=self._add_newtonsoft,
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
            "`Assets/` and `Packages/manifest.json`."
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

    def add_path(self, path: Path, emit: bool = True) -> bool:
        resolved = path.resolve()
        if any(existing == resolved for existing in self._paths):
            return False

        result = validate_to(resolved)
        if not result.ok:
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
        chosen = QFileDialog.getExistingDirectory(self, "Choose target Unity project", start_dir)
        if chosen:
            self.add_path(Path(chosen))

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
    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle(f"{APP_TITLE} v{APP_VERSION}")
        self.resize(900, 700)

        central = QWidget(self)
        self.setCentralWidget(central)

        root_layout = QVBoxLayout(central)
        root_layout.setContentsMargins(16, 16, 16, 16)
        root_layout.setSpacing(12)

        header = QLabel(
            "Drag the source repo into FROM and one or more Unity projects into TO. "
            "Install replaces ChievFX MCP tools, core bridge, and first-party extension folders "
            "in every TO project."
        )
        header.setWordWrap(True)
        header.setStyleSheet("color: #cfd2d6;")
        root_layout.addWidget(header)

        zones = QHBoxLayout()
        zones.setSpacing(12)

        self._from_zone = DropZone(
            "FROM (source)",
            "Root of the repo containing `Tools/ChievfxMcp` and ChievFX MCP editor folders.",
            validate_from,
        )
        self._to_zone = MultiTargetZone()

        zones.addWidget(self._from_zone, 1)
        zones.addWidget(self._to_zone, 1)
        root_layout.addLayout(zones)

        controls = QHBoxLayout()
        self._autodetect_button = QPushButton("Auto-detect FROM (use installer's repo)")
        self._autodetect_button.clicked.connect(self._on_autodetect_from)
        controls.addWidget(self._autodetect_button)

        controls.addStretch(1)

        self._install_button = QPushButton("Install")
        self._install_button.setMinimumHeight(36)
        self._install_button.setEnabled(False)
        self._install_button.clicked.connect(self._on_install_clicked)
        controls.addWidget(self._install_button)

        root_layout.addLayout(controls)

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
        self._to_zone.paths_changed.connect(self._refresh_install_button)
        self._to_zone.paths_changed.connect(self._remember_to_paths)

        self._worker_thread: QThread | None = None
        self._worker: _InstallWorker | None = None

        self._try_autodetect_from_silently()
        self._try_restore_last_to_silently()

    def _refresh_install_button(self) -> None:
        self._install_button.setEnabled(
            self._from_zone.is_valid() and self._to_zone.has_valid_paths()
        )

    def _on_autodetect_from(self) -> None:
        self._try_autodetect_from_silently(force=True)

    def _try_autodetect_from_silently(self, force: bool = False) -> None:
        installer_dir = Path(__file__).resolve().parent
        candidate = installer_dir.parent
        if validate_from(candidate).ok:
            if force or self._from_zone.path() is None:
                self._from_zone.set_path(candidate)

    def _try_restore_last_to_silently(self) -> None:
        if not self._to_zone.paths():
            self._to_zone.set_paths(load_last_to_paths())

    def _remember_to_paths(self) -> None:
        save_last_to_paths(self._to_zone.paths())

    def _append_log(self, line: str) -> None:
        self._log_view.appendPlainText(line)

    def _on_install_clicked(self) -> None:
        from_root = self._from_zone.path()
        to_roots = self._to_zone.paths()
        if from_root is None or not to_roots:
            return
        matching_roots = [to_root for to_root in to_roots if from_root == to_root]
        if matching_roots:
            QMessageBox.warning(
                self,
                APP_TITLE,
                "FROM cannot also be a TO folder:\n" + "\n".join(str(path) for path in matching_roots),
            )
            return

        target_lines = "\n".join(f"  - {path}" for path in to_roots)

        confirm = QMessageBox.question(
            self,
            APP_TITLE,
            (
                "This will install to these TO folders:\n"
                f"{target_lines}\n\n"
                "In each TO folder, this will DELETE:\n"
                "  - Tools/ChievfxMcp\n"
                "  - Assets/Editor/ChievfxMcp (and its .meta)\n"
                "  - Assets/Editor/ChievfxMcpExtensions (and its .meta)\n"
                "  - Assets/Plugins/NuGet (and its .meta)\n\n"
                "It also removes MCP test folders left by earlier installs.\n\n"
                "Then copy fresh versions from FROM, without MCP tests. Continue?"
            ),
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if confirm != QMessageBox.StandardButton.Yes:
            return

        missing_newtonsoft = [
            to_root
            for to_root in to_roots
            if not manifest_has_package(
                to_root / "Packages" / "manifest.json",
                REQUIRED_NEWTONSOFT_PACKAGE,
            )[0]
        ]
        add_newtonsoft = False
        if missing_newtonsoft:
            missing_lines = "\n".join(f"  - {path}" for path in missing_newtonsoft)
            response = QMessageBox.question(
                self,
                APP_TITLE,
                (
                    f"`{REQUIRED_NEWTONSOFT_PACKAGE}` is required by the bridge but is "
                    "missing from `Packages/manifest.json` in:\n"
                    f"{missing_lines}\n\n"
                    f"Add `{REQUIRED_NEWTONSOFT_PACKAGE}@{REQUIRED_NEWTONSOFT_VERSION}` "
                    "to those manifests now?\n\n"
                    "(If you say No, the bridge will not compile until you add it "
                    "manually via Unity Package Manager.)"
                ),
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.Yes,
            )
            add_newtonsoft = response == QMessageBox.StandardButton.Yes

        save_last_to_paths(to_roots)
        self._install_button.setEnabled(False)
        self._autodetect_button.setEnabled(False)
        self._log_view.clear()

        self._worker_thread = QThread(self)
        self._worker = _InstallWorker(from_root, to_roots, add_newtonsoft)
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
        QMessageBox.information(self, APP_TITLE, "Install complete.")
        self._refresh_buttons_after_run()

    def _on_install_finished_err(self, message: str) -> None:
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
    app.setPalette(palette)


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName(APP_TITLE)
    app.setStyle("Fusion")
    _apply_dark_palette(app)

    window = InstallerWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
