# Codebase Improvement Plan

## Overview
This plan addresses issues identified during the codebase review of the ArcGIS Pro Bridge MCP Server. Changes are ordered by priority.

---

## 1. Fix Code Injection in Geoprocessing Templates (HIGH)

### File: `arcgis_script_templates.py`

**Problem**: `build_buffer_features_code()` and `build_clip_features_code()` use f-strings to embed user input directly into generated Python code. While `!r` calls `repr()` which provides escaping, the f-string pattern is inconsistent with the safer placeholder pattern used elsewhere in the codebase.

**Fix**: Convert both functions to use the `__PLACEHOLDER__` + `.replace()` pattern, matching `build_project_layers_code()` and `build_project_context_code()`.

### Changes for `build_buffer_features_code()`:
- Replace f-string with a plain string containing placeholders: `__IN_FEATURES__`, `__OUT_FEATURE_CLASS__`, `__BUFFER_DISTANCE_OR_FIELD__`, `__DISSOLVE_OPTION__`, `__DISSOLVE_FIELD__`, `__METHOD__`
- Use `.replace()` with `repr()` for each placeholder

### Changes for `build_clip_features_code()`:
- Replace f-string with a plain string containing placeholders: `__IN_FEATURES__`, `__CLIP_FEATURES__`, `__OUT_FEATURE_CLASS__`, `__CLUSTER_TOLERANCE__`
- Use `.replace()` with `repr()` for each placeholder

---

## 2. Fix `build_gdb_schema_code()` to Use Placeholder Pattern (HIGH)

### File: `arcgis_script_templates.py`

**Problem**: `build_gdb_schema_code(gdb_path)` uses an f-string to embed `gdb_path` directly into the generated script:
```python
arcpy.env.workspace = {gdb_path!r}
```

This is fragile with paths containing backslashes or special characters.

**Fix**: Convert to use the placeholder pattern:
- Replace `{gdb_path!r}` with `__GDB_PATH__`
- Add `.replace("__GDB_PATH__", repr(gdb_path))` at the end
- Also replace the hardcoded `gdb_path` in `arcpy.env.workspace = ...` line

---

## 3. Wire `validate_path()` into Tool Entry Points (HIGH)

### File: `arcgis_mcp_server.py`

**Problem**: `validate_path()` in `arcgis_runtime_utils.py` is defined but never called. Path allowlisting via `ARCGIS_MCP_ALLOWED_PATHS` is not enforced.

**Fix**: Add path validation to tools that accept file paths:
- `buffer_features()`: validate `in_features` and `out_feature_class`
- `clip_features()`: validate `in_features`, `clip_features_path`, and `out_feature_class`
- `inspect_gdb()`: validate `gdb_path`
- `list_gis_layers()`: validate `project_path`
- `inspect_project_context()`: validate `project_path`
- `execute_arcpy_code()`: validate `workspace` and `project_path`

Import `validate_path` from `arcgis_runtime_utils` and wrap path parameters before use. Handle `ValueError` exceptions by returning an error response.

---

## 4. Integrate or Remove Dead `arcgis_mcp_logging.py` (MEDIUM)

### File: `arcgis_mcp_logging.py`, `arcgis_mcp_server.py`

**Problem**: The entire logging module is dead code — never imported or used anywhere.

**Option A (Recommended)**: Remove the file entirely. The MCP framework already handles logging, and this module adds no value.

**Option B**: Integrate it by:
1. Importing `get_logger()` and `get_operation_logger()` in `arcgis_mcp_server.py`
2. Adding logging calls to each tool entry/exit
3. Using `OperationLogger` for timing geoprocessing operations

Given the module's simplicity and the fact that MCP has built-in telemetry, **Option A (removal)** is recommended.

---

## 5. Use `tempfile.mkdtemp()` for Temp Workspace (MEDIUM)

### File: `arcgis_runtime_utils.py`

**Problem**: `create_temp_workspace()` uses `uuid4().hex[:8]` for directory names (only 2^32 possible values).

**Fix**: Replace the UUID-based approach with `tempfile.mkdtemp()`:
```python
def create_temp_workspace(prefix: str, root: str | None = None) -> Path:
    base_dir = Path(root) if root else Path(tempfile.gettempdir())
    base_dir.mkdir(parents=True, exist_ok=True)
    temp_path = tempfile.mkdtemp(prefix=prefix, dir=str(base_dir))
    return Path(temp_path)
```

This uses the OS's secure temp directory creation with proper permissions.

---

## 6. Fix `_iter_registry_install_dirs()` (LOW)

### File: `arcgis_mcp_server.py`

**Problem**: Line 149 hardcodes `0` for `KEY_READ`:
```python
registry_views = [0]
```

**Fix**: Use the actual constant:
```python
registry_views = [getattr(winreg, "KEY_READ", 0)]
```

---

## 7. Add Python 3.11 to CI Matrix (LOW)

### File: `.github/workflows/ci.yml`

**Problem**: CI only tests Python 3.12, but `pyproject.toml` declares `>=3.11`.

**Fix**: Add a matrix strategy:
```yaml
strategy:
  matrix:
    python-version: ["3.11", "3.12"]
```

Update the `setup-python` step to use `python-version: ${{ matrix.python-version }}`.

---

## 8. Update Tests for Template Changes

### File: `tests/test_arcgis_mcp_server.py`

After changing the template functions, verify existing tests still pass. The tests mock `run_in_arcgis_env` so they should be unaffected, but add a test to verify the generated code strings are valid Python:

```python
def test_buffer_features_code_generates_valid_python(self):
    code = build_buffer_features_code(
        in_features="roads",
        out_feature_class="roads_buf",
        buffer_distance_or_field="50 Meters",
        dissolve_option="NONE",
        dissolve_field=None,
        method="PLANAR",
    )
    compile(code, "<test>", "exec")  # Should not raise
```

---

## Execution Order

1. Fix template code injection (#1, #2) — can be done together
2. Wire path validation (#3)
3. Remove dead logging module (#4)
4. Fix temp workspace (#5)
5. Fix registry constant (#6)
6. Update CI (#7)
7. Add template validation tests (#8)
8. Run full test suite + lint
