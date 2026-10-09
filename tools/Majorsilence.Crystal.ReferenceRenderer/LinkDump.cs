using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.DataDefModel;

namespace Majorsilence.Crystal.ReferenceRenderer
{
    /// <summary>
    /// --links: prints each report's tables, their fields, and the links between them as the
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
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("error " + ex.GetType().Name + ": " + ex.Message.Replace("\r", " ").Replace("\n", " "));
                }
            }
            return 0;
        }
    }
}
