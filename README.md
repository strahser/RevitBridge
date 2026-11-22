# RevitBridge — мост OpenCode ↔ Revit 2024

Позволяет агенту (OpenCode) самостоятельно открыть `.rvt`, прочитать документ/помещения/уровни,
выполнить команды плагинов и собрать лист — без ручных кликов в Revit.

## Архитектура

```
OpenCode --stdio--> mcp-server/server.py --WebSocket ws://127.0.0.1:17871/bridge--> RevitBridge.Revit --> Revit API
```

* **RevitBridge.Revit** (C#, net48, Revit 2024) — `IExternalApplication`, `HttpListener` на
  `127.0.0.1:17871` (порт — `REVIT_BRIDGE_PORT`, fallback `REVIT_MCP_PORT`). HTTP `GET /health` +
  WebSocket `/bridge` (protocol v1); все вызовы Revit API идут на главном потоке через `ExternalEvent`.
* **mcp-server/server.py** (Python, stdlib; дочернему DwgParser нужен `mcp>=2.0`) — stdio MCP-фасад:
  61 static tool, до 126 dynamic WS-команд (55 built-in + 71 из манифестов) и 106 `dwg_*` из DwgParser.
  Read-only по умолчанию. Легаси HTTP принимает только `GET`; все `POST` → `403 legacy_write_disabled`.

## Установка (один раз)

1. Собрать аддин:
   `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" RevitBridge.Revit\RevitBridge.Revit.csproj /t:Restore,Build /p:Configuration=Release.R24 /p:Platform="Any CPU"`
2. Разложить в Revit:
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install_addin.ps1`
3. Python-зависимости: `pip install -r mcp-server\requirements.txt` (нужен `websocket-client`).
4. Подключить к OpenCode — добавить в `opencode.json` (пример в `opencode.json.example`):
   ```json
   "mcp": {
     "revit-bridge": {
       "type": "local",
       "command": ["python", "-X", "utf8", "D:\\Projects\\01-bim\\RevitBridge\\mcp-server\\server.py"],
       "enabled": true
     }
   }
   ```
5. Перезапустить Revit 2024 (подхватит `RevitBridge.addin`), затем открыть модель через
   `revit_open_document`.

## Инструменты

По умолчанию `MCP_TOOLS_MODE=compact`: в `tools/list` только 7 прямых инструментов.

| Инструмент | Назначение |
|---|---|
| `bridge_status` | здоровье моста и активный документ |
| `revit_doc_info` | заголовок/путь/счётчики документа |
| `revit_open_document` | открыть `.rvt`/`.rte` (WS) |
| `tools_search` | поиск по каталогу: query/domain/read_only |
| `tools_describe` | точная схема и аннотации инструмента |
| `tools_call` | вызов любого инструмента по имени |
| `tools_health` | состояние провайдеров (`revit-ws`, `dwgparser`, `static`) |

Полный legacy-список (`MCP_TOOLS_MODE=full`) — **297** имён: 61 static + 4 meta + 126 dynamic + 106 `dwg_*`.

### Профили и запись

```powershell
# read-only (по умолчанию)
$env:MCP_PROFILE = "revit-read"

# запись через gateway; C# всё равно требует AllowWrites
$env:MCP_PROFILE = "revit-write"
$env:MCP_ALLOW_WRITES = "1"
$env:REVIT_BRIDGE_ALLOW_WRITES = "1"
```

`tools_call` для destructive-команд требует `confirmed=true`. `tools_search` фильтрует по
`read_only`. MCP-клиент видит аннотации в `tools/list`.

Запись включается только при **обоих** условиях: write-профиль MCP и `REVIT_BRIDGE_ALLOW_WRITES=1`
в процессе Revit. Иначе C# вернёт `Write commands are disabled`.

## Каталог и аудит (P5)

`tool_catalog.db` — производный кэш/аудит, не источник истины. Источник — live-провайдеры и
манифесты. База удаляется и пересобирается в любой момент:

```powershell
python mcp-server\catalog_build.py --db D:\temp\tool_catalog.db --verify
```

Для сборки нужен запущенный Revit (WS) и доступный DwgParser: кэш отражает **живой** каталог,
а не только файлы манифестов.

`--verify` — consistency gate: DB == gateway, duplicate canonical = 0, FTS = tools.
Если задан `MCP_CATALOG_DB`, `tools_search`/`tools_describe` читают кэш, а `tools_call` пишет
строку в `tool_calls` (`ok` / `policy_error` / `unknown_tool` / `provider_error`).
Без переменной всё работает из памяти, аудит отключён.

## Классификация DwgParser (P1.6)

Дочерний DwgParser помечает почти все инструменты `readOnlyHint=false` (дефолт MCP), поэтому
источник истины — `mcp-server/manifests/dwg_policy.json`:

* 51 read-only (чтение модели и файловые экспорты DXF/PDF/DOCX);
* 55 writers (создание/изменение/удаление в Revit, произвольные команды);
* 5 destructive (delete/close/send_command/execute_revit_calls/run_heatloss) — нужен `confirmed=true`.

Инструмент, которого нет в манифесте, считается writer (fail closed). Проверка соответствия
живому списку:

```powershell
python tools\dwg_policy_audit.py
```

## Live / background / test

* **Live:** пользователь открыл Revit; `revit-bridge` подключается к add-in, `bridge_status`
  показывает мост и активный документ.
* **Background:** запусти Revit заранее; `revit_open_document` идёт в WS и требует работающий Revit.
  `revit_close_document(save=false)` завершает сценарий. Revit остаётся GUI-процессом: это
  автоматизация без ручных кликов, не headless.
* **Test:** `RevitHeadless`/`Nice3point.TUnit.Revit` — отдельный контур, мост не участвует.
* **Безопасность:** `REVIT_BRIDGE_TOKEN` (hello-token), `REVIT_BRIDGE_ALLOW_WRITES=0` — read-only,
  `send_code_to_revit` запрещён политикой.
* **Подпись:** unsigned add-in показывает диалог Revit «Загрузить» (нажать один раз) или подписать
  сертификатом, доверенным в `TrustedPublisher`/`Root`.

Проверено 2026-11-22: read-only — 143 вида, 17 листов, PDF-экспорт; write-профиль — compose A3 на
копии `K1_HVAC_demo.rvt` → `valid=true`, PDF 68 234 B, исходный проект не изменён. Виды в `.rte`
пустые: `Viewport.Create` для них падает, для compose-тестов нужен проект с непустыми видами.

## Подпись add-in (доверенный сертификат)

Revit проверяет Authenticode-подпись DLL. Без доверенного сертификата он показывает диалог
«Безопасность: недопустимая подпись»; подписанный доверенный add-in грузится без вопроса.

**Локальная разработка (самоподписанный сертификат):**

```powershell
# 1. Создать сертификат подписи кода (один раз)
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject "CN=RevitBridge Local Dev" `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable

# 2. Доверить сертификат (Root может показать подтверждение — нажать «Да»)
Export-Certificate -Cert $cert -FilePath "$env:TEMP\RevitBridgeLocalDev.cer" -Force
certutil -addstore -user Root "$env:TEMP\RevitBridgeLocalDev.cer"
certutil -addstore -user TrustedPublisher "$env:TEMP\RevitBridgeLocalDev.cer"

# 3. Подписать собранные DLL (после каждой пересборки!)
.\scripts\sign_addin.ps1 -Thumbprint $cert.Thumbprint -Configuration Debug.R24

# 4. Установить add-in
.\scripts\install_addin.ps1 -Configuration Debug.R24
```

Проверка:

```powershell
Get-AuthenticodeSignature .\RevitBridge.Revit\bin\Debug.R24\RevitBridge.Revit.dll |
    Select-Object Status, @{n='Subject';e={$_.SignerCertificate.Subject}}
# Status = Valid
```

**Продакшн:** используйте сертификат код-подписи от CA (не самоподписанный), PFX храните в секрете,
подписывайте с timestamp (`-TimestampUrl http://timestamp.digicert.com`), корень доверяйте в
`LocalMachine` через групповую политику/установщик. Самоподписанный сертификат годится только для
локальной машины разработчика.

## Манифесты команд

* `manifests/heatloss.bridge.manifest.json` — команды HeatLossRevit4.
* `DwgParser` и `NetworkBuilder` кладут `bridge.manifest.json` рядом со своим плагином.
* Add-in читает `*.manifest.json` из `D:\Projects\01-bim`, `04-network`, `03-heating`
  (override: `REVIT_BRIDGE_MANIFESTS=A;B`).
* При вызове команд через мост выставляется `REVIT_BRIDGE_SUPPRESS_UI=1`; UI-команды должны
  возвращать отчёт через `IExternalCommand.message`, а не открывать `TaskDialog`.

## Переменные окружения

| Переменная | Сторона | По умолчанию | Назначение |
|---|---|---|---|
| `REVIT_MCP_URL` | MCP | `http://127.0.0.1:17871` | HTTP health/legacy GET |
| `REVIT_BRIDGE_URL` | MCP | `ws://127.0.0.1:17871/bridge` | WebSocket моста |
| `REVIT_BRIDGE_TOKEN` | обе | пусто | hello-token; пусто = локальный dev |
| `REVIT_BRIDGE_ALLOW_WRITES` | Revit | `0` | разрешить write-команды C# |
| `REVIT_BRIDGE_PORT` | Revit | `17871` | порт HTTP/WS |
| `REVIT_BRIDGE_MANIFESTS` | Revit | `D:\Projects\01-bim;04-network;03-heating` | корни манифестов |
| `MCP_PROFILE` | MCP | `revit-read` | профиль политики |
| `MCP_ALLOW_WRITES` | MCP | `0` | write в gateway |
| `MCP_TOOLS_MODE` | MCP | `compact` | `compact` (7) / `full` (297) |
| `MCP_CATALOG_DB` | MCP | пусто | SQLite-кэш и аудит |
| `DWG_POLICY` | MCP | `mcp-server/manifests/dwg_policy.json` | классификация DwgParser |
| `DWGPARSER_MCP_SERVER` | MCP | `DwgParser/mcp_server.py` | дочерний MCP |
| `REVIT_EXE` | MCP | авто | путь к `Revit.exe` |
| `REVIT_DEFAULT_MODEL` | MCP | авто | модель по умолчанию |

## Тесты

```powershell
# C# (Core, без Revit)
dotnet run --project tests\RevitBridge.Core.Tests\RevitBridge.Core.Tests.csproj -c Debug --no-restore

# Python
python -X utf8 -m unittest discover -s mcp-server/tests -p "test_*.py"

# Инвентаризация и гейты
python tools\tool_surface_audit.py
python tools\dwg_policy_audit.py
python mcp-server\catalog_build.py --db D:\temp\tool_catalog.db --verify
```

`dotnet test` в .NET 10 SDK не подходит для TUnit (`VSTest target is no longer supported`);
используйте `dotnet run` выше.

## Диагностика

* Лог аддина: `%TEMP%\<guid>\RevitBridge.log` (временная папка процесса Revit).
* `revit_ping` падает с `RevitDown` — Revit не запущен или аддин не загрузился
  (проверить `%AppData%\Autodesk\Revit\Addins\2024\RevitBridge.addin`).
* `POST /run` требует загруженный `MainAppHeatLoss.dll` (плагин HeatLoss установлен).

## GitHub

<https://github.com/strahser/RevitBridge>
