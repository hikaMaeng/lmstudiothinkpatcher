using System.ComponentModel;

namespace LmStudioThinkPatcher;

sealed class PatchModelRow : INotifyPropertyChanged
{
    private string patchName = "";
    private string status = "";
    private string note = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ModelName { get; init; } = "";
    public string VariationName { get; init; } = "";
    public bool IsVariation { get; init; }
    public string DisplayName => IsVariation ? "    " + VariationName : ModelName;
    public string PatchName
    {
        get => patchName;
        set
        {
            if (patchName == value)
            {
                return;
            }

            patchName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PatchName)));
        }
    }

    public string Status
    {
        get => status;
        set
        {
            if (status == value)
            {
                return;
            }

            status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public string Note
    {
        get => note;
        set
        {
            if (note == value)
            {
                return;
            }

            note = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
        }
    }

    public string RelativePath { get; init; } = "";
    public string ModelDirectory { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string PatchPublisher { get; set; } = "";
    public string BaseModelKey { get; init; } = "";
    public string HubModelPath { get; set; } = "";
    public List<string> PrimaryGgufFiles { get; init; } = [];
    public string TemplateSource { get; set; } = "";
    public bool SupportsReasoningPatch { get; set; } = true;
    public int GgufFileCount { get; init; }
    public bool IsPatched => Status.StartsWith("Patched", StringComparison.OrdinalIgnoreCase);
    public bool IsPrimaryModel => !IsVariation;
    public bool IsPatchable => IsPrimaryModel && SupportsReasoningPatch;
}
