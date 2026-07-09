namespace LmStudioThinkPatcher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--scan", StringComparison.OrdinalIgnoreCase))
        {
            var service = new ModelPatchService();
            foreach (var row in service.Analyze(args[1], args.Length >= 3 ? args[2] : "-ndx"))
            {
                Console.WriteLine($"{row.DisplayName}\t{row.PatchPublisher}/{row.PatchName}\t{row.Status}\t{row.Note}\t{row.RelativePath}");
            }

            return;
        }

        if (args.Length >= 2 && args[0].Equals("--patch", StringComparison.OrdinalIgnoreCase))
        {
            var service = new ModelPatchService();
            var suffix = args.Length >= 3 ? args[2] : "-ndx";
            var patched = 0;
            var skipped = 0;

            foreach (var row in service.Analyze(args[1], suffix).Where(row => !row.IsVariation))
            {
                if (!row.IsPatchable)
                {
                    skipped++;
                    Console.WriteLine($"SKIP\t{row.DisplayName}\t{row.Status}\t{row.Note}");
                    continue;
                }

                service.Patch(row, suffix);
                if (row.IsPatched)
                {
                    patched++;
                    Console.WriteLine($"PATCHED\t{row.DisplayName}\t{row.PatchPublisher}/{row.PatchName}\t{row.HubModelPath}");
                }
                else
                {
                    skipped++;
                    Console.WriteLine($"SKIP\t{row.DisplayName}\t{row.Status}\t{row.Note}");
                }
            }

            Console.WriteLine($"Done. Patched: {patched}, skipped: {skipped}");
            return;
        }

#if NET10_0_WINDOWS
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
#else
        Console.Error.WriteLine("GUI mode is available only in the Windows build. Use --scan or --patch on this platform.");
        Environment.ExitCode = 1;
#endif
    }    
}
