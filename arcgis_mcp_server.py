from __future__ import annotations

import json
import os
import subprocess
import sys
from dataclasses import asdict, dataclass
from functools import lru_cache
from pathlib import Path
from textwrap import dedent
from typing import Any

from mcp.server.fastmcp import FastMCP

from arcgis_aprx_archive import (
    can_read_project_archive,
    read_project_context_from_archive,
    read_project_layers_from_archive,
)
from arcgis_mcp_named_pipe import (
    AddInNotAvailableError,
    AddInOperationError,
    call_addin,
)
from arcgis_mcp_resources import (
    build_gdb_schema_resource_uri,
    build_project_context_resource_uri,
    build_project_layers_resource_uri,
    build_resource_payload,
    coerce_result_data,
    register_resources,
    result_to_dict,
)
from arcgis_mcp_services import (
    build_doctor_report,
    read_gdb_schema,
    read_project_context,
    read_project_layers,
    run_arcpy_runtime_check,
)
from arcgis_runtime_utils import (
    build_arcgis_subprocess_env,
    build_execution_hint,
    build_tool_payload,
    collect_runtime_context,
    create_temp_workspace,
    decode_resource_path,
    guess_install_dir_from_python,
    normalize_path,
    path_exists,
    remove_tree,
    resolve_temp_root,
    timestamp_utc_iso,
    validate_path,
)
from arcgis_script_templates import (
    build_arcpy_runtime_check_code,
    build_buffer_features_code,
    build_clip_features_code,
    build_conflict_analysis_code,
    build_export_suitability_map_code,
    build_gdb_schema_code,
    build_prepare_analysis_inputs_code,
    build_project_context_code,
    build_project_layers_code,
    build_raster_area_summary_code,
    build_reclassify_criteria_code,
    build_sensitivity_check_code,
    build_validate_project_data_code,
    build_weighted_suitability_code,
)

try:
    import winreg
except ImportError:  # pragma: no cover - only triggered on non-Windows
    winreg = None


SERVER_NAME = "ArcGIS Pro Bridge MCP Server"
DEFAULT_TIMEOUT_SECONDS = 300
ARCGIS_REGISTRY_PATHS = (
    r"SOFTWARE\ESRI\ArcGISPro",
    r"SOFTWARE\WOW6432Node\ESRI\ArcGISPro",
)
ARCGIS_PYTHON_RELATIVE_PATHS = (
    Path("bin/Python/envs/arcgispro-py3/python.exe"),
    Path("bin/Python/scripts/propy.bat"),
)
RUNNER_FILENAME = "arcgis_runner.py"
PAYLOAD_FILENAME = "payload.json"
RESULT_FILENAME = "result.json"

mcp = FastMCP(
    name=SERVER_NAME,
    instructions=(
        "Bridges AI agents with local ArcGIS Pro. "
        "ArcPy logic executes via ArcGIS Pro's bundled Python subprocess. "
        "pro.* tools interact with a live ArcGIS Pro session via the "
        "APBridgeAddIn C# Add-In over Named Pipes."
    ),
    json_response=True,
)


def _validate_gis_path(path: str | None, label: str) -> str | None:
    """Validate a GIS path against ARCGIS_MCP_ALLOWED_PATHS if set."""
    if path is None:
        return None
    try:
        return str(validate_path(path))
    except ValueError as exc:
        raise ValueError(f"{label}: {exc}") from exc


class ArcGISDiscoveryError(RuntimeError):
    """Raised when ArcGIS Pro environment cannot be discovered."""


def is_running_inside_pro() -> bool:
    """Detect whether this process is running inside ArcGIS Pro's Python window.

    ArcGIS Pro sets the ARCGIS_PRO_RUNNING environment variable and provides
    arcpy.mp.ArcGISProject("CURRENT") only when running in-process.
    """
    if os.environ.get("ARCGIS_PRO_RUNNING") == "1":
        return True
    try:
        import arcpy  # type: ignore

        test_project = arcpy.mp.ArcGISProject("CURRENT")
        return getattr(test_project, "filePath", None) is not None
    except Exception:
        return False


@dataclass(slots=True)
class ArcGISPythonInfo:
    install_dir: str
    python_executable: str
    source: str


@dataclass(slots=True)
class ArcPyExecutionResult:
    status: str
    exit_code: int
    python_executable: str
    stdout: str
    stderr: str
    data: Any | None = None
    error: dict[str, Any] | None = None
    hint: str | None = None
    workspace: str | None = None
    project_path: str | None = None


def _iter_env_python_candidates() -> list[tuple[str, str, str]]:
    candidates: list[tuple[str, str, str]] = []
    explicit_python = os.environ.get("ARCGIS_PRO_PYTHON")
    if explicit_python:
        python_path = Path(explicit_python).expanduser()
        normalized_python_path = normalize_path(python_path)
        candidates.append(
            (
                "env:ARCGIS_PRO_PYTHON",
                normalized_python_path,
                guess_install_dir_from_python(normalized_python_path),
            )
        )

    install_dir = os.environ.get("ARCGIS_PRO_INSTALL_DIR")
    if install_dir:
        install_path = Path(install_dir).expanduser()
        normalized_install_path = normalize_path(install_path)
        for relative_path in ARCGIS_PYTHON_RELATIVE_PATHS:
            candidates.append(
                (
                    "env:ARCGIS_PRO_INSTALL_DIR",
                    normalize_path(install_path / relative_path),
                    normalized_install_path,
                )
            )

    return candidates


def _iter_registry_install_dirs() -> list[tuple[str, str]]:
    if winreg is None:
        return []

    discovered: list[tuple[str, str]] = []
    key_read = getattr(winreg, "KEY_READ", 0)
    registry_views = [key_read]
    for extra_flag_name in ("KEY_WOW64_64KEY", "KEY_WOW64_32KEY"):
        extra_flag = getattr(winreg, extra_flag_name, 0)
        if extra_flag:
            registry_views.append(key_read | extra_flag)

    for registry_path in ARCGIS_REGISTRY_PATHS:
        for view_flag in registry_views:
            try:
                with winreg.OpenKey(
                    winreg.HKEY_LOCAL_MACHINE, registry_path, 0, key_read | view_flag
                ) as key:
                    install_dir, _ = winreg.QueryValueEx(key, "InstallDir")
            except OSError:
                continue
            discovered.append((f"registry:{registry_path}", normalize_path(install_dir)))

    return discovered


def _build_python_candidates() -> list[tuple[str, str, str]]:
    candidates: list[tuple[str, str, str]] = []
    seen: set[str] = set()

    for source, python_path, install_dir in _iter_env_python_candidates():
        normalized = normalize_path(python_path)
        if normalized in seen:
            continue
        seen.add(normalized)
        candidates.append((source, normalized, install_dir))

    for source, install_dir in _iter_registry_install_dirs():
        install_path = Path(install_dir)
        for relative_path in ARCGIS_PYTHON_RELATIVE_PATHS:
            python_path = normalize_path(install_path / relative_path)
            if python_path in seen:
                continue
            seen.add(python_path)
            candidates.append((source, python_path, normalize_path(install_path)))

    filesystem_fallback = Path(r"C:\Program Files\ArcGIS\Pro")
    for relative_path in ARCGIS_PYTHON_RELATIVE_PATHS:
        python_path = normalize_path(filesystem_fallback / relative_path)
        if python_path in seen:
            continue
        seen.add(python_path)
        candidates.append(("filesystem:default", python_path, normalize_path(filesystem_fallback)))

    return candidates


def clear_discovery_cache() -> None:
    discover_arcgis_pro_python.cache_clear()


@lru_cache(maxsize=1)
def discover_arcgis_pro_python() -> ArcGISPythonInfo:
    """Auto-discover ArcGIS Pro's bundled Python interpreter."""
    for source, python_path, install_dir in _build_python_candidates():
        if Path(python_path).exists():
            return ArcGISPythonInfo(
                install_dir=normalize_path(install_dir),
                python_executable=python_path,
                source=source,
            )

    raise ArcGISDiscoveryError(
        "ArcGIS Pro Python interpreter not found. Confirm ArcGIS Pro is installed, "
        "or provide path via ARCGIS_PRO_PYTHON / ARCGIS_PRO_INSTALL_DIR."
    )


def _build_runner_script() -> str:
    """Generate wrapper script executed in ArcGIS Python environment."""
    return dedent(
        """
        from __future__ import annotations

        import contextlib
        import io
        import json
        import os
        import sys
        import traceback
        from pathlib import Path

        payload_path = Path(sys.argv[1])
        result_path = Path(sys.argv[2])

        payload = json.loads(payload_path.read_text(encoding="utf-8"))
        code = payload["code"]
        workspace = payload.get("workspace")
        project_path = payload.get("project_path")
        open_current_project = payload.get("open_current_project", False)
        require_arcpy = payload.get("require_arcpy", True)

        class ArcGISProNotRunningError(RuntimeError):
            pass

        stdout_buffer = io.StringIO()
        stderr_buffer = io.StringIO()
        namespace = {
            "__name__": "__main__",
            "__file__": str(payload_path.with_name("user_code.py")),
        }
        namespace["__arcgis_mcp_result__"] = None

        def set_result(value):
            namespace["__arcgis_mcp_result__"] = value

        namespace["set_result"] = set_result
        error = None
        status = "success"

        try:
            arcpy = None
            if require_arcpy:
                import arcpy  # type: ignore

                namespace["arcpy"] = arcpy
                if workspace:
                    arcpy.env.workspace = workspace

                def open_project(path=None):
                    if open_current_project and not path and not project_path:
                        raise ArcGISProNotRunningError(
                            'ArcGISProject("CURRENT") only works inside '
                            "ArcGIS Pro's Python window. "
                            "Close ArcGIS Pro and provide an explicit .aprx "
                            "path via project_path, or run this code directly "
                            "in ArcGIS Pro's Python window."
                        )
                    target = path or project_path
                    return arcpy.mp.ArcGISProject(target)

                namespace["open_project"] = open_project

                if project_path:
                    namespace["arcgis_project"] = arcpy.mp.ArcGISProject(project_path)
                elif open_current_project:
                    raise ArcGISProNotRunningError(
                        "open_current_project=True only works inside ArcGIS Pro's Python window. "
                        "Provide a project_path parameter with an explicit .aprx file path instead."
                    )

            os.environ["ARCGIS_MCP_WORKSPACE"] = workspace or ""
            os.environ["ARCGIS_MCP_PROJECT_PATH"] = project_path or ""

            with (
                contextlib.redirect_stdout(stdout_buffer),
                contextlib.redirect_stderr(stderr_buffer),
            ):
                exec(compile(code, namespace["__file__"], "exec"), namespace, namespace)
        except Exception as exc:  # noqa: BLE001
            status = "error"
            error = {
                "type": exc.__class__.__name__,
                "message": str(exc),
                "traceback": traceback.format_exc(),
            }

        result = {
            "status": status,
            "stdout": stdout_buffer.getvalue(),
            "stderr": stderr_buffer.getvalue(),
            "data": namespace.get("__arcgis_mcp_result__"),
            "error": error,
            "workspace": workspace,
            "project_path": project_path,
        }
        result_path.write_text(
            json.dumps(result, ensure_ascii=False, indent=2, default=str),
            encoding="utf-8",
        )
        sys.exit(0 if status == "success" else 1)
        """
    ).strip()


