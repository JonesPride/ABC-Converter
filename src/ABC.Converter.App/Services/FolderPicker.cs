using Microsoft.Win32;

namespace ABC.Converter.App.Services;

public sealed class FolderPicker
{
    public IReadOnlyList<string> PickComicFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add CBZ comics",
            Filter = "Comic book archives (*.cbz)|*.cbz",
            CheckFileExists = true,
            Multiselect = true,
        };

        return dialog.ShowDialog() == true
            ? dialog.FileNames
            : [];
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory ?? string.Empty,
        };

        return dialog.ShowDialog() == true
            ? dialog.FolderName
            : null;
    }
}
