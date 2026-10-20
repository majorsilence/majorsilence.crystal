using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.DataDefModel;

namespace Majorsilence.Crystal.ReferenceRenderer
{
    /// <summary>
    /// --links: prints each report's tables, their fields, the links between them, and its
    /// sort fields and groups (a subreport's prefixed "sub"), as the
    /// runtime's own object model reports them. The ground truth the parser's QESession
    /// decode is checked against. One block per report; "error" lines for a report the
    /// runtime will not load.
    /// </summary>
    internal static class LinkDump
    {
        public static int Run(IEnumerable<string> paths)
        {
            // "@file" names a file listing one path per line, for a corpus too large for the command line.
            var expanded = paths.SelectMany(p => p.StartsWith("@") ? File.ReadAllLines(p.Substring(1)).Where(l => l.Length > 0) : new[] { p });
            foreach (var path in expanded)
            {
                Console.WriteLine("== " + Path.GetFileName(path));
                try
                {
                    using (var doc = new ReportDocument())
                    {
                        doc.Load(path);
                        var db = doc.ReportClientDocument.DatabaseController.Database;
                        foreach (ISCRTable t in db.Tables)
                        {
                            Console.WriteLine($"table alias=\"{t.Alias}\" name=\"{t.Name}\" qualified=\"{t.QualifiedName}\" class={t.ClassName} fields={t.DataFields.Count}");
                            foreach (ISCRField f in t.DataFields)
                                Console.WriteLine($"  field \"{f.Name}\" type={(int)f.Type}:{f.Type}");
                        }
                        foreach (ISCRTableLink l in db.TableLinks)
                        {
                            Console.WriteLine($"link {l.SourceTableAlias} -> {l.TargetTableAlias} join={(int)l.JoinType}:{l.JoinType} src=[{string.Join("|", l.SourceFieldNames.Cast<string>())}] dst=[{string.Join("|", l.TargetFieldNames.Cast<string>())}]");
                        }
                        foreach (CrystalDecisions.CrystalReports.Engine.TableLink l in doc.Database.Links)
                        {
                            Console.WriteLine($"enginelink {l.SourceTable.Name} -> {l.DestinationTable.Name} join={l.JoinType} src=[{string.Join("|", l.SourceFields.Cast<DatabaseFieldDefinition>().Select(f => f.Name))}] dst=[{string.Join("|", l.DestinationFields.Cast<DatabaseFieldDefinition>().Select(f => f.Name))}]");
                        }
                        // The sort fields in the order the runtime applies them: each group's
                        // condition field first (SortType GroupSortField), then the record sorts.
                        DumpSorts("", doc);
                        DumpGroupOptions("", doc.ReportClientDocument.DataDefController.DataDefinition);
                        foreach (ReportDocument sub in doc.Subreports)
                        {
                            DumpSorts($"sub \"{sub.Name}\" ", sub);
                            DumpGroupOptions($"sub \"{sub.Name}\" ",
                                doc.ReportClientDocument.SubreportController.GetSubreport(sub.Name).DataDefController.DataDefinition);
                        }
                        // Each placed subreport object, by its object name, and the name the
                        // runtime knows its report by (what Subreports[name] takes).
                        foreach (Section s in doc.ReportDefinition.Sections)
                            foreach (ReportObject o in s.ReportObjects)
                                if (o is SubreportObject so)
                                    Console.WriteLine($"subobj \"{so.Name}\" report=\"{so.SubreportName}\"");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("error " + ex.GetType().Name + ": " + ex.Message.Replace("\r", " ").Replace("\n", " "));
                }
            }
            return 0;
        }

        private static void DumpSorts(string prefix, ReportDocument doc)
        {
            foreach (CrystalDecisions.CrystalReports.Engine.SortField s in doc.DataDefinition.SortFields)
                Console.WriteLine($"{prefix}sort \"{s.Field.FormulaName}\" dir={s.SortDirection} type={s.SortType}");
            foreach (CrystalDecisions.CrystalReports.Engine.Group g in doc.DataDefinition.Groups)
                Console.WriteLine($"{prefix}group \"{g.ConditionField.FormulaName}\"");
        }

        // Each group's options as the data-definition model holds them: for a date or time
        // field, the interval it groups by ("for each month").
        private static void DumpGroupOptions(string prefix, CrystalDecisions.ReportAppServer.DataDefModel.DataDefinition definition)
        {
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.Group g in definition.Groups)
            {
                string options = g.Options is DateGroupOptions date
                    ? $"date condition={(int)date.DateCondition}:{date.DateCondition}"
                    : g.Options?.GetType().Name ?? "none";
                Console.WriteLine($"{prefix}groupoptions \"{g.ConditionField.FormulaForm}\" {options}");
            }
        }
    }
}
