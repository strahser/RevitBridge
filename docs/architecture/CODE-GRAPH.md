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

В репозитории **нет** `.sln`/`.slnx` (только 20 `.csproj`: `BimExtractor`, `RevitBimCore`,
`RevitHeadless`, `AccCore`, `external`). `load_solution` требует решение, поэтому варианты:

1. Создать решение (рекомендуется, отдельный коммит):
   ```powershell
   cd D:\Projects\01-bim\DwgParser
   dotnet new sln -n DwgParser
   dotnet sln DwgParser.sln add BimExtractor\**\*.csproj RevitBimCore\**\*.csproj RevitHeadless\RevitHeadless.csproj
   ```
   `external\ExcelSpecificationGOST` и `AccCore` подключать по необходимости.
2. Либо анализировать DwgParser точечно через `astgrep`/grep, пока решение не создано.

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
