"""Terminal UI for the guided build: styled text, keyboard menus and a build progress view.

Plain ANSI escape codes and termios, no curses, so the terminal keeps its scrollback. Menus
redraw in place and never draw more lines than the terminal shows.
"""
from __future__ import annotations

import os
import re
import select
import shutil
import subprocess
import sys
import termios
import textwrap
import threading
import time
import tty
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

UNICODE = (sys.stdout.encoding or "").lower().replace("-", "").replace("_", "") == "utf8"
COLOR = sys.stdout.isatty() and "NO_COLOR" not in os.environ and os.environ.get("TERM") != "dumb"
ANSI = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")


def _style(code: str) -> Callable[[str], str]:
    def apply(text: str) -> str:
        return f"\x1b[{code}m{text}\x1b[0m" if COLOR and text else text
    return apply


bold, dim, red, green, yellow, cyan = (_style(code) for code in ("1", "2", "31", "32", "33", "36"))
OK = green("✓" if UNICODE else "+")
FAIL = red("✗" if UNICODE else "x")
WARN = yellow("!")
POINTER = "›" if UNICODE else ">"
SPINNER = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏" if UNICODE else "|/-\\"
UP_DOWN = "↑↓" if UNICODE else "up/down"


def size() -> tuple[int, int]:
    columns, lines = shutil.get_terminal_size((100, 30))
    return max(columns, 40), max(lines, 12)


def visible_len(text: str) -> int:
    return len(ANSI.sub("", text))


def clip(text: str, width: int) -> str:
    """`text` cut to `width` visible columns, keeping its escape codes."""
    out, used, i = [], 0, 0
    while i < len(text):
        code = ANSI.match(text, i)
        if code:
            out.append(code.group())
            i = code.end()
            continue
        if used == width:
            return "".join(out) + ("\x1b[0m" if COLOR else "")
        out.append(text[i])
        used += 1
        i += 1
    return "".join(out)


def pad(text: str, width: int) -> str:
    return text + " " * max(0, width - visible_len(text))


def wrap(text: str, width: int, indent: str = "") -> list[str]:
    lines = []
    for paragraph in text.splitlines() or [""]:
        lines.extend(textwrap.wrap(paragraph, width, initial_indent=indent, subsequent_indent=indent) or [indent])
    return lines


def write(text: str) -> None:
    sys.stdout.write(text)
    sys.stdout.flush()


def elapsed(seconds: float) -> str:
    seconds = int(seconds)
    hours, rest = divmod(seconds, 3600)
    return f"{hours}:{rest // 60:02}:{rest % 60:02}" if hours else f"{rest // 60}:{rest % 60:02}"


class Quit(Exception):
    """The user asked to leave (q in a menu)."""


KEYS = {b"\r": "enter", b"\n": "enter", b" ": "space", b"\x1b": "esc", b"\x7f": "backspace", b"\t": "tab",
        b"\x1b[A": "up", b"\x1b[B": "down", b"\x1b[C": "right", b"\x1b[D": "left",
        b"\x1bOA": "up", b"\x1bOB": "down", b"\x1bOC": "right", b"\x1bOD": "left",
        b"\x1b[5~": "pageup", b"\x1b[6~": "pagedown", b"\x1b[H": "home", b"\x1b[F": "end"}


def read_key() -> str:
    fd = sys.stdin.fileno()
    saved = termios.tcgetattr(fd)
    try:
        tty.setraw(fd)
        data = os.read(fd, 1)
        if data == b"\x1b":
            # An escape sequence arrives at once; a lone Esc does not continue.
            while select.select([fd], [], [], 0.03)[0]:
                data += os.read(fd, 1)
                if len(data) >= 3 and (data[-1:].isalpha() or data.endswith(b"~")):
                    break
    finally:
        termios.tcsetattr(fd, termios.TCSADRAIN, saved)
    if data == b"\x03":
        raise KeyboardInterrupt
    if data == b"\x04":
        raise Quit
    if data in KEYS:
        return KEYS[data]
    return data.decode(errors="replace").lower() if len(data) == 1 else ""


class Block:
    """Lines that are redrawn in place."""

    def __init__(self) -> None:
        self.height = 0

    def draw(self, lines: list[str]) -> None:
        columns, _ = size()
        head = f"\x1b[{self.height}A\r" if self.height else ""
        write(head + "\x1b[J" + "".join(clip(line, columns - 1) + "\n" for line in lines))
        self.height = len(lines)

    def clear(self) -> None:
        if self.height:
            write(f"\x1b[{self.height}A\r\x1b[J")
            self.height = 0


