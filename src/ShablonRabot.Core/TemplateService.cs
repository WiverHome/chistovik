using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ShablonRabot.Core;

public enum GenerationStage { Building, UpdatingFields, ExportingPdf, Done }

/// <summary>Почему Word не обновил поля (null — обновил).</summary>
public enum WordIssue { None, NotInstalled, TimedOut, Failed, Disabled }

public sealed record GenerationResult(string DocxPath, string? PdfPath, WordIssue WordIssue, bool PdfRequested, string? WordError)
{
    public bool FieldsUpdated => WordIssue == WordIssue.None;
}

/// <summary>Файл назначения занят другой программой (обычно открыт в Word).</summary>
public sealed class FileLockedException(string path, Exception inner) : IOException($"Файл занят: {path}", inner);

/// <summary>Сборка шаблона и обновление полей (содержание, номера) через установленный Microsoft Word.</summary>
public static class TemplateService
{
    const int WdExportFormatPdf = 17;
    public static readonly TimeSpan WordTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Проверка по реестру, без запуска Word.</summary>
    public static bool IsWordInstalled
    {
        get
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"Word.Application\CurVer");
            return key?.GetValue(null) is string;
        }
    }

    /// <exception cref="FileLockedException">Файл открыт в другой программе.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на запись в папку.</exception>
    /// <exception cref="OperationCanceledException">Отменено пользователем; частичный файл удалён.</exception>
    public static GenerationResult Generate(Profile profile, string docxPath, bool pdf, bool useWord = true,
                                            IProgress<GenerationStage>? progress = null, CancellationToken ct = default)
    {
        var pdfPath = pdf ? Path.ChangeExtension(docxPath, ".pdf") : null;
        EnsureWritable(docxPath);
        if (pdfPath != null) EnsureWritable(pdfPath);
        // Собираем во временный файл рядом и заменяем итоговый только в конце:
        // отмена или ошибка не портят уже существующий файл
        var work = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(docxPath))!, $"~{Guid.NewGuid():N}.docx");
        var workPdf = pdfPath != null ? Path.ChangeExtension(work, ".pdf") : null;

        try
        {
            progress?.Report(GenerationStage.Building);
            var word = useWord && IsWordInstalled;
            TemplateGenerator.Generate(profile, work, updateFieldsOnOpen: !word);
            ct.ThrowIfCancellationRequested();

            var issue = !useWord ? WordIssue.Disabled : !word ? WordIssue.NotInstalled : WordIssue.None;
            string? error = null;
            if (issue == WordIssue.None)
            {
                progress?.Report(GenerationStage.UpdatingFields);
                (issue, error) = UpdateWithWord(work, workPdf, progress, ct);
                if (issue != WordIssue.None) TemplateGenerator.Generate(profile, work, updateFieldsOnOpen: true);
            }
            ct.ThrowIfCancellationRequested();

            File.Move(work, docxPath, overwrite: true);
            string? savedPdf = null;
            if (issue == WordIssue.None && workPdf != null && File.Exists(workPdf))
            {
                File.Move(workPdf, pdfPath!, overwrite: true);
                savedPdf = pdfPath;
            }
            return Done(new GenerationResult(docxPath, savedPdf, issue, pdf, error));
        }
        finally
        {
            TryDelete(work);
            if (workPdf != null) TryDelete(workPdf);
        }

        GenerationResult Done(GenerationResult r)
        {
            progress?.Report(GenerationStage.Done);
            return r;
        }
    }

    static void EnsureWritable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path)) return;
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e)
        {
            throw new FileLockedException(path, e);
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Word работает в отдельном потоке с копией файла в короткой временной папке (длинные пути он не открывает).
    /// При отмене или тайм-ауте процесс Word, запущенный нами, завершается принудительно.
    /// </summary>
    static (WordIssue, string?) UpdateWithWord(string docxPath, string? pdfPath, IProgress<GenerationStage>? progress, CancellationToken ct)
    {
        var tmp = Directory.CreateTempSubdirectory("shablon");
        var workDocx = Path.Combine(tmp.FullName, "t.docx");
        var workPdf = Path.Combine(tmp.FullName, "t.pdf");
        File.Copy(docxPath, workDocx, true);

        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        var ourPid = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            dynamic? word = null;
            try
            {
                word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
                // Уникальный заголовок окна позволяет найти именно наш процесс Word
                var caption = "shablon-" + Guid.NewGuid().ToString("N");
                word.Caption = caption;
                var hwnd = FindWindow("OpusApp", caption);
                if (hwnd != IntPtr.Zero) { GetWindowThreadProcessId(hwnd, out var pid); ourPid = (int)pid; }
                word.Visible = false;
                word.DisplayAlerts = 0;
                dynamic doc = word.Documents.Open(workDocx, false, false, false);
                doc.Fields.Update();
                int tocs = doc.TablesOfContents.Count;
                for (var i = 1; i <= tocs; i++) doc.TablesOfContents.Item(i).Update();
                doc.Save();
                if (pdfPath != null)
                {
                    progress?.Report(GenerationStage.ExportingPdf);
                    doc.ExportAsFixedFormat(workPdf, WdExportFormatPdf);
                }
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
                    try { word.Quit(false); } catch { /* Word уже закрыт или завершён */ }
                    try { Marshal.FinalReleaseComObject(word); } catch { /* объект уже освобождён */ }
                }
            }
        }) { IsBackground = true, Name = "Word" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var deadline = DateTime.UtcNow + WordTimeout;
        var timedOut = false;
        while (!thread.Join(200))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                timedOut = !ct.IsCancellationRequested;
                KillOurWord(ourPid, before);
                thread.Join(5000);
                break;
            }
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            if (timedOut) return (WordIssue.TimedOut, null);
            if (failure != null) return (WordIssue.Failed, failure.Message);
            File.Copy(workDocx, docxPath, true);
            if (pdfPath != null && File.Exists(workPdf)) File.Copy(workPdf, pdfPath, true);
            return (WordIssue.None, null);
        }
        finally
        {
            try { tmp.Delete(true); } catch (IOException) { /* временная папка подчистится системой */ }
        }
    }

    /// <summary>Завершает только экземпляр Word, запущенный нами (документы пользователя не трогаем).</summary>
    static void KillOurWord(int pid, HashSet<int> before)
    {
        var started = Process.GetProcessesByName("WINWORD").Where(p => !before.Contains(p.Id)).ToList();
        // Если наш PID не определился, завершаем новый Word, только когда он один:
        // второй мог открыть сам пользователь за время сборки
        var targets = pid != 0 ? started.Where(p => p.Id == pid) : started.Count == 1 ? started : [];
        foreach (var p in targets)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* процесс уже завершился */ }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