def run_in_arcgis_env(
    code: str,
    *,
    workspace: str | None = None,
    project_path: str | None = None,
    open_current_project: bool = False,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
    python_executable: str | None = None,
    require_arcpy: bool = True,
) -> ArcPyExecutionResult:
    """Execute code in ArcGIS Pro Python environment and return structured results."""
    resolved_python = python_executable
    if resolved_python is None:
        resolved_python = discover_arcgis_pro_python().python_executable

    temp_root = resolve_temp_root()
    temp_path = create_temp_workspace("arcgis-mcp-", temp_root)
    try:
        payload_path = temp_path / PAYLOAD_FILENAME
        result_path = temp_path / RESULT_FILENAME
        runner_path = temp_path / RUNNER_FILENAME
        local_appdata_path = temp_path / "localappdata"

        payload = {
            "code": code,
            "workspace": workspace,
            "project_path": project_path,
            "open_current_project": open_current_project,
            "require_arcpy": require_arcpy,
        }
        payload_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        runner_path.write_text(_build_runner_script(), encoding="utf-8")

        try:
            completed = subprocess.run(  # noqa: S603
                [resolved_python, str(runner_path), str(payload_path), str(result_path)],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                stdin=subprocess.DEVNULL,
                cwd=str(temp_path),
                env=build_arcgis_subprocess_env(local_appdata_root=local_appdata_path),
                timeout=timeout_seconds,
                check=False,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except subprocess.TimeoutExpired as exc:
            return ArcPyExecutionResult(
                status="error",
                exit_code=-1,
                python_executable=normalize_path(resolved_python),
                stdout=exc.stdout or "",
                stderr=exc.stderr or "",
                data=None,
                error={
                    "type": "TimeoutExpired",
                    "message": (
                        "ArcGIS Python subprocess execution timed out "
                        f"after {timeout_seconds} seconds."
                    ),
                },
                hint=(
                    "Narrow the processing scope, optimize the script, "
                    "or increase timeout_seconds and retry."
                ),
                workspace=workspace,
                project_path=project_path,
            )

        if result_path.exists():
            result_payload = json.loads(result_path.read_text(encoding="utf-8"))
        else:
            result_payload = {
                "status": "error",
                "stdout": completed.stdout,
                "stderr": completed.stderr,
                "data": None,
                "error": {
                    "type": "RunnerExecutionError",
                    "message": "ArcGIS Python subprocess did not produce a result file.",
                },
                "workspace": workspace,
                "project_path": project_path,
            }
    finally:
        remove_tree(temp_path)

    hint = build_execution_hint(result_payload.get("stderr", ""), result_payload.get("error"))
    return ArcPyExecutionResult(
        status=result_payload.get("status", "error"),
        exit_code=completed.returncode,
        python_executable=normalize_path(resolved_python),
        stdout=result_payload.get("stdout", ""),
        stderr=result_payload.get("stderr", completed.stderr),
        data=result_payload.get("data"),
        error=result_payload.get("error"),
        hint=hint,
        workspace=workspace,
        project_path=project_path,
    )


def _run_arcpy_runtime_check(
    *,
    timeout_seconds: int = 60,
) -> ArcPyExecutionResult:
    return run_arcpy_runtime_check(
        run_in_arcgis_env=run_in_arcgis_env,
        build_arcpy_runtime_check_code=build_arcpy_runtime_check_code,
        timeout_seconds=timeout_seconds,
    )


def _read_project_layers(
    *,
    project_path: str | None = None,
    open_current_project: bool = False,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
    include_fields: bool = False,
    include_data_source_details: bool = False,
) -> ArcPyExecutionResult:
    if can_read_project_archive(project_path, open_current_project=open_current_project):
        return ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=normalize_path(sys.executable),
            stdout="",
            stderr="",
            data=read_project_layers_from_archive(
                project_path,
                include_fields=include_fields,
                include_data_source_details=include_data_source_details,
            ),
            workspace=None,
            project_path=project_path,
        )

    return read_project_layers(
        run_in_arcgis_env=run_in_arcgis_env,
        build_project_layers_code=build_project_layers_code,
        project_path=project_path,
        open_current_project=open_current_project,
        timeout_seconds=timeout_seconds,
        include_fields=include_fields,
        include_data_source_details=include_data_source_details,
    )


def _read_gdb_schema(gdb_path: str) -> ArcPyExecutionResult:
    return read_gdb_schema(
        run_in_arcgis_env=run_in_arcgis_env,
        build_gdb_schema_code=build_gdb_schema_code,
        gdb_path=gdb_path,
    )


def _read_project_context(
    *,
    project_path: str | None = None,
    open_current_project: bool = False,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
    include_source_details: bool = False,
) -> ArcPyExecutionResult:
    if can_read_project_archive(project_path, open_current_project=open_current_project):
        return ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=normalize_path(sys.executable),
            stdout="",
            stderr="",
            data=read_project_context_from_archive(
                project_path,
                include_source_details=include_source_details,
            ),
            workspace=None,
            project_path=project_path,
        )

    return read_project_context(
        run_in_arcgis_env=run_in_arcgis_env,
        build_project_context_code=build_project_context_code,
        project_path=project_path,
        open_current_project=open_current_project,
        timeout_seconds=timeout_seconds,
        include_source_details=include_source_details,
    )


def _build_doctor_report(timeout_seconds: int = 60) -> dict[str, Any]:
    return build_doctor_report(
        server_name=SERVER_NAME,
        timestamp_utc_iso=timestamp_utc_iso,
        discover_arcgis_pro_python=discover_arcgis_pro_python,
        arcgis_discovery_error=ArcGISDiscoveryError,
        run_runtime_check=_run_arcpy_runtime_check,
        path_exists=path_exists,
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        timeout_seconds=timeout_seconds,
    )


_resource_handlers = register_resources(
    mcp,
    server_name=SERVER_NAME,
    decode_resource_path=decode_resource_path,
    discover_arcgis_pro_python=discover_arcgis_pro_python,
    arcgis_discovery_error=ArcGISDiscoveryError,
    read_project_layers=lambda **kwargs: _read_project_layers(**kwargs),
    read_project_context=lambda **kwargs: _read_project_context(**kwargs),
    read_gdb_schema=lambda gdb_path: _read_gdb_schema(gdb_path),
)
server_status = _resource_handlers["server_status"]
gis_resource_catalog = _resource_handlers["gis_resource_catalog"]
current_project_layers_resource = _resource_handlers["current_project_layers_resource"]
project_layers_resource = _resource_handlers["project_layers_resource"]
current_project_context_resource = _resource_handlers["current_project_context_resource"]
project_context_resource = _resource_handlers["project_context_resource"]
gdb_schema_resource = _resource_handlers["gdb_schema_resource"]


@mcp.tool()
def detect_arcgis_environment() -> dict[str, Any]:
    """Detect ArcGIS Pro installation and Python interpreter path."""
    try:
        return {
            "status": "ready",
            "arcgis": asdict(discover_arcgis_pro_python()),
            "resource_catalog_uri": "arcgis://resources/catalog",
        }
    except ArcGISDiscoveryError as exc:
        return {
            "status": "unavailable",
            "message": str(exc),
        }


@mcp.tool()
def ping() -> dict[str, Any]:
    """Return a minimal verifiable result to confirm the client is actually calling the MCP Tool."""
    return {
        "status": "ok",
        "server": SERVER_NAME,
        "timestamp_utc": timestamp_utc_iso(),
        "message": (
            "If you see this result, the request has successfully entered the MCP Tool call chain."
        ),
    }


