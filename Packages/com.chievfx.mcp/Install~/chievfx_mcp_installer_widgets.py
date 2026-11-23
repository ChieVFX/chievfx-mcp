"""Folder pickers and drop zones of the ChievFX Unity MCP installer window (PyQt6)."""

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



from chievfx_mcp_installer import *  # noqa: F403 - the zones validate folders with the install logic

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
