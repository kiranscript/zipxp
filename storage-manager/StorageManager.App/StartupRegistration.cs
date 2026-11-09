using System.Reflection;
using Microsoft.Win32;

namespace StorageManager.App;

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ZipXP.StorageManager";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue(Name) != null; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (!value) { key.DeleteValue(Name, false); return; }
            var process = Environment.ProcessPath ?? throw new IOException("Cannot find the application executable.");
            var command = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{process}\" \"{Assembly.GetExecutingAssembly().Location}\" --tray" : $"\"{process}\" --tray";
            key.SetValue(Name, command);
        }
    }
}
