# Static MCP tools → WebSocket bridge commands (P3/P4 mapping)

Read-only mapping. Sources: `mcp-server/server.py` (`TOOLS` 61, `tool_call`), `RevitBridge.Revit/BridgeCommandHost.cs` (55 registered WS commands), `src/RevitBridge.Core/BridgeRouter.cs` (protocol types `hello`/`ping`/`commands.list`/`call`), `RevitBridge.Revit/McpHttpServer.cs`, `src/RevitBridge.Core/LegacyHttpPolicy.cs`, `manifests/heatloss.bridge.manifest.json`, `ExtendedOps.cs`/`ModelingOps.cs`.

Полные данные — `STATIC-TO-WS-MAP.json` (плоский массив из 61 записи, сортировка по `name`).

> **P1 note (2026-11-22):** это сырая карта соответствий, а не отчёт о фактической миграции. `status=migrated` означает, что у операции есть WS/protocol-аналог, но static tool мог ещё не быть переведён. После P1 фактически переведены 6 static tools (`ping/open/close/doc_info/list_levels/list_rooms`); 31 static write tool без WS-команды блокируется фасадом с `legacy_write_disabled`. Счётчики read/write в этом файле — эвристика по именам, они могут отличаться от фактической policy-классификации `_WRITE_TOKENS`.

## Легенда

- `current_transport`: `GET`/`POST` — вызов `_api(...)` в `tool_call`; `WS` — уже идёт через `_bridge_call`; `WS+GET` — композит (bridge_status).
- `status`: `migrated` — WS-команда (или protocol type) уже есть; `needs_ws_command` — write без WS-дубля; `read_only_http` — read, можно оставить; `legacy_tcp` — относится к DwgParser/TCP (здесь 0).

## Counts

| Метрика | Значение |
|---|---:|
| static_total | 61 |
| read | 26 |
| write | 35 |
| migrated | 19 |
| needs_ws_command | 26 |
| read_only_http | 16 |
| legacy_tcp | 0 |
| ws_commands_total | 55 |
| ws_without_static_double | 44 |

## Приоритет P3/P4: write static tools без WS-команды (26)

| static tool | transport | suggested domain.command | почему нет дубля |
|---|---|---|---|
| `revit_backup_document` | POST | `revit.backup_document` | POST → 403; revit.save_document(path) делает Save As, а не backup-копию. |
| `revit_connect_pipes` | POST | `mep.connect_pipes` | POST → 403; pipeline.connect_terminal_duct — только terminal→duct. |
| `revit_copy_element` | POST | `model.copy_element` | POST → 403; WS-команды нет. |
| `revit_create_ceiling` | POST | `model.create_ceiling` | POST → 403; WS-команды нет. |
| `revit_create_floor` | POST | `model.create_floor` | POST → 403; WS-команды нет. |
| `revit_create_grid` | POST | `model.create_grid` | POST → 403; WS-команды нет. |
| `revit_create_level` | POST | `model.create_level` | POST /create_level → 403; WS-команды нет. Приоритет P3/P4. |
| `revit_create_roof` | POST | `model.create_roof` | POST → 403; WS-команды нет. |
| `revit_create_room` | POST | `model.create_room` | POST → 403; pipeline.create_spaces создаёт MEP-пространства, не помещения. |
| `revit_create_schedule` | POST | `spec.create_schedule` | POST → 403; WS-команды нет (dwg_revit_create_schedule — DwgParser, не этот WS-мост). |
| `revit_create_view_3d` | POST | `view.create_3d` | POST → 403; pipeline.create_plan_view — только план этажа; dwg_revit_create_3d_view — другой сервер (DwgParser stdio-прокси). |
| `revit_create_wall_type` | POST | `model.create_wall_type` | POST → 403; WS-команды нет. |
| `revit_cut_opening` | POST | `model.cut_opening` | POST → 403; WS-команды нет. |
| `revit_delete_elements` | POST | `model.delete_elements` | POST → 403; pipeline.delete_views/delete_spaces — только конкретные типы, не общая очистка. |
| `revit_dry_run` | POST | `audit.dry_run` | POST → 403, хотя операция read-only; нужен WS-дубль. |
| `revit_duplicate_schedule` | POST | `spec.duplicate_schedule` | POST → 403; WS-команды нет (pipeline.create_level_explication дублирует только экпликацию). |
| `revit_export_schedule` | POST | `spec.export_schedule` | POST → 403; pipeline.export_sheet_pdf — только листы. |
| `revit_load_family` | POST | `model.load_family` | POST → 403; WS-команды нет. |
| `revit_mirror_element` | POST | `model.mirror_element` | POST → 403; WS-команды нет. |
| `revit_modify_schedule` | POST | `spec.modify_schedule` | POST → 403; WS-команды нет. |
| `revit_move_element` | POST | `model.move_element` | POST → 403; WS-команды нет. |
| `revit_place_family` | POST | `model.place_family` | POST → 403; pipeline.place_air_terminal — только воздухоприёмники, не универсальная вставка семейства. |
| `revit_refresh_schedule` | POST | `spec.refresh_schedule` | POST → 403; WS-команды нет. |
| `revit_rotate_element` | POST | `model.rotate_element` | POST → 403; WS-команды нет. |
| `revit_set_active_view` | POST | `view.set_active` | POST → 403; WS-команды нет. |
| `revit_undo_last` | POST | `revit.undo_last` | POST → 403; WS-команды нет; в реализации best-effort. |

