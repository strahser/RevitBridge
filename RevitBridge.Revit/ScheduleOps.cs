using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitBridge
{
    internal static class ScheduleOps
    {
        public static object GetScheduleData(UIApplication app, JObject args)
        {
            var doc = BridgeOps.Doc(app);
            var schedule = doc.GetElement(BridgeOps.Id(args, "scheduleId")) as ViewSchedule
                ?? throw new InvalidOperationException("Schedule not found.");
            var maxRows = args?.Value<int?>("maxRows") ?? 25;

            var data = schedule.GetTableData();
            var body = data.GetSectionData(SectionType.Body);
            var header = data.GetSectionData(SectionType.Header);

            var fields = schedule.Definition.GetFieldOrder()
                .Select(fieldId => schedule.Definition.GetField(fieldId)?.GetName() ?? "")
                .ToList();

            string Cell(TableSectionData section, int row, int col)
            {
                try { return section?.GetCellText(row, col) ?? ""; }
                catch (Exception) { return ""; }
            }

            var headerRows = new List<string[]>();
            for (var row = 0; row < (header?.NumberOfRows ?? 0) && row < 3; row++)
            {
                var values = new string[header.NumberOfColumns];
                for (var col = 0; col < values.Length; col++) values[col] = Cell(header, row, col);
                headerRows.Add(values);
            }

            var rows = new List<string[]>();
            for (var row = 0; row < body.NumberOfRows && row < maxRows; row++)
            {
                var values = new string[body.NumberOfColumns];
                for (var col = 0; col < values.Length; col++) values[col] = Cell(body, row, col);
                rows.Add(values);
            }

            return new Dictionary<string, object>
            {
                { "id", schedule.Id.Value },
                { "name", schedule.Name },
                { "bodyRows", body.NumberOfRows },
                { "bodyColumns", body.NumberOfColumns },
                { "headerRows", header?.NumberOfRows ?? 0 },
                { "fields", fields },
                { "itemized", schedule.Definition.IsItemized },
                { "header", headerRows },
                { "rows", rows },
                { "valid", body.NumberOfRows > 0 && body.NumberOfColumns > 0 },
            };
        }
    }
}
