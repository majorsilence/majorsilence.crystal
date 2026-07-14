using Majorsilence.Crystal.Cli.Commands;

if (args.Length == 0)
    return Usage();

return args[0].ToLowerInvariant() switch
{
    "convert" => ConvertCommand.Run(args[1..]),
    "scan" => ScanCommand.Run(args[1..]),
    "tags" => TagsCommand.Run(args[1..]),
    "ole" => OleCommand.Run(args[1..]),
    "verify" => VerifyCommand.Run(args[1..]),
    "-h" or "--help" or "help" => Usage(),
    _ => Usage($"Unknown verb '{args[0]}'.")
};

static int Usage(string? error = null)
{
    if (error is not null)
        Console.Error.WriteLine($"error: {error}");
    Console.WriteLine("""
        Crystal Reports .rpt batch tool

        usage:
          crystalcli convert <file-or-dir> [-o <outdir>] [-r]
              Parse each .rpt and write the converted .rdl next to it,
              or under <outdir> (mirroring subdirectories). -r recurses.

          crystalcli scan <dir> [--json <out.json>]
              Batch-parse every .rpt under <dir> (recursive) and report
              parse failures, a TSLV tag histogram, and feature-detector
              hits that identify files exercising unimplemented features.

          crystalcli tags <file.rpt> [--hex <tag>] [--strings <tag>]
              Dump the flat TSLV tag sequence for reverse engineering.
              --hex dumps payload bytes for records with the given tag;
              --strings lists MUTF-8 strings found in those payloads.
        """);
    return error is null ? 0 : 2;
}