@mcp.tool()
def health_check(timeout_seconds: int = 30) -> dict[str, Any]:
    """Return a lightweight health check to determine if MCP and
    ArcGIS environment are available.
    """
    payload: dict[str, Any] = {
        "status": "ready",
        "server": SERVER_NAME,
        "timestamp_utc": timestamp_utc_iso(),
        "mcp": {
            "status": "ok",
            "message": "health_check has been called, client is currently using MCP Tool.",
        },
    }

    try:
        python_info = discover_arcgis_pro_python()
        payload["arcgis_python"] = asdict(python_info)
    except ArcGISDiscoveryError as exc:
        payload["status"] = "unavailable"
        payload["arcgis_python"] = None
        payload["message"] = str(exc)
        payload["next_step"] = (
            "Confirm ArcGIS Pro is installed and can start; if testing "
            "in Trae or Cursor, explicitly ask the client to call the "
            "MCP Tool directly instead of writing test scripts or "
            "manually starting the server."
        )
        return payload

    runtime_result = _run_arcpy_runtime_check(timeout_seconds=timeout_seconds)
    payload["runtime"] = result_to_dict(runtime_result)
    payload["runtime_data"] = coerce_result_data(runtime_result)

    if runtime_result.status != "success":
        payload["status"] = "warning"
        payload["message"] = (
            runtime_result.error.get("message")
            if runtime_result.error
            else "ArcPy runtime check did not pass."
        )
        payload["next_step"] = (
            "Next step: call doctor for full diagnostics, "
            "focus on license status, ArcPy runtime, and whether the client is actually using MCP."
        )
        return payload

    payload["message"] = "MCP reachable, ArcGIS Pro Python discovered, ArcPy runtime check passed."
    payload["next_step"] = (
        "Continue with inspect_gdb, inspect_project_context, buffer_features, or clip_features."
    )
    return payload


@mcp.tool()
def doctor(timeout_seconds: int = 60) -> dict[str, Any]:
    """Return a comprehensive environment diagnostic report for GIS users."""
    return _build_doctor_report(timeout_seconds=timeout_seconds)


@mcp.tool()
def debug_runtime_context() -> dict[str, Any]:
    """Return the current MCP process runtime context for debugging
    Trae or sandbox environment differences.
    """
    return {
        "status": "ready",
        "server": SERVER_NAME,
        "timestamp_utc": timestamp_utc_iso(),
        "context": collect_runtime_context(),
    }


@mcp.tool()
def execute_arcpy_code(
    code: str,
    workspace: str | None = None,
    project_path: str | None = None,
    open_current_project: bool = False,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
) -> dict[str, Any]:
    """Execute ArcPy code in ArcGIS Pro Python environment and return
    stdout, stderr, and exception info.
    """
    try:
        workspace = _validate_gis_path(workspace, "workspace")
        project_path = _validate_gis_path(project_path, "project_path")
    except ValueError as exc:
        return {"status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            code,
            workspace=workspace,
            project_path=project_path,
            open_current_project=open_current_project,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "status": "unavailable",
            "message": str(exc),
        }
    return result_to_dict(result)


@mcp.tool()
def buffer_features(
    in_features: str,
    out_feature_class: str,
    buffer_distance_or_field: str,
    dissolve_option: str = "NONE",
    dissolve_field: str | None = None,
    method: str = "PLANAR",
    workspace: str | None = None,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
) -> dict[str, Any]:
    """Execute Buffer analysis, return output feature summary and execution info."""
    try:
        in_features = _validate_gis_path(in_features, "in_features")
        out_feature_class = _validate_gis_path(out_feature_class, "out_feature_class")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "buffer_features", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_buffer_features_code(
                in_features=in_features,
                out_feature_class=out_feature_class,
                buffer_distance_or_field=buffer_distance_or_field,
                dissolve_option=dissolve_option,
                dissolve_field=dissolve_field,
                method=method,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "buffer_features",
            "status": "unavailable",
            "message": str(exc),
        }

    return build_tool_payload(
        result,
        tool_name="buffer_features",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Buffer execution completed." if result.status == "success" else None,
        inputs={
            "in_features": in_features,
            "out_feature_class": out_feature_class,
            "buffer_distance_or_field": buffer_distance_or_field,
            "dissolve_option": dissolve_option,
            "dissolve_field": dissolve_field,
            "method": method,
            "workspace": workspace,
            "timeout_seconds": timeout_seconds,
        },
    )


@mcp.tool()
def clip_features(
    in_features: str,
    clip_features_path: str,
    out_feature_class: str,
    cluster_tolerance: str | None = None,
    workspace: str | None = None,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
) -> dict[str, Any]:
    """Execute Clip analysis, return output feature summary and execution info."""
    try:
        in_features = _validate_gis_path(in_features, "in_features")
        clip_features_path = _validate_gis_path(clip_features_path, "clip_features_path")
        out_feature_class = _validate_gis_path(out_feature_class, "out_feature_class")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "clip_features", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_clip_features_code(
                in_features=in_features,
                clip_features=clip_features_path,
                out_feature_class=out_feature_class,
                cluster_tolerance=cluster_tolerance,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "clip_features",
            "status": "unavailable",
            "message": str(exc),
        }

    return build_tool_payload(
        result,
        tool_name="clip_features",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Clip execution completed." if result.status == "success" else None,
        inputs={
            "in_features": in_features,
            "clip_features": clip_features_path,
            "out_feature_class": out_feature_class,
            "cluster_tolerance": cluster_tolerance,
            "workspace": workspace,
            "timeout_seconds": timeout_seconds,
        },
    )


@mcp.tool()
def build_gis_resource_uri(
    resource_kind: str,
    path: str | None = None,
    open_current_project: bool = False,
) -> dict[str, Any]:
    """Generate a readable ArcGIS Resource URI based on resource type and local path."""
    if resource_kind == "project_layers":
        return {
            "status": "ready",
            "resource_kind": resource_kind,
            "resource_uri": build_project_layers_resource_uri(
                path,
                open_current_project=open_current_project,
            ),
        }

    if resource_kind == "project_context":
        return {
            "status": "ready",
            "resource_kind": resource_kind,
            "resource_uri": build_project_context_resource_uri(
                path,
                open_current_project=open_current_project,
            ),
        }

    if resource_kind == "gdb_schema":
        if not path:
            return {
                "status": "error",
                "message": "gdb_schema resource requires a gdb path.",
            }
        return {
            "status": "ready",
            "resource_kind": resource_kind,
            "resource_uri": build_gdb_schema_resource_uri(path),
        }

    return {
        "status": "error",
        "message": (
            "Unsupported resource_kind, valid values are project_layers, "
            "project_context, or gdb_schema."
        ),
    }


@mcp.tool()
def list_gis_layers(
    project_path: str | None = None,
    open_current_project: bool = False,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
    include_fields: bool = False,
    include_data_source_details: bool = False,
) -> dict[str, Any]:
    """List maps, layers, fields, and spatial references in the project
    and return corresponding Resource URI.
    """
    try:
        project_path = _validate_gis_path(project_path, "project_path")
    except ValueError as exc:
        return {"status": "error", "message": str(exc)}
    result = _read_project_layers(
        project_path=project_path,
        open_current_project=open_current_project,
        timeout_seconds=timeout_seconds,
        include_fields=include_fields,
        include_data_source_details=include_data_source_details,
    )
    return build_resource_payload(
        result,
        resource_uri=build_project_layers_resource_uri(
            project_path,
            open_current_project=open_current_project,
        ),
        resource_kind="project_layers",
    )


@mcp.tool()
def inspect_project_context(
    project_path: str | None = None,
    open_current_project: bool = False,
    timeout_seconds: int = DEFAULT_TIMEOUT_SECONDS,
    include_source_details: bool = False,
) -> dict[str, Any]:
    """Read project overview including layouts, map frames, default map
    candidates, and data source status.
    """
    try:
        project_path = _validate_gis_path(project_path, "project_path")
    except ValueError as exc:
        return {"status": "error", "message": str(exc)}
    result = _read_project_context(
        project_path=project_path,
        open_current_project=open_current_project,
        timeout_seconds=timeout_seconds,
        include_source_details=include_source_details,
    )
    return build_resource_payload(
        result,
        resource_uri=build_project_context_resource_uri(
            project_path,
            open_current_project=open_current_project,
        ),
        resource_kind="project_context",
    )


@mcp.tool()
def inspect_gdb(gdb_path: str) -> dict[str, Any]:
    """Inspect GDB feature classes, fields, and spatial references and
    return corresponding Resource URI.
    """
    try:
        gdb_path = _validate_gis_path(gdb_path, "gdb_path")
    except ValueError as exc:
        return {"status": "error", "message": str(exc)}
    result = _read_gdb_schema(gdb_path)
    return build_resource_payload(
        result,
        resource_uri=build_gdb_schema_resource_uri(gdb_path),
        resource_kind="gdb_schema",
    )


@mcp.tool()
def generate_sync_plan(
    source_description: str, project_context: str | None = None
) -> dict[str, Any]:
    """Placeholder interface for data sync logic, to be extended later
    with diff analysis and script generation.
    """
    return {
        "status": "todo",
        "message": "Data sync capability is not yet implemented, tool interface is reserved.",
        "source_description": source_description,
        "project_context": project_context,
    }


@mcp.tool()
def validate_project_data(
    project_gdb: str,
    land_values_gdb: str | None = None,
    required_layers: str = "",
    target_srs: str = "28356",
    study_area_fc: str | None = None,
    workspace: str | None = None,
    timeout_seconds: int = 120,
) -> dict[str, Any]:
    """Pre-flight check that all required data exists and is compatible."""
    try:
        project_gdb = _validate_gis_path(project_gdb, "project_gdb")
        land_values_gdb = _validate_gis_path(land_values_gdb, "land_values_gdb")
        study_area_fc = _validate_gis_path(study_area_fc, "study_area_fc")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "validate_project_data", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_validate_project_data_code(
                project_gdb=project_gdb,
                land_values_gdb=land_values_gdb,
                required_layers_json=required_layers,
                target_srs=target_srs,
                study_area_fc=study_area_fc,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "validate_project_data",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="validate_project_data",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Validation completed." if result.status == "success" else None,
        inputs={
            "project_gdb": project_gdb,
            "land_values_gdb": land_values_gdb,
            "required_layers": required_layers,
            "target_srs": target_srs,
            "study_area_fc": study_area_fc,
        },
    )


