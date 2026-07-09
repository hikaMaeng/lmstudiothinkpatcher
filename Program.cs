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

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }    
}