class Session:
    """Hides the cursor while menus run; always restores it."""

    def __enter__(self) -> "Session":
        write("\x1b[?25l")
        return self

    def __exit__(self, *exc) -> None:
        write("\x1b[0m\x1b[?25h")


@dataclass
class Item:
    key: str
    label: str
    note: str = ""          # short text after the label
    detail: str = ""        # shown under the list while the item has focus
    kind: str = "check"     # check | radio | action | header
    checked: bool = False
    locked: str = ""        # checked and fixed, with the reason ("needed by ...")
    disabled: str = ""      # cannot be chosen, with the reason


def _row(item: Item, focused: bool, label_width: int) -> str:
    pointer = cyan(POINTER) if focused else " "
    if item.kind == "header":
        return " " + bold(item.label)
    if item.kind == "action":
        return f"{pointer}  {cyan('+')}  {cyan(item.label)}"
    if item.kind == "radio":
        mark = ("(•)" if UNICODE else "(*)") if item.checked else "( )"
    else:
        mark = "[x]" if item.checked else ("[-]" if item.disabled else "[ ]")
    label = pad(item.label, label_width)
    if item.disabled:
        return f"{pointer} {dim(mark)} {dim(label)}  {dim(item.disabled)}"
    note = item.note + (f"  ({item.locked})" if item.locked else "")
    return f"{pointer} {green(mark) if item.checked else mark} {bold(label) if focused else label}  {dim(note)}"


