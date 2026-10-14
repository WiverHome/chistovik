using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Services;

public sealed class DialogService(Func<Window> owner) : IDialogService
{
    public string? SaveFile(string title, string filter, string fileName, string? directory)
    {
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = filter, FileName = fileName, AddExtension = true,
            OverwritePrompt = true, InitialDirectory = directory ?? "",
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? directory)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = directory ?? "" };
        return dialog.ShowDialog(owner()) == true ? dialog.FolderName : null;
    }

    public void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { ShowInFolder(path); }
    }

    public void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    public void CopyFileToClipboard(string path) => Clipboard.SetFileDropList(new StringCollection { path });

    public void CopyText(string text) => Clipboard.SetText(text);

    public string? GetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    public bool HasClipboardText()
    {
        try { return Clipboard.ContainsText(); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }
}
