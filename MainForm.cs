using System.ComponentModel;

namespace LmStudioThinkPatcher;

sealed class MainForm : Form
{
    private readonly AppSettings settings = AppSettings.Load();
    private readonly ModelPatchService patchService = new();
    private readonly BindingList<PatchModelRow> rows = [];

    private readonly TextBox rootTextBox = new();
    private readonly TextBox suffixTextBox = new();
    private readonly DataGridView grid = new();
    private readonly Label summaryLabel = new();

    public MainForm()
    {
        Text = "LM Studio Think Patcher";
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();

        rootTextBox.Text = settings.ModelRoot;
        suffixTextBox.Text = string.IsNullOrWhiteSpace(settings.Suffix) ? "-ndx" : settings.Suffix;
        grid.DataSource = rows;
    }

    private void BuildLayout()
    {
        var rootLabel = new Label { Text = "Model root", AutoSize = true, Anchor = AnchorStyles.Left };
        rootTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var browseButton = new Button { Text = "Browse", AutoSize = true };
        browseButton.Click += (_, _) => BrowseModelRoot();

        var analyzeButton = new Button { Text = "Analysis", AutoSize = true };
        analyzeButton.Click += (_, _) => Analyze();

        var suffixLabel = new Label { Text = "Suffix", AutoSize = true, Anchor = AnchorStyles.Left };
        suffixTextBox.Width = 160;
        suffixTextBox.TextChanged += (_, _) => SaveSettings();

        var patchAllButton = new Button { Text = "Patch all", AutoSize = true };
        patchAllButton.Click += (_, _) => PatchRows(rows.Where(row => row.IsPatchable).ToList());

        var patchMissingButton = new Button { Text = "Patch unpatched", AutoSize = true };
        patchMissingButton.Click += (_, _) => PatchRows(rows.Where(row => row.IsPatchable && !row.IsPatched).ToList());

        var repatchSelectedButton = new Button { Text = "Repatch selected", AutoSize = true };
        repatchSelectedButton.Click += (_, _) => PatchRows(SelectedRows());

        var deleteSelectedButton = new Button { Text = "Delete selected", AutoSize = true };
        deleteSelectedButton.Click += (_, _) => DeleteRows(SelectedRows());

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 10,
            Padding = new Padding(10),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var index = 2; index < 10; index++)
        {
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        top.Controls.Add(rootLabel, 0, 0);
        top.Controls.Add(rootTextBox, 1, 0);
        top.Controls.Add(browseButton, 2, 0);
        top.Controls.Add(analyzeButton, 3, 0);
        top.Controls.Add(suffixLabel, 4, 0);
        top.Controls.Add(suffixTextBox, 5, 0);
        top.Controls.Add(patchAllButton, 6, 0);
        top.Controls.Add(patchMissingButton, 7, 0);
        top.Controls.Add(repatchSelectedButton, 8, 0);
        top.Controls.Add(deleteSelectedButton, 9, 0);

        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
        grid.RowHeadersVisible = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.CellFormatting += (_, args) =>
        {
            if (args.RowIndex < 0 || grid.Rows[args.RowIndex].DataBoundItem is not PatchModelRow { IsVariation: true })
            {
                return;
            }

            args.CellStyle.ForeColor = Color.DimGray;
            args.CellStyle.BackColor = Color.FromArgb(248, 248, 248);
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Model", DataPropertyName = nameof(PatchModelRow.DisplayName), ReadOnly = true, FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Patch name", DataPropertyName = nameof(PatchModelRow.PatchName), ReadOnly = false, FillWeight = 22 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "GGUF", DataPropertyName = nameof(PatchModelRow.GgufFileCount), ReadOnly = true, FillWeight = 6 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(PatchModelRow.Status), ReadOnly = true, FillWeight = 10 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Hub model", DataPropertyName = nameof(PatchModelRow.Note), ReadOnly = true, FillWeight = 18 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Model folder", DataPropertyName = nameof(PatchModelRow.RelativePath), ReadOnly = true, FillWeight = 30 });

