# Граф кода: переключение репозиториев

`roslyn_graph` (`RoslynCodeLens.Mcp`) держит несколько решений одновременно и переключает
**активное** решение. По умолчанию в окружении было загружено только `HeatLossRevit.sln`;
это не значит, что граф умеет работать лишь с ним.

## Инструменты

| Инструмент | Назначение |
|---|---|
| `list_solutions` | какие решения загружены, какое активно, skipped-проекты |
| `load_solution {path}` | загрузить `.sln`/`.slnx` и сделать активным |
| `set_active_solution {name}` | переключить активное (частичное имя, case-insensitive) |
| `unload_solution {name}` | выгрузить и освободить память |
| `rebuild_solution` | пересобрать индексы после смены `Directory.Build.props`/пакетов |

`load_solution` принимает `include`/`rootProjects` (фильтр по **именам файлов** `.csproj`), но
фильтр, не совпавший ни с одним проектом, — ошибка, а не fallback на полное решение.

## RevitBridge

```jsonc
// tools_call("load_solution", { "path": "D:\\Projects\\01-bim\\RevitBridge\\RevitBridge.slnx" })
{ "path": "D:\\Projects\\01-bim\\RevitBridge\\RevitBridge.slnx" }
```

Проверено 2026-11-22: загружено 4 проекта, `status=ready`, `skippedProjects=[]`.
Дальше работают обычные запросы (`find_references`, `find_god_objects`, `get_complexity_metrics`,
`check_architecture` и т.д.) уже по RevitBridge.

## DwgParser

В репозитории теперь есть `DwgParser.slnx` (18 first-party проектов: `BimExtractor`,
`RevitBimCore`, `RevitHeadless`, `AccCore`). Проверено 2026-11-22:

```jsonc
// tools_call("load_solution", { "path": "D:\\Projects\\01-bim\\DwgParser\\DwgParser.slnx" })
{ "loaded": "Loaded 18 project(s) ... 3 project(s) loaded with unresolved references" }
```

Три проекта (`RevitHeadless`, `RevitBimCore.Infrastructure`, `RevitBimCore.Infrastructure.Tests`)
не разрешили metadata references (Revit API / design-time build) — для них результаты графа
могут быть неполными, остальные 15 проектов индексируются нормально.

Для чисто BimExtractor-аудита можно загрузить подмножество:

```jsonc
{ "path": "...\\DwgParser.slnx", "include": ["BimExtractor.*"] }
```

`external\ExcelSpecificationGOST` и устаревший `tests\BimExtractor.Tests` (ссылается на
несуществующий `src\BimExtractor.*`) в решение не включены.

## Порядок для аудита

```text
list_solutions
load_solution(RevitBridge.slnx)      # или DwgParser.sln, когда появится
... запросы графа ...
set_active_solution("HeatLossRevit") # вернуть прежнее активное
unload_solution("RevitBridge")       # если граф больше не нужен
```

Не оставляйте несколько больших решений загруженными без необходимости: каждый workspace
держит компиляцию в памяти.
