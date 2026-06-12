from __future__ import annotations

import json
import os
import sys
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch
from uuid import uuid4

import arcgis_mcp_named_pipe as named_pipe
import arcgis_mcp_server as server
from arcgis_runtime_utils import build_arcgis_subprocess_env, remove_tree
from arcgis_script_templates import (
    build_buffer_features_code,
    build_clip_features_code,
    build_conflict_analysis_code,
    build_export_suitability_map_code,
    build_gdb_schema_code,
    build_prepare_analysis_inputs_code,
    build_raster_area_summary_code,
    build_reclassify_criteria_code,
    build_sensitivity_check_code,
    build_validate_project_data_code,
    build_weighted_suitability_code,
)

TEST_TEMP_ROOT = Path.cwd() / ".tmp-tests"
TEST_TEMP_ROOT.mkdir(parents=True, exist_ok=True)
os.environ["ARCGIS_MCP_TEMP_DIR"] = str(TEST_TEMP_ROOT)


def make_test_dir(prefix: str) -> Path:
    path = TEST_TEMP_ROOT / f"{prefix}{uuid4().hex[:8]}"
    path.mkdir(parents=True, exist_ok=False)
    return path


def create_sample_aprx(path: Path) -> None:
    files = {
        "GISProject.json": {
            "type": "CIMGISProject",
            "defaultGeoDatabase": r".\Demo.gdb",
            "defaultToolbox": r".\toolbox.atbx",
            "defaultFolder": r"D:\GIS\Workspace",
        },
        "Index.json": {
            "DocumentType": "Index",
            "NumberOfNodes": 3,
            "Nodes": [
                {
                    "NodeId": 1,
                    "NodeType": "Map",
                    "FileName": "maps/main.json",
                    "ChildNodeIds": "2",
                },
                {
                    "NodeId": 2,
                    "NodeType": "Layer",
                    "FileName": "layers/roads.json",
                    "ChildNodeIds": "",
                },
                {
                    "NodeId": 3,
                    "NodeType": "Layout",
                    "FileName": "layouts/layout.json",
                    "ChildNodeIds": "",
                },
            ],
        },
        "maps/main.json": {
            "type": "CIMMap",
            "name": "主地图",
            "uRI": "CIMPATH=maps/main.json",
            "mapType": "Map",
            "spatialReference": {"wkid": 4490, "latestWkid": 4490},
            "layers": ["CIMPATH=layers/roads.json"],
        },
        "layers/roads.json": {
            "type": "CIMFeatureLayer",
            "name": "道路",
            "visibility": True,
            "featureTable": {
                "dataConnection": {
                    "type": "CIMStandardDataConnection",
                    "workspaceConnectionString": r"DATABASE=.\Demo.gdb",
                    "workspaceFactory": "FileGDB",
                    "dataset": "道路",
                    "datasetType": "esriDTFeatureClass",
                },
                "fieldDescriptions": [
                    {
                        "fieldName": "道路.NAME",
                        "alias": "名称",
                        "visible": True,
                        "readOnly": False,
                    }
                ],
            },
        },
        "layouts/layout.json": {
            "type": "CIMLayout",
            "name": "布局1",
            "page": {"width": 297, "height": 210, "units": {"uwkid": 1025}},
            "elements": [
                {
                    "type": "CIMMapFrame",
                    "name": "地图框",
                    "uRI": "CIMPATH=maps/main.json",
                    "view": {
                        "viewableObjectPath": "CIMPATH=maps/main.json",
                        "camera": {"scale": 50000, "heading": 0},
                    },
                }
            ],
        },
    }
    with zipfile.ZipFile(path, "w") as zf:
        for name, payload in files.items():
            zf.writestr(name, json.dumps(payload, ensure_ascii=False))


class DiscoverArcGISPythonTests(unittest.TestCase):
    def tearDown(self) -> None:
        server.clear_discovery_cache()

    def test_prefers_explicit_python_environment_variable(self) -> None:
        temp_dir = make_test_dir("discover-")
        self.addCleanup(remove_tree, temp_dir)
        fake_python = temp_dir / "python.exe"
        fake_python.write_text("", encoding="utf-8")

        with patch.dict(os.environ, {"ARCGIS_PRO_PYTHON": str(fake_python)}, clear=True):
            info = server.discover_arcgis_pro_python()

        self.assertEqual(info.python_executable, str(fake_python.resolve()))
        self.assertEqual(info.source, "env:ARCGIS_PRO_PYTHON")

    def test_uses_install_dir_environment_variable(self) -> None:
        install_dir = make_test_dir("discover-")
        self.addCleanup(remove_tree, install_dir)
        python_dir = install_dir / "bin" / "Python" / "envs" / "arcgispro-py3"
        python_dir.mkdir(parents=True)
        fake_python = python_dir / "python.exe"
        fake_python.write_text("", encoding="utf-8")

        with patch.dict(os.environ, {"ARCGIS_PRO_INSTALL_DIR": str(install_dir)}, clear=True):
            info = server.discover_arcgis_pro_python()

        self.assertEqual(info.python_executable, str(fake_python.resolve()))
        self.assertEqual(info.install_dir, str(install_dir.resolve()))
        self.assertEqual(info.source, "env:ARCGIS_PRO_INSTALL_DIR")


class RuntimeIsolationTests(unittest.TestCase):
    def test_build_arcgis_subprocess_env_strips_parent_python_and_trae_variables(self) -> None:
        local_appdata_root = TEST_TEMP_ROOT / "localappdata-env"
        self.addCleanup(remove_tree, local_appdata_root)
        payload = build_arcgis_subprocess_env(
            {
                "PATH": r"C:\Windows\System32",
                "PYTHONPATH": "demo",
                "VIRTUAL_ENV": "venv",
                "UV_PROJECT_ENVIRONMENT": ".venv",
                "TRAE_SANDBOX": "1",
            },
            local_appdata_root=local_appdata_root,
        )

        self.assertEqual(payload["PATH"], r"C:\Windows\System32")
        self.assertNotIn("PYTHONPATH", payload)
        self.assertNotIn("VIRTUAL_ENV", payload)
        self.assertNotIn("UV_PROJECT_ENVIRONMENT", payload)
        self.assertNotIn("TRAE_SANDBOX", payload)
        self.assertEqual(payload["PYTHONUTF8"], "1")
        self.assertEqual(payload["ARCGIS_MCP_SUBPROCESS"], "1")
        self.assertEqual(payload["LOCALAPPDATA"], str(local_appdata_root.resolve()))
        self.assertTrue((local_appdata_root / "ESRI" / "ArcGISPro" / "Toolboxes").exists())

    def test_run_in_arcgis_env_uses_isolated_subprocess_settings(self) -> None:
        completed = type(
            "CompletedProcess",
            (),
            {"returncode": 0, "stdout": "", "stderr": ""},
        )()

        temp_dir = make_test_dir("runtime-")
        self.addCleanup(remove_tree, temp_dir)

        with patch("arcgis_mcp_server.create_temp_workspace", return_value=temp_dir):
            with patch("arcgis_mcp_server.subprocess.run", return_value=completed) as mocked_run:
                result_path = temp_dir / server.RESULT_FILENAME
                result_path.write_text(
                    json.dumps(
                        {
                            "status": "success",
                            "stdout": "",
                            "stderr": "",
                            "data": {"ok": True},
                            "error": None,
                            "workspace": None,
                            "project_path": None,
                        },
                        ensure_ascii=False,
                    ),
                    encoding="utf-8",
                )

                result = server.run_in_arcgis_env(
                    "set_result({'ok': True})",
                    python_executable=sys.executable,
                    require_arcpy=False,
                    timeout_seconds=20,
                )

        self.assertEqual(result.status, "success")
        mocked_run.assert_called_once()
        kwargs = mocked_run.call_args.kwargs
        self.assertEqual(kwargs["stdin"], server.subprocess.DEVNULL)
        self.assertEqual(kwargs["env"]["ARCGIS_MCP_SUBPROCESS"], "1")
        self.assertEqual(kwargs["cwd"], str(temp_dir))
        self.assertEqual(
            kwargs["env"]["LOCALAPPDATA"],
            str((temp_dir / "localappdata").resolve()),
        )
        self.assertEqual(kwargs["creationflags"], getattr(server.subprocess, "CREATE_NO_WINDOW", 0))
        self.assertEqual(temp_dir.parent, TEST_TEMP_ROOT)


class RunInArcGISEnvTests(unittest.TestCase):
    def test_captures_stdout_without_arcpy_for_self_test(self) -> None:
        result = server.run_in_arcgis_env(
            "print('hello from subprocess')",
            python_executable=sys.executable,
            require_arcpy=False,
        )

        self.assertEqual(result.status, "success")
        self.assertEqual(result.exit_code, 0)
        self.assertIn("hello from subprocess", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_returns_structured_error_when_user_code_fails(self) -> None:
        result = server.run_in_arcgis_env(
            "raise RuntimeError('boom')",
            python_executable=sys.executable,
            require_arcpy=False,
        )

        self.assertEqual(result.status, "error")
        self.assertNotEqual(result.exit_code, 0)
        self.assertIsNotNone(result.error)
        self.assertEqual(result.error["type"], "RuntimeError")
        self.assertIn("boom", result.error["message"])

    def test_supports_structured_result_channel(self) -> None:
        result = server.run_in_arcgis_env(
            "set_result({'layers': 3, 'project': 'demo'}); print('ok')",
            python_executable=sys.executable,
            require_arcpy=False,
        )

        self.assertEqual(result.status, "success")
        self.assertEqual(result.data, {"layers": 3, "project": "demo"})


class ResourceHelpersTests(unittest.TestCase):
    def test_project_resource_uri_roundtrip(self) -> None:
        project_path = r"D:\GIS\Projects\City.aprx"
        resource_uri = server.build_project_layers_resource_uri(project_path)
        project_ref = resource_uri.split("/")[3]

        self.assertEqual(
            server.decode_resource_path(project_ref),
            str(Path(project_path).resolve()),
        )

    def test_project_context_resource_uri_roundtrip(self) -> None:
        project_path = r"D:\GIS\Projects\Atlas.aprx"
        resource_uri = server.build_project_context_resource_uri(project_path)
        project_ref = resource_uri.split("/")[3]

        self.assertEqual(
            server.decode_resource_path(project_ref),
            str(Path(project_path).resolve()),
        )

    def test_current_project_resource_uri(self) -> None:
        self.assertEqual(
            server.build_project_layers_resource_uri(open_current_project=True),
            "arcgis://project/current/layers",
        )

    def test_gdb_schema_resource_returns_json_payload(self) -> None:
        result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"workspace": r"D:\GIS\Data\demo.gdb", "tables": []},
        )
        encoded = server.build_gdb_schema_resource_uri(r"D:\GIS\Data\demo.gdb").split("/")[3]

        with patch("arcgis_mcp_server._read_gdb_schema", return_value=result):
            payload = json.loads(server.gdb_schema_resource(encoded))

        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["resource_kind"], "gdb_schema")
        self.assertEqual(
            payload["data"]["workspace"],
            str(Path(r"D:\GIS\Data\demo.gdb").resolve()),
        )

    def test_project_context_resource_returns_json_payload(self) -> None:
        result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "project": {"file_path": r"D:\GIS\Projects\demo.aprx"},
                "default_map_candidate": {"name": "BaseMap"},
                "layouts": [],
                "maps": [],
                "broken_data_sources": [],
            },
        )
        encoded = server.build_project_context_resource_uri(r"D:\GIS\Projects\demo.aprx").split(
            "/"
        )[3]

        with patch("arcgis_mcp_server._read_project_context", return_value=result):
            payload = json.loads(server.project_context_resource(encoded))

        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["resource_kind"], "project_context")
        self.assertEqual(payload["data"]["project"]["file_path"], r"D:\GIS\Projects\demo.aprx")

    def test_inspect_project_context_uses_aprx_archive_fast_path(self) -> None:
        temp_dir = make_test_dir("aprx-")
        self.addCleanup(remove_tree, temp_dir)
        aprx_path = temp_dir / "demo.aprx"
        create_sample_aprx(aprx_path)

        payload = server.inspect_project_context(
            project_path=str(aprx_path),
            timeout_seconds=10,
            include_source_details=False,
        )

        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["data"]["read_mode"], "aprx_archive")
        self.assertEqual(payload["data"]["project"]["map_count"], 1)
        self.assertEqual(payload["data"]["default_map_candidate"]["name"], "主地图")

    def test_list_gis_layers_uses_aprx_archive_fast_path(self) -> None:
        temp_dir = make_test_dir("aprx-")
        self.addCleanup(remove_tree, temp_dir)
        aprx_path = temp_dir / "demo.aprx"
        create_sample_aprx(aprx_path)

        payload = server.list_gis_layers(
            project_path=str(aprx_path),
            timeout_seconds=10,
            include_fields=True,
            include_data_source_details=True,
        )

        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["data"]["read_mode"], "aprx_archive")
        layer = payload["data"]["maps"][0]["layers"][0]
        self.assertEqual(layer["name"], "道路")
        self.assertEqual(layer["fields"][0]["alias"], "名称")
        self.assertEqual(layer["source_status"]["workspace_factory"], "FileGDB")


    def test_build_gis_resource_uri_project_layers(self) -> None:
        payload = server.build_gis_resource_uri(
            resource_kind="project_layers", path=r"D:\GIS\Projects\City.aprx"
        )
        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["resource_kind"], "project_layers")
        self.assertIn("arcgis://", payload["resource_uri"])

    def test_build_gis_resource_uri_project_context(self) -> None:
        payload = server.build_gis_resource_uri(
            resource_kind="project_context", path=r"D:\GIS\Projects\Atlas.aprx"
        )
        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["resource_kind"], "project_context")

    def test_build_gis_resource_uri_gdb_schema(self) -> None:
        payload = server.build_gis_resource_uri(
            resource_kind="gdb_schema", path=r"D:\GIS\Data\demo.gdb"
        )
        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["resource_kind"], "gdb_schema")

    def test_build_gis_resource_uri_gdb_schema_requires_path(self) -> None:
        payload = server.build_gis_resource_uri(resource_kind="gdb_schema")
        self.assertEqual(payload["status"], "error")
        self.assertIn("requires a gdb path", payload["message"])

    def test_build_gis_resource_uri_unsupported_kind(self) -> None:
        payload = server.build_gis_resource_uri(resource_kind="invalid")
        self.assertEqual(payload["status"], "error")
        self.assertIn("Unsupported", payload["message"])

    def test_build_gis_resource_uri_current_project(self) -> None:
        payload = server.build_gis_resource_uri(
            resource_kind="project_layers", open_current_project=True
        )
        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["resource_uri"], "arcgis://project/current/layers")


class DiagnosticToolTests(unittest.TestCase):
    def test_ping_returns_ok(self) -> None:
        payload = server.ping()

        self.assertEqual(payload["status"], "ok")
        self.assertEqual(payload["server"], server.SERVER_NAME)
        self.assertIn("MCP Tool", payload["message"])

    def test_health_check_reports_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.discover_arcgis_pro_python",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro not installed"),
        ):
            payload = server.health_check()

        self.assertEqual(payload["status"], "unavailable")
        self.assertIsNone(payload["arcgis_python"])
        self.assertIn("ArcGIS Pro not installed", payload["message"])

    def test_health_check_reports_ready_when_runtime_passes(self) -> None:
        python_info = server.ArcGISPythonInfo(
            install_dir=r"D:\Program Files\ArcGIS\Pro",
            python_executable=(
                r"D:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe"
            ),
            source="registry:SOFTWARE\\ESRI\\ArcGISPro",
        )
        runtime_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=python_info.python_executable,
            stdout="",
            stderr="",
            data={"product_name": "ArcGISPro", "version": "3.4"},
        )

        with patch("arcgis_mcp_server.discover_arcgis_pro_python", return_value=python_info):
            with patch("arcgis_mcp_server._run_arcpy_runtime_check", return_value=runtime_result):
                payload = server.health_check(timeout_seconds=12)

        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["arcgis_python"]["install_dir"], python_info.install_dir)
        self.assertEqual(payload["runtime"]["status"], "success")
        self.assertEqual(payload["runtime_data"]["product_name"], "ArcGISPro")

    def test_doctor_returns_detailed_report(self) -> None:
        python_info = server.ArcGISPythonInfo(
            install_dir=r"D:\Program Files\ArcGIS\Pro",
            python_executable=sys.executable,
            source="env:ARCGIS_PRO_PYTHON",
        )
        runtime_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"product_name": "ArcGISPro", "version": "3.4"},
        )

        with patch("arcgis_mcp_server.discover_arcgis_pro_python", return_value=python_info):
            with patch("arcgis_mcp_server._run_arcpy_runtime_check", return_value=runtime_result):
                payload = server.doctor(timeout_seconds=20)

        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["server"], server.SERVER_NAME)
        self.assertEqual(payload["runtime"]["product_name"], "ArcGISPro")
        self.assertTrue(any(check["name"] == "mcp_tool_reachable" for check in payload["checks"]))

    def test_debug_runtime_context_returns_context(self) -> None:
        payload = server.debug_runtime_context()

        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["server"], server.SERVER_NAME)
        self.assertIn("context", payload)
        self.assertIn("cwd", payload["context"])

    def test_detect_arcgis_environment_returns_ready_when_discovery_succeeds(self) -> None:
        python_info = server.ArcGISPythonInfo(
            install_dir=r"D:\Program Files\ArcGIS\Pro",
            python_executable=(
                r"D:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe"
            ),
            source="registry:SOFTWARE\\ESRI\\ArcGISPro",
        )
        with patch("arcgis_mcp_server.discover_arcgis_pro_python", return_value=python_info):
            payload = server.detect_arcgis_environment()

        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["arcgis"]["install_dir"], python_info.install_dir)
        self.assertEqual(payload["arcgis"]["source"], python_info.source)
        self.assertIn("resource_catalog_uri", payload)

    def test_detect_arcgis_environment_returns_unavailable_on_discovery_error(self) -> None:
        with patch(
            "arcgis_mcp_server.discover_arcgis_pro_python",
            side_effect=server.ArcGISDiscoveryError("Not found"),
        ):
            payload = server.detect_arcgis_environment()

        self.assertEqual(payload["status"], "unavailable")
        self.assertIn("Not found", payload["message"])


