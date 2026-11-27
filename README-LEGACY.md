# RevitMcp — ЗАМЕНЁН (16.11.2026)

**Этот репозиторий больше не используется.** Вместо него собственный MCP
живого Revit реализован в `D:\ПлагиныРевит\DwgParser` (P6):

- `DwgParser\mcp_server.py` — 100 инструментов (эталонные 24 + 15 дополнительных
  живых + 10 DwgParser-специфичных), гибрид `transport=core|revit`;
- `DwgParser\RevitBimCore\RevitBimCore.Plugin\LiveRevitOps.cs` — 54 операции
  Revit API через ExternalEvent, TCP `127.0.0.1:8098`;
- план и инструкция: `DwgParser\docs\tasks\PLAN-live-revit-mcp.md`,
  `DwgParser\docs\workstreams\p6-live-revit-mcp\README.md`.

В конфигах `opencode.json` (agent-skills, DwgParser, HeatLossRevit4, ModelTesting)
запись `revit-mcp` отключена (`enabled: false`), включён `dwgparser`.

Статус: **до живого E2E репозиторий сохраняется как откат**. После успешного
прогона (deploy → ping → OpenDocument(K1_HVAC_demo.rvt) → 9-check audit →
`hvac_probe_w1`) папку можно удалить.

Проверки, уже пройденные для замены: сборка DwgParser 0 ошибок, Core-тесты
1320/1320, headless-тесты MCP 7 passed / 4 skipped (живой Revit), `tools/list`
— 100 инструментов.
