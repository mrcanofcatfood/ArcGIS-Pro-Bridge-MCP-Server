from __future__ import annotations

import json
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
            if exc.winerror in (2, 231):  # ERROR_FILE_NOT_FOUND, ERROR_PIPE_BUSY
                time.sleep(0.1)
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
        request = json.dumps({"op": op, "args": args or {}}).encode("utf-8")
        win32file.WriteFile(handle, request)

        overlapped = pywintypes.OVERLAPPED()
        buffer_size = 65536
        try:
            hr, data = win32file.ReadFile(handle, buffer_size, overlapped)
            if hr == 997:  # ERROR_IO_PENDING
                wait_result = pywintypes.WaitForSingleObject(overlapped.hEvent, int(timeout * 1000))
                if wait_result != 0:
                    win32file.CancelIo(handle)
                    raise AddInNotAvailableError(
                        f"Read from Add-In pipe timed out after {timeout}s"
                    )
                hr, data = win32file.GetOverlappedResult(handle, overlapped, True)
        except pywintypes.error as exc:
            if exc.winerror == 997:
                win32file.CancelIo(handle)
                raise AddInNotAvailableError(
                    f"Read from Add-In pipe timed out after {timeout}s"
                ) from exc
            raise AddInNotAvailableError(f"Pipe read error: {exc}") from exc

        response = json.loads(data.decode("utf-8").strip("\0"))
        if not response.get("ok", False):
            raise AddInOperationError(response.get("error", "Unknown Add-In error"))
        return response.get("data")
    finally:
        win32file.CloseHandle(handle)