class ProjectContextToolTests(unittest.TestCase):
    def test_inspect_project_context_uses_lightweight_mode_by_default(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"project": {"file_path": r"D:\GIS\Projects\demo.aprx"}},
        )

        with patch(
            "arcgis_mcp_server._read_project_context", return_value=execution_result
        ) as mocked_read:
            payload = server.inspect_project_context(project_path=r"D:\GIS\Projects\demo.aprx")

        mocked_read.assert_called_once_with(
            project_path=r"D:\GIS\Projects\demo.aprx",
            open_current_project=False,
            timeout_seconds=server.DEFAULT_TIMEOUT_SECONDS,
            include_source_details=False,
        )
        self.assertEqual(payload["status"], "success")

    def test_inspect_project_context_supports_explicit_detail_and_timeout(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"project": {"file_path": r"D:\GIS\Projects\demo.aprx"}},
        )

        with patch(
            "arcgis_mcp_server._read_project_context", return_value=execution_result
        ) as mocked_read:
            server.inspect_project_context(
                project_path=r"D:\GIS\Projects\demo.aprx",
                timeout_seconds=180,
                include_source_details=True,
            )

        mocked_read.assert_called_once_with(
            project_path=r"D:\GIS\Projects\demo.aprx",
            open_current_project=False,
            timeout_seconds=180,
            include_source_details=True,
        )


class ProjectLayersToolTests(unittest.TestCase):
    def test_list_gis_layers_uses_lightweight_mode_by_default(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"maps": []},
        )

        with patch(
            "arcgis_mcp_server._read_project_layers", return_value=execution_result
        ) as mocked_read:
            payload = server.list_gis_layers(project_path=r"D:\GIS\Projects\demo.aprx")

        mocked_read.assert_called_once_with(
            project_path=r"D:\GIS\Projects\demo.aprx",
            open_current_project=False,
            timeout_seconds=server.DEFAULT_TIMEOUT_SECONDS,
            include_fields=False,
            include_data_source_details=False,
        )
        self.assertEqual(payload["status"], "success")

    def test_list_gis_layers_supports_explicit_detail_flags(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"maps": []},
        )

        with patch(
            "arcgis_mcp_server._read_project_layers", return_value=execution_result
        ) as mocked_read:
            server.list_gis_layers(
                project_path=r"D:\GIS\Projects\demo.aprx",
                timeout_seconds=180,
                include_fields=True,
                include_data_source_details=True,
            )

        mocked_read.assert_called_once_with(
            project_path=r"D:\GIS\Projects\demo.aprx",
            open_current_project=False,
            timeout_seconds=180,
            include_fields=True,
            include_data_source_details=True,
        )


class GeoprocessingToolTests(unittest.TestCase):
    def test_buffer_features_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"tool": "Buffer", "output_path": r"D:\GIS\Data\roads_buffer", "row_count": 42},
        )

        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.buffer_features(
                in_features=r"D:\GIS\Data\roads",
                out_feature_class=r"D:\GIS\Data\roads_buffer",
                buffer_distance_or_field="50 Meters",
                workspace=r"D:\GIS\Data\demo.gdb",
                timeout_seconds=90,
            )

        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "buffer_features")
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["data"]["output_path"], r"D:\GIS\Data\roads_buffer")
        self.assertEqual(payload["inputs"]["buffer_distance_or_field"], "50 Meters")

    def test_clip_features_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"tool": "Clip", "output_path": r"D:\GIS\Data\roads_clip", "row_count": 18},
        )

        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.clip_features(
                in_features=r"D:\GIS\Data\roads",
                clip_features_path=r"D:\GIS\Data\county_boundary",
                out_feature_class=r"D:\GIS\Data\roads_clip",
                workspace=r"D:\GIS\Data\demo.gdb",
                timeout_seconds=120,
            )

        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "clip_features")
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["data"]["row_count"], 18)
        self.assertEqual(payload["inputs"]["clip_features"], r"D:\GIS\Data\county_boundary")

    def test_buffer_features_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.buffer_features(
                in_features="in_fc",
                out_feature_class="out_fc",
                buffer_distance_or_field="10 Meters",
            )

        self.assertEqual(payload["status"], "unavailable")
        self.assertEqual(payload["tool"], "buffer_features")


class TemplateCompilationTests(unittest.TestCase):
    def test_buffer_features_code_generates_valid_python(self) -> None:
        code = build_buffer_features_code(
            in_features="roads",
            out_feature_class="roads_buf",
            buffer_distance_or_field="50 Meters",
            dissolve_option="NONE",
            dissolve_field=None,
            method="PLANAR",
        )
        compile(code, "<test>", "exec")

    def test_buffer_features_code_handles_special_chars(self) -> None:
        code = build_buffer_features_code(
            in_features=r"C:\GIS\Data\roads.shp",
            out_feature_class=r"C:\GIS\Data\output.gdb\roads_buf",
            buffer_distance_or_field="100 Meters",
            dissolve_option="ALL",
            dissolve_field="NAME;TYPE",
            method="GEODESIC",
        )
        compile(code, "<test>", "exec")

    def test_clip_features_code_generates_valid_python(self) -> None:
        code = build_clip_features_code(
            in_features="roads",
            clip_features="boundary",
            out_feature_class="roads_clip",
            cluster_tolerance="0.001 Meters",
        )
        compile(code, "<test>", "exec")

    def test_clip_features_code_handles_special_chars(self) -> None:
        code = build_clip_features_code(
            in_features=r"C:\GIS\Data\roads.shp",
            clip_features=r"C:\GIS\Data\boundary.gdb\county",
            out_feature_class=r"C:\GIS\Data\output.gdb\roads_clip",
            cluster_tolerance=None,
        )
        compile(code, "<test>", "exec")

    def test_gdb_schema_code_generates_valid_python(self) -> None:
        code = build_gdb_schema_code(r"C:\GIS\Data\sample.gdb")
        compile(code, "<test>", "exec")

    def test_gdb_schema_code_handles_special_chars(self) -> None:
        code = build_gdb_schema_code(r"D:\GIS\Projects\My Data\output.gdb")
        compile(code, "<test>", "exec")

    def test_validate_project_data_code_generates_valid_python(self) -> None:
        code = build_validate_project_data_code(
            project_gdb=r"C:\GIS\project_data.gdb",
            land_values_gdb=r"C:\GIS\land_values.gdb",
            required_layers_json=json.dumps(["dem", "land_use"]),
            target_srs="28356",
            study_area_fc=r"C:\GIS\project_data.gdb\gold_coast_lga",
        )
        compile(code, "<test>", "exec")

    def test_prepare_analysis_inputs_code_generates_valid_python(self) -> None:
        code = build_prepare_analysis_inputs_code(
            project_gdb=r"C:\GIS\project_data.gdb",
            land_values_gdb=r"C:\GIS\land_values.gdb",
            study_area_fc=r"C:\GIS\project_data.gdb\gold_coast_lga",
            cell_size=30,
            snap_raster_name="forest_prop",
            dem_name="dem",
            distance_sources_json=json.dumps({"water": "water_courses"}),
            output_gdb=r"C:\GIS\analysis_outputs.gdb",
        )
        compile(code, "<test>", "exec")

    def test_reclassify_criteria_code_generates_valid_python(self) -> None:
        remap = [[0, 200, 5], [200, 500, 4], [500, 10000, 1]]
        code = build_reclassify_criteria_code(
            reclass_table_json=json.dumps(
                [{"input_raster": r"C:\GIS\dist_water", "output_name": "rcl_water", "remap": remap}]
            ),
            output_gdb=r"C:\GIS\output.gdb",
            nodata_value=1,
        )
        compile(code, "<test>", "exec")

    def test_weighted_suitability_code_generates_valid_python(self) -> None:
        code = build_weighted_suitability_code(
            model_name="conservation",
            criteria_json=json.dumps(
                [
                    {"raster_path": r"C:\GIS\rcl_water", "weight": 0.25},
                    {"raster_path": r"C:\GIS\rcl_slope", "weight": 0.75},
                ]
            ),
            output_raster=r"C:\GIS\output.gdb\conservation_wlc",
            normalize=True,
            eval_min=1.0,
            eval_max=5.0,
        )
        compile(code, "<test>", "exec")

    def test_conflict_analysis_code_generates_valid_python(self) -> None:
        code = build_conflict_analysis_code(
            conservation_raster=r"C:\GIS\conservation_wlc",
            urban_raster=r"C:\GIS\urban_wlc",
            threshold=4.0,
            output_gdb=r"C:\GIS\output.gdb",
            land_use_raster=None,
            cell_size_area_ha=0.09,
        )
        compile(code, "<test>", "exec")

    def test_raster_area_summary_code_generates_valid_python(self) -> None:
        code = build_raster_area_summary_code(
            raster_path=r"C:\GIS\conservation_wlc",
            cell_area_ha=0.09,
            output_csv=r"C:\GIS\summary.csv",
        )
        compile(code, "<test>", "exec")

    def test_sensitivity_check_code_generates_valid_python(self) -> None:
        code = build_sensitivity_check_code(
            conservation_rasters_json=json.dumps([r"C:\GIS\c1", r"C:\GIS\c2"]),
            urban_rasters_json=json.dumps([r"C:\GIS\u1", r"C:\GIS\u2"]),
            conservation_weights_json=json.dumps([0.5, 0.5]),
            urban_weights_json=json.dumps([0.5, 0.5]),
            baseline_allocation=r"C:\GIS\allocation_map",
            perturbation_pct=10.0,
            thresholds_json=json.dumps([3.5, 4.0, 4.5]),
            output_gdb=r"C:\GIS\output.gdb",
            output_csv=r"C:\GIS\sensitivity.csv",
        )
        compile(code, "<test>", "exec")

    def test_export_suitability_map_code_generates_valid_python(self) -> None:
        code = build_export_suitability_map_code(
            project_path=r"C:\GIS\project.aprx",
            raster_path=r"C:\GIS\conservation_wlc",
            map_name="Conservation",
            title="Conservation Suitability",
            subtitle=None,
            classification_json=json.dumps(
                [
                    {"value": 1, "label": "Low", "rgb": [255, 0, 0]},
                ]
            ),
            output_format="PDF",
            output_path=r"C:\GIS\map.pdf",
            dpi=300,
        )
        compile(code, "<test>", "exec")


class ValidateProjectDataToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"overall": "pass", "checks": [], "warnings": [], "errors": []},
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.validate_project_data(
                project_gdb=r"D:\GIS\project_data.gdb",
                land_values_gdb=r"D:\GIS\land_values.gdb",
                required_layers=json.dumps(["dem", "land_use"]),
                target_srs="28356",
                study_area_fc=r"D:\GIS\project_data.gdb\gold_coast_lga",
                timeout_seconds=120,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "validate_project_data")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.validate_project_data(
                project_gdb=r"D:\GIS\project_data.gdb",
            )
        self.assertEqual(payload["status"], "unavailable")
        self.assertEqual(payload["tool"], "validate_project_data")


class PrepareAnalysisInputsToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"outputs": {"dem_30m": "path"}, "cell_size": 30},
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.prepare_analysis_inputs(
                project_gdb=r"D:\GIS\project_data.gdb",
                study_area_fc=r"D:\GIS\project_data.gdb\gold_coast_lga",
                cell_size=30,
                timeout_seconds=900,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "prepare_analysis_inputs")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.prepare_analysis_inputs(
                project_gdb=r"D:\GIS\project_data.gdb",
                study_area_fc=r"D:\GIS\project_data.gdb\gold_coast_lga",
            )
        self.assertEqual(payload["status"], "unavailable")


class ReclassifyCriteriaToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        remap = [[0, 200, 5], [200, 10000, 1]]
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={"results": [{"output_name": "rcl_water", "status": "success"}]},
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.reclassify_criteria(
                reclass_table=json.dumps(
                    [
                        {
                            "input_raster": r"D:\GIS\dist_water",
                            "output_name": "rcl_water",
                            "remap": remap,
                        }
                    ]
                ),
                output_gdb=r"D:\GIS\output.gdb",
                nodata_value=1,
                timeout_seconds=600,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "reclassify_criteria")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        remap = [[0, 200, 5], [200, 10000, 1]]
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.reclassify_criteria(
                reclass_table=json.dumps(
                    [
                        {
                            "input_raster": r"D:\GIS\dist_water",
                            "output_name": "rcl_water",
                            "remap": remap,
                        }
                    ]
                ),
                output_gdb=r"D:\GIS\output.gdb",
            )
        self.assertEqual(payload["status"], "unavailable")


class WeightedSuitabilityToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "model_name": "conservation",
                "output_path": r"D:\GIS\output.gdb\conservation_wlc",
                "statistics": {"min": 1.2, "max": 4.8, "mean": 3.1, "std": 0.9},
            },
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.weighted_suitability(
                model_name="conservation",
                criteria=json.dumps(
                    [
                        {"raster_path": r"D:\GIS\rcl_water", "weight": 0.5},
                        {"raster_path": r"D:\GIS\rcl_slope", "weight": 0.5},
                    ]
                ),
                output_raster=r"D:\GIS\output.gdb\conservation_wlc",
                normalize=True,
                timeout_seconds=600,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "weighted_suitability")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.weighted_suitability(
                model_name="conservation",
                criteria="[]",
                output_raster=r"D:\GIS\output.gdb\wlc",
            )
        self.assertEqual(payload["status"], "unavailable")


class ConflictAnalysisToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "conflict_map": r"D:\GIS\conflict_map",
                "allocation_map": r"D:\GIS\allocation_map",
                "conflict_area": {"conflict_ha": 1234.5, "conflict_pct": 1.4},
            },
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.conflict_analysis(
                conservation_raster=r"D:\GIS\conservation_wlc",
                urban_raster=r"D:\GIS\urban_wlc",
                threshold=4.0,
                output_gdb=r"D:\GIS\output.gdb",
                timeout_seconds=600,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "conflict_analysis")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.conflict_analysis(
                conservation_raster=r"D:\GIS\conservation_wlc",
                urban_raster=r"D:\GIS\urban_wlc",
                output_gdb=r"D:\GIS\output.gdb",
            )
        self.assertEqual(payload["status"], "unavailable")


class RasterAreaSummaryToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "raster": r"D:\GIS\conservation_wlc",
                "cell_area_ha": 0.09,
                "total_area_ha": 98000.0,
                "classes": [{"class": 1, "cell_count": 50000, "area_ha": 4500.0}],
            },
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.raster_area_summary(
                raster_path=r"D:\GIS\conservation_wlc",
                cell_area_ha=0.09,
                output_csv=r"D:\GIS\summary.csv",
                timeout_seconds=120,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "raster_area_summary")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.raster_area_summary(
                raster_path=r"D:\GIS\conservation_wlc",
            )
        self.assertEqual(payload["status"], "unavailable")


class SensitivityCheckToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "total_scenarios": 30,
                "most_sensitive": "Conservation_C1",
                "most_sensitive_pct": "2.30",
                "csv_path": r"D:\GIS\sensitivity.csv",
            },
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.sensitivity_check(
                conservation_rasters=json.dumps([r"D:\GIS\c1", r"D:\GIS\c2"]),
                urban_rasters=json.dumps([r"D:\GIS\u1", r"D:\GIS\u2"]),
                conservation_weights=json.dumps([0.5, 0.5]),
                urban_weights=json.dumps([0.5, 0.5]),
                baseline_allocation=r"D:\GIS\allocation_map",
                perturbation_pct=10.0,
                thresholds=json.dumps([3.5, 4.0, 4.5]),
                output_gdb=r"D:\GIS\output.gdb",
                timeout_seconds=1800,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "sensitivity_check")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.sensitivity_check(
                conservation_rasters="[]",
                urban_rasters="[]",
                conservation_weights="[]",
                urban_weights="[]",
                baseline_allocation=r"D:\GIS\allocation_map",
                output_gdb=r"D:\GIS\output.gdb",
            )
        self.assertEqual(payload["status"], "unavailable")


class ExportSuitabilityMapToolTests(unittest.TestCase):
    def test_wraps_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "output_path": r"D:\GIS\map.pdf",
                "format": "PDF",
                "dpi": 300,
                "file_size_mb": 2.5,
            },
        )
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            return_value=execution_result,
        ) as mocked_run:
            payload = server.export_suitability_map(
                project_path=r"D:\GIS\project.aprx",
                raster_path=r"D:\GIS\conservation_wlc",
                map_name="Conservation",
                title="Conservation Suitability",
                output_format="PDF",
                output_path=r"D:\GIS\map.pdf",
                dpi=300,
                timeout_seconds=300,
            )
        mocked_run.assert_called_once()
        self.assertEqual(payload["tool"], "export_suitability_map")
        self.assertEqual(payload["status"], "success")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("ArcGIS Pro Python not found"),
        ):
            payload = server.export_suitability_map(
                project_path=r"D:\GIS\project.aprx",
                raster_path=r"D:\GIS\conservation_wlc",
                output_path=r"D:\GIS\map.pdf",
            )
        self.assertEqual(payload["status"], "unavailable")


