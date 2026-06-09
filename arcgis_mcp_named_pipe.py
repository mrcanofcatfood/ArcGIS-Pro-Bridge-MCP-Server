from __future__ import annotations

import json
import threading
import time
from typing import Any

PIPE_NAME = r"\\.\pipe\ArcGisProBridgePipe"
CONNECT_TIMEOUT_SECONDS = 3.0
READ_TIMEOUT_SECONDS = 10.0

try:
    import pywintypes  # noqa: F401
    import win32file
    import win32pipe

    _PYWIN32_AVAILABLE = True
except ImportError:
    _PYWIN32_AVAILABLE = False


class AddInNotAvailableError(RuntimeError):
    """Raised when the ArcGIS Pro Add-In Named Pipe cannot be reached."""


class AddInOperationError(RuntimeError):
    """Raised when the Add-In returns an error for an IPC operation."""


def _read_pipe(handle, size, timeout):
    """Read from pipe with timeout using a background thread."""
    result = []
    done = threading.Event()

    def reader():
        try:
            hr, data = win32file.ReadFile(handle, size)
            result.append((hr, data))
        except Exception as exc:
            result.append(exc)
        done.set()

    t = threading.Thread(target=reader, daemon=True)
    t.start()

    if not done.wait(timeout):
        win32file.CancelIo(handle)
        raise AddInNotAvailableError(
            f"Read from Add-In pipe timed out after {timeout}s"
        )

    if isinstance(result[0], Exception):
        exc = result[0]
        if isinstance(exc, pywintypes.error):
            winerror = exc.args[0] if exc.args else 0
            raise AddInNotAvailableError(
                f"Pipe read error (winerror={winerror}): {exc}"
            ) from exc
        raise AddInNotAvailableError(f"Pipe read error: {exc}") from exc

    return result[0]


def is_addin_available() -> bool:
    if not _PYWIN32_AVAILABLE:
        return False
    try:
        _open_pipe(timeout=0.5)
        return True
    except AddInNotAvailableError:
        return False


def _open_pipe(timeout: float = CONNECT_TIMEOUT_SECONDS):
    if not _PYWIN32_AVAILABLE:
        raise AddInNotAvailableError(
            "pywin32 is not installed. Install it with: pip install pywin32"
        )
    deadline = time.monotonic() + timeout
    last_error: str | None = None
    attempt = 0
    while time.monotonic() < deadline:
        try:
            handle = win32file.CreateFile(
                PIPE_NAME,
                win32file.GENERIC_READ | win32file.GENERIC_WRITE,
                0,
                None,
                win32file.OPEN_EXISTING,
                win32file.FILE_FLAG_OVERLAPPED,
                None,
            )
            win32pipe.SetNamedPipeHandleState(
                handle,
                win32pipe.PIPE_READMODE_MESSAGE,
                None,
                None,
            )
            return handle
        except pywintypes.error as exc:
            last_error = str(exc)
            winerror = exc.args[0] if exc.args else 0
            if winerror in (2, 231):  # ERROR_FILE_NOT_FOUND, ERROR_PIPE_BUSY
                attempt += 1
                delay = min(0.05 * (2 ** attempt), 1.0)  # exponential backoff: 100ms, 200ms, 400ms, 800ms, cap at 1s
                time.sleep(delay)
                continue
            raise AddInNotAvailableError(f"Failed to connect to Add-In pipe: {exc}") from exc
    raise AddInNotAvailableError(
        f"Could not connect to ArcGIS Pro Add-In pipe "
        f"within {timeout}s. Is ArcGIS Pro running with the "
        f"APBridgeAddIn loaded?\n  Last error: {last_error}"
    )


def call_addin(
    op: str,
    args: dict[str, str] | None = None,
    timeout: float = READ_TIMEOUT_SECONDS,
) -> Any:
    handle = _open_pipe()
    try:
        request = (json.dumps({"op": op, "args": args or {}}) + "\n").encode("utf-8")
        win32file.WriteFile(handle, request)

        _, data = _read_pipe(handle, 65536, timeout)

        response = json.loads(data.decode("utf-8").strip("\0"))
        if not response.get("ok", False):
            raise AddInOperationError(response.get("error", "Unknown Add-In error"))
        return response.get("data")
    finally:
        win32file.CloseHandle(handle)
