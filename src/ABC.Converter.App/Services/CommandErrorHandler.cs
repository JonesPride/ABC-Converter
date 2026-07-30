using System.Diagnostics;
using System.Windows;

namespace ABC.Converter.App.Services;

public sealed class CommandErrorHandler
{
    public void Handle(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Trace.TraceError("Unhandled command failure: {0}", exception);
        MessageBox.Show(
            "ABC Converter could not complete that action. " +
            "Please check the application log for details.",
            "ABC Converter",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