class InspectGdbToolTests(unittest.TestCase):
    """Tests for the inspect_gdb MCP tool."""

    def test_returns_success_with_schema_data(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
            data={
                "workspace": r"D:\GIS\Data\demo.gdb",
                "tables": [
                    {"name": "Parcels", "fields": [{"name": "ZONE", "type": "String"}]},
                ],
            },
        )
        with patch("arcgis_mcp_server._read_gdb_schema", return_value=execution_result):
            payload = server.inspect_gdb(gdb_path=r"D:\GIS\Data\demo.gdb")
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["resource_kind"], "gdb_schema")
        self.assertEqual(payload["data"]["workspace"], str(Path(r"D:\GIS\Data\demo.gdb").resolve()))

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server._read_gdb_schema",
            side_effect=server.ArcGISDiscoveryError("GDB not accessible"),
        ):
            payload = server.inspect_gdb(gdb_path=r"D:\GIS\Data\demo.gdb")
        self.assertEqual(payload["status"], "unavailable")

    def test_returns_error_on_invalid_path(self) -> None:
        payload = server.inspect_gdb(gdb_path="bad\x00path")
        self.assertEqual(payload["status"], "error")


class ExecuteArcPyCodeToolTests(unittest.TestCase):
    """Tests for the execute_arcpy_code MCP tool."""

    def test_returns_success_with_execution_result(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="Hello from ArcPy",
            stderr="",
            data={"product_name": "ArcGISPro"},
        )
        with patch("arcgis_mcp_server.run_in_arcgis_env", return_value=execution_result):
            payload = server.execute_arcpy_code(code="print('hello')")
        self.assertEqual(payload["status"], "success")
        self.assertEqual(payload["stdout"], "Hello from ArcPy")

    def test_returns_unavailable_when_discovery_fails(self) -> None:
        with patch(
            "arcgis_mcp_server.run_in_arcgis_env",
            side_effect=server.ArcGISDiscoveryError("Python not found"),
        ):
            payload = server.execute_arcpy_code(code="print('hello')")
        self.assertEqual(payload["status"], "unavailable")

    def test_returns_error_on_invalid_workspace_path(self) -> None:
        payload = server.execute_arcpy_code(code="print('hello')", workspace="bad\x00path")
        self.assertEqual(payload["status"], "error")

    def test_returns_error_on_invalid_project_path(self) -> None:
        payload = server.execute_arcpy_code(code="print('hello')", project_path="bad\x00path")
        self.assertEqual(payload["status"], "error")

    def test_forwards_workspace_and_project_path(self) -> None:
        execution_result = server.ArcPyExecutionResult(
            status="success",
            exit_code=0,
            python_executable=sys.executable,
            stdout="",
            stderr="",
        )
        with patch("arcgis_mcp_server.run_in_arcgis_env", return_value=execution_result) as mock_run:
            server.execute_arcpy_code(
                code="import arcpy",
                workspace=r"D:\GIS\scratch.gdb",
                project_path=r"D:\GIS\project.aprx",
                timeout_seconds=60,
            )
        mock_run.assert_called_once()
        args, kwargs = mock_run.call_args
        self.assertIn("import arcpy", kwargs.get("code", args[0]))
        self.assertEqual(kwargs.get("workspace"), r"D:\GIS\scratch.gdb")
        self.assertEqual(kwargs.get("project_path"), r"D:\GIS\project.aprx")


class SyncPlanToolTests(unittest.TestCase):
    """Tests for the generate_sync_plan MCP tool."""

    def test_returns_todo_status(self) -> None:
        payload = server.generate_sync_plan(source_description="CSV export")
        self.assertEqual(payload["status"], "todo")
        self.assertIn("not yet implemented", payload["message"])

    def test_returns_source_description(self) -> None:
        payload = server.generate_sync_plan(source_description="Shapefile batch")
        self.assertEqual(payload["source_description"], "Shapefile batch")

    def test_handles_project_context_param(self) -> None:
        payload = server.generate_sync_plan(
            source_description="GeoJSON files",
            project_context=r"D:\GIS\project.aprx",
        )
        self.assertEqual(payload["status"], "todo")
        self.assertEqual(payload["project_context"], r"D:\GIS\project.aprx")


class NamedPipeModuleTests(unittest.TestCase):
    """Tests for the arcgis_mcp_named_pipe module."""

    def test_is_addin_available_returns_false_when_pywin32_missing(self) -> None:
        with patch("arcgis_mcp_named_pipe._PYWIN32_AVAILABLE", False):
            self.assertFalse(named_pipe.is_addin_available())

    def test_call_addin_raises_when_pywin32_missing(self) -> None:
        with patch("arcgis_mcp_named_pipe._PYWIN32_AVAILABLE", False):
            with self.assertRaises(named_pipe.AddInNotAvailableError):
                named_pipe.call_addin("pro.ping")

    def test_call_addin_raises_with_helpful_message_when_pywin32_missing(self) -> None:
        with patch("arcgis_mcp_named_pipe._PYWIN32_AVAILABLE", False):
            with self.assertRaises(named_pipe.AddInNotAvailableError) as ctx:
                named_pipe.call_addin("pro.ping")
            self.assertIn("pywin32 is not installed", str(ctx.exception))

    def test_call_addin_handles_pipe_not_found_gracefully(self) -> None:
        if not named_pipe._PYWIN32_AVAILABLE:
            self.skipTest("pywin32 not available on this Python")
        if named_pipe.is_addin_available():
            self.skipTest("Add-in is available; test requires pipe to be unreachable")
        with self.assertRaises(named_pipe.AddInNotAvailableError):
            named_pipe.call_addin("pro.ping", timeout=0.1)

    def test_open_pipe_raises_when_pywin32_missing(self) -> None:
        with patch("arcgis_mcp_named_pipe._PYWIN32_AVAILABLE", False):
            with self.assertRaises(named_pipe.AddInNotAvailableError):
                named_pipe._open_pipe(timeout=0.1)


