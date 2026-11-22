# RevitBridge — мост OpenCode ↔ Revit 2024

Позволяет агенту (OpenCode) самостоятельно: открыть `.rvt` файл, прочитать
информацию о документе/помещениях/уровнях и запустить окна плагина
`HeatLossRevit4` — без ручных кликов в Revit.

## Архитектура

```
OpenCode --stdio--> mcp-server/server.py --WebSocket ws://127.0.0.1:17871/bridge--> RevitBridge.Revit (внутри Revit) --> Revit API
```

* **RevitBridge.Revit** (C#, net48, Revit 2024) — `IExternalApplication`, держит
  `HttpListener` на `127.0.0.1:17871` (порт — `REVIT_BRIDGE_PORT`, fallback `REVIT_MCP_PORT`);
  HTTP `GET /health` + WebSocket `/bridge` (protocol v1, команды `bridge_ping/commands/call`), все вызовы
  Revit API выполняет на главном потоке через `ExternalEvent`.
* **mcp-server/server.py** (Python, stdlib; дочернему DwgParser нужен `mcp>=2.0`) —
  stdio MCP-фасад. В текущем состоянии: 61 static tool, до 126 dynamic WS-команд
  (55 built-in + 71 из манифестов) и 106 `dwg_*` из дочернего DwgParser.
  Read-only по умолчанию; `REVIT_BRIDGE_ALLOW_WRITES=1` включает запись в Revit.
  Легаси HTTP принимает только `GET`; все `POST` → `403 legacy_write_disabled`.

## Установка (один раз)

1. Собрать аддин:
   `"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" RevitBridge.Revit\RevitBridge.Revit.csproj /t:Restore,Build /p:Configuration=Release.R24 /p:Platform="Any CPU"`
2. Разложить в Revit:
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install_addin.ps1`
3. Python-зависимость: `pip install -r mcp-server\requirements.txt` (уже стоит `mcp 2.0.0`)
4. Подключить к OpenCode — добавить в `opencode.json` (пример в `opencode.json.example`):
   ```json
   "mcp": {
     "revit-mcp": {
       "type": "local",
       "command": ["python", "-X", "utf8", "E:\\ПлагиныРевит\\\\_bim\\RevitBridge\\mcp-server\\server.py"],
       "enabled": true
     }
   }
   ```
5. Перезапустить Revit 2024 (подхватит `RevitBridge.addin`), затем:
   `revit_open_document` с путём `D:\Projects\_testdata\ТестыОВ\newBuilding\HvackFinal.rvt`.

## Инструменты

| Инструмент | Транспорт | Назначение |
|---|---|---|
| `revit_ping`, `revit_doc_info`, `revit_list_levels`, `revit_list_rooms` | WS | базовое чтение документа |
| `revit_open_document`, `revit_close_document` | WS | открыть/закрыть активный документ (без HTTP POST) |
| `bridge_commands`, `bridge_status` | WS/GET | диагностика моста и список команд |
| `bridge_call`, `revit_call` | WS | generic-вызов команды; write блокируется политикой |
| `cmd_*` | WS | динамические команды провайдеров (126 live) |
| `dwg_*` | stdio child | DWG/DXF/PDF/3D из DwgParser |

`revit_run_heatloss` и остальные legacy static write tools без WS-команды
сейчас возвращают `legacy_write_disabled`: HTTP POST закрыт, перенос на WS — P3/P4.
Для записи в Revit нужен `REVIT_BRIDGE_ALLOW_WRITES=1` и write-профиль MCP.

## Единый MCP (P3)

Один сервер `revit-bridge` (stdio) объединяет:

- статические `bridge_*` / `revit_*` инструменты (61);
- динамические `cmd_<provider>_<command>` — все команды из live `commands.list`
  (126 = 55 built-in + 57 DwgParser + 8 HeatLoss + 6 NetworkBuilder);
- `dwg_*` — прокси DwgParser MCP (DWG/DXF/PDF/3D), дочерний процесс
  `DwgParser/mcp_server.py` (override: `DWGPARSER_MCP_SERVER`), 106 инструментов;
- `revit_call` — generic-вызов любой bridge-команды.

Итого до ~293 имён в одном MCP (с дублями legacy/WS). P2-gateway (`tools_search`,
`tools_describe`, `tools_call`) сокращает прямой список до 8–10 инструментов.

## Live / background / test (P6)

- **Live:** пользователь открыл Revit; `revit-bridge` подключается к add-in, `bridge_status`
  показывает `bridge` + активный документ.
- **Background:** запусти Revit с нужной моделью заранее; после P1 static
  `revit_open_document` идёт в WS и требует уже работающий Revit.
  `revit_close_document(save=false)` завершает сценарий.
  Revit остаётся GUI-процессом; это автоматизация без ручных кликов, не headless.
- **Test:** `RevitHeadless`/`Nice3point.TUnit.Revit` — отдельный контур, мост не участвует.
- **Безопасность:** `REVIT_BRIDGE_TOKEN` (hello-token), `REVIT_BRIDGE_ALLOW_WRITES=0` —
  read-only, `send_code_to_revit` запрещён политикой.
- **Подпись:** unsigned add-in показывает диалог Revit «Загрузить» (нажать один раз) или
  подписать сертификатом, доверенным в `TrustedPublisher`/`Root`.

## Подпись add-in (доверенный сертификат)

Revit проверяет Authenticode-подпись DLL. Без доверенного сертификата он показывает
диалог «Безопасность: недопустимая подпись»; подписанный доверенный add-in грузится без вопроса.

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

**Продакшн:** используйте сертификат код-подписи от CA (не самоподписанный), PFX храните
в секрете, подписывайте с timestamp (`-TimestampUrl http://timestamp.digicert.com`), а корень
доверяйте в `LocalMachine` через групповую политику/установщик. Самоподписанный сертификат
годится только для локальной машины разработчика.

## Манифесты команд (P2)

- `manifests/heatloss.bridge.manifest.json` — команды HeatLossRevit4.
- `DwgParser` и `NetworkBuilder` кладут `bridge.manifest.json` рядом со своим плагином.
- Add-in читает `*.manifest.json` из `D:\Projects\01-bim`, `04-network`, `03-heating`
  (override: `REVIT_BRIDGE_MANIFESTS=A;B`).
- Инструменты MCP: `bridge_ping`, `bridge_commands`, `bridge_call {command,args}`.
- При вызове команд через мост выставляется `REVIT_BRIDGE_SUPPRESS_UI=1`; UI-команды
  должны возвращать отчёт через `IExternalCommand.message`, а не открывать `TaskDialog`.

## Диагностика

* Лог аддина: `%TEMP%\<guid>\RevitBridge.log` (временная папка процесса Revit)
* `revit_ping` падает с `RevitDown` — Revit не запущен или аддин не загрузился
  (проверить `%AppData%\Autodesk\Revit\Addins\2024\RevitBridge.addin`).
* `POST /run` требует загруженный `MainAppHeatLoss.dll` (плагин HeatLoss установлен).

## GitHub

Репозитория пока нет (локальный проект `D:\Projects\01-bim\RevitBridge\`).
Если нужен `git init + push` — скажите, создам репозиторий на GitHub.
