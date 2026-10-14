using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShablonRabot.Core.Detection;

public enum DetectionError { Unreadable, NeedsWord, Locked, Nothing, WordTimeout }

/// <summary>Ошибка анализа с видом (DESIGN.md 17.4.6, E1–E5).</summary>
public sealed class DetectionException(DetectionError kind, string fileName, Exception? inner = null)
    : Exception(kind.ToString(), inner)
{
    public DetectionError Kind { get; } = kind;
    public string FileName { get; } = fileName;
}

public enum DetectionStage { Reading, WordOpening, Analyzing, Comparing }

/// <summary>Точка входа анализа: .docx — напрямую, .doc/.rtf/.odt/.pdf — через копию .docx, сохранённую Word.</summary>
public static class SampleAnalyzer
{
    public static readonly string[] DirectExtensions = [".docx", ".docm", ".dotx", ".pdf"];
    /// <summary>Эти форматы открывает Word и сохраняет копию .docx. PDF читается напрямую.</summary>
    public static readonly string[] WordExtensions = [".doc", ".rtf", ".odt"];
    public static bool IsSupported(string path) => DirectExtensions.Concat(WordExtensions).Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool NeedsWord(string path) => WordExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    static readonly TimeSpan WordTimeout = TimeSpan.FromSeconds(60);

    /// <exception cref="DetectionException"/>
    /// <exception cref="OperationCanceledException"/>
    public static RulesDetection AnalyzeFile(string path, IProgress<DetectionStage>? progress = null, CancellationToken ct = default)
    {
        var name = Path.GetFileName(path);
        progress?.Report(DetectionStage.Reading);
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (IOException e) when (e is not FileNotFoundException)
        {
            throw new DetectionException(DetectionError.Locked, name, e);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or FileNotFoundException)
        {
            throw new DetectionException(DetectionError.Unreadable, name, e);
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        string docx = path;
        string? temp = null;
        try
        {
            if (WordExtensions.Contains(ext))
            {
                if (!TemplateService.IsWordInstalled) throw new DetectionException(DetectionError.NeedsWord, name);
                progress?.Report(DetectionStage.WordOpening);
                temp = ConvertWithWord(path, name, ct);
                docx = temp;
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(DetectionStage.Analyzing);
            RulesDetection result;
            try
            {
                result = ext == ".pdf" ? PdfAnalyzer.Analyze(docx, name) : DocxAnalyzer.Analyze(docx, name, approximate: false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not DetectionException)
            {
                throw new DetectionException(DetectionError.Unreadable, name, e);
            }
            progress?.Report(DetectionStage.Comparing);
            if (result.Rules.Count(r => r.Key != "hyphenation") == 0) throw new DetectionException(DetectionError.Nothing, name);
            return result;
        }
        finally
        {
            if (temp != null) try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Word открывает файл и сохраняет копию .docx во временной папке; при отмене или тайм-ауте завершается только наш Word.</summary>
    static string ConvertWithWord(string path, string name, CancellationToken ct)
    {
        var target = Path.Combine(Path.GetTempPath(), $"shablon-sample-{Guid.NewGuid():N}.docx");
        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var ourPid = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            dynamic? word = null;
            try
            {
                word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
                var caption = "shablon-" + Guid.NewGuid().ToString("N");
                word.Caption = caption;
                var hwnd = FindWindow("OpusApp", caption);
                if (hwnd != IntPtr.Zero) { GetWindowThreadProcessId(hwnd, out var pid); ourPid = (int)pid; }
                word.Visible = false;
                word.DisplayAlerts = 0;
                // ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false
                dynamic doc = word.Documents.Open(path, false, true, false);
                doc.SaveAs2(target, 16); // wdFormatXMLDocument
                doc.Close(false);
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                if (word != null)
                {
                    try { word.Quit(false); } catch { /* Word уже закрыт */ }
                    try { Marshal.FinalReleaseComObject(word); } catch { /* освобождён */ }
                }
            }
        }) { IsBackground = true, Name = "Word-convert" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var deadline = DateTime.UtcNow + WordTimeout;
        while (!thread.Join(200))
        {
            if (!ct.IsCancellationRequested && DateTime.UtcNow <= deadline) continue;
            var targets = ourPid != 0
                ? Process.GetProcessesByName("WINWORD").Where(p => p.Id == ourPid)
                : Process.GetProcessesByName("WINWORD").Where(p => !before.Contains(p.Id)).Take(1);
            foreach (var p in targets) try { p.Kill(entireProcessTree: true); } catch (Exception) { /* уже завершён */ }
            thread.Join(5000);
            ct.ThrowIfCancellationRequested();
            throw new DetectionException(DetectionError.WordTimeout, name);
        }
        if (failure != null || !File.Exists(target)) throw new DetectionException(DetectionError.Unreadable, name, failure);
        return target;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