## WS-команды без static-дубля (44)

Не обязательно проблема: это в основном pipeline/sheet/audit-операции, которых не было в плоском static-наборе. WS-дубли считаются по `ws_command` в JSON (11 из 55 уже использованы static-инструментами).

```text
revit.save_document
revit.new_project_from_template
revit.current_view_info
revit.current_view_elements
revit.selected_elements
revit.tag_walls
revit.tag_rooms
revit.model_statistics
revit.material_quantities
revit.ai_element_filter
revit.operate_element
revit.color_splash
revit.create_dimensions
revit.create_structural_framing
pipeline.list_views
pipeline.list_sheets
pipeline.clear_model
pipeline.set_link_room_bounding
pipeline.regenerate
pipeline.link_revit
pipeline.create_spaces
pipeline.delete_spaces
pipeline.create_plan_view
pipeline.delete_views
pipeline.connect_terminal_duct
pipeline.create_system
pipeline.place_air_terminal
pipeline.validate_system
pipeline.set_view_scale
pipeline.set_view_crop
pipeline.audit_model
pipeline.purge_model_content
pipeline.reset_energy_analysis
pipeline.create_sheet
pipeline.place_view
pipeline.place_schedule
pipeline.layout_sheet
pipeline.compose_sheet
pipeline.list_title_blocks
pipeline.inspect_titleblock_family
pipeline.collect_spec
pipeline.debug_spaces
pipeline.create_level_explication
pipeline.export_sheet_pdf
```

## Выводы

1. **Все legacy HTTP POST сегодня мертвы**: `LegacyHttpPolicy.IsReadOnlyRequest` пропускает только GET, поэтому write-инструменты получают `403 legacy_write_disabled` ещё до вызова `McpOps`. В текущей рабочей копии P1 дополнительно блокирует их уже на фасаде MCP (`tool_call` → `legacy_write_error`), не делая HTTP-запроса; 6 инструментов переведены на WS через `WS_STATIC_MAP`. Это главный аргумент за перенос оставшихся write на WS.
2. **19 инструментов уже имеют WS-дубль**, в том числе 4 bridge/call-обёртки (protocol types) + композит bridge_status, 6 name-дублей (`revit.*`) и 8 семантических дублей (`heatloss.*`, `pipeline.get_schedule`, `revit.element_parameters`, `pipeline.set_parameter`, `pipeline.list_systems`, `revit.create_line_element` ×3).
3. **`revit.create_line_element` — полный, но скрытый дубль** для create_wall/create_pipe/create_duct: реализация читает `typeId/baseLevel/topLevel/flip`, `systemType/pipeType/levelId/diameterMm`, `systemType/ductType/levelId/widthMm/heightMm`, однако `argsSchema` объявляет только `kind/startMm/endMm`. Перед миграцией нужно расширить схему (или дать три явные команды).
4. **`revit_run_heatloss` переносится без новой команды**: манифест даёт 8 команд `heatloss.*` (загрузка условная — нужен `MainAppHeatLoss.dll`).
5. **16 read-инструментов можно оставить на HTTP**; частичное функциональное покрытие есть у `revit.ai_element_filter` (query_elements) и `revit.element_parameters` (get_element), но это не полные дубли.
6. `legacy_tcp = 0`: инструменты `dwg_*` — динамический stdio-прокси DwgParser, а не static `TOOLS`.
7. Карта отражена по состоянию рабочей копии на момент генерации (в репозитории параллельно идёт P1: `mcp-server/server.py` уже содержит `WS_STATIC_MAP` и блокировку write на фасаде).