def menu(title: str, items: list[Item], *, intro: list[str] = (), keys: dict[str, str] | None = None,
         on_toggle: Callable[[Item], str | None] | None = None, focus: str | None = None,
         enter_label: str = "continue") -> tuple[str, str | None, str]:
    """Show `items` until the user leaves; returns (event, key, focused item key).

    Events: "done" on Enter (key = the focused item), "action" on Enter over an action item,
    "back" on Esc or Left (key None), and "key" for an extra key in `keys` (key -> footer label).
    Space toggles a check item and picks a radio item; on_toggle runs after each change and may
    return a message to show.
    """
    keys = keys or {}
    focusable = [i for i, item in enumerate(items) if item.kind != "header"]
    if not focusable:
        raise ValueError("menu without choices")
    cursor = next((i for i in focusable if items[i].key == focus), focusable[0])
    message = ""
    block = Block()
    while True:
        columns, lines = size()
        label_width = min(36, max((visible_len(item.label) for item in items if item.kind != "header"), default=0))
        detail = items[cursor].detail
        detail_lines = wrap(detail, columns - 6, "    ")[:4] if detail else []
        footer = [f"{UP_DOWN} move", "space select", f"enter {enter_label}", "esc back"]
        footer += [f"{k} {label}" for k, label in keys.items()] + ["q quit"]
        head = [bold(title), *intro, ""]
        tail = ["", *(dim(line) for line in detail_lines), *( [yellow("  " + message)] if message else []),
                dim("  " + "   ".join(footer))]
        room = max(3, lines - len(head) - len(tail) - 1)
        top = min(max(0, cursor - room // 2), max(0, len(items) - room))
        window = items[top:top + room]
        rows = [_row(item, top + n == cursor, label_width) for n, item in enumerate(window)]
        if top > 0:
            rows[0] = dim(f"   {'↑' if UNICODE else '^'} {top} more")
        if top + room < len(items):
            rows[-1] = dim(f"   {'↓' if UNICODE else 'v'} {len(items) - top - room} more")
        block.draw(head + rows + tail)
        key = read_key()
        message = ""
        if key in ("up", "k"):
            cursor = max((i for i in focusable if i < cursor), default=cursor)
        elif key in ("down", "j"):
            cursor = min((i for i in focusable if i > cursor), default=cursor)
        elif key in ("pageup", "home"):
            cursor = focusable[0] if key == "home" else max((i for i in focusable if i <= cursor - room), default=focusable[0])
        elif key in ("pagedown", "end"):
            cursor = focusable[-1] if key == "end" else min((i for i in focusable if i >= cursor + room), default=focusable[-1])
        elif key in ("esc", "left"):
            block.clear()
            return "back", None, items[cursor].key
        elif key == "q":
            block.clear()
            raise Quit
        elif key in keys:
            block.clear()
            return "key", key, items[cursor].key
        elif key in ("space", "x", "enter"):
            item = items[cursor]
            if item.kind == "action":
                if key == "enter":
                    block.clear()
                    return "action", item.key, item.key
                continue
            if key == "enter" and item.kind == "check":
                block.clear()
                return "done", item.key, item.key
            if item.disabled:
                message = f"{item.label}: {item.disabled}"
                continue
            if item.kind == "radio":
                for other in items:
                    if other.kind == "radio":
                        other.checked = other is item
                if key == "enter":
                    block.clear()
                    return "done", item.key, item.key
            elif item.locked:
                message = f"{item.label} stays selected: {item.locked}"
                continue
            else:
                item.checked = not item.checked
            if on_toggle:
                message = on_toggle(item) or ""


def screen(title: str, lines: list[str], keys: dict[str, str]) -> str:
    """Show `lines` and wait for one of `keys` (key -> footer label); returns that key. q quits."""
    block = Block()
    footer = dim("  " + "   ".join([*(f"{k} {label}" for k, label in keys.items()), "q quit"]))
    block.draw([bold(title), *lines, "", footer])
    while True:
        key = read_key()
        if key == "q":
            block.clear()
            raise Quit
        if key in keys:
            block.clear()
            return key


def ask_text(prompt: str, default: str = "") -> str:
    write("\x1b[?25h")
    try:
        value = input(f"  {prompt}{f' [{default}]' if default else ''}: ").strip()
    finally:
        write("\x1b[?25l")
    return value or default


class Spinner:
    """A one-line spinner while a short task runs."""

    def __init__(self, text: str) -> None:
        self.text = text
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._spin, daemon=True)

    def _spin(self) -> None:
        frame = 0
        while not self.stop.wait(0.1):
            write("\r\x1b[K" + clip(f"  {cyan(SPINNER[frame % len(SPINNER)])} {self.text}", size()[0] - 1))
            frame += 1

    def __enter__(self) -> "Spinner":
        self.thread.start()
        return self

    def __exit__(self, *exc) -> None:
        self.stop.set()
        self.thread.join()
        write("\r\x1b[K")


class Progress:
    """The build's phase list with a live line for the running phase; command output goes to a log.

    Installed as terrabuilder_pkg.cli.OUTPUT, so the CLI's run(), say() and phase() report here.
    """

    def __init__(self, log_path: Path) -> None:
        log_path.parent.mkdir(parents=True, exist_ok=True)
        self.log_path = log_path
        self.log = log_path.open("w", encoding="utf-8", errors="replace")
        self.started = time.monotonic()
        self.current: str | None = None
        self.current_started = 0.0
        self.status = ""
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._spin, daemon=True)
        self.thread.start()

    def _log(self, text: str) -> None:
        self.log.write(text)
        self.log.flush()

    def phase(self, title: str) -> None:
        self._log(f"== {title}\n")
        with self.lock:
            self._finish_line(OK)
            self.current, self.current_started, self.status = title, time.monotonic(), ""

    def say(self, text: str) -> None:
        self._log(text + "\n")
        with self.lock:
            self.status = text

    def run(self, cmd: list[str], cwd: Path | None = None, env: dict[str, str] | None = None) -> None:
        self._log("+ " + " ".join(cmd) + "\n")
        with self.lock:
            self.status = "waiting for another build to finish" if cmd[0] == "flock" else ""
        process = subprocess.Popen(cmd, cwd=cwd, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, errors="replace", bufsize=1)
        try:
            for line in process.stdout:
                self.log.write(line)
                text = line.strip()
                if text:
                    with self.lock:
                        self.status = text
            self.log.flush()
        finally:
            code = process.wait()
        if code:
            raise subprocess.CalledProcessError(code, cmd)

    def tail(self, count: int) -> list[str]:
        if not self.log.closed:
            self.log.flush()
        lines = self.log_path.read_text(encoding="utf-8", errors="replace").splitlines()
        return [line for line in lines if line.strip()][-count:]

    def close(self, ok: bool) -> float:
        self.stop.set()
        self.thread.join()
        with self.lock:
            self._finish_line(OK if ok else FAIL)
        self.log.close()
        return time.monotonic() - self.started

    def _finish_line(self, mark: str) -> None:
        if self.current is None:
            return
        write("\r\x1b[K" + f"  {mark} {pad(self.current, 32)} {dim(elapsed(time.monotonic() - self.current_started))}\n")
        self.current = None

    def _spin(self) -> None:
        frame = 0
        while not self.stop.wait(0.12):
            with self.lock:
                if self.current is None:
                    continue
                line = (f"  {cyan(SPINNER[frame % len(SPINNER)])} {pad(self.current, 32)} "
                        f"{dim(elapsed(time.monotonic() - self.current_started))}  {dim(self.status)}")
                write("\r\x1b[K" + clip(line, size()[0] - 1))
            frame += 1