class ProToolsTests(unittest.TestCase):
    """Tests for the pro.* MCP tools in arcgis_mcp_server."""

    def setUp(self):
        if "unavailable" in self._testMethodName and named_pipe.is_addin_available():
            self.skipTest("Add-in is available; test requires unreachable pipe")

    def test_pro_ping_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_ping()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_ping_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"pong": "addin"}):
            result = server.pro_ping()
        self.assertEqual(result["status"], "ok")

    def test_pro_ping_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_ping()
        self.assertEqual(result["status"], "error")

    def test_pro_get_active_map_name_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_active_map_name()
        mock_call.assert_called_once_with("pro.getActiveMapName", None)

    def test_pro_list_layers_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_layers()
        mock_call.assert_called_once_with("pro.listLayers", None)

    def test_pro_count_features_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_count_features(layer="Parcels")
        mock_call.assert_called_once_with("pro.countFeatures", {"layer": "Parcels"})

    def test_pro_get_layer_schema_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_layer_schema(layer="Roads")
        mock_call.assert_called_once_with("pro.getLayerSchema", {"layer": "Roads"})

    def test_pro_get_selection_count_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_selection_count(layer="Buildings")
        mock_call.assert_called_once_with("pro.getSelectionCount", {"layer": "Buildings"})

    def test_pro_select_by_attribute_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_by_attribute(layer="Parcels", where="ZONE = 'Residential'")
        mock_call.assert_called_once_with(
            "pro.selectByAttribute",
            {"layer": "Parcels", "where": "ZONE = 'Residential'"},
        )

    def test_pro_clear_selection_with_layer_forwards_layer_arg(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_clear_selection(layer="Parcels")
        mock_call.assert_called_once_with("pro.clearSelection", {"layer": "Parcels"})

    def test_pro_clear_selection_without_layer_forwards_empty_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_clear_selection()
        mock_call.assert_called_once_with("pro.clearSelection", {})

    def test_pro_zoom_to_layer_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_zoom_to_layer(layer="Parcels")
        mock_call.assert_called_once_with("pro.zoomToLayer", {"layer": "Parcels"})

    def test_pro_get_current_extent_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_current_extent()
        mock_call.assert_called_once_with("pro.getCurrentExtent", None)

    def test_pro_pan_to_extent_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_pan_to_extent(xmin=500000, ymin=6900000, xmax=510000, ymax=6910000)
        mock_call.assert_called_once_with(
            "pro.panToExtent",
            {"xmin": "500000", "ymin": "6900000", "xmax": "510000", "ymax": "6910000"},
        )

    def test_pro_tools_return_unavailable_with_next_step_on_connection_error(self) -> None:
        err = named_pipe.AddInNotAvailableError("timeout")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_ping()
        self.assertEqual(result["status"], "unavailable")
        self.assertIn("next_step", result)
        self.assertIn("APBridgeAddIn", result["next_step"])

    # --- Phase 0: pro_get_camera ---

    def test_pro_get_camera_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_camera()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_camera_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"x": 1.0, "y": 2.0}):
            result = server.pro_get_camera()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_camera_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_camera()
        self.assertEqual(result["status"], "error")

    def test_pro_get_camera_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_camera()
        mock_call.assert_called_once_with("pro.getCamera", None)

    # --- Phase 0: pro_set_layer_visibility ---

    def test_pro_set_layer_visibility_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_visibility(layer="Roads", visible=False)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_layer_visibility_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_layer_visibility(layer="Roads", visible=True)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_layer_visibility_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_visibility(layer="Roads", visible=False)
        self.assertEqual(result["status"], "error")

    def test_pro_set_layer_visibility_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_layer_visibility(layer="Roads", visible=False)
        mock_call.assert_called_once_with(
            "pro.setLayerVisibility",
            {"layer": "Roads", "visible": "false"},
        )

    # --- Phase 0: pro_get_layer_extent ---

    def test_pro_get_layer_extent_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_extent(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_layer_extent_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"xmin": 0.0, "ymin": 0.0}):
            result = server.pro_get_layer_extent(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_get_layer_extent_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_extent(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_get_layer_extent_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_layer_extent(layer="Buildings")
        mock_call.assert_called_once_with("pro.getLayerExtent", {"layer": "Buildings"})

    # --- Phase 0: pro_select_by_rectangle ---

    def test_pro_select_by_rectangle_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_by_rectangle(layer="Parcels", xmin=0, ymin=0, xmax=1, ymax=1)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_select_by_rectangle_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_select_by_rectangle(layer="Parcels", xmin=0, ymin=0, xmax=1, ymax=1)
        self.assertEqual(result["status"], "ok")

    def test_pro_select_by_rectangle_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_by_rectangle(layer="Parcels", xmin=0, ymin=0, xmax=1, ymax=1)
        self.assertEqual(result["status"], "error")

    def test_pro_select_by_rectangle_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_by_rectangle(
                layer="Parcels", xmin=100.5, ymin=200.5, xmax=300.5, ymax=400.5
            )
        mock_call.assert_called_once_with(
            "pro.selectByRectangle",
            {
                "layer": "Parcels",
                "xmin": "100.5",
                "ymin": "200.5",
                "xmax": "300.5",
                "ymax": "400.5",
                "selectionType": "NEW",
            },
        )

    def test_pro_select_by_rectangle_forwards_custom_selection_type(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_by_rectangle(
                layer="Parcels", xmin=0, ymin=0, xmax=1, ymax=1, selection_type="ADD"
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["selectionType"], "ADD")

    # --- Phase 0: pro_switch_selection ---

    def test_pro_switch_selection_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_switch_selection()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_switch_selection_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_switch_selection(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_switch_selection_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_switch_selection()
        self.assertEqual(result["status"], "error")

    def test_pro_switch_selection_with_layer_forwards_layer_arg(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_switch_selection(layer="Parcels")
        mock_call.assert_called_once_with("pro.switchSelection", {"layer": "Parcels"})

    def test_pro_switch_selection_without_layer_forwards_empty_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_switch_selection()
        mock_call.assert_called_once_with("pro.switchSelection", {})

    # --- Phase 0: pro_get_feature_by_oid ---

    def test_pro_get_feature_by_oid_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_feature_by_oid(layer="Parcels", oid=42)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_feature_by_oid_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"attributes": {"Name": "Test"}}):
            result = server.pro_get_feature_by_oid(layer="Parcels", oid=1)
        self.assertEqual(result["status"], "ok")

    def test_pro_get_feature_by_oid_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_feature_by_oid(layer="Parcels", oid=1)
        self.assertEqual(result["status"], "error")

    def test_pro_get_feature_by_oid_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_feature_by_oid(layer="Buildings", oid=99)
        mock_call.assert_called_once_with(
            "pro.getFeatureByOid",
            {"layer": "Buildings", "oid": "99"},
        )

    # --- Phase 0: pro_undo_edit ---

    def test_pro_undo_edit_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_undo_edit()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_undo_edit_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"undoPerformed": True}):
            result = server.pro_undo_edit()
        self.assertEqual(result["status"], "ok")

    def test_pro_undo_edit_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_undo_edit()
        self.assertEqual(result["status"], "error")

    def test_pro_undo_edit_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_undo_edit()
        mock_call.assert_called_once_with("pro.undoEdit", None)

    # --- Phase 0: pro_redo_edit ---

    def test_pro_redo_edit_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_redo_edit()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_redo_edit_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"redoPerformed": True}):
            result = server.pro_redo_edit()
        self.assertEqual(result["status"], "ok")

    def test_pro_redo_edit_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_redo_edit()
        self.assertEqual(result["status"], "error")

    def test_pro_redo_edit_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_redo_edit()
        mock_call.assert_called_once_with("pro.redoEdit", None)

    # --- Phase 0: pro_set_active_tool ---

    def test_pro_set_active_tool_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_active_tool(tool="esri_mapping_selectTool")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_active_tool_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_active_tool(tool="esri_mapping_exploreTool")
        self.assertEqual(result["status"], "ok")

    def test_pro_set_active_tool_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_active_tool(tool="esri_mapping_identifyTool")
        self.assertEqual(result["status"], "error")

    def test_pro_set_active_tool_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_active_tool(tool="esri_mapping_selectTool")
        mock_call.assert_called_once_with(
            "pro.setActiveTool",
            {"tool": "esri_mapping_selectTool"},
        )

    # --- Phase 0: pro_is_3d ---

    def test_pro_is_3d_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_is_3d()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_is_3d_returns_ok_when_pipe_reachable(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"is3d": False, "viewingMode": "Map"},
        ):
            result = server.pro_is_3d()
        self.assertEqual(result["status"], "ok")

    def test_pro_is_3d_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_is_3d()
        self.assertEqual(result["status"], "error")

    def test_pro_is_3d_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_is_3d()
        mock_call.assert_called_once_with("pro.is3d", None)

    # --- Phase 1: pro_get_layer_renderer ---

    def test_pro_get_layer_renderer_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_renderer(layer="Roads")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_layer_renderer_returns_ok_when_pipe_reachable(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"rendererType": "Simple", "field": None},
        ):
            result = server.pro_get_layer_renderer(layer="Roads")
        self.assertEqual(result["status"], "ok")

    def test_pro_get_layer_renderer_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_renderer(layer="Roads")
        self.assertEqual(result["status"], "error")

    def test_pro_get_layer_renderer_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_layer_renderer(layer="Parcels")
        mock_call.assert_called_once_with("pro.getLayerRenderer", {"layer": "Parcels"})

    # --- Phase 1: pro_set_layer_color ---

    def test_pro_set_layer_color_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_color(layer="Parcels", r=255, g=0, b=0)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_layer_color_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_layer_color(layer="Parcels", r=255, g=0, b=0)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_layer_color_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_color(layer="Parcels", r=0, g=255, b=0)
        self.assertEqual(result["status"], "error")

    def test_pro_set_layer_color_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_layer_color(layer="Roads", r=100, g=150, b=200)
        mock_call.assert_called_once_with(
            "pro.setLayerColor",
            {"layer": "Roads", "r": "100", "g": "150", "b": "200"},
        )

    # --- Phase 1: pro_remove_layer ---

    def test_pro_remove_layer_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_remove_layer(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_remove_layer_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_remove_layer(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_remove_layer_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_remove_layer(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_remove_layer_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_remove_layer(layer="FloodZones")
        mock_call.assert_called_once_with("pro.removeLayer", {"layer": "FloodZones"})

    # --- Phase 1: pro_add_layer_from_file ---

    def test_pro_add_layer_from_file_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layer_from_file(path=r"C:\data\roads.lyrx")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_layer_from_file_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_layer_from_file(path=r"D:\gis\parcels.lyrx")
        self.assertEqual(result["status"], "ok")

    def test_pro_add_layer_from_file_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layer_from_file(path=r"C:\nonexistent.lyrx")
        self.assertEqual(result["status"], "error")

    def test_pro_add_layer_from_file_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layer_from_file(path=r"D:\data\buildings.lyrx")
        mock_call.assert_called_once_with(
            "pro.addLayerFromFile",
            {"path": r"D:\data\buildings.lyrx"},
        )

    # --- pro_add_layer_from_service ---

    def test_pro_add_layer_from_service_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layer_from_service(url="https://example.com/arcgis/rest/services/MapServer")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_layer_from_service_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "layerName": "WebLayer"}):
            result = server.pro_add_layer_from_service(url="https://example.com/arcgis/rest/services/MapServer")
        self.assertEqual(result["status"], "ok")

    def test_pro_add_layer_from_service_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layer_from_service(url="https://example.com/arcgis/rest/services/MapServer")
        self.assertEqual(result["status"], "error")

    def test_pro_add_layer_from_service_forwards_op_and_url(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layer_from_service(
                url="https://sampleserver.arcgisonline.com/arcgis/rest/services/World/MapServer"
            )
        mock_call.assert_called_once_with(
            "pro.addLayerFromService",
            {"url": "https://sampleserver.arcgisonline.com/arcgis/rest/services/World/MapServer"},
        )

    def test_pro_add_layer_from_service_forwards_service_type(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layer_from_service(
                url="https://example.com/wms",
                service_type="WMS",
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["serviceType"], "WMS")

    # --- Phase 1: pro_select_by_polygon ---

    def test_pro_select_by_polygon_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_by_polygon(
                layer="Parcels", coordinates="0,0 10,0 10,10 0,10 0,0"
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_select_by_polygon_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_select_by_polygon(
                layer="Parcels", coordinates="100,200 300,400 500,600"
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_select_by_polygon_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_by_polygon(
                layer="Parcels", coordinates="0,0 1,0 1,1 0,1 0,0"
            )
        self.assertEqual(result["status"], "error")

    def test_pro_select_by_polygon_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_by_polygon(
                layer="Zones",
                coordinates="0,0 10,0 10,10 0,10 0,0",
            )
        mock_call.assert_called_once_with(
            "pro.selectByPolygon",
            {
                "layer": "Zones",
                "coordinates": "0,0 10,0 10,10 0,10 0,0",
                "selectionType": "NEW",
            },
        )

    def test_pro_select_by_polygon_forwards_custom_selection_type(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_by_polygon(
                layer="Zones",
                coordinates="0,0 5,0 5,5 0,5 0,0",
                selection_type="SUBTRACT",
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["selectionType"], "SUBTRACT")

    # --- Phase 1: pro_list_layouts ---

    def test_pro_list_layouts_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_layouts()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_layouts_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value=[{"name": "Layout1"}]):
            result = server.pro_list_layouts()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_layouts_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_layouts()
        self.assertEqual(result["status"], "error")

    def test_pro_list_layouts_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_layouts()
        mock_call.assert_called_once_with("pro.listLayouts", None)

    # --- Phase 1: pro_get_project_properties ---

    def test_pro_get_project_properties_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_project_properties()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_project_properties_returns_ok_when_pipe_reachable(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"name": "MyProject", "defaultGdb": "..."},
        ):
            result = server.pro_get_project_properties()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_project_properties_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_project_properties()
        self.assertEqual(result["status"], "error")

    def test_pro_get_project_properties_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_project_properties()
        mock_call.assert_called_once_with("pro.getProjectProperties", None)

    # --- Phase 1: pro_get_geometry_distance ---

    def test_pro_get_geometry_distance_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_geometry_distance(x1=0, y1=0, x2=10, y2=10)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_geometry_distance_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"distance": 14.14}):
            result = server.pro_get_geometry_distance(x1=0, y1=0, x2=10, y2=10)
        self.assertEqual(result["status"], "ok")

    def test_pro_get_geometry_distance_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_geometry_distance(x1=0, y1=0, x2=10, y2=10)
        self.assertEqual(result["status"], "error")

    def test_pro_get_geometry_distance_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_geometry_distance(x1=100.5, y1=200.5, x2=300.5, y2=400.5)
        mock_call.assert_called_once_with(
            "pro.getGeometryDistance",
            {"x1": "100.5", "y1": "200.5", "x2": "300.5", "y2": "400.5"},
        )

    # --- Phase 2: pro_set_layer_transparency ---

    def test_pro_set_layer_transparency_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_transparency(layer="Roads", transparency=50)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_layer_transparency_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_layer_transparency(layer="Roads", transparency=25)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_layer_transparency_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_transparency(layer="Roads", transparency=75)
        self.assertEqual(result["status"], "error")

    def test_pro_set_layer_transparency_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_layer_transparency(layer="Parcels", transparency=33.5)
        mock_call.assert_called_once_with(
            "pro.setLayerTransparency",
            {"layer": "Parcels", "transparency": "33.5"},
        )

    # --- Phase 2: pro_get_all_map_names ---

    def test_pro_get_all_map_names_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_all_map_names()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_all_map_names_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value=[{"name": "Map1"}]):
            result = server.pro_get_all_map_names()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_all_map_names_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_all_map_names()
        self.assertEqual(result["status"], "error")

    def test_pro_get_all_map_names_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_all_map_names()
        mock_call.assert_called_once_with("pro.getAllMapNames", None)

    # --- Phase 2: pro_get_map_frame ---

    def test_pro_get_map_frame_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_map_frame(layout_name="Layout1")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_map_frame_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value=[{"name": "Map Frame"}]):
            result = server.pro_get_map_frame(layout_name="Layout1")
        self.assertEqual(result["status"], "ok")

    def test_pro_get_map_frame_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_map_frame(layout_name="Layout1")
        self.assertEqual(result["status"], "error")

    def test_pro_get_map_frame_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_map_frame(layout_name="MyLayout")
        mock_call.assert_called_once_with("pro.getMapFrame", {"layoutName": "MyLayout"})

    def test_pro_get_map_frame_forwards_optional_map_frame_name(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_map_frame(layout_name="Layout1", map_frame_name="Main Map")
        mock_call.assert_called_once_with(
            "pro.getMapFrame",
            {"layoutName": "Layout1", "mapFrameName": "Main Map"},
        )

    # --- Phase 2: pro_select_by_layer ---

    def test_pro_select_by_layer_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_by_layer(target_layer="Buildings", source_layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_select_by_layer_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_select_by_layer(target_layer="Buildings", source_layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_select_by_layer_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_by_layer(target_layer="Buildings", source_layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_select_by_layer_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_by_layer(
                target_layer="Buildings",
                source_layer="Parcels",
                spatial_relationship="Contains",
                selection_type="ADD",
            )
        mock_call.assert_called_once_with(
            "pro.selectByLayer",
            {
                "targetLayer": "Buildings",
                "sourceLayer": "Parcels",
                "spatialRelationship": "Contains",
                "selectionType": "ADD",
            },
        )

    # --- Phase 2: pro_get_features_by_extent ---

    def test_pro_get_features_by_extent_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_features_by_extent(
                layer="Parcels", xmin=0, ymin=0, xmax=10, ymax=10
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_features_by_extent_returns_ok_when_pipe_reachable(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"features": [], "count": 0},
        ):
            result = server.pro_get_features_by_extent(
                layer="Parcels", xmin=0, ymin=0, xmax=10, ymax=10
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_get_features_by_extent_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_features_by_extent(
                layer="Parcels", xmin=0, ymin=0, xmax=10, ymax=10
            )
        self.assertEqual(result["status"], "error")

    def test_pro_get_features_by_extent_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_features_by_extent(
                layer="Parcels", xmin=100, ymin=200, xmax=300, ymax=400
            )
        mock_call.assert_called_once_with(
            "pro.getFeaturesByExtent",
            {
                "layer": "Parcels",
                "xmin": "100",
                "ymin": "200",
                "xmax": "300",
                "ymax": "400",
                "maxFeatures": "100",
            },
        )

    def test_pro_get_features_by_extent_forwards_fields(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_features_by_extent(
                layer="Roads",
                xmin=0,
                ymin=0,
                xmax=1,
                ymax=1,
                fields="NAME,TYPE",
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["fields"], "NAME,TYPE")

    # --- pro_find_features ---

    def test_pro_find_features_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_find_features(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_find_features_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_find_features(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_find_features_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_find_features(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_find_features_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_find_features(
                layer="Roads",
                where="OBJECTID >= 0",
                fields="NAME,TYPE",
                max_features=500,
            )
        call_args = mock_call.call_args[0]
        self.assertEqual(call_args[0], "pro.findFeatures")
        self.assertEqual(call_args[1]["layer"], "Roads")
        self.assertEqual(call_args[1]["where"], "OBJECTID >= 0")
        self.assertEqual(call_args[1]["fields"], "NAME,TYPE")
        self.assertEqual(call_args[1]["maxFeatures"], "500")

    def test_pro_find_features_forwards_default_max_features(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_find_features(layer="Roads")
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["maxFeatures"], "1000")

    def test_pro_find_features_omits_optional_params_when_not_given(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_find_features(layer="Roads")
        call_args = mock_call.call_args[0][1]
        self.assertNotIn("where", call_args)
        self.assertNotIn("fields", call_args)

    # --- Phase 2: pro_delete_features_by_oid ---

    def test_pro_delete_features_by_oid_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_features_by_oid(layer="Parcels", oids="1,2,3")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_delete_features_by_oid_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_delete_features_by_oid(layer="Parcels", oids="5")
        self.assertEqual(result["status"], "ok")

    def test_pro_delete_features_by_oid_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_features_by_oid(layer="Parcels", oids="1")
        self.assertEqual(result["status"], "error")

    def test_pro_delete_features_by_oid_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_delete_features_by_oid(layer="Zones", oids="10,11,12")
        mock_call.assert_called_once_with(
            "pro.deleteFeaturesByOid",
            {"layer": "Zones", "oids": "10,11,12"},
        )

    # --- Phase 2: pro_update_feature_attributes ---

    def test_pro_update_feature_attributes_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_update_feature_attributes(
                layer="Parcels", oid=1, attributes='{"Name":"New"}'
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_update_feature_attributes_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_update_feature_attributes(
                layer="Parcels", oid=1, attributes='{"Name":"New"}'
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_update_feature_attributes_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_update_feature_attributes(
                layer="Parcels", oid=1, attributes='{"Name":"New"}'
            )
        self.assertEqual(result["status"], "error")

    def test_pro_update_feature_attributes_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_update_feature_attributes(
                layer="Buildings",
                oid=42,
                attributes='{"Height":100,"Name":"Tower"}',
            )
        mock_call.assert_called_once_with(
            "pro.updateFeatureAttributes",
            {
                "layer": "Buildings",
                "oid": "42",
                "attributes": '{"Height":100,"Name":"Tower"}',
            },
        )

    # --- Phase 2: pro_create_point_feature ---

    def test_pro_create_point_feature_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_point_feature(layer="Trees", x=100, y=200)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_point_feature_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "objectId": 1}):
            result = server.pro_create_point_feature(layer="Trees", x=100, y=200)
        self.assertEqual(result["status"], "ok")

    def test_pro_create_point_feature_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_point_feature(layer="Trees", x=100, y=200)
        self.assertEqual(result["status"], "error")

    def test_pro_create_point_feature_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_point_feature(layer="Trees", x=100.5, y=200.5)
        mock_call.assert_called_once_with(
            "pro.createPointFeature",
            {"layer": "Trees", "x": "100.5", "y": "200.5"},
        )

    def test_pro_create_point_feature_forwards_optional_wkid_and_attributes(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_point_feature(
                layer="Trees",
                x=100,
                y=200,
                wkid=28356,
                attributes='{"Species":"Oak"}',
            )
        mock_call.assert_called_once_with(
            "pro.createPointFeature",
            {
                "layer": "Trees",
                "x": "100",
                "y": "200",
                "wkid": "28356",
                "attributes": '{"Species":"Oak"}',
            },
        )

    # --- Phase 4: pro_apply_unique_value_renderer ---

    def test_pro_apply_unique_value_renderer_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_apply_unique_value_renderer(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_apply_unique_value_renderer_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "classCount": 3}):
            result = server.pro_apply_unique_value_renderer(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "ok")

    def test_pro_apply_unique_value_renderer_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_apply_unique_value_renderer(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "error")

    def test_pro_apply_unique_value_renderer_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_apply_unique_value_renderer(layer="Parcels", field="ZONE")
        mock_call.assert_called_once_with(
            "pro.applyUniqueValueRenderer",
            {"layer": "Parcels", "field": "ZONE"},
        )

    def test_pro_apply_unique_value_renderer_forwards_color_ramp(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_apply_unique_value_renderer(
                layer="Parcels", field="ZONE", color_ramp="[[255,0,0],[0,255,0]]"
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["colorRamp"], "[[255,0,0],[0,255,0]]")

    # --- Phase 4: pro_apply_class_breaks_renderer ---

    def test_pro_apply_class_breaks_renderer_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_apply_class_breaks_renderer(layer="Parcels", field="AREA")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_apply_class_breaks_renderer_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "breakCount": 5}):
            result = server.pro_apply_class_breaks_renderer(layer="Parcels", field="AREA")
        self.assertEqual(result["status"], "ok")

    def test_pro_apply_class_breaks_renderer_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_apply_class_breaks_renderer(layer="Parcels", field="AREA")
        self.assertEqual(result["status"], "error")

    def test_pro_apply_class_breaks_renderer_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_apply_class_breaks_renderer(layer="Parcels", field="AREA", break_count=3)
        mock_call.assert_called_once_with(
            "pro.applyClassBreaksRenderer",
            {"layer": "Parcels", "field": "AREA", "breakCount": "3"},
        )

    # --- Phase 4: pro_get_elevation_sources ---

    def test_pro_get_elevation_sources_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_elevation_sources()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_elevation_sources_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin", return_value={"isScene": True, "elevationSources": []}
        ):  # noqa: E501
            result = server.pro_get_elevation_sources()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_elevation_sources_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_elevation_sources()
        self.assertEqual(result["status"], "error")

    def test_pro_get_elevation_sources_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_elevation_sources()
        mock_call.assert_called_once_with("pro.getElevationSources", None)

    # --- Phase 4: pro_set_ground_opacity ---

    def test_pro_set_ground_opacity_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_ground_opacity(opacity=50)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_ground_opacity_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_ground_opacity(opacity=75)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_ground_opacity_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_ground_opacity(opacity=25)
        self.assertEqual(result["status"], "error")

    def test_pro_set_ground_opacity_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_ground_opacity(opacity=60)
        mock_call.assert_called_once_with(
            "pro.setGroundOpacity",
            {"opacity": "60"},
        )

    # --- Phase 4: pro_get_active_tool ---

    def test_pro_get_active_tool_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_active_tool()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_active_tool_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin", return_value={"activeTool": "esri_mapping_selectTool"}
        ):  # noqa: E501
            result = server.pro_get_active_tool()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_active_tool_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_active_tool()
        self.assertEqual(result["status"], "error")

    def test_pro_get_active_tool_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_active_tool()
        mock_call.assert_called_once_with("pro.getActiveTool", None)

    # --- Phase 4: pro_list_field_values ---

    def test_pro_list_field_values_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_field_values(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_field_values_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin", return_value={"field": "ZONE", "distinctCount": 3}
        ):  # noqa: E501
            result = server.pro_list_field_values(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "ok")

    def test_pro_list_field_values_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_field_values(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "error")

    def test_pro_list_field_values_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_field_values(layer="Parcels", field="ZONE", max_values=50)
        mock_call.assert_called_once_with(
            "pro.listFieldValues",
            {"layer": "Parcels", "field": "ZONE", "maxValues": "50"},
        )

    # --- Phase 4: pro_add_field ---

    def test_pro_add_field_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_field(
                layer="Parcels", field_name="NEW_FIELD", field_type="Text"
            )  # noqa: E501
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_field_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_field(
                layer="Parcels", field_name="AREA_HA", field_type="Double"
            )  # noqa: E501
        self.assertEqual(result["status"], "ok")

    def test_pro_add_field_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_field(layer="Parcels", field_name="F", field_type="Integer")
        self.assertEqual(result["status"], "error")

    def test_pro_add_field_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_field(
                layer="Parcels", field_name="DESCRIPTION", field_type="Text", length=255
            )  # noqa: E501
        mock_call.assert_called_once_with(
            "pro.addField",
            {"layer": "Parcels", "fieldName": "DESCRIPTION", "fieldType": "Text", "length": "255"},
        )

    def test_pro_add_field_forwards_optional_precision_scale(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_field(
                layer="Parcels", field_name="RATIO", field_type="Double", precision=10, scale=4
            )  # noqa: E501
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["precision"], "10")
        self.assertEqual(call_args["scale"], "4")

    # --- Phase 4: pro_delete_field ---

    def test_pro_delete_field_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_field(layer="Parcels", field_name="OLD_FIELD")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_delete_field_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_delete_field(layer="Parcels", field_name="TEMP")
        self.assertEqual(result["status"], "ok")

    def test_pro_delete_field_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_field(layer="Parcels", field_name="TEMP")
        self.assertEqual(result["status"], "error")

    def test_pro_delete_field_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_delete_field(layer="Parcels", field_name="DEPRECATED")
        mock_call.assert_called_once_with(
            "pro.deleteField",
            {"layer": "Parcels", "fieldName": "DEPRECATED"},
        )

    # --- pro_calculate_field ---

    def test_pro_calculate_field_returns_unavailable_when_pipe_unreachable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_calculate_field(
                layer="Parcels", field="AREA", expression="!SHAPE.AREA!"
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_calculate_field_returns_ok_when_pipe_reachable(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_calculate_field(
                layer="Parcels", field="AREA", expression="!SHAPE.AREA!"
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_calculate_field_returns_error_on_operation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_calculate_field(
                layer="Parcels", field="AREA", expression="!SHAPE.AREA!"
            )
        self.assertEqual(result["status"], "error")

    def test_pro_calculate_field_forwards_correct_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_calculate_field(
                layer="Roads",
                field="LENGTH",
                expression="!Shape_Length! * 3.28084",
                expression_type="PYTHON3",
            )
        mock_call.assert_called_once_with(
            "pro.calculateField",
            {
                "layer": "Roads",
                "field": "LENGTH",
                "expression": "!Shape_Length! * 3.28084",
                "expressionType": "PYTHON3",
            },
        )

    def test_pro_calculate_field_omits_optional_params(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_calculate_field(
                layer="Roads", field="LENGTH", expression="!Shape_Length!"
            )
        call_args = mock_call.call_args[0][1]
        self.assertNotIn("expressionType", call_args)
        self.assertNotIn("codeBlock", call_args)

    # --- Phase 4: pro_create_polygon_feature ---

    def test_pro_create_polygon_feature_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_polygon_feature(
                layer="Parcels", coordinates="0,0 10,0 10,10 0,10 0,0"
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_polygon_feature_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "objectId": 1}):
            result = server.pro_create_polygon_feature(
                layer="Zones", coordinates="100,100 200,100 200,200 100,200 100,100"
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_create_polygon_feature_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_polygon_feature(
                layer="Zones", coordinates="0,0 1,0 1,1 0,1 0,0"
            )
        self.assertEqual(result["status"], "error")

    def test_pro_create_polygon_feature_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_polygon_feature(layer="Zones", coordinates="0,0 10,0 10,10 0,10 0,0")
        mock_call.assert_called_once_with(
            "pro.createPolygonFeature",
            {"layer": "Zones", "coordinates": "0,0 10,0 10,10 0,10 0,0"},
        )

    def test_pro_create_polygon_feature_forwards_optional_wkid_and_attributes(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_polygon_feature(
                layer="Zones",
                coordinates="0,0 1,1 2,0 0,0",
                wkid=28356,
                attributes='{"Name":"Test"}',
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["wkid"], "28356")
        self.assertEqual(call_args["attributes"], '{"Name":"Test"}')

    # --- Phase 4: pro_create_line_feature ---

    def test_pro_create_line_feature_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_line_feature(layer="Roads", coordinates="0,0 10,10")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_line_feature_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "objectId": 1}):
            result = server.pro_create_line_feature(layer="Roads", coordinates="0,0 100,100 200,0")
        self.assertEqual(result["status"], "ok")

    def test_pro_create_line_feature_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_line_feature(layer="Roads", coordinates="0,0 1,1")
        self.assertEqual(result["status"], "error")

    def test_pro_create_line_feature_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_line_feature(layer="Roads", coordinates="0,0 50,50 100,0")
        mock_call.assert_called_once_with(
            "pro.createLineFeature",
            {"layer": "Roads", "coordinates": "0,0 50,50 100,0"},
        )

    def test_pro_create_line_feature_forwards_optional_wkid_and_attributes(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_line_feature(
                layer="Roads",
                coordinates="0,0 1,1",
                wkid=28356,
                attributes='{"Type":"Highway"}',
            )
        call_args = mock_call.call_args[0][1]
        self.assertEqual(call_args["wkid"], "28356")
        self.assertEqual(call_args["attributes"], '{"Type":"Highway"}')

    # --- Phase 5: pro_set_map_scale ---

    def test_pro_set_map_scale_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_map_scale(scale=50000)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_map_scale_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "scale": 25000}):
            result = server.pro_set_map_scale(scale=25000)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_map_scale_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_map_scale(scale=1000)
        self.assertEqual(result["status"], "error")

    def test_pro_set_map_scale_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_map_scale(scale=50000)
        mock_call.assert_called_once_with(
            "pro.setMapScale",
            {"scale": "50000"},
        )

    # --- Phase 5: pro_get_map_scale ---

    def test_pro_get_map_scale_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_map_scale()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_map_scale_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"scale": 10000}):
            result = server.pro_get_map_scale()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_map_scale_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_map_scale()
        self.assertEqual(result["status"], "error")

    def test_pro_get_map_scale_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_map_scale()
        mock_call.assert_called_once_with("pro.getMapScale", None)

    # --- Phase 5: pro_zoom_to_selected ---

    def test_pro_zoom_to_selected_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_zoom_to_selected()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_zoom_to_selected_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_zoom_to_selected()
        self.assertEqual(result["status"], "ok")

    def test_pro_zoom_to_selected_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_zoom_to_selected()
        self.assertEqual(result["status"], "error")

    def test_pro_zoom_to_selected_forwards_op_without_layer(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_zoom_to_selected()
        mock_call.assert_called_once_with("pro.zoomToSelected", {})

    def test_pro_zoom_to_selected_forwards_op_with_layer(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_zoom_to_selected(layer="Parcels")
        mock_call.assert_called_once_with(
            "pro.zoomToSelected",
            {"layer": "Parcels"},
        )

    # --- Phase 5: pro_get_edit_state ---

    def test_pro_get_edit_state_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_edit_state()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_edit_state_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"undoCount": 3, "redoCount": 0}):
            result = server.pro_get_edit_state()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_edit_state_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_edit_state()
        self.assertEqual(result["status"], "error")

    def test_pro_get_edit_state_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_edit_state()
        mock_call.assert_called_once_with("pro.getEditState", None)

    # --- Phase 5: pro_set_snapping ---

    def test_pro_set_snapping_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_snapping(enabled=True)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_snapping_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin", return_value={"done": True, "snappingEnabled": True}
        ):  # noqa: E501
            result = server.pro_set_snapping(enabled=True)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_snapping_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_snapping(enabled=False)
        self.assertEqual(result["status"], "error")

    def test_pro_set_snapping_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_snapping(enabled=True)
        mock_call.assert_called_once_with(
            "pro.setSnapping",
            {"enabled": "true"},
        )

    # --- pro.listBookmarks ---

    def test_pro_list_bookmarks_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_bookmarks()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_bookmarks_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value=[]):
            result = server.pro_list_bookmarks()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_bookmarks_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_bookmarks()
        self.assertEqual(result["status"], "error")

    def test_pro_list_bookmarks_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_list_bookmarks()
        mock_call.assert_called_once_with("pro.listBookmarks", {})

    # --- pro.zoomToBookmark ---

    def test_pro_zoom_to_bookmark_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_zoom_to_bookmark(name="ZoomTarget")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_zoom_to_bookmark_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_zoom_to_bookmark(name="ZoomTarget")
        self.assertEqual(result["status"], "ok")

    def test_pro_zoom_to_bookmark_error(self) -> None:
        err = named_pipe.AddInOperationError("bookmark not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_zoom_to_bookmark(name="ZoomTarget")
        self.assertEqual(result["status"], "error")

    def test_pro_zoom_to_bookmark_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_zoom_to_bookmark(name="ZoomTarget")
        mock_call.assert_called_once_with("pro.zoomToBookmark", {"name": "ZoomTarget"})

    # --- pro.createBookmark ---

    def test_pro_create_bookmark_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_bookmark(name="NewBM")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_bookmark_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_create_bookmark(name="NewBM")
        self.assertEqual(result["status"], "ok")

    def test_pro_create_bookmark_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_bookmark(name="NewBM")
        self.assertEqual(result["status"], "error")

    def test_pro_create_bookmark_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_create_bookmark(name="My Bookmark")
        mock_call.assert_called_once_with(
            "pro.createBookmark",
            {"name": "My Bookmark"},
        )

    # --- Phase 5: pro_delete_bookmark ---

    def test_pro_delete_bookmark_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_bookmark(name="zoom1")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_delete_bookmark_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "name": "zoom1"}):
            result = server.pro_delete_bookmark(name="zoom1")
        self.assertEqual(result["status"], "ok")

    def test_pro_delete_bookmark_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_bookmark(name="zoom1")
        self.assertEqual(result["status"], "error")

    def test_pro_delete_bookmark_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_delete_bookmark(name="My Bookmark")
        mock_call.assert_called_once_with(
            "pro.deleteBookmark",
            {"name": "My Bookmark"},
        )

    # --- Phase 5: pro_flash_selection ---

    def test_pro_flash_selection_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_flash_selection(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_flash_selection_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "flashedCount": 5}):
            result = server.pro_flash_selection(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_flash_selection_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_flash_selection(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_flash_selection_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_flash_selection(layer="Buildings")
        mock_call.assert_called_once_with(
            "pro.flashSelection",
            {"layer": "Buildings"},
        )

    # --- Phase 5: pro_select_all ---

    def test_pro_select_all_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_all(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_select_all_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "count": 100}):
            result = server.pro_select_all(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_select_all_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_select_all(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_select_all_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_select_all(layer="Roads")
        mock_call.assert_called_once_with(
            "pro.selectAll",
            {"layer": "Roads"},
        )

    # --- Phase 5: pro_set_status_bar_message ---

    def test_pro_set_status_bar_message_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_status_bar_message(message="Processing...")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_status_bar_message_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True, "message": "Done"}):
            result = server.pro_set_status_bar_message(message="Done")
        self.assertEqual(result["status"], "ok")

    def test_pro_set_status_bar_message_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_status_bar_message(message="Error")
        self.assertEqual(result["status"], "error")

    def test_pro_set_status_bar_message_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_status_bar_message(message="Loading layer...")
        mock_call.assert_called_once_with(
            "pro.setStatusBarMessage",
            {"message": "Loading layer..."},
        )

    # --- Phase 5: pro_list_standalone_tables ---

    def test_pro_list_standalone_tables_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_standalone_tables()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_standalone_tables_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value=[{"name": "Table1"}]):
            result = server.pro_list_standalone_tables()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_standalone_tables_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_standalone_tables()
        self.assertEqual(result["status"], "error")

    def test_pro_list_standalone_tables_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_standalone_tables()
        mock_call.assert_called_once_with("pro.listStandaloneTables", None)

    # --- Phase 6: pro_list_gp_history ---

    def test_pro_list_gp_history_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_gp_history()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_gp_history_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value=[{"toolName": "Buffer"}]):
            result = server.pro_list_gp_history()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_gp_history_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_gp_history()
        self.assertEqual(result["status"], "error")

    def test_pro_list_gp_history_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_gp_history(max_items=10)
        mock_call.assert_called_once_with(
            "pro.listGpHistory",
            {"maxItems": "10"},
        )

    # --- Phase 6: pro_is_time_enabled ---

    def test_pro_is_time_enabled_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_is_time_enabled()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_is_time_enabled_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"isTimeEnabled": True}):
            result = server.pro_is_time_enabled()
        self.assertEqual(result["status"], "ok")

    def test_pro_is_time_enabled_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_is_time_enabled()
        self.assertEqual(result["status"], "error")

    def test_pro_is_time_enabled_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_is_time_enabled()
        mock_call.assert_called_once_with("pro.isTimeEnabled", None)

    # --- Phase 6: pro_get_time_extent ---

    def test_pro_get_time_extent_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_time_extent()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_time_extent_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"hasTimeExtent": True, "start": "2020", "end": "2025"},
        ):  # noqa: E501
            result = server.pro_get_time_extent()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_time_extent_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_time_extent()
        self.assertEqual(result["status"], "error")

    def test_pro_get_time_extent_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_time_extent()
        mock_call.assert_called_once_with("pro.getTimeExtent", None)

    # --- Phase 6: pro_set_time_extent ---

    def test_pro_set_time_extent_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_time_extent(start="2020-01-01", end="2025-12-31")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_time_extent_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_time_extent(start="2021-06-01", end="2022-06-01")
        self.assertEqual(result["status"], "ok")

    def test_pro_set_time_extent_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_time_extent(start="2020-01-01", end="2020-12-31")
        self.assertEqual(result["status"], "error")

    def test_pro_set_time_extent_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_time_extent(start="2020-01-01T00:00:00", end="2025-12-31T23:59:59")
        mock_call.assert_called_once_with(
            "pro.setTimeExtent",
            {"start": "2020-01-01T00:00:00", "end": "2025-12-31T23:59:59"},
        )

    # --- Phase 6: pro_list_layout_elements ---

    def test_pro_list_layout_elements_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_layout_elements(layout_name="Layout1")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_layout_elements_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"layoutName": "Layout1", "elementCount": 3},
        ):  # noqa: E501
            result = server.pro_list_layout_elements(layout_name="Layout1")
        self.assertEqual(result["status"], "ok")

    def test_pro_list_layout_elements_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_layout_elements(layout_name="Layout1")
        self.assertEqual(result["status"], "error")

    def test_pro_list_layout_elements_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_layout_elements(layout_name="MyLayout")
        mock_call.assert_called_once_with(
            "pro.listLayoutElements",
            {"layoutName": "MyLayout"},
        )

    # --- Phase 6: pro_rename_field ---

    def test_pro_rename_field_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_rename_field(layer="Parcels", old_name="OLD", new_name="NEW")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_rename_field_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_rename_field(layer="Parcels", old_name="OLD", new_name="NEW")
        self.assertEqual(result["status"], "ok")

    def test_pro_rename_field_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_rename_field(layer="Parcels", old_name="OLD", new_name="NEW")
        self.assertEqual(result["status"], "error")

    def test_pro_rename_field_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_rename_field(layer="Roads", old_name="OLD_NAME", new_name="NEW_NAME")
        mock_call.assert_called_once_with(
            "pro.renameField",
            {"layer": "Roads", "oldName": "OLD_NAME", "newName": "NEW_NAME"},
        )

    # --- Phase 6: pro_get_layer_description ---

    def test_pro_get_layer_description_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_description(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_layer_description_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin", return_value={"description": "Parcel boundaries"}
        ):  # noqa: E501
            result = server.pro_get_layer_description(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_get_layer_description_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_description(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_get_layer_description_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_layer_description(layer="Buildings")
        mock_call.assert_called_once_with("pro.getLayerDescription", {"layer": "Buildings"})

    # --- Phase 6: pro_set_layer_description ---

    def test_pro_set_layer_description_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_description(layer="Parcels", description="Test")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_layer_description_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_layer_description(layer="Parcels", description="Updated desc")
        self.assertEqual(result["status"], "ok")

    def test_pro_set_layer_description_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_description(layer="Parcels", description="Error")
        self.assertEqual(result["status"], "error")

    def test_pro_set_layer_description_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_layer_description(layer="Roads", description="Major roads layer")
        mock_call.assert_called_once_with(
            "pro.setLayerDescription",
            {"layer": "Roads", "description": "Major roads layer"},
        )

    # --- Phase 6: pro_list_scene_layer_types ---

    def test_pro_list_scene_layer_types_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_scene_layer_types()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_scene_layer_types_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"isScene": True, "layers": []}):
            result = server.pro_list_scene_layer_types()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_scene_layer_types_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_scene_layer_types()
        self.assertEqual(result["status"], "error")

    def test_pro_list_scene_layer_types_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_scene_layer_types()
        mock_call.assert_called_once_with("pro.listSceneLayerTypes", None)

    # --- Phase 6: pro_count_features_by_expression ---

    def test_pro_count_features_by_expression_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_count_features_by_expression(layer="Parcels", where="ZONE = 'A'")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_count_features_by_expression_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"count": 42}):
            result = server.pro_count_features_by_expression(layer="Parcels", where="AREA > 1000")
        self.assertEqual(result["status"], "ok")

    def test_pro_count_features_by_expression_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_count_features_by_expression(layer="Parcels", where="1=1")
        self.assertEqual(result["status"], "error")

    def test_pro_count_features_by_expression_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_count_features_by_expression(layer="Roads", where="TYPE = 'Highway'")
        mock_call.assert_called_once_with(
            "pro.countFeaturesByExpression",
            {"layer": "Roads", "where": "TYPE = 'Highway'"},
        )

    # --- Phase 7: Advanced Editing & GP ---

    def test_pro_split_features_unavailable(self) -> None:
        geom = '{"type":"polyline","paths":[[[0,0],[1,1]]]}'
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_split_features(layer="Parcels", cut_geometry=geom)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_split_features_ok(self) -> None:
        geom = '{"type":"polyline","paths":[[[0,0],[1,1]]]}'
        with patch("arcgis_mcp_server.call_addin", return_value={"splitCount": 2}):
            result = server.pro_split_features(layer="Parcels", cut_geometry=geom)
        self.assertEqual(result["status"], "ok")

    def test_pro_split_features_error(self) -> None:
        geom = '{"type":"polyline","paths":[[[0,0],[1,1]]]}'
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_split_features(layer="Parcels", cut_geometry=geom)
        self.assertEqual(result["status"], "error")

    def test_pro_split_features_forwards_op_and_args(self) -> None:
        geom = '{"type":"polyline","paths":[[[0,0],[1,1]]]}'
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_split_features(layer="Roads", cut_geometry=geom)
        mock_call.assert_called_once_with(
            "pro.splitFeatures",
            {"layer": "Roads", "cutGeometry": geom},
        )

    def test_pro_merge_features_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_merge_features(layer="Parcels", object_ids="[1,2,3]", target_oid=1)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_merge_features_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_merge_features(layer="Parcels", object_ids="[1,2,3]", target_oid=1)
        self.assertEqual(result["status"], "ok")

    def test_pro_merge_features_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_merge_features(layer="Parcels", object_ids="[1,2,3]", target_oid=1)
        self.assertEqual(result["status"], "error")

    def test_pro_merge_features_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_merge_features(layer="Roads", object_ids="[4,5,6]", target_oid=4)
        mock_call.assert_called_once_with(
            "pro.mergeFeatures",
            {"layer": "Roads", "objectIds": "[4,5,6]", "targetOid": "4"},
        )

    def test_pro_run_gp_tool_unavailable(self) -> None:
        params = '["roads", "roads_buf", "50 Meters"]'
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_run_gp_tool(tool_name="Buffer", parameters=params)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_run_gp_tool_ok(self) -> None:
        params = '["roads", "roads_buf", "50 Meters"]'
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_run_gp_tool(tool_name="Buffer", parameters=params)
        self.assertEqual(result["status"], "ok")

    def test_pro_run_gp_tool_error(self) -> None:
        params = '["roads", "roads_buf", "50 Meters"]'
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_run_gp_tool(tool_name="Buffer", parameters=params)
        self.assertEqual(result["status"], "error")

    def test_pro_run_gp_tool_forwards_op_and_args(self) -> None:
        params = '["roads", "roads_buf", "50 Meters"]'
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_run_gp_tool(tool_name="Buffer", parameters=params)
        mock_call.assert_called_once_with(
            "pro.runGpTool",
            {"toolName": "Buffer", "parameters": params},
        )

    def test_pro_list_gp_tools_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_gp_tools()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_gp_tools_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"toolCount": 5}):
            result = server.pro_list_gp_tools()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_gp_tools_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_gp_tools()
        self.assertEqual(result["status"], "error")

    def test_pro_list_gp_tools_forwards_op_and_args_defaults(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_gp_tools()
        mock_call.assert_called_once_with(
            "pro.listGpTools",
            {"searchText": "", "maxResults": "50"},
        )

    def test_pro_list_gp_tools_forwards_op_and_args_with_search(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_gp_tools(search_text="Buffer", max_results=10)
        mock_call.assert_called_once_with(
            "pro.listGpTools",
            {"searchText": "Buffer", "maxResults": "10"},
        )

    def test_pro_copy_features_unavailable(self) -> None:
        out = "C:\\out.gdb\\Parcels_Copy"
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_copy_features(layer="Parcels", output_path=out)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_copy_features_ok(self) -> None:
        out = "C:\\out.gdb\\Parcels_Copy"
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_copy_features(layer="Parcels", output_path=out)
        self.assertEqual(result["status"], "ok")

    def test_pro_copy_features_error(self) -> None:
        out = "C:\\out.gdb\\Parcels_Copy"
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_copy_features(layer="Parcels", output_path=out)
        self.assertEqual(result["status"], "error")

    def test_pro_copy_features_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_copy_features(layer="Roads", output_path="C:\\out.gdb\\Roads_Copy")
        mock_call.assert_called_once_with(
            "pro.copyFeatures",
            {"layer": "Roads", "outputPath": "C:\\out.gdb\\Roads_Copy"},
        )

    def test_pro_rename_layer_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_rename_layer(layer="Parcels", new_name="Parcels_Renamed")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_rename_layer_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_rename_layer(layer="Parcels", new_name="Parcels_Renamed")
        self.assertEqual(result["status"], "ok")

    def test_pro_rename_layer_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_rename_layer(layer="Parcels", new_name="Parcels_Renamed")
        self.assertEqual(result["status"], "error")

    def test_pro_rename_layer_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_rename_layer(layer="Roads", new_name="Roads_V2")
        mock_call.assert_called_once_with(
            "pro.renameLayer",
            {"layer": "Roads", "newName": "Roads_V2"},
        )

    def test_pro_get_layer_statistics_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_statistics(layer="Parcels", field="AREA")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_layer_statistics_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"min": 0, "max": 100, "mean": 50}):
            result = server.pro_get_layer_statistics(layer="Parcels", field="AREA")
        self.assertEqual(result["status"], "ok")

    def test_pro_get_layer_statistics_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_layer_statistics(layer="Parcels", field="AREA")
        self.assertEqual(result["status"], "error")

    def test_pro_get_layer_statistics_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_layer_statistics(layer="Roads", field="LENGTH")
        mock_call.assert_called_once_with(
            "pro.getLayerStatistics",
            {"layer": "Roads", "field": "LENGTH"},
        )

    def test_pro_project_geometry_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_project_geometry(x=500000, y=6900000, from_wkid=28356, to_wkid=4326)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_project_geometry_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"x": 152.94, "y": -27.47}):
            result = server.pro_project_geometry(x=500000, y=6900000, from_wkid=28356, to_wkid=4326)
        self.assertEqual(result["status"], "ok")

    def test_pro_project_geometry_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_project_geometry(x=500000, y=6900000, from_wkid=28356, to_wkid=4326)
        self.assertEqual(result["status"], "error")

    def test_pro_project_geometry_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_project_geometry(x=500000, y=6900000, from_wkid=28356, to_wkid=4326)
        mock_call.assert_called_once_with(
            "pro.projectGeometry",
            {"x": "500000", "y": "6900000", "fromWkid": "28356", "toWkid": "4326"},
        )

    # --- Phase 8: Layout & Map Automation ---

    def test_pro_add_layout_text_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_text(layout_name="Layout1", text="Hello", x=10, y=20)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_layout_text_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"elementName": "Text_abc"}):
            result = server.pro_add_layout_text(layout_name="Layout1", text="Hello", x=10, y=20)
        self.assertEqual(result["status"], "ok")

    def test_pro_add_layout_text_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_text(layout_name="Layout1", text="Hello", x=10, y=20)
        self.assertEqual(result["status"], "error")

    def test_pro_add_layout_text_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layout_text(
                layout_name="Layout1",
                text="Hello World",
                x=10,
                y=20,
                font_size=14,
                color_rgb="255,0,0",
            )
        mock_call.assert_called_once_with(
            "pro.addLayoutText",
            {
                "layoutName": "Layout1",
                "text": "Hello World",
                "x": "10",
                "y": "20",
                "fontSize": "14",
                "colorRgb": "255,0,0",
            },
        )

    def test_pro_add_layout_text_forwards_defaults(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layout_text(layout_name="L", text="Hi", x=0, y=0)
        mock_call.assert_called_once_with(
            "pro.addLayoutText",
            {"layoutName": "L", "text": "Hi", "x": "0", "y": "0", "fontSize": "12"},
        )

    def test_pro_add_layout_picture_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_picture(
                layout_name="L",
                image_path="C:\\img.png",
                x=0,
                y=0,
                width=100,
                height=50,
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_layout_picture_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_layout_picture(
                layout_name="L",
                image_path="C:\\img.png",
                x=0,
                y=0,
                width=100,
                height=50,
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_add_layout_picture_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_picture(
                layout_name="L",
                image_path="C:\\img.png",
                x=0,
                y=0,
                width=100,
                height=50,
            )
        self.assertEqual(result["status"], "error")

    def test_pro_add_layout_picture_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layout_picture(
                layout_name="MyLayout",
                image_path="D:\\photo.jpg",
                x=50,
                y=100,
                width=200,
                height=150,
            )
        mock_call.assert_called_once_with(
            "pro.addLayoutPicture",
            {
                "layoutName": "MyLayout",
                "imagePath": "D:\\photo.jpg",
                "x": "50",
                "y": "100",
                "width": "200",
                "height": "150",
            },
        )

    def test_pro_add_layout_legend_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_legend(layout_name="L", x=10, y=20)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_layout_legend_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_layout_legend(layout_name="L", x=10, y=20)
        self.assertEqual(result["status"], "ok")

    def test_pro_add_layout_legend_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_legend(layout_name="L", x=10, y=20)
        self.assertEqual(result["status"], "error")

    def test_pro_add_layout_legend_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layout_legend(
                layout_name="Layout1",
                x=5,
                y=5,
                map_frame_name="MapFrame1",
            )
        mock_call.assert_called_once_with(
            "pro.addLayoutLegend",
            {"layoutName": "Layout1", "x": "5", "y": "5", "mapFrameName": "MapFrame1"},
        )

    def test_pro_add_layout_legend_forwards_no_mapframe(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layout_legend(layout_name="L", x=0, y=0)
        mock_call.assert_called_once_with(
            "pro.addLayoutLegend",
            {"layoutName": "L", "x": "0", "y": "0"},
        )

    def test_pro_add_layout_north_arrow_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_north_arrow(
                layout_name="L",
                map_frame_name="MF1",
                x=10,
                y=20,
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_layout_north_arrow_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_layout_north_arrow(
                layout_name="L",
                map_frame_name="MF1",
                x=10,
                y=20,
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_add_layout_north_arrow_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_layout_north_arrow(
                layout_name="L",
                map_frame_name="MF1",
                x=10,
                y=20,
            )
        self.assertEqual(result["status"], "error")

    def test_pro_add_layout_north_arrow_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_layout_north_arrow(
                layout_name="MyLayout",
                map_frame_name="MapFrame1",
                x=15,
                y=25,
            )
        mock_call.assert_called_once_with(
            "pro.addLayoutNorthArrow",
            {"layoutName": "MyLayout", "mapFrameName": "MapFrame1", "x": "15", "y": "25"},
        )

    def test_pro_remove_layout_element_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_remove_layout_element(layout_name="L", element_name="Text1")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_remove_layout_element_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_remove_layout_element(layout_name="L", element_name="Text1")
        self.assertEqual(result["status"], "ok")

    def test_pro_remove_layout_element_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_remove_layout_element(layout_name="L", element_name="Text1")
        self.assertEqual(result["status"], "error")

    def test_pro_remove_layout_element_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_remove_layout_element(layout_name="Layout1", element_name="MyImage")
        mock_call.assert_called_once_with(
            "pro.removeLayoutElement",
            {"layoutName": "Layout1", "elementName": "MyImage"},
        )

    def test_pro_create_layout_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_layout(layout_name="L", width=297, height=210)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_layout_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_create_layout(layout_name="L", width=297, height=210)
        self.assertEqual(result["status"], "ok")

    def test_pro_create_layout_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_layout(layout_name="L", width=297, height=210)
        self.assertEqual(result["status"], "error")

    def test_pro_create_layout_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_layout(layout_name="A3_Layout", width=420, height=297, units="MM")
        mock_call.assert_called_once_with(
            "pro.createLayout",
            {"layoutName": "A3_Layout", "width": "420", "height": "297", "units": "MM"},
        )

    def test_pro_create_layout_forwards_default_units(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_layout(layout_name="L", width=100, height=50)
        mock_call.assert_called_once_with(
            "pro.createLayout",
            {"layoutName": "L", "width": "100", "height": "50", "units": "MM"},
        )

    def test_pro_create_map_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_map(map_name="NewMap")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_map_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_create_map(map_name="NewMap")
        self.assertEqual(result["status"], "ok")

    def test_pro_create_map_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_map(map_name="NewMap")
        self.assertEqual(result["status"], "error")

    def test_pro_create_map_forwards_op_and_args_defaults(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_map(map_name="MyMap")
        mock_call.assert_called_once_with(
            "pro.createMap",
            {"mapName": "MyMap", "mapType": "Map"},
        )

    def test_pro_create_map_forwards_with_scene_and_basemap(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_map(map_name="Scene1", map_type="LocalScene", basemap="Imagery")
        mock_call.assert_called_once_with(
            "pro.createMap",
            {"mapName": "Scene1", "mapType": "LocalScene", "basemap": "Imagery"},
        )

    def test_pro_add_basemap_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_basemap(basemap_name="Streets")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_basemap_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_basemap(basemap_name="Imagery")
        self.assertEqual(result["status"], "ok")

    def test_pro_add_basemap_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_basemap(basemap_name="Streets")
        self.assertEqual(result["status"], "error")

    def test_pro_add_basemap_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_basemap(basemap_name="Topographic")
        mock_call.assert_called_once_with(
            "pro.addBasemap",
            {"basemapName": "Topographic"},
        )

    # --- Phase 9: Advanced 3D & Visualization ---

    def test_pro_set_atmosphere_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_atmosphere(fog_density=50)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_atmosphere_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_atmosphere(fog_density=30, horizon_fog=True)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_atmosphere_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_atmosphere(fog_density=50)
        self.assertEqual(result["status"], "error")

    def test_pro_set_atmosphere_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_atmosphere(fog_density=75, horizon_fog=True, fog_color="100,150,200")
        mock_call.assert_called_once_with(
            "pro.setAtmosphere",
            {"fogDensity": "75", "horizonFog": "true", "fogColor": "100,150,200"},
        )

    def test_pro_set_atmosphere_forwards_defaults(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_atmosphere(fog_density=0)
        mock_call.assert_called_once_with(
            "pro.setAtmosphere",
            {"fogDensity": "0", "horizonFog": "false"},
        )

    def test_pro_set_sun_position_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_sun_position(azimuth=180, altitude=45)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_sun_position_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_sun_position(azimuth=90, altitude=30)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_sun_position_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_sun_position(azimuth=270, altitude=60)
        self.assertEqual(result["status"], "error")

    def test_pro_set_sun_position_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_sun_position(azimuth=135, altitude=22.5)
        mock_call.assert_called_once_with(
            "pro.setSunPosition",
            {"azimuth": "135", "altitude": "22.5"},
        )

    def test_pro_get_sun_position_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_sun_position()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_get_sun_position_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"azimuth": 180, "altitude": 45}):
            result = server.pro_get_sun_position()
        self.assertEqual(result["status"], "ok")

    def test_pro_get_sun_position_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_sun_position()
        self.assertEqual(result["status"], "error")

    def test_pro_get_sun_position_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_get_sun_position()
        mock_call.assert_called_once_with("pro.getSunPosition", None)

    def test_pro_explore_3d_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_explore_3d(x=500000, y=6900000, target_z=100, distance=500)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_explore_3d_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_explore_3d(x=500000, y=6900000, target_z=100, distance=500)
        self.assertEqual(result["status"], "ok")

    def test_pro_explore_3d_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_explore_3d(x=500000, y=6900000, target_z=100, distance=500)
        self.assertEqual(result["status"], "error")

    def test_pro_explore_3d_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_explore_3d(
                x=500000,
                y=6900000,
                target_z=200,
                distance=1000,
                heading_delta=45,
                pitch_delta=-10,
            )
        mock_call.assert_called_once_with(
            "pro.explore3D",
            {
                "x": "500000",
                "y": "6900000",
                "targetZ": "200",
                "distance": "1000",
                "headingDelta": "45",
                "pitchDelta": "-10",
            },
        )

    def test_pro_explore_3d_forwards_without_deltas(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_explore_3d(x=0, y=0, target_z=50, distance=300)
        mock_call.assert_called_once_with(
            "pro.explore3D",
            {"x": "0", "y": "0", "targetZ": "50", "distance": "300"},
        )

    def test_pro_set_layer_elevation_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_elevation(
                layer="Parcels",
                elevation_mode="absolute",
                z_offset=50,
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_layer_elevation_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_layer_elevation(
                layer="Parcels",
                elevation_mode="relative",
                z_offset=10,
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_set_layer_elevation_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_layer_elevation(
                layer="Parcels",
                elevation_mode="dra",
                z_offset=0,
            )
        self.assertEqual(result["status"], "error")

    def test_pro_set_layer_elevation_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_layer_elevation(
                layer="Buildings",
                elevation_mode="absolute",
                z_offset=100,
            )
        mock_call.assert_called_once_with(
            "pro.setLayerElevation",
            {"layer": "Buildings", "elevationMode": "absolute", "zOffset": "100"},
        )

    def test_pro_set_scene_background_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_scene_background(r=255, g=128, b=0)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_scene_background_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_scene_background(r=100, g=150, b=200)
        self.assertEqual(result["status"], "ok")

    def test_pro_set_scene_background_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_scene_background(r=0, g=0, b=0)
        self.assertEqual(result["status"], "error")

    def test_pro_set_scene_background_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_scene_background(r=50, g=100, b=150, background_type="none")
        mock_call.assert_called_once_with(
            "pro.setSceneBackground",
            {"r": "50", "g": "100", "b": "150", "backgroundType": "none"},
        )

    def test_pro_set_scene_background_forwards_default_type(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_scene_background(r=255, g=255, b=255)
        mock_call.assert_called_once_with(
            "pro.setSceneBackground",
            {"r": "255", "g": "255", "b": "255", "backgroundType": "color"},
        )

    # --- Phase 10: Project & Data Management ---

    def test_pro_create_feature_class_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_feature_class(
                gdb_path="C:\\data.gdb",
                name="NewFC",
                geometry_type="Polygon",
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_create_feature_class_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_create_feature_class(
                gdb_path="C:\\data.gdb",
                name="Points",
                geometry_type="Point",
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_create_feature_class_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_feature_class(
                gdb_path="C:\\data.gdb",
                name="BadFC",
                geometry_type="Polyline",
            )
        self.assertEqual(result["status"], "error")

    def test_pro_create_feature_class_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_feature_class(
                gdb_path="C:\\proj.gdb",
                name="Roads",
                geometry_type="Polyline",
                wkid=28356,
                fields_json='[{"fieldName":"Name","fieldType":"TEXT"}]',
            )
        mock_call.assert_called_once_with(
            "pro.createFeatureClass",
            {
                "gdbPath": "C:\\proj.gdb",
                "name": "Roads",
                "geometryType": "Polyline",
                "wkid": "28356",
                "fieldsJson": '[{"fieldName":"Name","fieldType":"TEXT"}]',
            },
        )

    def test_pro_create_feature_class_forwards_minimal(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_create_feature_class(
                gdb_path="C:\\data.gdb",
                name="FC",
                geometry_type="Point",
            )
        mock_call.assert_called_once_with(
            "pro.createFeatureClass",
            {"gdbPath": "C:\\data.gdb", "name": "FC", "geometryType": "Point"},
        )

    def test_pro_delete_feature_class_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_feature_class(path="C:\\data.gdb\\OldFC")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_delete_feature_class_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_delete_feature_class(path="C:\\data.gdb\\ToDelete")
        self.assertEqual(result["status"], "ok")

    def test_pro_delete_feature_class_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_delete_feature_class(path="C:\\data.gdb\\Missing")
        self.assertEqual(result["status"], "error")

    def test_pro_delete_feature_class_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_delete_feature_class(path="C:\\proj.gdb\\OldParcels")
        mock_call.assert_called_once_with(
            "pro.deleteFeatureClass",
            {"path": "C:\\proj.gdb\\OldParcels"},
        )

    def test_pro_save_project_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_save_project()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_save_project_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"path": "C:\\project.aprx"}):
            result = server.pro_save_project()
        self.assertEqual(result["status"], "ok")

    def test_pro_save_project_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_save_project()
        self.assertEqual(result["status"], "error")

    def test_pro_save_project_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_save_project()
        mock_call.assert_called_once_with("pro.saveProject", None)

    def test_pro_add_attribute_index_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_attribute_index(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_add_attribute_index_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_add_attribute_index(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "ok")

    def test_pro_add_attribute_index_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_add_attribute_index(layer="Parcels", field="ZONE")
        self.assertEqual(result["status"], "error")

    def test_pro_add_attribute_index_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_add_attribute_index(
                layer="Roads",
                field="ROAD_NAME",
                index_name="idx_road_name",
                unique=True,
            )
        mock_call.assert_called_once_with(
            "pro.addAttributeIndex",
            {
                "layer": "Roads",
                "field": "ROAD_NAME",
                "indexName": "idx_road_name",
                "unique": "true",
            },
        )

    def test_pro_search_address_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_search_address(address="123 Main St")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_search_address_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"resultCount": 2}):
            result = server.pro_search_address(address="Brisbane")
        self.assertEqual(result["status"], "ok")

    def test_pro_search_address_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_search_address(address="Nowhere")
        self.assertEqual(result["status"], "error")

    def test_pro_search_address_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_search_address(address="1 George St", max_results=5)
        mock_call.assert_called_once_with(
            "pro.searchAddress",
            {"address": "1 George St", "maxResults": "5"},
        )

    def test_pro_open_attribute_table_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_open_attribute_table(layer="Parcels")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_open_attribute_table_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_open_attribute_table(layer="Parcels")
        self.assertEqual(result["status"], "ok")

    def test_pro_open_attribute_table_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_open_attribute_table(layer="Parcels")
        self.assertEqual(result["status"], "error")

    def test_pro_open_attribute_table_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_open_attribute_table(layer="Roads")
        mock_call.assert_called_once_with(
            "pro.openAttributeTable",
            {"layer": "Roads"},
        )

    # --- Phase 11: Data Exchange ---

    def test_pro_export_to_csv_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_csv(layer="Parcels", output_path="C:\\out.csv")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_export_to_csv_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"rowCount": 100}):
            result = server.pro_export_to_csv(layer="Parcels", output_path="C:\\out.csv")
        self.assertEqual(result["status"], "ok")

    def test_pro_export_to_csv_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_csv(layer="Parcels", output_path="C:\\out.csv")
        self.assertEqual(result["status"], "error")

    def test_pro_export_to_csv_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_export_to_csv(layer="Roads", output_path="D:\\data\\roads.csv")
        mock_call.assert_called_once_with(
            "pro.exportToCsv",
            {"layer": "Roads", "outputPath": "D:\\data\\roads.csv"},
        )

    def test_pro_export_to_geo_json_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_geo_json(layer="Parcels", output_path="C:\\out.geojson")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_export_to_geo_json_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_export_to_geo_json(layer="Parcels", output_path="C:\\out.geojson")
        self.assertEqual(result["status"], "ok")

    def test_pro_export_to_geo_json_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_geo_json(layer="Parcels", output_path="C:\\out.geojson")
        self.assertEqual(result["status"], "error")

    def test_pro_export_to_geo_json_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_export_to_geo_json(layer="Buildings", output_path="D:\\buildings.geojson")
        mock_call.assert_called_once_with(
            "pro.exportToGeoJSON",
            {"layer": "Buildings", "outputPath": "D:\\buildings.geojson"},
        )

    def test_pro_import_csv_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_import_csv(
                csv_path="C:\\data.csv",
                gdb_path="C:\\proj.gdb",
                fc_name="Points",
                x_field="X",
                y_field="Y",
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_import_csv_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"rowCount": 50}):
            result = server.pro_import_csv(
                csv_path="C:\\data.csv",
                gdb_path="C:\\proj.gdb",
                fc_name="Points",
                x_field="Lon",
                y_field="Lat",
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_import_csv_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_import_csv(
                csv_path="C:\\data.csv",
                gdb_path="C:\\proj.gdb",
                fc_name="Points",
                x_field="X",
                y_field="Y",
            )
        self.assertEqual(result["status"], "error")

    def test_pro_import_csv_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_import_csv(
                csv_path="D\\survey.csv",
                gdb_path="C:\\proj.gdb",
                fc_name="SurveyPts",
                x_field="Easting",
                y_field="Northing",
                wkid=28356,
            )
        mock_call.assert_called_once_with(
            "pro.importCsv",
            {
                "csvPath": "D\\survey.csv",
                "gdbPath": "C:\\proj.gdb",
                "fcName": "SurveyPts",
                "xField": "Easting",
                "yField": "Northing",
                "wkid": "28356",
            },
        )

    def test_pro_export_to_shapefile_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_shapefile(layer="Parcels", output_path="C:\\parcels.shp")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_export_to_shapefile_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_export_to_shapefile(layer="Parcels", output_path="C:\\parcels.shp")
        self.assertEqual(result["status"], "ok")

    def test_pro_export_to_shapefile_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_shapefile(layer="Parcels", output_path="C:\\parcels.shp")
        self.assertEqual(result["status"], "error")

    def test_pro_export_to_shapefile_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_export_to_shapefile(layer="Roads", output_path="D:\\roads.shp")
        mock_call.assert_called_once_with(
            "pro.exportToShapefile",
            {"layer": "Roads", "outputPath": "D:\\roads.shp"},
        )

    def test_pro_export_to_kml_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_kml(layer="Parcels", output_path="C:\\parcels.kmz")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_export_to_kml_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_export_to_kml(layer="Parcels", output_path="C:\\parcels.kmz")
        self.assertEqual(result["status"], "ok")

    def test_pro_export_to_kml_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_to_kml(layer="Parcels", output_path="C:\\parcels.kmz")
        self.assertEqual(result["status"], "error")

    def test_pro_export_to_kml_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_export_to_kml(layer="Buildings", output_path="C:\\buildings.kmz")
        mock_call.assert_called_once_with(
            "pro.exportToKml",
            {"layer": "Buildings", "outputPath": "C:\\buildings.kmz"},
        )

    def test_pro_import_geo_json_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_import_geo_json(
                geojson_path="C:\\data.geojson",
                gdb_path="C:\\proj.gdb",
                fc_name="Imported",
            )
        self.assertEqual(result["status"], "unavailable")

    def test_pro_import_geo_json_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"rowCount": 25}):
            result = server.pro_import_geo_json(
                geojson_path="C:\\data.geojson",
                gdb_path="C:\\proj.gdb",
                fc_name="Imported",
            )
        self.assertEqual(result["status"], "ok")

    def test_pro_import_geo_json_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_import_geo_json(
                geojson_path="C:\\data.geojson",
                gdb_path="C:\\proj.gdb",
                fc_name="Imported",
            )
        self.assertEqual(result["status"], "error")

    def test_pro_import_geo_json_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_import_geo_json(
                geojson_path="D:\\buildings.geojson",
                gdb_path="C:\\proj.gdb",
                fc_name="Buildings",
            )
        mock_call.assert_called_once_with(
            "pro.importGeoJSON",
            {
                "geojsonPath": "D:\\buildings.geojson",
                "gdbPath": "C:\\proj.gdb",
                "fcName": "Buildings",
            },
        )

    # --- Phase 12: Pro GUI Automation ---

    def test_pro_show_message_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_show_message(message="Hello")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_show_message_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_show_message(message="Done", type="info")
        self.assertEqual(result["status"], "ok")

    def test_pro_show_message_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_show_message(message="Error")
        self.assertEqual(result["status"], "error")

    def test_pro_show_message_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_show_message(message="Processing complete", type="warning", title="Alert")
        mock_call.assert_called_once_with(
            "pro.showMessage",
            {"message": "Processing complete", "type": "warning", "title": "Alert"},
        )

    def test_pro_show_message_forwards_defaults(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_show_message(message="Hi")
        mock_call.assert_called_once_with(
            "pro.showMessage",
            {"message": "Hi", "type": "info"},
        )

    def test_pro_show_progress_dialog_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_show_progress_dialog(title="Working", message="Please wait")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_show_progress_dialog_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_show_progress_dialog(title="Processing", message="Analyzing...")
        self.assertEqual(result["status"], "ok")

    def test_pro_show_progress_dialog_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_show_progress_dialog(title="Failed", message="Error")
        self.assertEqual(result["status"], "error")

    def test_pro_show_progress_dialog_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_show_progress_dialog(title="Exporting", message="Writing features...")
        mock_call.assert_called_once_with(
            "pro.showProgressDialog",
            {"title": "Exporting", "message": "Writing features..."},
        )

    def test_pro_set_status_bar_progress_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_status_bar_progress(percent=50, message="Halfway")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_set_status_bar_progress_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_set_status_bar_progress(percent=75, message="Almost done")
        self.assertEqual(result["status"], "ok")

    def test_pro_set_status_bar_progress_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_status_bar_progress(percent=0, message="Start")
        self.assertEqual(result["status"], "error")

    def test_pro_set_status_bar_progress_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_set_status_bar_progress(percent=100, message="Complete")
        mock_call.assert_called_once_with(
            "pro.setStatusBarProgress",
            {"percent": "100", "message": "Complete"},
        )

    def test_pro_list_dockpanes_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_dockpanes()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_list_dockpanes_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"dockpaneCount": 9}):
            result = server.pro_list_dockpanes()
        self.assertEqual(result["status"], "ok")

    def test_pro_list_dockpanes_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_dockpanes()
        self.assertEqual(result["status"], "error")

    def test_pro_list_dockpanes_forwards_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_list_dockpanes()
        mock_call.assert_called_once_with("pro.listDockpanes", None)

    def test_pro_activate_ribbon_tab_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_activate_ribbon_tab(tab_id="Map")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_activate_ribbon_tab_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_activate_ribbon_tab(tab_id="Edit")
        self.assertEqual(result["status"], "ok")

    def test_pro_activate_ribbon_tab_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_activate_ribbon_tab(tab_id="View")
        self.assertEqual(result["status"], "error")

    def test_pro_activate_ribbon_tab_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_activate_ribbon_tab(tab_id="Analysis")
        mock_call.assert_called_once_with(
            "pro.activateRibbonTab",
            {"tabId": "Analysis"},
        )

    # --- Phase 13: Schema Management ---

    def test_pro_list_domains_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_domains(gdb_path=r"C:\data\test.gdb")
        assert result["status"] == "unavailable"

    def test_pro_list_domains_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_list_domains(gdb_path=r"C:\data\test.gdb")
        assert result["status"] == "ok"

    def test_pro_list_domains_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_domains(gdb_path=r"C:\data\test.gdb")
        assert result["status"] == "error"

    def test_pro_list_domains_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_list_domains(gdb_path=r"C:\data\test.gdb")
        mock_call.assert_called_once_with(
            "pro.listDomains",
            {"gdbPath": r"C:\data\test.gdb"},
        )

    def test_pro_create_domain_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_domain(
                gdb_path=r"C:\data\test.gdb",
                name="ZoneType",
                description="Zone type codes",
                field_type="String",
                coded_values='{"R":"Residential","C":"Commercial"}',
            )
        assert result["status"] == "unavailable"

    def test_pro_create_domain_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_create_domain(
                gdb_path=r"C:\data\test.gdb",
                name="ZoneType",
                description="Zone type codes",
                field_type="String",
                coded_values='{"R":"Residential","C":"Commercial"}',
            )
        assert result["status"] == "ok"

    def test_pro_create_domain_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_create_domain(
                gdb_path=r"C:\data\test.gdb",
                name="ZoneType",
                description="Zone type codes",
                field_type="String",
                coded_values='{"R":"Residential","C":"Commercial"}',
            )
        assert result["status"] == "error"

    def test_pro_create_domain_forwards_op_and_args(self) -> None:
        cv_json = '{"R":"Residential","C":"Commercial"}'
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_create_domain(
                gdb_path=r"C:\data\test.gdb",
                name="ZoneType",
                description="Zone type codes",
                field_type="String",
                coded_values=cv_json,
            )
        mock_call.assert_called_once_with(
            "pro.createDomain",
            {
                "gdbPath": r"C:\data\test.gdb",
                "name": "ZoneType",
                "description": "Zone type codes",
                "fieldType": "String",
                "codedValues": cv_json,
            },
        )

    def test_pro_assign_domain_to_field_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_assign_domain_to_field(
                layer="Parcels", field="ZoneCode", domain_name="ZoneType"
            )
        assert result["status"] == "unavailable"

    def test_pro_assign_domain_to_field_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_assign_domain_to_field(
                layer="Parcels", field="ZoneCode", domain_name="ZoneType"
            )
        assert result["status"] == "ok"

    def test_pro_assign_domain_to_field_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_assign_domain_to_field(
                layer="Parcels", field="ZoneCode", domain_name="ZoneType"
            )
        assert result["status"] == "error"

    def test_pro_assign_domain_to_field_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_assign_domain_to_field(
                layer="Parcels", field="ZoneCode", domain_name="ZoneType"
            )
        mock_call.assert_called_once_with(
            "pro.assignDomainToField",
            {"layer": "Parcels", "field": "ZoneCode", "domainName": "ZoneType"},
        )

    def test_pro_list_subtypes_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_subtypes(layer="Parcels")
        assert result["status"] == "unavailable"

    def test_pro_list_subtypes_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_list_subtypes(layer="Parcels")
        assert result["status"] == "ok"

    def test_pro_list_subtypes_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_subtypes(layer="Parcels")
        assert result["status"] == "error"

    def test_pro_list_subtypes_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_list_subtypes(layer="Parcels")
        mock_call.assert_called_once_with(
            "pro.listSubtypes",
            {"layer": "Parcels"},
        )

    def test_pro_set_subtype_field_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_subtype_field(layer="Parcels", field="ZoneCode")
        assert result["status"] == "unavailable"

    def test_pro_set_subtype_field_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_set_subtype_field(layer="Parcels", field="ZoneCode")
        assert result["status"] == "ok"

    def test_pro_set_subtype_field_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_subtype_field(layer="Parcels", field="ZoneCode")
        assert result["status"] == "error"

    def test_pro_set_subtype_field_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_set_subtype_field(layer="Parcels", field="ZoneCode")
        mock_call.assert_called_once_with(
            "pro.setSubtypeField",
            {"layer": "Parcels", "field": "ZoneCode"},
        )

    def test_pro_enable_attachments_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_enable_attachments(layer="Parcels")
        assert result["status"] == "unavailable"

    def test_pro_enable_attachments_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_enable_attachments(layer="Parcels")
        assert result["status"] == "ok"

    def test_pro_enable_attachments_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_enable_attachments(layer="Parcels")
        assert result["status"] == "error"

    def test_pro_enable_attachments_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_enable_attachments(layer="Parcels")
        mock_call.assert_called_once_with(
            "pro.enableAttachments",
            {"layer": "Parcels"},
        )

    # --- Phase 14: Advanced Geoprocessing ---

    def test_pro_list_toolboxes_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_toolboxes()
        assert result["status"] == "unavailable"

    def test_pro_list_toolboxes_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_list_toolboxes()
        assert result["status"] == "ok"

    def test_pro_list_toolboxes_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_list_toolboxes()
        assert result["status"] == "error"

    def test_pro_list_toolboxes_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_list_toolboxes()
        mock_call.assert_called_once_with(
            "pro.listToolboxes",
            {},
        )

    def test_pro_describe_tool_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_describe_tool(tool_name="Buffer_analysis")
        assert result["status"] == "unavailable"

    def test_pro_describe_tool_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_describe_tool(tool_name="Buffer_analysis")
        assert result["status"] == "ok"

    def test_pro_describe_tool_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_describe_tool(tool_name="Buffer_analysis")
        assert result["status"] == "error"

    def test_pro_describe_tool_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_describe_tool(tool_name="Buffer_analysis")
        mock_call.assert_called_once_with(
            "pro.describeTool",
            {"toolName": "Buffer_analysis"},
        )

    def test_pro_get_geoprocessing_history_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_geoprocessing_history()
        assert result["status"] == "unavailable"

    def test_pro_get_geoprocessing_history_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_get_geoprocessing_history()
        assert result["status"] == "ok"

    def test_pro_get_geoprocessing_history_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_geoprocessing_history(count=5)
        assert result["status"] == "error"

    def test_pro_get_geoprocessing_history_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_get_geoprocessing_history(count=10)
        mock_call.assert_called_once_with(
            "pro.getGeoprocessingHistory",
            {"count": "10"},
        )

    def test_pro_run_python_script_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_run_python_script(code="print('hello')")
        assert result["status"] == "unavailable"

    def test_pro_run_python_script_ok(self) -> None:
        code = "print('hello')"
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_run_python_script(code=code)
        assert result["status"] == "ok"

    def test_pro_run_python_script_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_run_python_script(code="print('hello')")
        assert result["status"] == "error"

    def test_pro_run_python_script_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_run_python_script(code="print('hello')", timeout_seconds=30)
        mock_call.assert_called_once_with(
            "pro.runPythonScript",
            {"code": "print('hello')", "timeoutSeconds": "30"},
        )

    def test_pro_set_environment_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_environment(key="workspace", value=r"C:\data\test.gdb")
        assert result["status"] == "unavailable"

    def test_pro_set_environment_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_set_environment(key="workspace", value=r"C:\data\test.gdb")
        assert result["status"] == "ok"

    def test_pro_set_environment_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_environment(key="cellSize", value="30")
        assert result["status"] == "error"

    def test_pro_set_environment_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_set_environment(key="overwriteOutput", value="true")
        mock_call.assert_called_once_with(
            "pro.setEnvironment",
            {"key": "overwriteOutput", "value": "true"},
        )

    def test_pro_get_environment_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_environment()
        assert result["status"] == "unavailable"

    def test_pro_get_environment_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_get_environment(key="workspace")
        assert result["status"] == "ok"

    def test_pro_get_environment_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_get_environment(key="cellSize")
        assert result["status"] == "error"

    def test_pro_get_environment_forwards_op_and_args_key(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_get_environment(key="workspace")
        mock_call.assert_called_once_with(
            "pro.getEnvironment",
            {"key": "workspace"},
        )

    def test_pro_get_environment_forwards_op_and_args_no_key(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_get_environment()
        mock_call.assert_called_once_with(
            "pro.getEnvironment",
            {},
        )


    # --- pro_reorder_layer ---

    def test_pro_reorder_layer_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_reorder_layer(layer="Parcels", index=2)
        assert result["status"] == "unavailable"

    def test_pro_reorder_layer_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_reorder_layer(layer="Roads", index=0)
        assert result["status"] == "ok"

    def test_pro_reorder_layer_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_reorder_layer(layer="Zones", index=3)
        assert result["status"] == "error"

    def test_pro_reorder_layer_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_reorder_layer(layer="Buildings", index=1)
        mock_call.assert_called_once_with(
            "pro.reorderLayer",
            {"layer": "Buildings", "index": "1"},
        )

    # --- pro_set_labels_enabled ---

    def test_pro_set_labels_enabled_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_labels_enabled(layer="Parcels", enabled=True)
        assert result["status"] == "unavailable"

    def test_pro_set_labels_enabled_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_set_labels_enabled(layer="Roads", enabled=False)
        assert result["status"] == "ok"

    def test_pro_set_labels_enabled_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_set_labels_enabled(layer="Zones", enabled=True)
        assert result["status"] == "error"

    def test_pro_set_labels_enabled_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_set_labels_enabled(layer="Buildings", enabled=True)
        mock_call.assert_called_once_with(
            "pro.setLabelsEnabled",
            {"layer": "Buildings", "enabled": "True"},
        )

    # --- pro_open_dockpane ---

    def test_pro_open_dockpane_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_open_dockpane(dockpane_id="Geoprocessing")
        assert result["status"] == "unavailable"

    def test_pro_open_dockpane_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_open_dockpane(dockpane_id="Catalog")
        assert result["status"] == "ok"

    def test_pro_open_dockpane_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_open_dockpane(dockpane_id="Unknown")
        assert result["status"] == "error"

    def test_pro_open_dockpane_forwards_op_and_args(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_open_dockpane(dockpane_id="Contents")
        mock_call.assert_called_once_with(
            "pro.openDockpane",
            {"dockpaneId": "Contents"},
        )

    # --- pro_export_layout_to_file ---

    def test_pro_export_layout_to_file_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_layout_to_file(
                layout_name="Layout1", output_path=r"C:\output.pdf"
            )
        assert result["status"] == "unavailable"

    def test_pro_export_layout_to_file_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_export_layout_to_file(
                layout_name="Layout1", output_path=r"C:\output.png", format="PNG", dpi=300
            )
        assert result["status"] == "ok"

    def test_pro_export_layout_to_file_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_export_layout_to_file(
                layout_name="Layout1", output_path=r"C:\output.pdf"
            )
        assert result["status"] == "error"

    def test_pro_export_layout_to_file_forwards_op_and_args_required(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_export_layout_to_file(
                layout_name="MyLayout", output_path=r"D:\maps\result.pdf"
            )
        mock_call.assert_called_once_with(
            "pro.exportLayoutToFile",
            {"layoutName": "MyLayout", "outputPath": r"D:\maps\result.pdf"},
        )

    def test_pro_export_layout_to_file_forwards_op_and_args_all(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_export_layout_to_file(
                layout_name="MyLayout",
                output_path=r"D:\maps\result.png",
                format="PNG",
                dpi=300,
            )
        mock_call.assert_called_once_with(
            "pro.exportLayoutToFile",
            {
                "layoutName": "MyLayout",
                "outputPath": r"D:\maps\result.png",
                "format": "PNG",
                "dpi": "300",
            },
        )

    # --- pro_fly_to_location ---

    def test_pro_fly_to_location_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_fly_to_location(x=100.0, y=200.0, z=500.0)
        assert result["status"] == "unavailable"

    def test_pro_fly_to_location_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"status": "ok", "data": {}}):
            result = server.pro_fly_to_location(
                x=100.0, y=200.0, z=500.0, heading=45.0, pitch=30.0, duration_seconds=2.5
            )
        assert result["status"] == "ok"

    def test_pro_fly_to_location_error(self) -> None:
        err = named_pipe.AddInOperationError("Test error")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_fly_to_location(x=100.0, y=200.0, z=500.0)
        assert result["status"] == "error"

    def test_pro_fly_to_location_forwards_op_and_args_required(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_fly_to_location(x=1.5, y=2.5, z=100.0)
        mock_call.assert_called_once_with(
            "pro.flyToLocation",
            {"x": "1.5", "y": "2.5", "z": "100.0"},
        )

    def test_pro_fly_to_location_forwards_op_and_args_all(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={}) as mock_call:
            server.pro_fly_to_location(
                x=1.5, y=2.5, z=100.0, heading=90.0, pitch=45.0, duration_seconds=3.0
            )
        mock_call.assert_called_once_with(
            "pro.flyToLocation",
            {
                "x": "1.5",
                "y": "2.5",
                "z": "100.0",
                "heading": "90.0",
                "pitch": "45.0",
                "durationSeconds": "3.0",
            },
        )

    # --- pro_ping_python_runtime ---

    def test_pro_ping_python_runtime_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_ping_python_runtime()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_ping_python_runtime_ok(self) -> None:
        with patch(
            "arcgis_mcp_server.call_addin",
            return_value={"python_version": "3.13.7", "arcpy_version": "3.6.3"},
        ):
            result = server.pro_ping_python_runtime()
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["data"]["python_version"], "3.13.7")

    def test_pro_ping_python_runtime_error(self) -> None:
        err = named_pipe.AddInOperationError("bad op")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_ping_python_runtime()
        self.assertEqual(result["status"], "error")

    def test_pro_ping_python_runtime_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_ping_python_runtime()
        mock_call.assert_called_once_with("pro.pingPythonRuntime", {})

    # --- pro_plugin_batch_export ---

    def test_pro_plugin_batch_export_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_plugin_batch_export("csv", "C:/out")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_plugin_batch_export_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"done": True}):
            result = server.pro_plugin_batch_export("geojson", "C:/out")
        self.assertEqual(result["status"], "ok")

    def test_pro_plugin_batch_export_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_plugin_batch_export("kml", "C:/out")
        mock_call.assert_called_once_with("pro.plugin.batchExport", {"targetFormat": "kml", "outputDir": "C:/out"})

    # --- pro_plugin_coordinate_capture ---

    def test_pro_plugin_coordinate_capture_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_plugin_coordinate_capture()
        self.assertEqual(result["status"], "unavailable")

    def test_pro_plugin_coordinate_capture_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"centerX": 100.0, "centerY": 200.0}):
            result = server.pro_plugin_coordinate_capture(target_wkid=4326)
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["data"]["centerX"], 100.0)

    def test_pro_plugin_coordinate_capture_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_plugin_coordinate_capture()
        mock_call.assert_called_once_with("pro.plugin.coordinateCapture", {})

    def test_pro_plugin_coordinate_capture_with_wkid(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_plugin_coordinate_capture(target_wkid=28356)
        mock_call.assert_called_once_with("pro.plugin.coordinateCapture", {"targetWkid": "28356"})

    # --- pro_plugin_feature_inspector ---

    def test_pro_plugin_feature_inspector_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_plugin_feature_inspector("Parcels", 42)
        self.assertEqual(result["status"], "unavailable")

    def test_pro_plugin_feature_inspector_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"oid": 42, "geometryType": "Polygon"}):
            result = server.pro_plugin_feature_inspector("Parcels", 42)
        self.assertEqual(result["status"], "ok")

    def test_pro_plugin_feature_inspector_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_plugin_feature_inspector("Roads", 7)
        mock_call.assert_called_once_with("pro.plugin.featureInspector", {"layer": "Roads", "oid": "7"})

    # --- pro_plugin_query_builder ---

    def test_pro_plugin_query_builder_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_plugin_query_builder("Parcels", "ZONE", "Residential")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_plugin_query_builder_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"matchCount": 15}):
            result = server.pro_plugin_query_builder("Parcels", "ZONE", "Residential", operator_name="contains")
        self.assertEqual(result["status"], "ok")

    def test_pro_plugin_query_builder_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            server.pro_plugin_query_builder("Buildings", "HEIGHT", "10", operator_name="gt")
        mock_call.assert_called_once_with("pro.plugin.queryBuilder", {"layer": "Buildings", "field": "HEIGHT", "value": "10", "operator": "gt"})

    # --- pro_plugin_field_calculator ---

    def test_pro_plugin_field_calculator_unavailable(self) -> None:
        err = named_pipe.AddInNotAvailableError("Not found")
        with patch("arcgis_mcp_server.call_addin", side_effect=err):
            result = server.pro_plugin_field_calculator("Parcels", "AreaHa", "!Shape_Area! / 10000")
        self.assertEqual(result["status"], "unavailable")

    def test_pro_plugin_field_calculator_ok(self) -> None:
        with patch("arcgis_mcp_server.call_addin", return_value={"rowCount": 250}):
            result = server.pro_plugin_field_calculator("Parcels", "AreaHa", "!Shape_Area! / 10000")
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["data"]["rowCount"], 250)

    def test_pro_plugin_field_calculator_forwards_correct_op(self) -> None:
        with patch("arcgis_mcp_server.call_addin") as mock_call:
            expr = "!Population! / !Area!"
            server.pro_plugin_field_calculator("Blocks", "Density", expr)
        mock_call.assert_called_once_with("pro.plugin.fieldCalculator", {"layer": "Blocks", "field": "Density", "expression": expr})

    # --- arcgis_name_resolver ---

    def test_resolve_layer_name_exact_match(self) -> None:
        from arcgis_name_resolver import resolve_layer_name

        result = resolve_layer_name("Parcels", ["Parcels", "Roads", "Hydrology"])
        self.assertEqual(result, "Parcels")

    def test_resolve_layer_name_case_insensitive(self) -> None:
        from arcgis_name_resolver import resolve_layer_name

        result = resolve_layer_name("parcels", ["Parcels", "Roads"])
        self.assertEqual(result, "Parcels")

    def test_resolve_layer_name_fuzzy_match(self) -> None:
        from arcgis_name_resolver import resolve_layer_name

        result = resolve_layer_name("parcel", ["Parcels_2024", "Roads", "Hydrology"])
        self.assertEqual(result, "Parcels_2024")

    def test_resolve_layer_name_no_match(self) -> None:
        from arcgis_name_resolver import resolve_layer_name

        result = resolve_layer_name("buildings", ["Parcels", "Roads"])
        self.assertIsNone(result)

    def test_resolve_field_name_exact(self) -> None:
        from arcgis_name_resolver import resolve_field_name

        result = resolve_field_name("ZONE", ["OBJECTID", "ZONE", "AREA"])
        self.assertEqual(result, "ZONE")

    def test_resolve_field_name_fuzzy(self) -> None:
        from arcgis_name_resolver import resolve_field_name

        result = resolve_field_name("zone_code", ["ZONE_CODE_2024", "AREA", "SHAPE"])
        self.assertEqual(result, "ZONE_CODE_2024")

    def test_resolve_with_details_exact(self) -> None:
        from arcgis_name_resolver import resolve_layer_with_details
        layers = [{"Name": "Parcels_2024"}, {"Name": "Roads"}]
        with patch("arcgis_name_resolver.call_addin", return_value=layers):
            result = resolve_layer_with_details("Parcels_2024")
        self.assertEqual(result["resolved_name"], "Parcels_2024")
        self.assertEqual(result["confidence"], 1.0)

    def test_resolve_with_details_fuzzy(self) -> None:
        from arcgis_name_resolver import resolve_layer_with_details
        layers = [{"Name": "Parcels_2024"}, {"Name": "Roads"}]
        with patch("arcgis_name_resolver.call_addin", return_value=layers):
            result = resolve_layer_with_details("parcels")
        self.assertEqual(result["resolved_name"], "Parcels_2024")

    def test_resolve_with_details_no_match(self) -> None:
        from arcgis_name_resolver import resolve_layer_with_details
        layers = [{"Name": "Parcels_2024"}, {"Name": "Roads"}]
        with patch("arcgis_name_resolver.call_addin", return_value=layers):
            result = resolve_layer_with_details("buildings")
        self.assertIsNone(result["resolved_name"])
        self.assertIn("candidates", result)

    def test_resolve_with_details_empty_map(self) -> None:
        from arcgis_name_resolver import resolve_layer_with_details
        with patch("arcgis_name_resolver.call_addin", return_value=[]):
            result = resolve_layer_with_details("anything")
        self.assertIsNone(result["resolved_name"])

    def test_resolve_with_details_unavailable(self) -> None:
        from arcgis_name_resolver import resolve_layer_with_details, AddInNotAvailableError
        with patch("arcgis_name_resolver.call_addin", side_effect=AddInNotAvailableError("No Add-In")):
            result = resolve_layer_with_details("anything")
        self.assertIsNone(result["resolved_name"])
        self.assertIn("error", result)

    def test_pro_resolve_layer_tool(self) -> None:
        from arcgis_name_resolver import resolve_layer_with_details
        layers = [{"Name": "Parcels_2024"}, {"Name": "Roads_Main"}]
        with patch("arcgis_name_resolver.call_addin", return_value=layers):
            result = server.pro_resolve_layer("parcels")
        self.assertEqual(result.get("resolved_name"), "Parcels_2024")

    # --- arcgis_workflows ---

    def test_execute_macro_all_steps_succeed(self) -> None:
        from arcgis_workflows import execute_macro

        macro = {
            "name": "test",
            "steps": [
                {"op": "pro.getActiveMapName", "args": {}, "name": "get_map"},
                {"op": "pro.listLayers", "args": {}, "name": "list_layers"},
            ],
        }
        with patch("arcgis_workflows.call_addin", return_value={"done": True}):
            result = execute_macro(macro)
        self.assertEqual(result["status"], "ok")
        self.assertTrue(result["all_succeeded"])
        self.assertEqual(result["completed"], 2)

    def test_execute_macro_step_fails_stops(self) -> None:
        from arcgis_workflows import execute_macro
        from arcgis_mcp_named_pipe import AddInOperationError

        macro = {
            "name": "failing",
            "steps": [
                {"op": "pro.getActiveMapName", "args": {}},
                {"op": "pro.badOp", "args": {}},
                {"op": "pro.listLayers", "args": {}},
            ],
        }
        side_effects = [
            {"name": "Map"},
            AddInOperationError("bad op"),
        ]
        with patch("arcgis_workflows.call_addin", side_effect=side_effects):
            result = execute_macro(macro)
        self.assertEqual(result["status"], "error")
        self.assertEqual(result["completed"], 2)
        self.assertFalse(result["all_succeeded"])
        self.assertEqual(result["results"][1]["status"], "error")

    def test_execute_macro_addin_unavailable(self) -> None:
        from arcgis_workflows import execute_macro
        from arcgis_mcp_named_pipe import AddInNotAvailableError

        macro = {
            "name": "unavailable",
            "steps": [
                {"op": "pro.ping", "args": {}},
            ],
        }
        with patch("arcgis_workflows.call_addin", side_effect=AddInNotAvailableError("No pipe")):
            result = execute_macro(macro)
        self.assertEqual(result["status"], "error")
        self.assertEqual(result["results"][0]["status"], "unavailable")

    def test_execute_macro_no_steps(self) -> None:
        from arcgis_workflows import execute_macro

        result = execute_macro({"name": "empty", "steps": []})
        self.assertEqual(result["status"], "error")
        self.assertIn("no steps", result.get("message", "").lower())

    def test_list_builtin_macros(self) -> None:
        from arcgis_workflows import list_builtin_macros

        macros = list_builtin_macros()
        self.assertGreaterEqual(len(macros), 4)
        names = [m["name"] for m in macros]
        self.assertIn("Select and Zoom", names)
        self.assertIn("Export All Layers", names)
        self.assertIn("Inspect Feature", names)
        self.assertIn("Capture and Project", names)

    def test_load_macro_by_name(self) -> None:
        from arcgis_workflows import load_macro

        macro = load_macro("Select and Zoom")
        self.assertIsNotNone(macro)
        self.assertEqual(macro["name"], "Select and Zoom")
        self.assertIn("steps", macro)

    def test_load_macro_by_stem(self) -> None:
        from arcgis_workflows import load_macro

        macro = load_macro("select_and_zoom")
        self.assertIsNotNone(macro)
        self.assertIn("steps", macro)

    def test_load_macro_not_found(self) -> None:
        from arcgis_workflows import load_macro

        result = load_macro("nonexistent_macro_xyz")
        self.assertIsNone(result)

    def test_pro_run_macro_with_inline_json(self) -> None:
        macro_json = '{"name":"inline","steps":[{"op":"pro.ping","args":{}}]}'
        with patch("arcgis_workflows.call_addin", return_value={"pong": "addin"}):
            result = server.pro_run_macro(macro_json)
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["completed"], 1)

    def test_pro_run_macro_with_builtin_name(self) -> None:
        with patch("arcgis_workflows.call_addin", return_value={"done": True}):
            result = server.pro_run_macro("Select and Zoom")
        self.assertEqual(result["status"], "ok")

    def test_pro_run_macro_not_found(self) -> None:
        result = server.pro_run_macro("nonexistent_macro_xyz")
        self.assertEqual(result["status"], "error")
        self.assertIn("not found", result.get("message", ""))

    def test_pro_list_macros(self) -> None:
        result = server.pro_list_macros()
        self.assertEqual(result["status"], "ok")
        self.assertGreaterEqual(result["macro_count"], 4)


if __name__ == "__main__":
    unittest.main()
