using System.IO;
namespace ABC.Converter.App.Services;

public static class ApplicationDataPaths
{
    public static string QueueDatabasePath
    {
        get
        {
            string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ABC Converter");
            Directory.CreateDirectory(parent);
            return Path.Combine(parent, "abc-converter.db");
        }
    }
}
