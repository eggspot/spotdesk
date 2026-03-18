using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SpotDesk.UI.ViewModels;

namespace SpotDesk.UI.Dialogs;

public partial class ImportWizard : Window
{
    public ImportWizard()
    {
        InitializeComponent();
        SetupDragDrop();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ImportWizardViewModel vm)
            vm.FilePickerRequested += OnFilePickerRequested;
    }

    private async void OnFilePickerRequested()
    {
        if (DataContext is not ImportWizardViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Select connection file",
            AllowMultiple  = false,
            FileTypeFilter =
            [
                new FilePickerFileType("All supported formats")      { Patterns = ["*.xml", "*.json", "*.rdm", "*.rdf", "*.rdp", "*.mxtsessions"] },
                new FilePickerFileType("mRemoteNG XML")              { Patterns = ["*.xml"] },
                new FilePickerFileType("Devolutions RDM XML / JSON") { Patterns = ["*.xml", "*.json", "*.rdm", "*.rdf"] },
                new FilePickerFileType("MobaXterm sessions")         { Patterns = ["*.mxtsessions"] },
                new FilePickerFileType("Windows RDP file")           { Patterns = ["*.rdp"] },
                new FilePickerFileType("All files")                  { Patterns = ["*"] }
            ]
        });

        if (files.FirstOrDefault() is IStorageFile file)
            vm.SetFilePath(file.Path.LocalPath);
    }

    private void SetupDragDrop()
    {
        var dropZone = this.FindControl<Border>("DropZone");
        if (dropZone is null) return;

        DragDrop.SetAllowDrop(dropZone, true);

#pragma warning disable CS0618 // DataFormats.Files / DragEventArgs.Data are deprecated but still functional
        dropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        });

        dropZone.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.Data.GetFiles() is IEnumerable<IStorageItem> files)
            {
                var first = files.FirstOrDefault();
                if (first is IStorageFile sf && DataContext is ImportWizardViewModel vm)
                    vm.SetFilePath(sf.Path.LocalPath);
            }
        });
#pragma warning restore CS0618
    }
}
