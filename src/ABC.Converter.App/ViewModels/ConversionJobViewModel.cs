using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ABC.Converter.App.ViewModels;

public sealed class ConversionJobViewModel : INotifyPropertyChanged
{
    private string _sourcePath = string.Empty;
    private string _series = string.Empty;
    private string _status = "Queued";
    private int _progress;
    private string? _errorMessage;
    private string? _outputPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SourcePath
    {
        get => _sourcePath;
        set => SetField(ref _sourcePath, value);
    }

    public string Series
    {
        get => _series;
        set => SetField(ref _series, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        set => SetField(ref _outputPath, value);
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