@mcp.tool()
def prepare_analysis_inputs(
    project_gdb: str,
    study_area_fc: str,
    land_values_gdb: str | None = None,
    cell_size: int = 30,
    snap_raster_name: str = "forest_prop",
    dem_name: str = "dem",
    distance_sources: str = "",
    output_gdb: str | None = None,
    workspace: str | None = None,
    timeout_seconds: int = 900,
) -> dict[str, Any]:
    """One-shot data preparation: clip, resample DEM, derive slope, create distance rasters."""
    try:
        project_gdb = _validate_gis_path(project_gdb, "project_gdb")
        study_area_fc = _validate_gis_path(study_area_fc, "study_area_fc")
        land_values_gdb = _validate_gis_path(land_values_gdb, "land_values_gdb")
        output_gdb = _validate_gis_path(output_gdb, "output_gdb")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "prepare_analysis_inputs", "status": "error", "message": str(exc)}
    if distance_sources == "":
        distance_sources = json.dumps(
            {
                "water": "water_courses",
                "roads": "roads_tracks",
                "coastline": "coastline",
                "protected": "protected_areas",
            }
        )
    if output_gdb is None and project_gdb:
        parent = os.path.dirname(project_gdb)
        output_gdb = os.path.join(parent, "analysis_outputs.gdb")
    try:
        result = run_in_arcgis_env(
            build_prepare_analysis_inputs_code(
                project_gdb=project_gdb,
                land_values_gdb=land_values_gdb,
                study_area_fc=study_area_fc,
                cell_size=cell_size,
                snap_raster_name=snap_raster_name,
                dem_name=dem_name,
                distance_sources_json=distance_sources,
                output_gdb=output_gdb,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "prepare_analysis_inputs",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="prepare_analysis_inputs",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Data preparation completed." if result.status == "success" else None,
        inputs={
            "project_gdb": project_gdb,
            "study_area_fc": study_area_fc,
            "cell_size": cell_size,
            "output_gdb": output_gdb,
        },
    )


@mcp.tool()
def reclassify_criteria(
    reclass_table: str,
    output_gdb: str,
    nodata_value: int = 1,
    workspace: str | None = None,
    timeout_seconds: int = 600,
) -> dict[str, Any]:
    """Batch reclassify multiple rasters to a common 1-5 suitability scale."""
    try:
        output_gdb = _validate_gis_path(output_gdb, "output_gdb")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "reclassify_criteria", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_reclassify_criteria_code(
                reclass_table_json=reclass_table,
                output_gdb=output_gdb,
                nodata_value=nodata_value,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "reclassify_criteria",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="reclassify_criteria",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Reclassification completed." if result.status == "success" else None,
        inputs={
            "output_gdb": output_gdb,
            "nodata_value": nodata_value,
        },
    )


@mcp.tool()
def weighted_suitability(
    model_name: str,
    criteria: str,
    output_raster: str,
    normalize: bool = True,
    eval_min: float = 1.0,
    eval_max: float = 5.0,
    workspace: str | None = None,
    timeout_seconds: int = 600,
) -> dict[str, Any]:
    """Weighted Linear Combination of reclassified criteria rasters."""
    try:
        output_raster = _validate_gis_path(output_raster, "output_raster")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "weighted_suitability", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_weighted_suitability_code(
                model_name=model_name,
                criteria_json=criteria,
                output_raster=output_raster,
                normalize=normalize,
                eval_min=eval_min,
                eval_max=eval_max,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "weighted_suitability",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="weighted_suitability",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Suitability model completed." if result.status == "success" else None,
        inputs={
            "model_name": model_name,
            "output_raster": output_raster,
            "normalize": normalize,
        },
    )


@mcp.tool()
def conflict_analysis(
    conservation_raster: str,
    urban_raster: str,
    threshold: float = 4.0,
    output_gdb: str | None = None,
    land_use_raster: str | None = None,
    cell_size_area_ha: float | None = None,
    workspace: str | None = None,
    timeout_seconds: int = 600,
) -> dict[str, Any]:
    """Identify conflict zones and create allocation map from two suitability rasters."""
    if not output_gdb:
        return {
            "tool": "conflict_analysis",
            "status": "error",
            "message": "output_gdb is required and cannot be empty",
        }
    try:
        conservation_raster = _validate_gis_path(conservation_raster, "conservation_raster")
        urban_raster = _validate_gis_path(urban_raster, "urban_raster")
        land_use_raster = _validate_gis_path(land_use_raster, "land_use_raster")
        output_gdb = _validate_gis_path(output_gdb, "output_gdb")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "conflict_analysis", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_conflict_analysis_code(
                conservation_raster=conservation_raster,
                urban_raster=urban_raster,
                threshold=threshold,
                output_gdb=output_gdb,
                land_use_raster=land_use_raster,
                cell_size_area_ha=cell_size_area_ha,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "conflict_analysis",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="conflict_analysis",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Conflict analysis completed." if result.status == "success" else None,
        inputs={
            "conservation_raster": conservation_raster,
            "urban_raster": urban_raster,
            "threshold": threshold,
            "output_gdb": output_gdb,
        },
    )


@mcp.tool()
def raster_area_summary(
    raster_path: str,
    cell_area_ha: float | None = None,
    output_csv: str | None = None,
    workspace: str | None = None,
    timeout_seconds: int = 120,
) -> dict[str, Any]:
    """Compute area statistics by suitability class for report tables."""
    try:
        raster_path = _validate_gis_path(raster_path, "raster_path")
        output_csv = _validate_gis_path(output_csv, "output_csv")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "raster_area_summary", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_raster_area_summary_code(
                raster_path=raster_path,
                cell_area_ha=cell_area_ha,
                output_csv=output_csv,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "raster_area_summary",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="raster_area_summary",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Area summary completed." if result.status == "success" else None,
        inputs={
            "raster_path": raster_path,
            "cell_area_ha": cell_area_ha,
            "output_csv": output_csv,
        },
    )


@mcp.tool()
def sensitivity_check(
    conservation_rasters: str,
    urban_rasters: str,
    conservation_weights: str,
    urban_weights: str,
    baseline_allocation: str,
    perturbation_pct: float = 10.0,
    thresholds: str = "[3.5, 4.0, 4.5]",
    output_gdb: str = "",
    output_csv: str | None = None,
    workspace: str | None = None,
    timeout_seconds: int = 1800,
) -> dict[str, Any]:
    """Run WLC with perturbed weights and report allocation change sensitivity."""
    try:
        baseline_allocation = _validate_gis_path(baseline_allocation, "baseline_allocation")
        output_gdb = _validate_gis_path(output_gdb, "output_gdb")
        output_csv = _validate_gis_path(output_csv, "output_csv")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "sensitivity_check", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_sensitivity_check_code(
                conservation_rasters_json=conservation_rasters,
                urban_rasters_json=urban_rasters,
                conservation_weights_json=conservation_weights,
                urban_weights_json=urban_weights,
                baseline_allocation=baseline_allocation,
                perturbation_pct=perturbation_pct,
                thresholds_json=thresholds,
                output_gdb=output_gdb,
                output_csv=output_csv,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "sensitivity_check",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="sensitivity_check",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Sensitivity analysis completed." if result.status == "success" else None,
        inputs={
            "baseline_allocation": baseline_allocation,
            "perturbation_pct": perturbation_pct,
            "output_gdb": output_gdb,
        },
    )


@mcp.tool()
def export_suitability_map(
    project_path: str,
    raster_path: str,
    map_name: str = "Suitability",
    title: str = "Suitability Map",
    subtitle: str | None = None,
    classification: str = "",
    output_format: str = "PDF",
    output_path: str = "",
    dpi: int = 300,
    workspace: str | None = None,
    timeout_seconds: int = 300,
) -> dict[str, Any]:
    """Create a publication-quality layout and export to PDF or PNG."""
    try:
        project_path = _validate_gis_path(project_path, "project_path")
        raster_path = _validate_gis_path(raster_path, "raster_path")
        output_path = _validate_gis_path(output_path, "output_path")
        workspace = _validate_gis_path(workspace, "workspace")
    except ValueError as exc:
        return {"tool": "export_suitability_map", "status": "error", "message": str(exc)}
    try:
        result = run_in_arcgis_env(
            build_export_suitability_map_code(
                project_path=project_path,
                raster_path=raster_path,
                map_name=map_name,
                title=title,
                subtitle=subtitle,
                classification_json=classification,
                output_format=output_format,
                output_path=output_path,
                dpi=dpi,
            ),
            workspace=workspace,
            timeout_seconds=timeout_seconds,
            require_arcpy=True,
        )
    except ArcGISDiscoveryError as exc:
        return {
            "tool": "export_suitability_map",
            "status": "unavailable",
            "message": str(exc),
        }
    return build_tool_payload(
        result,
        tool_name="export_suitability_map",
        result_to_dict=result_to_dict,
        coerce_result_data=coerce_result_data,
        message="Map export completed." if result.status == "success" else None,
        inputs={
            "project_path": project_path,
            "raster_path": raster_path,
            "map_name": map_name,
            "output_format": output_format,
            "output_path": output_path,
            "dpi": dpi,
        },
    )


def _call_addin(op: str, args: dict[str, str] | None = None) -> dict[str, Any]:
    try:
        data = call_addin(op, args)
        return {"status": "ok", "data": data}
    except AddInNotAvailableError as exc:
        return {
            "status": "unavailable",
            "message": str(exc),
            "next_step": (
                "Ensure ArcGIS Pro is running with the APBridgeAddIn loaded "
                "(it auto-starts on Pro launch after installation). "
                "If the Add-In is not installed, build the project in "
                "addin/APBridgeAddIn/ with Visual Studio and install the "
                "resulting .esriAddInX file."
            ),
        }
    except AddInOperationError as exc:
        return {"status": "error", "message": str(exc)}


@mcp.tool()
def pro_ping() -> dict[str, Any]:
    """Ping the ArcGIS Pro Add-In to verify Named Pipe connectivity."""
    return _call_addin("pro.ping")


@mcp.tool()
def pro_get_active_map_name() -> dict[str, Any]:
    """Get the name of the active map in ArcGIS Pro."""
    return _call_addin("pro.getActiveMapName")


@mcp.tool()
def pro_list_layers() -> dict[str, Any]:
    """List all layers in the active ArcGIS Pro map with visibility and type."""
    return _call_addin("pro.listLayers")


@mcp.tool()
def pro_count_features(layer: str) -> dict[str, Any]:
    """Count features in a layer by name in the active ArcGIS Pro map."""
    return _call_addin("pro.countFeatures", {"layer": layer})


@mcp.tool()
def pro_get_layer_schema(layer: str) -> dict[str, Any]:
    """Get field schema (name, type, alias, length, precision, etc.) for a layer."""
    return _call_addin("pro.getLayerSchema", {"layer": layer})


@mcp.tool()
def pro_get_selection_count(layer: str) -> dict[str, Any]:
    """Count selected features in a layer by name."""
    return _call_addin("pro.getSelectionCount", {"layer": layer})


@mcp.tool()
def pro_select_by_attribute(layer: str, where: str) -> dict[str, Any]:
    """Select features in a layer using a SQL where clause."""
    return _call_addin("pro.selectByAttribute", {"layer": layer, "where": where})


@mcp.tool()
def pro_clear_selection(layer: str | None = None) -> dict[str, Any]:
    """Clear selection on a specific layer, or all layers if no layer specified."""
    args = {}
    if layer:
        args["layer"] = layer
    return _call_addin("pro.clearSelection", args)


@mcp.tool()
def pro_zoom_to_layer(layer: str) -> dict[str, Any]:
    """Zoom the active map view to a layer's extent."""
    return _call_addin("pro.zoomToLayer", {"layer": layer})


@mcp.tool()
def pro_get_current_extent() -> dict[str, Any]:
    """Get the current map view extent (xmin, ymin, xmax, ymax, spatial reference)."""
    return _call_addin("pro.getCurrentExtent")


@mcp.tool()
def pro_pan_to_extent(
    xmin: float,
    ymin: float,
    xmax: float,
    ymax: float,
) -> dict[str, Any]:
    """Pan the active map view to a specified bounding box extent."""
    return _call_addin(
        "pro.panToExtent",
        {
            "xmin": str(xmin),
            "ymin": str(ymin),
            "xmax": str(xmax),
            "ymax": str(ymax),
        },
    )


@mcp.tool()
def pro_get_camera() -> dict[str, Any]:
    """Get the current camera position from the active ArcGIS Pro map view."""
    return _call_addin("pro.getCamera")


@mcp.tool()
def pro_set_layer_visibility(layer: str, visible: bool) -> dict[str, Any]:
    """Set the visibility of a layer by name in the active ArcGIS Pro map."""
    return _call_addin(
        "pro.setLayerVisibility",
        {"layer": layer, "visible": str(visible).lower()},
    )


@mcp.tool()
def pro_get_layer_extent(layer: str) -> dict[str, Any]:
    """Get the full spatial extent (xmin, ymin, xmax, ymax) of a feature layer."""
    return _call_addin("pro.getLayerExtent", {"layer": layer})


@mcp.tool()
def pro_select_by_rectangle(
    layer: str,
    xmin: float,
    ymin: float,
    xmax: float,
    ymax: float,
    selection_type: str = "NEW",
) -> dict[str, Any]:
    """Select features in a layer within a rectangle. selection_type: NEW, ADD, SUBTRACT, or AND."""
    return _call_addin(
        "pro.selectByRectangle",
        {
            "layer": layer,
            "xmin": str(xmin),
            "ymin": str(ymin),
            "xmax": str(xmax),
            "ymax": str(ymax),
            "selectionType": selection_type,
        },
    )


@mcp.tool()
def pro_switch_selection(layer: str | None = None) -> dict[str, Any]:
    """Invert the selection on a specific layer, or all layers if none specified."""
    args = {}
    if layer:
        args["layer"] = layer
    return _call_addin("pro.switchSelection", args)


@mcp.tool()
def pro_get_feature_by_oid(layer: str, oid: int) -> dict[str, Any]:
    """Get all attribute values for a feature by its ObjectID."""
    return _call_addin("pro.getFeatureByOid", {"layer": layer, "oid": str(oid)})


@mcp.tool()
def pro_undo_edit() -> dict[str, Any]:
    """Undo the last edit operation in ArcGIS Pro."""
    return _call_addin("pro.undoEdit")


@mcp.tool()
def pro_redo_edit() -> dict[str, Any]:
    """Redo the last undone edit operation in ArcGIS Pro."""
    return _call_addin("pro.redoEdit")


@mcp.tool()
def pro_set_active_tool(tool: str) -> dict[str, Any]:
    """Set the active map tool by DAML ID (e.g. esri_mapping_exploreTool)."""
    return _call_addin("pro.setActiveTool", {"tool": tool})


@mcp.tool()
def pro_is_3d() -> dict[str, Any]:
    """Check if the active map view is a 3D scene (GlobalScene or LocalScene)."""
    return _call_addin("pro.is3d")


@mcp.tool()
def pro_get_layer_renderer(layer: str) -> dict[str, Any]:
    """Get the renderer type and classification field for a layer."""
    return _call_addin("pro.getLayerRenderer", {"layer": layer})


@mcp.tool()
def pro_set_layer_color(layer: str, r: int, g: int, b: int) -> dict[str, Any]:
    """Set the fill color for layers with a simple renderer using RGB values (0-255 each)."""
    return _call_addin(
        "pro.setLayerColor",
        {"layer": layer, "r": str(r), "g": str(g), "b": str(b)},
    )


@mcp.tool()
def pro_remove_layer(layer: str) -> dict[str, Any]:
    """Remove a layer from the active ArcGIS Pro map by name."""
    return _call_addin("pro.removeLayer", {"layer": layer})


@mcp.tool()
def pro_add_layer_from_file(path: str) -> dict[str, Any]:
    """Add a layer from a .lyrx file or feature class path to the active ArcGIS Pro map."""
    return _call_addin("pro.addLayerFromFile", {"path": path})


@mcp.tool()
def pro_select_by_polygon(
    layer: str,
    coordinates: str,
    selection_type: str = "NEW",
) -> dict[str, Any]:
    """Select features in a layer by polygon coordinates. Space-separated 'x,y' pairs.
    selection_type: NEW, ADD, SUBTRACT, or AND."""
    return _call_addin(
        "pro.selectByPolygon",
        {
            "layer": layer,
            "coordinates": coordinates,
            "selectionType": selection_type,
        },
    )


@mcp.tool()
def pro_list_layouts() -> dict[str, Any]:
    """List all layouts in the current ArcGIS Pro project."""
    return _call_addin("pro.listLayouts")


@mcp.tool()
def pro_get_project_properties() -> dict[str, Any]:
    """Get project metadata including name, path, default geodatabase, summary, and tags."""
    return _call_addin("pro.getProjectProperties")


@mcp.tool()
def pro_get_geometry_distance(
    x1: float,
    y1: float,
    x2: float,
    y2: float,
) -> dict[str, Any]:
    """Calculate Euclidean distance between two map coordinates."""
    return _call_addin(
        "pro.getGeometryDistance",
        {
            "x1": str(x1),
            "y1": str(y1),
            "x2": str(x2),
            "y2": str(y2),
        },
    )


@mcp.tool()
def pro_set_layer_transparency(layer: str, transparency: float) -> dict[str, Any]:
    """Set layer transparency percentage (0 = opaque, 100 = fully transparent)."""
    return _call_addin(
        "pro.setLayerTransparency",
        {"layer": layer, "transparency": str(transparency)},
    )


@mcp.tool()
def pro_get_all_map_names() -> dict[str, Any]:
    """List all maps in the current ArcGIS Pro project."""
    return _call_addin("pro.getAllMapNames")


@mcp.tool()
def pro_get_map_frame(
    layout_name: str,
    map_frame_name: str | None = None,
) -> dict[str, Any]:
    """Get map frame properties (camera, map name, dimensions) from a layout."""
    args: dict[str, str] = {"layoutName": layout_name}
    if map_frame_name:
        args["mapFrameName"] = map_frame_name
    return _call_addin("pro.getMapFrame", args)


@mcp.tool()
def pro_select_by_layer(
    target_layer: str,
    source_layer: str,
    spatial_relationship: str = "Intersects",
    selection_type: str = "NEW",
) -> dict[str, Any]:
    """Select features by spatial relationship to another layer."""
    return _call_addin(
        "pro.selectByLayer",
        {
            "targetLayer": target_layer,
            "sourceLayer": source_layer,
            "spatialRelationship": spatial_relationship,
            "selectionType": selection_type,
        },
    )


@mcp.tool()
def pro_get_features_by_extent(
    layer: str,
    xmin: float,
    ymin: float,
    xmax: float,
    ymax: float,
    fields: str | None = None,
    max_features: int = 100,
) -> dict[str, Any]:
    """Get feature attributes within a bounding box extent. Optionally limit fields."""
    args: dict[str, str] = {
        "layer": layer,
        "xmin": str(xmin),
        "ymin": str(ymin),
        "xmax": str(xmax),
        "ymax": str(ymax),
        "maxFeatures": str(max_features),
    }
    if fields:
        args["fields"] = fields
    return _call_addin("pro.getFeaturesByExtent", args)


@mcp.tool()
def pro_delete_features_by_oid(layer: str, oids: str) -> dict[str, Any]:
    """Delete features by comma-separated OIDs in a layer (e.g. '1,2,3')."""
    return _call_addin("pro.deleteFeaturesByOid", {"layer": layer, "oids": oids})


@mcp.tool()
def pro_update_feature_attributes(
    layer: str,
    oid: int,
    attributes: str,
) -> dict[str, Any]:
    """Update attributes of a feature by OID. Attributes as JSON string."""
    return _call_addin(
        "pro.updateFeatureAttributes",
        {"layer": layer, "oid": str(oid), "attributes": attributes},
    )


@mcp.tool()
def pro_create_point_feature(
    layer: str,
    x: float,
    y: float,
    wkid: int | None = None,
    attributes: str | None = None,
) -> dict[str, Any]:
    """Create a point feature at (x, y) with optional WKID and JSON attributes."""
    args: dict[str, str] = {
        "layer": layer,
        "x": str(x),
        "y": str(y),
    }
    if wkid is not None:
        args["wkid"] = str(wkid)
    if attributes is not None:
        args["attributes"] = attributes
    return _call_addin("pro.createPointFeature", args)


@mcp.tool()
def pro_apply_unique_value_renderer(
    layer: str,
    field: str,
    color_ramp: str | None = None,
) -> dict[str, Any]:
    """Apply a unique value renderer to a layer based on a field's distinct values."""
    args: dict[str, str] = {"layer": layer, "field": field}
    if color_ramp is not None:
        args["colorRamp"] = color_ramp
    return _call_addin("pro.applyUniqueValueRenderer", args)


@mcp.tool()
def pro_apply_class_breaks_renderer(
    layer: str,
    field: str,
    break_count: int = 5,
) -> dict[str, Any]:
    """Apply a class breaks renderer using equal interval classification."""
    return _call_addin(
        "pro.applyClassBreaksRenderer",
        {"layer": layer, "field": field, "breakCount": str(break_count)},
    )


@mcp.tool()
def pro_get_elevation_sources() -> dict[str, Any]:
    """List elevation surface sources in the active scene (ground)."""
    return _call_addin("pro.getElevationSources")


@mcp.tool()
def pro_set_ground_opacity(opacity: float) -> dict[str, Any]:
    """Set the ground surface opacity (0=transparent, 100=opaque) in the active scene."""
    return _call_addin("pro.setGroundOpacity", {"opacity": str(opacity)})


@mcp.tool()
def pro_get_active_tool() -> dict[str, Any]:
    """Get the DAML ID of the currently active map tool."""
    return _call_addin("pro.getActiveTool")


@mcp.tool()
def pro_list_field_values(
    layer: str,
    field: str,
    max_values: int = 100,
) -> dict[str, Any]:
    """List distinct field values for a layer. Use max_values to cap results."""
    return _call_addin(
        "pro.listFieldValues",
        {"layer": layer, "field": field, "maxValues": str(max_values)},
    )


@mcp.tool()
def pro_add_field(
    layer: str,
    field_name: str,
    field_type: str,
    precision: int | None = None,
    scale: int | None = None,
    length: int | None = None,
) -> dict[str, Any]:
    """Add a new field to a layer's feature class. field_type: Double, Integer, Text, Date, etc."""
    args: dict[str, str] = {
        "layer": layer,
        "fieldName": field_name,
        "fieldType": field_type,
    }
    if precision is not None:
        args["precision"] = str(precision)
    if scale is not None:
        args["scale"] = str(scale)
    if length is not None:
        args["length"] = str(length)
    return _call_addin("pro.addField", args)


@mcp.tool()
def pro_delete_field(layer: str, field_name: str) -> dict[str, Any]:
    """Delete a field from a layer's feature class. Cannot delete required fields."""
    return _call_addin(
        "pro.deleteField",
        {"layer": layer, "fieldName": field_name},
    )


@mcp.tool()
def pro_create_polygon_feature(
    layer: str,
    coordinates: str,
    wkid: int | None = None,
    attributes: str | None = None,
) -> dict[str, Any]:
    """Create a polygon feature from space-separated 'x,y' coordinates (min 3 pairs)."""
    args: dict[str, str] = {"layer": layer, "coordinates": coordinates}
    if wkid is not None:
        args["wkid"] = str(wkid)
    if attributes is not None:
        args["attributes"] = attributes
    return _call_addin("pro.createPolygonFeature", args)


@mcp.tool()
def pro_create_line_feature(
    layer: str,
    coordinates: str,
    wkid: int | None = None,
    attributes: str | None = None,
) -> dict[str, Any]:
    """Create a line/polyline feature from space-separated 'x,y' coordinates (min 2 pairs)."""
    args: dict[str, str] = {"layer": layer, "coordinates": coordinates}
    if wkid is not None:
        args["wkid"] = str(wkid)
    if attributes is not None:
        args["attributes"] = attributes
    return _call_addin("pro.createLineFeature", args)


@mcp.tool()
def pro_set_map_scale(scale: float) -> dict[str, Any]:
    """Set the active map view to a specific scale."""
    return _call_addin("pro.setMapScale", {"scale": str(scale)})


@mcp.tool()
def pro_get_map_scale() -> dict[str, Any]:
    """Get the current scale of the active map view."""
    return _call_addin("pro.getMapScale")


@mcp.tool()
def pro_zoom_to_selected(layer: str | None = None) -> dict[str, Any]:
    """Zoom to selected features. Optionally scope to a specific layer."""
    args = {}
    if layer:
        args["layer"] = layer
    return _call_addin("pro.zoomToSelected", args)


@mcp.tool()
def pro_get_edit_state() -> dict[str, Any]:
    """Get undo and redo operation counts."""
    return _call_addin("pro.getEditState")


@mcp.tool()
def pro_set_snapping(enabled: bool) -> dict[str, Any]:
    """Enable or disable map snapping."""
    return _call_addin("pro.setSnapping", {"enabled": str(enabled).lower()})


@mcp.tool()
def pro_delete_bookmark(name: str) -> dict[str, Any]:
    """Delete a bookmark by name from the active map."""
    return _call_addin("pro.deleteBookmark", {"name": name})


@mcp.tool()
def pro_flash_selection(layer: str) -> dict[str, Any]:
    """Visually flash selected features in a layer on the map."""
    return _call_addin("pro.flashSelection", {"layer": layer})


@mcp.tool()
def pro_select_all(layer: str) -> dict[str, Any]:
    """Select all features in a layer."""
    return _call_addin("pro.selectAll", {"layer": layer})


@mcp.tool()
def pro_set_status_bar_message(message: str) -> dict[str, Any]:
    """Set the ArcGIS Pro status bar message."""
    return _call_addin("pro.setStatusBarMessage", {"message": message})


@mcp.tool()
def pro_list_standalone_tables() -> dict[str, Any]:
    """List non-spatial standalone tables in the current project."""
    return _call_addin("pro.listStandaloneTables")


@mcp.tool()
def pro_list_gp_history(max_items: int = 20) -> dict[str, Any]:
    """List recent geoprocessing history items from the project."""
    return _call_addin("pro.listGpHistory", {"maxItems": str(max_items)})


@mcp.tool()
def pro_is_time_enabled() -> dict[str, Any]:
    """Check if the time slider is enabled on the active map."""
    return _call_addin("pro.isTimeEnabled")


@mcp.tool()
def pro_get_time_extent() -> dict[str, Any]:
    """Get the current time extent of the active map (start/end)."""
    return _call_addin("pro.getTimeExtent")


@mcp.tool()
def pro_set_time_extent(start: str, end: str) -> dict[str, Any]:
    """Set the map time extent. Dates as ISO strings (e.g. '2020-01-01T00:00:00')."""
    return _call_addin("pro.setTimeExtent", {"start": start, "end": end})


@mcp.tool()
def pro_list_layout_elements(layout_name: str) -> dict[str, Any]:
    """List all elements (graphics, map frames, surrounds) in a layout."""
    return _call_addin("pro.listLayoutElements", {"layoutName": layout_name})


@mcp.tool()
def pro_rename_field(layer: str, old_name: str, new_name: str) -> dict[str, Any]:
    """Rename a field on a feature layer."""
    return _call_addin(
        "pro.renameField",
        {"layer": layer, "oldName": old_name, "newName": new_name},
    )


@mcp.tool()
def pro_get_layer_description(layer: str) -> dict[str, Any]:
    """Get the description text for a layer (shown in TOC tooltips)."""
    return _call_addin("pro.getLayerDescription", {"layer": layer})


@mcp.tool()
def pro_set_layer_description(layer: str, description: str) -> dict[str, Any]:
    """Set the description text for a layer."""
    return _call_addin(
        "pro.setLayerDescription",
        {"layer": layer, "description": description},
    )


@mcp.tool()
def pro_list_scene_layer_types() -> dict[str, Any]:
    """List layers with scene/3D type info (FeatureLayer, PointCloudLayer, SceneLayer, etc.)."""
    return _call_addin("pro.listSceneLayerTypes")


@mcp.tool()
def pro_count_features_by_expression(layer: str, where: str) -> dict[str, Any]:
    """Count features in a layer matching a SQL where clause."""
    return _call_addin(
        "pro.countFeaturesByExpression",
        {"layer": layer, "where": where},
    )


# --- Phase 7: Advanced Editing & GP ---


@mcp.tool()
def pro_split_features(layer: str, cut_geometry: str) -> dict[str, Any]:
    """Split features intersecting a cutting geometry (GeoJSON polyline/polygon)."""
    return _call_addin(
        "pro.splitFeatures",
        {"layer": layer, "cutGeometry": cut_geometry},
    )


@mcp.tool()
def pro_merge_features(layer: str, object_ids: str, target_oid: int) -> dict[str, Any]:
    """Merge multiple features. object_ids: JSON array of OIDs, target_oid: survivor."""
    return _call_addin(
        "pro.mergeFeatures",
        {"layer": layer, "objectIds": object_ids, "targetOid": str(target_oid)},
    )


@mcp.tool()
def pro_run_gp_tool(tool_name: str, parameters: str) -> dict[str, Any]:
    """Execute an ArcGIS Geoprocessing tool by name. parameters is a JSON array of values."""
    return _call_addin(
        "pro.runGpTool",
        {"toolName": tool_name, "parameters": parameters},
    )


@mcp.tool()
def pro_list_gp_tools(
    search_text: str | None = None,
    max_results: int = 50,
) -> dict[str, Any]:
    """List GP tools from project toolboxes, optionally filtered by search_text."""
    return _call_addin(
        "pro.listGpTools",
        {"searchText": search_text or "", "maxResults": str(max_results)},
    )


@mcp.tool()
def pro_copy_features(layer: str, output_path: str) -> dict[str, Any]:
    """Copy features to a new feature class using the CopyFeatures GP tool."""
    return _call_addin(
        "pro.copyFeatures",
        {"layer": layer, "outputPath": output_path},
    )


@mcp.tool()
def pro_rename_layer(layer: str, new_name: str) -> dict[str, Any]:
    """Rename a layer in the current map."""
    return _call_addin(
        "pro.renameLayer",
        {"layer": layer, "newName": new_name},
    )


@mcp.tool()
def pro_get_layer_statistics(layer: str, field: str) -> dict[str, Any]:
    """Compute min, max, mean, stddev, count, and null count for a numeric field."""
    return _call_addin(
        "pro.getLayerStatistics",
        {"layer": layer, "field": field},
    )


@mcp.tool()
def pro_project_geometry(x: float, y: float, from_wkid: int, to_wkid: int) -> dict[str, Any]:
    """Project a point from one spatial reference to another using GeometryEngine."""
    return _call_addin(
        "pro.projectGeometry",
        {
            "x": str(x),
            "y": str(y),
            "fromWkid": str(from_wkid),
            "toWkid": str(to_wkid),
        },
    )


# --- Phase 8: Layout & Map Automation ---


@mcp.tool()
def pro_add_layout_text(
    layout_name: str,
    text: str,
    x: float,
    y: float,
    font_size: float = 12,
    color_rgb: str | None = None,
) -> dict[str, Any]:
    """Add a text element to a layout."""
    args = {
        "layoutName": layout_name,
        "text": text,
        "x": str(x),
        "y": str(y),
        "fontSize": str(font_size),
    }
    if color_rgb:
        args["colorRgb"] = color_rgb
    return _call_addin("pro.addLayoutText", args)


@mcp.tool()
def pro_add_layout_picture(
    layout_name: str,
    image_path: str,
    x: float,
    y: float,
    width: float,
    height: float,
) -> dict[str, Any]:
    """Add a picture/image element to a layout from a file path."""
    return _call_addin(
        "pro.addLayoutPicture",
        {
            "layoutName": layout_name,
            "imagePath": image_path,
            "x": str(x),
            "y": str(y),
            "width": str(width),
            "height": str(height),
        },
    )


@mcp.tool()
def pro_add_layout_legend(
    layout_name: str,
    x: float,
    y: float,
    map_frame_name: str | None = None,
) -> dict[str, Any]:
    """Add a legend to a layout's map frame."""
    args = {"layoutName": layout_name, "x": str(x), "y": str(y)}
    if map_frame_name:
        args["mapFrameName"] = map_frame_name
    return _call_addin("pro.addLayoutLegend", args)


@mcp.tool()
def pro_add_layout_north_arrow(
    layout_name: str,
    map_frame_name: str,
    x: float,
    y: float,
) -> dict[str, Any]:
    """Add a north arrow to a layout's map frame."""
    return _call_addin(
        "pro.addLayoutNorthArrow",
        {
            "layoutName": layout_name,
            "mapFrameName": map_frame_name,
            "x": str(x),
            "y": str(y),
        },
    )


@mcp.tool()
def pro_remove_layout_element(
    layout_name: str,
    element_name: str,
) -> dict[str, Any]:
    """Remove an element from a layout by name."""
    return _call_addin(
        "pro.removeLayoutElement",
        {"layoutName": layout_name, "elementName": element_name},
    )


@mcp.tool()
def pro_create_layout(
    layout_name: str,
    width: float,
    height: float,
    units: str = "MM",
) -> dict[str, Any]:
    """Create a new layout in the project and auto-save."""
    return _call_addin(
        "pro.createLayout",
        {
            "layoutName": layout_name,
            "width": str(width),
            "height": str(height),
            "units": units,
        },
    )


@mcp.tool()
def pro_create_map(
    map_name: str,
    map_type: str = "Map",
    basemap: str | None = None,
) -> dict[str, Any]:
    """Create a new map (Map/LocalScene/GlobalScene), activate it, and auto-save."""
    args = {"mapName": map_name, "mapType": map_type}
    if basemap:
        args["basemap"] = basemap
    return _call_addin("pro.createMap", args)


@mcp.tool()
def pro_add_basemap(
    basemap_name: str,
) -> dict[str, Any]:
    """Set the basemap of the active map (Streets, Imagery, Topographic, etc.)."""
    return _call_addin(
        "pro.addBasemap",
        {"basemapName": basemap_name},
    )


# --- Phase 9: Advanced 3D & Visualization ---


@mcp.tool()
def pro_set_atmosphere(
    fog_density: float,
    horizon_fog: bool = False,
    fog_color: str | None = None,
) -> dict[str, Any]:
    """Set atmospheric effects in a scene (fog density 0-100, optional horizon fog and RGB color)."""  # noqa: E501
    args = {"fogDensity": str(fog_density), "horizonFog": str(horizon_fog).lower()}
    if fog_color:
        args["fogColor"] = fog_color
    return _call_addin("pro.setAtmosphere", args)


@mcp.tool()
def pro_set_sun_position(
    azimuth: float,
    altitude: float,
) -> dict[str, Any]:
    """Set the sun position in a scene (azimuth 0-360, altitude 0-90)."""
    return _call_addin(
        "pro.setSunPosition",
        {"azimuth": str(azimuth), "altitude": str(altitude)},
    )


@mcp.tool()
def pro_get_sun_position() -> dict[str, Any]:
    """Get the current sun azimuth and altitude in a scene."""
    return _call_addin("pro.getSunPosition")


@mcp.tool()
def pro_explore_3d(
    x: float,
    y: float,
    target_z: float,
    distance: float,
    heading_delta: float | None = None,
    pitch_delta: float | None = None,
) -> dict[str, Any]:
    """Orbit/navigate camera to look at a 3D point from a given distance."""
    args = {
        "x": str(x),
        "y": str(y),
        "targetZ": str(target_z),
        "distance": str(distance),
    }
    if heading_delta is not None:
        args["headingDelta"] = str(heading_delta)
    if pitch_delta is not None:
        args["pitchDelta"] = str(pitch_delta)
    return _call_addin("pro.explore3D", args)


@mcp.tool()
def pro_set_layer_elevation(
    layer: str,
    elevation_mode: str,
    z_offset: float,
) -> dict[str, Any]:
    """Set elevation mode (absolute/relative/dra) and Z offset for a layer in a scene."""
    return _call_addin(
        "pro.setLayerElevation",
        {"layer": layer, "elevationMode": elevation_mode, "zOffset": str(z_offset)},
    )


@mcp.tool()
def pro_set_scene_background(
    r: int,
    g: int,
    b: int,
    background_type: str = "color",
) -> dict[str, Any]:
    """Set the scene background color (r,g,b 0-255) and type (color/none)."""
    return _call_addin(
        "pro.setSceneBackground",
        {"r": str(r), "g": str(g), "b": str(b), "backgroundType": background_type},
    )


# --- Phase 10: Project & Data Management ---


@mcp.tool()
def pro_create_feature_class(
    gdb_path: str,
    name: str,
    geometry_type: str,
    wkid: int | None = None,
    fields_json: str | None = None,
) -> dict[str, Any]:
    """Create a feature class in a geodatabase (geometry_type: Point/Polyline/Polygon)."""
    args = {"gdbPath": gdb_path, "name": name, "geometryType": geometry_type}
    if wkid is not None:
        args["wkid"] = str(wkid)
    if fields_json is not None:
        args["fieldsJson"] = fields_json
    return _call_addin("pro.createFeatureClass", args)


@mcp.tool()
def pro_delete_feature_class(
    path: str,
) -> dict[str, Any]:
    """Delete a feature class or table by full path."""
    return _call_addin("pro.deleteFeatureClass", {"path": path})


@mcp.tool()
def pro_save_project() -> dict[str, Any]:
    """Save the current ArcGIS Pro project."""
    return _call_addin("pro.saveProject")


@mcp.tool()
def pro_add_attribute_index(
    layer: str,
    field: str,
    index_name: str | None = None,
    unique: bool = False,
) -> dict[str, Any]:
    """Add an attribute index on a field for faster queries."""
    args = {
        "layer": layer,
        "field": field,
        "indexName": index_name or f"idx_{field}",
        "unique": str(unique).lower(),
    }  # noqa: E501
    return _call_addin("pro.addAttributeIndex", args)


@mcp.tool()
def pro_search_address(
    address: str,
    max_results: int = 10,
) -> dict[str, Any]:
    """Search for an address or place using the map's locators."""
    return _call_addin(
        "pro.searchAddress",
        {"address": address, "maxResults": str(max_results)},
    )


@mcp.tool()
def pro_open_attribute_table(
    layer: str,
) -> dict[str, Any]:
    """Open the attribute table view for a layer."""
    return _call_addin(
        "pro.openAttributeTable",
        {"layer": layer},
    )


# --- Phase 11: Data Exchange ---


@mcp.tool()
def pro_export_to_csv(
    layer: str,
    output_path: str,
) -> dict[str, Any]:
    """Export layer attribute table to a CSV file."""
    return _call_addin(
        "pro.exportToCsv",
        {"layer": layer, "outputPath": output_path},
    )


@mcp.tool()
def pro_export_to_geo_json(
    layer: str,
    output_path: str,
) -> dict[str, Any]:
    """Export layer features to a GeoJSON file."""
    return _call_addin(
        "pro.exportToGeoJSON",
        {"layer": layer, "outputPath": output_path},
    )


@mcp.tool()
def pro_import_csv(
    csv_path: str,
    gdb_path: str,
    fc_name: str,
    x_field: str,
    y_field: str,
    wkid: int = 4326,
) -> dict[str, Any]:
    """Import a CSV file as a point feature class (creates FC if needed)."""
    return _call_addin(
        "pro.importCsv",
        {
            "csvPath": csv_path,
            "gdbPath": gdb_path,
            "fcName": fc_name,
            "xField": x_field,
            "yField": y_field,
            "wkid": str(wkid),
        },
    )


@mcp.tool()
def pro_export_to_shapefile(
    layer: str,
    output_path: str,
) -> dict[str, Any]:
    """Export a layer to a shapefile."""
    return _call_addin(
        "pro.exportToShapefile",
        {"layer": layer, "outputPath": output_path},
    )


@mcp.tool()
def pro_export_to_kml(
    layer: str,
    output_path: str,
) -> dict[str, Any]:
    """Export a layer to a KML file."""
    return _call_addin(
        "pro.exportToKml",
        {"layer": layer, "outputPath": output_path},
    )


@mcp.tool()
def pro_import_geo_json(
    geojson_path: str,
    gdb_path: str,
    fc_name: str,
) -> dict[str, Any]:
    """Import a GeoJSON file as a feature class (point, line, or polygon)."""
    return _call_addin(
        "pro.importGeoJSON",
        {
            "geojsonPath": geojson_path,
            "gdbPath": gdb_path,
            "fcName": fc_name,
        },
    )


# --- Phase 12: Pro GUI Automation ---


@mcp.tool()
def pro_show_message(
    message: str,
    type: str = "info",
    title: str | None = None,
) -> dict[str, Any]:
    """Show a message dialog in ArcGIS Pro (type: info/warning/error)."""
    args = {"message": message, "type": type}
    if title:
        args["title"] = title
    return _call_addin("pro.showMessage", args)


@mcp.tool()
def pro_show_progress_dialog(
    title: str,
    message: str,
) -> dict[str, Any]:
    """Show a progress/info dialog in ArcGIS Pro."""
    return _call_addin(
        "pro.showProgressDialog",
        {"title": title, "message": message},
    )


@mcp.tool()
def pro_set_status_bar_progress(
    percent: int,
    message: str,
) -> dict[str, Any]:
    """Set the status bar progress percentage and message (0-100)."""
    return _call_addin(
        "pro.setStatusBarProgress",
        {"percent": str(percent), "message": message},
    )


@mcp.tool()
def pro_list_dockpanes() -> dict[str, Any]:
    """List known dockpanes available in ArcGIS Pro."""
    return _call_addin("pro.listDockpanes")


@mcp.tool()
def pro_activate_ribbon_tab(
    tab_id: str,
) -> dict[str, Any]:
    """Activate a ribbon tab by name (Map, Edit, Catalog, Insert, Analysis, View) or DAML ID."""
    return _call_addin(
        "pro.activateRibbonTab",
        {"tabId": tab_id},
    )


# --- Phase 13: Schema Management ---


@mcp.tool()
def pro_list_domains(
    gdb_path: str,
) -> dict[str, Any]:
    """List coded-value and range domains in a geodatabase."""
    return _call_addin(
        "pro.listDomains",
        {"gdbPath": gdb_path},
    )


@mcp.tool()
def pro_create_domain(
    gdb_path: str,
    name: str,
    description: str,
    field_type: str,
    coded_values: str | None = None,
) -> dict[str, Any]:
    """Create a coded-value domain (coded_values as JSON dict) or range domain."""
    args = {
        "gdbPath": gdb_path,
        "name": name,
        "description": description,
        "fieldType": field_type,
    }
    if coded_values:
        args["codedValues"] = coded_values
    return _call_addin("pro.createDomain", args)


@mcp.tool()
def pro_assign_domain_to_field(
    layer: str,
    field: str,
    domain_name: str,
) -> dict[str, Any]:
    """Assign a domain to a field on a layer."""
    return _call_addin(
        "pro.assignDomainToField",
        {"layer": layer, "field": field, "domainName": domain_name},
    )


@mcp.tool()
def pro_list_subtypes(
    layer: str,
) -> dict[str, Any]:
    """List subtypes for a feature layer."""
    return _call_addin(
        "pro.listSubtypes",
        {"layer": layer},
    )


@mcp.tool()
def pro_set_subtype_field(
    layer: str,
    field: str,
) -> dict[str, Any]:
    """Set the subtype field for a feature layer."""
    return _call_addin(
        "pro.setSubtypeField",
        {"layer": layer, "field": field},
    )


@mcp.tool()
def pro_enable_attachments(
    layer: str,
) -> dict[str, Any]:
    """Enable attachments on a feature layer."""
    return _call_addin(
        "pro.enableAttachments",
        {"layer": layer},
    )


# --- Phase 14: Advanced Geoprocessing ---


@mcp.tool()
def pro_list_toolboxes() -> dict[str, Any]:
    """List all available geoprocessing toolboxes (project + system)."""
    return _call_addin("pro.listToolboxes", {})


@mcp.tool()
def pro_describe_tool(
    tool_name: str,
) -> dict[str, Any]:
    """Describe a geoprocessing tool and its parameters.

    Runs arcpy.GetParameterInfo() in Pro's Python interpreter
    to return parameter name, datatype, direction, required flag,
    parameter type, category, and default value for each parameter.
    """
    return _call_addin(
        "pro.describeTool",
        {"toolName": tool_name},
    )


@mcp.tool()
def pro_get_geoprocessing_history(
    count: int = 20,
) -> dict[str, Any]:
    """Return recent geoprocessing execution history."""
    return _call_addin(
        "pro.getGeoprocessingHistory",
        {"count": str(count)},
    )


@mcp.tool()
def pro_run_python_script(
    code: str,
    timeout_seconds: int = 60,
) -> dict[str, Any]:
    """Execute a Python script in ArcGIS Pro's Python environment.

    Returns stdout, stderr, and exit code.
    """
    return _call_addin(
        "pro.runPythonScript",
        {"code": code, "timeoutSeconds": str(timeout_seconds)},
    )


@mcp.tool()
def pro_set_environment(
    key: str,
    value: str,
) -> dict[str, Any]:
    """Set a geoprocessing environment setting (e.g. workspace, cellSize, extent)."""
    return _call_addin(
        "pro.setEnvironment",
        {"key": key, "value": value},
    )


@mcp.tool()
def pro_get_environment(
    key: str | None = None,
) -> dict[str, Any]:
    """Get geoprocessing environment settings. Omit key to list all known settings."""
    args: dict[str, str] = {}
    if key:
        args["key"] = key
    return _call_addin("pro.getEnvironment", args)


@mcp.tool()
def pro_list_bookmarks() -> dict[str, Any]:
    """List bookmarks in the active map."""
    return _call_addin("pro.listBookmarks", {})


@mcp.tool()
def pro_zoom_to_bookmark(name: str) -> dict[str, Any]:
    """Zoom the active map view to a named bookmark."""
    return _call_addin("pro.zoomToBookmark", {"name": name})


@mcp.tool()
def pro_create_bookmark(name: str) -> dict[str, Any]:
    """Create a new bookmark in the active map (not accessible from AddIn SDK)."""
    return _call_addin("pro.createBookmark", {"name": name})


@mcp.tool()
def pro_reorder_layer(layer: str, index: int) -> dict[str, Any]:
    """Move a layer to the specified index in the table of contents."""
    return _call_addin("pro.reorderLayer", {"layer": layer, "index": str(index)})


@mcp.tool()
def pro_set_labels_enabled(layer: str, enabled: bool) -> dict[str, Any]:
    """Enable or disable labels on a feature layer."""
    return _call_addin("pro.setLabelsEnabled", {"layer": layer, "enabled": str(enabled)})


@mcp.tool()
def pro_open_dockpane(dockpane_id: str) -> dict[str, Any]:
    """Open a dockpane by DAML ID or friendly name (Contents, Catalog, Geoprocessing, etc.)."""
    return _call_addin("pro.openDockpane", {"dockpaneId": dockpane_id})


@mcp.tool()
def pro_export_layout_to_file(
    layout_name: str,
    output_path: str,
    format: str | None = None,
    dpi: int | None = None,
) -> dict[str, Any]:
    """Export a layout to PDF or PNG file."""
    args: dict[str, str] = {"layoutName": layout_name, "outputPath": output_path}
    if format:
        args["format"] = format
    if dpi:
        args["dpi"] = str(dpi)
    return _call_addin("pro.exportLayoutToFile", args)


@mcp.tool()
def pro_fly_to_location(
    x: float,
    y: float,
    z: float,
    heading: float | None = None,
    pitch: float | None = None,
    duration_seconds: float | None = None,
) -> dict[str, Any]:
    """Fly the camera to a 3D location (x, y, z) in a scene view."""
    args: dict[str, str] = {"x": str(x), "y": str(y), "z": str(z)}
    if heading is not None:
        args["heading"] = str(heading)
    if pitch is not None:
        args["pitch"] = str(pitch)
    if duration_seconds is not None:
        args["durationSeconds"] = str(duration_seconds)
    return _call_addin("pro.flyToLocation", args)


def main() -> None:
    """Server startup entry point."""
    if sys.platform != "win32":
        print("Warning: ArcGIS Pro MCP Server is designed for Windows.", file=sys.stderr)
    mcp.run()


if __name__ == "__main__":
    main()