        summaryLabel.Dock = DockStyle.Bottom;
        summaryLabel.Height = 34;
        summaryLabel.Padding = new Padding(10, 7, 10, 7);
        summaryLabel.Text = "Select a model root and run Analysis.";

        Controls.Add(grid);
        Controls.Add(summaryLabel);
        Controls.Add(top);
    }

    private void BrowseModelRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "LM Studio 모델이 저장된 루트 폴더를 선택하세요.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(rootTextBox.Text) ? rootTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        rootTextBox.Text = dialog.SelectedPath;
        SaveSettings();
    }

    private void Analyze()
    {
        RunSafely(() =>
        {
            SaveSettings();
            rows.Clear();
            foreach (var row in patchService.Analyze(rootTextBox.Text, suffixTextBox.Text))
            {
                rows.Add(row);
            }

            UpdateSummary();
        });
    }

    private void PatchRows(List<PatchModelRow> targetRows)
    {
        RunSafely(() =>
        {
            SaveSettings();
            grid.EndEdit();
            foreach (var row in targetRows)
            {
                patchService.Patch(row, suffixTextBox.Text);
            }

            rows.Clear();
            foreach (var row in patchService.Analyze(rootTextBox.Text, suffixTextBox.Text))
            {
                rows.Add(row);
            }

            UpdateSummary();
        });
    }

    private void DeleteRows(List<PatchModelRow> targetRows)
    {
        RunSafely(() =>
        {
            var deleteTargets = targetRows
                .Where(row => !string.IsNullOrWhiteSpace(row.HubModelPath) && Directory.Exists(row.HubModelPath))
                .DistinctBy(row => Path.GetFullPath(row.HubModelPath))
                .ToList();
            if (deleteTargets.Count == 0)
            {
                MessageBox.Show(this, "삭제할 hub patch 폴더가 선택되지 않았습니다.", "LM Studio Think Patcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var preview = string.Join(Environment.NewLine, deleteTargets.Take(8).Select(row => row.HubModelPath));
            if (deleteTargets.Count > 8)
            {
                preview += Environment.NewLine + $"... and {deleteTargets.Count - 8} more";
            }

            var result = MessageBox.Show(
                this,
                $"선택한 patch 폴더 {deleteTargets.Count}개를 삭제합니다.{Environment.NewLine}{Environment.NewLine}{preview}",
                "Delete selected patch folders",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
            {
                return;
            }

            foreach (var row in deleteTargets)
            {
                patchService.DeletePatchFolder(row);
            }

            rows.Clear();
            foreach (var row in patchService.Analyze(rootTextBox.Text, suffixTextBox.Text))
            {
                rows.Add(row);
            }

            UpdateSummary();
        });
    }

    private List<PatchModelRow> SelectedRows()
    {
        return grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<PatchModelRow>()
            .Distinct()
            .ToList();
    }

    private void SaveSettings()
    {
        settings.ModelRoot = rootTextBox.Text.Trim();
        settings.Suffix = suffixTextBox.Text.Trim();
        settings.Save();
    }

    private void UpdateSummary()
    {
        var primaryRows = rows.Where(row => row.IsPrimaryModel).ToList();
        var total = primaryRows.Count;
        var patched = primaryRows.Count(row => row.IsPatched);
        var partial = primaryRows.Count(row => row.Status == "Partial");
        var unpatched = primaryRows.Count(row => row.Status == "Unpatched");
        var notApplicable = primaryRows.Count(row => row.Status == "Not applicable");
        var variations = rows.Count(row => row.IsVariation);
        summaryLabel.Text = $"Model folders: {total}   Variations: {variations}   Patched: {patched}   Partial: {partial}   Unpatched: {unpatched}   Not applicable: {notApplicable}   Hub: {patchService.HubModelsRoot}";
    }

    private void RunSafely(Action action)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "LM Studio Think Patcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}
