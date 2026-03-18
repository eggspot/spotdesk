using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpotDesk.Core.Models;

namespace SpotDesk.UI.ViewModels;

/// <summary>Wraps DevolutionsImporter for DI / testability.</summary>
public interface IDevolutionsImporter
{
    Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path, string? masterKey = null);
}

/// <summary>Wraps RdpFileImporter for DI / testability.</summary>
public interface IRdpFileImporter
{
    Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path);
}

public enum WizardStep
{
    SelectFile,
    Configure,
    Confirm,
    Progress,
    Result
}

public partial class ImportPreviewItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    public string Name     { get; set; } = string.Empty;
    public string Host     { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public ConnectionEntry Source { get; init; } = null!;
}

public partial class ImportWizardViewModel : ObservableObject
{
    private readonly IDevolutionsImporter? _rdmImporter;
    private readonly IRdpFileImporter?     _rdpImporter;
    private readonly IMRemoteNgImporter?   _mrnImporter;
    private readonly IMobaXtermImporter?   _mobaImporter;

    // ── Step ─────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    [NotifyPropertyChangedFor(nameof(StepIndicator))]
    [NotifyPropertyChangedFor(nameof(IsStepSelectFile))]
    [NotifyPropertyChangedFor(nameof(IsStepConfigure))]
    [NotifyPropertyChangedFor(nameof(IsStepConfirm))]
    [NotifyPropertyChangedFor(nameof(IsStepProgress))]
    [NotifyPropertyChangedFor(nameof(IsStepResult))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(NextButtonText))]
    private WizardStep _currentStep = WizardStep.SelectFile;

    public bool IsStepSelectFile => CurrentStep == WizardStep.SelectFile;
    public bool IsStepConfigure  => CurrentStep == WizardStep.Configure;
    public bool IsStepConfirm    => CurrentStep == WizardStep.Confirm;
    public bool IsStepProgress   => CurrentStep == WizardStep.Progress;
    public bool IsStepResult     => CurrentStep == WizardStep.Result;

    public string StepTitle => CurrentStep switch
    {
        WizardStep.SelectFile => "Import Connections — Select File",
        WizardStep.Configure  => "Import Connections — Configure",
        WizardStep.Confirm    => "Import Connections — Confirm",
        WizardStep.Progress   => "Importing…",
        WizardStep.Result     => "Import Complete",
        _                     => "Import"
    };

    public string StepIndicator => CurrentStep switch
    {
        WizardStep.SelectFile => "Step 1 of 3",
        WizardStep.Configure  => "Step 2 of 3",
        WizardStep.Confirm    => "Step 3 of 3",
        _                     => string.Empty
    };

    public string NextButtonText => CurrentStep switch
    {
        WizardStep.Confirm => "Import →",
        WizardStep.Result  => "Done",
        _                  => "Next →"
    };

    public bool CanGoBack => CurrentStep is WizardStep.Configure or WizardStep.Confirm;
    public bool CanGoNext => CurrentStep switch
    {
        WizardStep.SelectFile => HasSelectedFile,
        WizardStep.Configure  => PreviewItems.Count > 0,
        WizardStep.Confirm    => true,
        WizardStep.Result     => true,
        _                     => false
    };

    // ── File selection ────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private string? _selectedFilePath;

    [ObservableProperty] private string  _selectedFileName  = string.Empty;
    [ObservableProperty] private string  _detectedFormat    = string.Empty;
    [ObservableProperty] private bool    _needsMasterKey;
    [ObservableProperty] private string  _masterKey         = string.Empty;

    public bool HasSelectedFile => !string.IsNullOrEmpty(SelectedFilePath);

    // ── Preview ───────────────────────────────────────────────────────────
    [ObservableProperty] private int    _previewCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(HasPreviewWarning))]
    private string _previewWarning = string.Empty;
    public bool HasPreviewWarning => !string.IsNullOrEmpty(PreviewWarning);
    public ObservableCollection<ImportPreviewItem> PreviewItems { get; } = [];

    // ── Confirm step ──────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<string> _availableGroups = ["Default"];
    [ObservableProperty] private string  _targetGroup      = "Default";
    [ObservableProperty] private bool    _conflictSkip     = true;
    [ObservableProperty] private bool    _conflictOverwrite;
    [ObservableProperty] private bool    _conflictRename;
    [ObservableProperty] private string  _importSummary    = string.Empty;

    // ── Progress ──────────────────────────────────────────────────────────
    [ObservableProperty] private double  _importProgress;
    [ObservableProperty] private string  _currentImportName = string.Empty;

    // ── Result ────────────────────────────────────────────────────────────
    [ObservableProperty] private int _importedCount;
    [ObservableProperty] private int _skippedCount;

    public event Action? CloseRequested;
    public event Action? OpenConnectionTreeRequested;

    /// <summary>
    /// Raised when the user clicks Browse. The view must show a file picker
    /// and call <see cref="SetFilePath"/> with the chosen path.
    /// </summary>
    public event Action? FilePickerRequested;

    /// <summary>Raised after a successful import with per-entry (entry, group) pairs.</summary>
    public event Action<IReadOnlyList<(ConnectionEntry Entry, string Group)>>? EntriesImported;

    public ImportWizardViewModel(
        IDevolutionsImporter? rdmImporter  = null,
        IRdpFileImporter?     rdpImporter  = null,
        IMRemoteNgImporter?   mrnImporter  = null,
        IMobaXtermImporter?   mobaImporter = null)
    {
        _rdmImporter  = rdmImporter;
        _rdpImporter  = rdpImporter;
        _mrnImporter  = mrnImporter;
        _mobaImporter = mobaImporter;
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseFile()
    {
        FilePickerRequested?.Invoke();
    }

    [RelayCommand]
    private void ClearFile()
    {
        SelectedFilePath = null;
        SelectedFileName = string.Empty;
        DetectedFormat   = string.Empty;
        PreviewItems.Clear();
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        switch (CurrentStep)
        {
            case WizardStep.SelectFile:
                await LoadPreviewAsync();
                CurrentStep = WizardStep.Configure;
                break;

            case WizardStep.Configure:
                BuildImportSummary();
                CurrentStep = WizardStep.Confirm;
                break;

            case WizardStep.Confirm:
                await RunImportAsync();
                break;

            case WizardStep.Result:
                CloseRequested?.Invoke();
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        CurrentStep = CurrentStep switch
        {
            WizardStep.Configure => WizardStep.SelectFile,
            WizardStep.Confirm   => WizardStep.Configure,
            _                    => CurrentStep
        };
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private void OpenConnectionTree() => OpenConnectionTreeRequested?.Invoke();

    [RelayCommand]
    private void ImportAnother()
    {
        SelectedFilePath  = null;
        SelectedFileName  = string.Empty;
        DetectedFormat    = string.Empty;
        MasterKey         = string.Empty;
        NeedsMasterKey    = false;
        PreviewWarning    = string.Empty;
        ImportProgress    = 0;
        ImportedCount     = 0;
        SkippedCount      = 0;
        PreviewItems.Clear();
        CurrentStep = WizardStep.SelectFile;
    }

    [RelayCommand]
    private void SelectNext() { }

    [RelayCommand]
    private void SelectPrevious() { }

    // ── Helpers ───────────────────────────────────────────────────────────

    public void SetFilePath(string path)
    {
        SelectedFilePath = path;
        SelectedFileName = Path.GetFileName(path);
        DetectedFormat   = DetectFormat(path);
        NeedsMasterKey   = DetectedFormat == "RDM (encrypted)";
    }

    private static string DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".rdp"         => "Windows RDP file",
            ".rdf"         => "RDM (encrypted)",
            ".json"        => "RDM JSON",
            ".xml"         => SniffXmlFormat(path),
            ".mxtsessions" => "MobaXterm sessions",
            _              => "Unknown"
        };
    }

    /// <summary>Peeks at the first 1 KB of an XML file to tell RDM XML from mRemoteNG XML.</summary>
    private static string SniffXmlFormat(string path)
    {
        try
        {
            Span<byte> buf = stackalloc byte[1024];
            using var fs = File.OpenRead(path);
            var read = fs.Read(buf);
            var snippet = System.Text.Encoding.UTF8.GetString(buf[..read]);
            if (snippet.Contains("mremoteng.org", StringComparison.OrdinalIgnoreCase) ||
                snippet.Contains("ConfVersion",   StringComparison.Ordinal))
                return "mRemoteNG XML";
        }
        catch { /* fall through to default */ }
        return "RDM XML";
    }

    private async Task LoadPreviewAsync()
    {
        PreviewItems.Clear();
        PreviewWarning = string.Empty;
        if (SelectedFilePath is null) return;

        IReadOnlyList<ConnectionEntry> entries = [];
        string? loadError = null;

        try
        {
            entries = DetectedFormat switch
            {
                "Windows RDP file" when _rdpImporter is not null =>
                    await _rdpImporter.ImportAsync(SelectedFilePath),
                "RDM (encrypted)" or "RDM XML" or "RDM JSON" when _rdmImporter is not null =>
                    await _rdmImporter.ImportAsync(SelectedFilePath, NeedsMasterKey ? MasterKey : null),
                "mRemoteNG XML" when _mrnImporter is not null =>
                    await _mrnImporter.ImportAsync(SelectedFilePath),
                "MobaXterm sessions" when _mobaImporter is not null =>
                    await _mobaImporter.ImportAsync(SelectedFilePath),
                _ => []
            };
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        foreach (var e in entries)
            PreviewItems.Add(new ImportPreviewItem
            {
                Name     = e.Name,
                Host     = e.Host,
                Protocol = e.Protocol.ToString(),
                Source   = e
            });

        PreviewCount = PreviewItems.Count;

        if (loadError is not null)
            PreviewWarning = $"Could not read file: {loadError}";
        else if (PreviewItems.Count == 0)
            PreviewWarning = "No compatible connections found in this file. Only RDP, SSH, and VNC connections are supported.";

        OnPropertyChanged(nameof(CanGoNext));
    }

    private void BuildImportSummary()
    {
        int selected = PreviewItems.Count(i => i.IsSelected);
        ImportSummary = $"{selected} connections will be imported into group \"{TargetGroup}\".\n" +
                        $"Conflict strategy: {(ConflictSkip ? "Skip" : ConflictOverwrite ? "Overwrite" : "Rename")}";
    }

    private async Task RunImportAsync()
    {
        CurrentStep   = WizardStep.Progress;
        ImportProgress = 0;
        ImportedCount  = 0;
        SkippedCount   = 0;

        var toImport = PreviewItems.Where(i => i.IsSelected).ToList();
        for (int i = 0; i < toImport.Count; i++)
        {
            CurrentImportName = toImport[i].Name;
            await Task.Delay(30); // simulate per-entry write latency
            ImportedCount++;
            ImportProgress = (double)(i + 1) / toImport.Count * 100;
        }

        SkippedCount = PreviewItems.Count(x => !x.IsSelected);
        CurrentStep  = WizardStep.Result;

        // Notify the host so it can add entries to the connection tree
        // Each entry carries its own group: use Tags[0] if present, else fall back to the wizard TargetGroup
        var imported = toImport
            .Select(i => (i.Source, i.Source.Tags.FirstOrDefault() is { } g ? g : TargetGroup))
            .ToList();
        EntriesImported?.Invoke(imported);
    }
}
