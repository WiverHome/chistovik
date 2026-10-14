using System.Globalization;
using System.Text.RegularExpressions;

namespace ShablonRabot.Core.Detection;

/// <summary>
/// Извлекает правила оформления из свободного текста требований (методичка, сообщение преподавателя):
/// «Поля: левое — 3 см, правое — 1 см… Шрифт Times New Roman 14, интервал полуторный, отступ 1,25».
/// </summary>
public static partial class RequirementsParser
{
    const string Num = @"(\d{1,3}(?:[.,]\d{1,2})?)";
    const string Dash = @"\s*(?:[-–—:=]|равн\w*|состав\w*|не менее|не более)?\s*";

    // «верхнее и нижнее — 20 мм», «сверху снизу по 2»
    [GeneratedRegex(@"(лев\w*|прав\w*|верх\w*|ниж\w*|слева|справа|сверху|снизу)(?:(?:\s*(?:и|,)\s*|\s+)(лев\w*|прав\w*|верх\w*|ниж\w*|слева|справа|сверху|снизу))?(?:\s+пол\w*)?" + Dash + @"(?:по\s+)?" + Num + @"\s*(мм|см|мм\.|см\.)?")]
    private static partial Regex MarginSide();

    // «25 мм слева, 10 мм справа» — число перед стороной, только с единицами
    [GeneratedRegex(Num + @"\s*(мм|см)\.?\s+(слева|справа|сверху|снизу)(?:\s*(?:и|,)?\s*(слева|справа|сверху|снизу))?")]
    private static partial Regex MarginValueSide();

    [GeneratedRegex(@"(?:все\s+)?пол[ея]\w*\s*(?:со всех сторон|все|по)?\s*(?:[-–—:]|по)\s*" + Num + @"\s*(мм|см)")]
    private static partial Regex MarginsAll();

    static readonly Regex MarginsSlashRegex = new(@"пол[ея]\w*([^.;\n\d]{0,40}?)" + string.Join(@"\s*[/\\]\s*", Enumerable.Repeat(@"(\d{1,2}(?:[.,]\d)?)", 4)) + @"\s*(мм|см)?", RegexOptions.Compiled);
    static Regex MarginsSlash() => MarginsSlashRegex;

    [GeneratedRegex(@"(times new roman|arial|calibri|cambria|pt serif|pt astra serif|pt sans|liberation serif|georgia|verdana|tahoma|courier new|book antiqua|garamond)")]
    private static partial Regex FontName();

    // Ленивые повторения — обычным Regex: генератор .NET 8 на них выдаёт некомпилируемый код
    static readonly Regex FontSizeExplicitRegex = new(@"(?:размер\w*\s+шрифта|кегл\w*|шрифт[^.;\n]{0,40}?)" + Dash + @"(\d{1,2})(?:\s*(?:пт|pt|пункт\w*|п\.))?(?![.,]\d)(?!\s*(?:мм|см))", RegexOptions.Compiled);
    static Regex FontSizeExplicit() => FontSizeExplicitRegex;

    [GeneratedRegex(@"(\d{1,2})\s*(?:-?\s*(?:й|ым|м)\s+)?(?:пт|pt|пункт\w*|кегл\w*)")]
    private static partial Regex FontSizeUnit();

    [GeneratedRegex(@"(полутор\w*|полтора|одинарн\w*|двойн\w*)")]
    private static partial Regex SpacingWord();

    static readonly Regex SpacingNumberRegex = new(@"(?:межстроч\w*\s+)?интервал\w*(?!\s*(?:перед|после))[^.;\n\d]{0,25}?(\d(?:[.,]\d{1,2})?)(?!\s*(?:мм|см|пт))", RegexOptions.Compiled);
    static Regex SpacingNumber() => SpacingNumberRegex;

    [GeneratedRegex(@"(абзацн\w*\s+отступ\w*|отступ\w*\s+(?:перв\w*\s+строк\w*|абзац\w*)|красн\w*\s+строк\w*|отступ\w*)" + Dash + Num + @"\s*(см|мм)?")]
    private static partial Regex Indent();

    [GeneratedRegex(@"[^.;\n]*номер\w*[^.;\n]*страниц[^.;\n]*|[^.;\n]*страниц[^.;\n]*нумер[^.;\n]*|[^.;\n]*нумерац\w*[^.;\n]*")]
    private static partial Regex PageNumberSentence();

    [GeneratedRegex(@"[^.;\n]*заголов[^.;\n]*")]
    private static partial Regex HeadingSentence();

    [GeneratedRegex(@"[^.;\n]*(?:таблиц)[^.;\n]*")]
    private static partial Regex TableSentence();

    [GeneratedRegex(@"рис(?:унок|\.)\s*\d+(?:[.,]\d+)?\s*([–—\-]|\.)\s*\p{L}")]
    private static partial Regex FigureExample();

    public static RulesDetection Parse(string text, string source = "Текст требований")
    {
        var result = new RulesDetection { Source = source };
        var original = text;
        // Десятичная точка («1.5») — не конец предложения: для разбора заменяем её запятой (длина не меняется,
        // позиции совпадают с исходным текстом, из которого берутся цитаты)
        var t = Normalize(Regex.Replace(text, @"(?<=\d)\.(?=\d)", ","));

        ParseMargins(t, original, result);

        if (FontName().Match(t) is { Success: true } font)
            result.Add("font", CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(font.Value).Replace("Pt ", "PT "), Confidence.High, QuoteSentence(original, font));
        else if (Regex.Match(t, @"\b(times|таймс\w*)\b") is { Success: true } times)
            result.Add("font", "Times New Roman", Confidence.Medium, QuoteSentence(original, times));

        var size = FontSizeExplicit().Matches(t).Cast<Match>().Concat(FontSizeUnit().Matches(t))
            .FirstOrDefault(m => int.TryParse(m.Groups[1].Value, out var v) && v is >= 8 and <= 20 && !IsTableContext(t, m.Index));
        if (size != null) result.Add("fontSize", double.Parse(size.Groups[1].Value), Confidence.High, QuoteSentence(original, size));

        if (SpacingWord().Match(t) is { Success: true } sw && Near(t, sw.Index, "интервал", 40))
        {
            var v = sw.Value.StartsWith("полутор") || sw.Value == "полтора" ? 1.5 : sw.Value.StartsWith("двойн") ? 2.0 : 1.0;
            result.Add("lineSpacing", v, Confidence.High, QuoteSentence(original, sw));
        }
        else if (SpacingNumber().Match(t) is { Success: true } sn && TryNumber(sn.Groups[1].Value, out var spacing) && spacing is >= 1 and <= 3)
            result.Add("lineSpacing", spacing, Confidence.High, QuoteSentence(original, sn));

        foreach (Match m in Indent().Matches(t))
        {
            var explicitIndent = !m.Groups[1].Value.StartsWith("отступ") || m.Groups[1].Value.Contains("перв") || m.Groups[1].Value.Contains("абзац");
            // «Поля: … отступ сверху 2 см» — это поле; «отступ 1,25, поля 30/15/20/20» — абзацный отступ
            if (!explicitIndent && IsMarginBefore(t, m.Index)) continue;
            // «отступы слева 3 см, справа…» — это поля; абзацный отступ ищем только в явной формулировке
            if (m.Groups[1].Value.StartsWith("отступ") && !m.Groups[1].Value.Contains(' ') && SideCount(t.Substring(m.Index, Math.Min(40, t.Length - m.Index))) > 0) continue;
            if (!TryNumber(m.Groups[2].Value, out var v)) continue;
            var cm = m.Groups[3].Value.StartsWith("мм") ? v / 10 : v;
            if (cm is <= 0 or > 3) continue;
            result.Add("indent", cm, explicitIndent ? Confidence.High : Confidence.Medium, QuoteSentence(original, m));
            break;
        }

        if (Regex.Match(t, @"по\s+ширин\w*") is { Success: true } j) result.Add("align", "justify", Confidence.High, QuoteSentence(original, j));
        else if (Regex.Match(t, @"(?:текст\w*|выравнива\w*)[^.;\n]{0,30}по\s+лев\w*\s+кра\w*") is { Success: true } l)
            result.Add("align", "left", Confidence.Medium, QuoteSentence(original, l));

        if (Regex.Match(t, @"без\s+перенос\w*|перенос\w*[^.;\n]{0,20}(?:не\s+допуска|запрещ|не\s+использ)") is { Success: true } nh)
            result.Add("hyphenation", false, Confidence.High, QuoteSentence(original, nh));
        else if (Regex.Match(t, @"автоматическ\w*\s+перенос\w*|перенос\w*\s+(?:слов\s+)?(?:допуска|разреш|включ)") is { Success: true } yh)
            result.Add("hyphenation", true, Confidence.Medium, QuoteSentence(original, yh));

        ParsePageNumber(t, original, result);
        ParseHeadings(t, original, result);
        ParseCaptions(t, original, result);

        result.Verdict = Standards.Match(result);
        return result;
    }

    static void ParseMargins(string t, string original, RulesDetection result)
    {
        double? left = null, right = null, top = null, bottom = null;
        Match? first = null;
        foreach (Match m in MarginSide().Matches(t))
        {
            // «верхнее и нижнее — 20 мм», «левое – 3 см»; без единиц — только в контексте полей
            if (!TryNumber(m.Groups[3].Value, out var v)) continue;
            var unit = m.Groups[4].Value;
            var sides = SideCount(Sentence(t, m.Index));
            if (unit.Length == 0 && !Near(t, m.Index, "пол", 60) && sides < 2) continue;
            if (IsIndentContext(t, m.Index) && sides < 2) continue;
            var mm = unit.StartsWith("см") || (unit.Length == 0 && v <= 5) ? v * 10 : v;
            if (mm is < 0 or > 60) continue;
            foreach (var side in new[] { m.Groups[1].Value, m.Groups[2].Value }.Where(s => s.Length > 0))
            {
                if (side.StartsWith("лев") || side == "слева") left ??= mm;
                else if (side.StartsWith("прав") || side == "справа") right ??= mm;
                else if (side.StartsWith("верх") || side == "сверху") top ??= mm;
                else if (side.StartsWith("ниж") || side == "снизу") bottom ??= mm;
            }
            first ??= m;
        }
        foreach (Match m in MarginValueSide().Matches(t))
        {
            if (!TryNumber(m.Groups[1].Value, out var v) || (IsIndentContext(t, m.Index) && SideCount(Sentence(t, m.Index)) < 2)) continue;
            var mm = m.Groups[2].Value == "см" ? v * 10 : v;
            if (mm is < 0 or > 60) continue;
            foreach (var side in new[] { m.Groups[3].Value, m.Groups[4].Value }.Where(s => s.Length > 0))
            {
                if (side == "слева") left ??= mm;
                else if (side == "справа") right ??= mm;
                else if (side == "сверху") top ??= mm;
                else bottom ??= mm;
            }
            first ??= m;
        }
        if (MarginsAll().Match(t) is { Success: true } all && TryNumber(all.Groups[1].Value, out var a))
        {
            var mm = all.Groups[2].Value == "см" ? a * 10 : a;
            left ??= mm; right ??= mm; top ??= mm; bottom ??= mm;
            first ??= all;
        }
        var confidence = Confidence.High;
        // «поля 30/15/20/20», «поля (в/н/л/п): 20/20/30/15» — без подсказки порядок «левое/правое/верхнее/нижнее»
        if (first == null && MarginsSlash().Match(t) is { Success: true } slash)
        {
            var hint = SlashOrderHint(slash.Groups[1].Value + " " + t.Substring(slash.Index + slash.Length, Math.Min(25, t.Length - slash.Index - slash.Length)));
            var order = hint ?? ["л", "п", "в", "н"];
            var unit = slash.Groups[6].Value;
            var values = Enumerable.Range(2, 4).Select(i => TryNumber(slash.Groups[i].Value, out var x) ? x : -1).ToArray();
            if (values.All(x => x >= 0))
            {
                for (var i = 0; i < 4; i++)
                {
                    var mm = unit.StartsWith("см") || (unit.Length == 0 && values.Max() <= 5) ? values[i] * 10 : values[i];
                    switch (order[i]) { case "л": left = mm; break; case "п": right = mm; break; case "в": top = mm; break; default: bottom = mm; break; }
                }
                first = slash;
                if (hint == null) confidence = Confidence.Medium;
            }
        }
        if (first == null) return;
        var value = new MarginsValue(left, right, top, bottom);
        result.Add("margins", value, value.IsComplete ? confidence : Confidence.Medium, QuoteSentence(original, first, 140));
    }

    /// <summary>Порядок сторон из подсказки вида «(л/п/в/н)» или «верхнее, нижнее, левое, правое».</summary>
    static string[]? SlashOrderHint(string text)
    {
        var sides = Regex.Matches(text, @"\b(лев\w*|прав\w*|верх\w*|ниж\w*|л|п|в|н)\b").Select(m => m.Value[..1]).Distinct().ToArray();
        return sides.Length == 4 ? sides : null;
    }

    static void ParsePageNumber(string t, string original, RulesDetection result)
    {
        foreach (Match s in PageNumberSentence().Matches(t))
        {
            var x = s.Value;
            var top = Regex.IsMatch(x, @"вверху|верхн");
            var bottom = Regex.IsMatch(x, @"внизу|нижн");
            var center = Regex.IsMatch(x, @"по\s+центру|посередин|центр");
            var right = Regex.IsMatch(x, @"справа|прав\w+\s+(?:угл|сторон|кра)");
            if (!top && !bottom && !center && !right) continue;
            var horizontal = right ? "right" : "center";
            var value = top ? $"top-{horizontal}" : horizontal;
            result.Add("pageNumber", value, (top || bottom) && (center || right) ? Confidence.High : Confidence.Medium, Quote(original, s));
            return;
        }
    }

    static void ParseHeadings(string t, string original, RulesDetection result)
    {
        foreach (Match s in HeadingSentence().Matches(t))
        {
            var x = s.Value;
            if (result["headingAlign"] == null)
            {
                if (Regex.IsMatch(x, @"по\s+центру|посередин|центрир")) result.Add("headingAlign", "center", Confidence.High, Quote(original, s));
                else if (Regex.IsMatch(x, @"с\s+абзац\w*(\s+отступ\w*)?|с\s+красн\w+\s+строк")) result.Add("headingAlign", "indent", Confidence.High, Quote(original, s));
            }
            // «прописными буквами» — весь заголовок заглавными; «с прописной буквы» — только первая буква (ГОСТ Р 2.105)
            if (result["structuralUpper"] == null)
            {
                var structural = Regex.IsMatch(x, @"введени|заключени|содержани|структурн|раздел") ? Confidence.High : Confidence.Medium;
                if (Regex.IsMatch(x, @"(прописн|заглавн|больш)\w*ми(\s+букв\w*)?|капс|верхн\w+\s+регистр|все\s+букв\w*\s+(прописн|заглавн)"))
                    result.Add("structuralUpper", true, structural, Quote(original, s));
                else if (Regex.IsMatch(x, @"с\s+(прописн|заглавн|больш)\w*\s+букв"))
                    result.Add("structuralUpper", false, structural, Quote(original, s));
            }
            if (result["chapterNewPage"] == null && Regex.IsMatch(x, @"(с|на)\s+нов\w*\s+(страниц|лист)"))
                result.Add("chapterNewPage", true, Confidence.High, Quote(original, s));
        }
        // «Каждая глава начинается с новой страницы» — без слова «заголовок»
        if (result["chapterNewPage"] == null && Regex.Match(t, @"(глав\w*|раздел\w*)[^.;\n]{0,40}(с|на)\s+нов\w*\s+(страниц|лист)") is { Success: true } np)
            result.Add("chapterNewPage", true, Confidence.High, Quote(original, np));
    }

    static void ParseCaptions(string t, string original, RulesDetection result)
    {
        if (FigureExample().Match(t) is { Success: true } f)
        {
            result.Add("figurePrefix", f.Value.StartsWith("рис.") ? "Рис." : "Рисунок", Confidence.High, QuoteSentence(original, f));
            result.Add("separator", f.Groups[1].Value == "." ? ". " : " – ", Confidence.High, QuoteSentence(original, f));
        }
        else if (Regex.Match(t, @"«?рис\.\s*\d") is { Success: true } r)
            result.Add("figurePrefix", "Рис.", Confidence.Medium, Quote(original, r));

        foreach (Match s in TableSentence().Matches(t))
        {
            var x = s.Value;
            if (result["tableCaptionAlign"] == null && Regex.IsMatch(x, @"назван\w*|подпис\w*|надпис\w*|слово\s+«?таблица"))
            {
                if (Regex.IsMatch(x, @"по\s+центру|посередин")) result.Add("tableCaptionAlign", "center", Confidence.High, Quote(original, s));
                else if (Regex.IsMatch(x, @"слева|по\s+лев\w*\s+кра|без\s+абзац")) result.Add("tableCaptionAlign", "left", Confidence.High, Quote(original, s));
            }
            if (result["tableFontSize"] == null && Regex.Match(x, @"(\d{1,2})\s*(?:-?\s*(?:й|м)\s+)?(?:пт|pt|пункт\w*|кегл\w*)|(?:кегл\w*|шрифт\w*|размер\w*)\D{0,15}(\d{1,2})") is { Success: true } m)
            {
                var v = int.Parse(m.Groups[1].Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : m.Groups[2].Value);
                if (v is >= 8 and <= 16) result.Add("tableFontSize", (double)v, Confidence.High, Quote(original, s));
            }
        }
    }

    // ------------------------------------------------------------ помощники

    static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е').Replace(' ', ' ').Replace(' ', ' ')
        .Replace('−', '-').Replace("“", "\"").Replace("”", "\"");

    static bool TryNumber(string s, out double v) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    static bool Near(string t, int index, string word, int radius)
    {
        var start = Math.Max(0, index - radius);
        return t.Substring(start, Math.Min(t.Length - start, radius * 2)).Contains(word, StringComparison.Ordinal);
    }

    static string Sentence(string t, int index)
    {
        var start = t.LastIndexOfAny(['.', ';', '\n'], Math.Max(0, index - 1)) + 1;
        var end = t.IndexOfAny(['.', ';', '\n'], index);
        return t[start..(end < 0 ? t.Length : end)];
    }

    /// <summary>Сколько разных сторон листа названо в предложении (слева/справа/сверху/снизу, левое/правое…).</summary>
    static int SideCount(string sentence) =>
        new[] { @"\bлев\w*|слева", @"\bправ\w*|справа", @"\bверх\w*|сверху", @"\bниж\w*|снизу" }.Count(p => Regex.IsMatch(sentence, p));

    static bool IsTableContext(string t, int index) => Sentence(t, index).Contains("таблиц", StringComparison.Ordinal);
    static bool IsMarginBefore(string t, int index)
    {
        var start = t.LastIndexOfAny(['.', ';', '\n'], Math.Max(0, index - 1)) + 1;
        return Regex.IsMatch(t[start..index], @"пол[еяй]\b|поля\b");
    }

    static bool IsMarginContext(string t, int index) => Regex.IsMatch(Sentence(t, index), @"пол[еяй]\b|поля\b");
    static bool IsIndentContext(string t, int index) => Regex.IsMatch(Sentence(t, index), @"отступ|красн\w* строк") && !Regex.IsMatch(Sentence(t, index), @"пол[еяй]\b");

    /// <summary>Цитата — всё предложение, в котором найдено правило.</summary>
    static string QuoteSentence(string original, Match m, int max = 110)
    {
        // Конец предложения — . ; ! ? или перевод строки, но не точка внутри числа («1.5»)
        bool IsStop(int i) => original[i] is ';' or '\n' or '!' or '?'
            || (original[i] == '.' && !(i > 0 && i + 1 < original.Length && char.IsDigit(original[i - 1]) && char.IsDigit(original[i + 1])));
        var start = Math.Max(0, m.Index - 1);
        while (start >= 0 && !IsStop(start)) start--;
        start++;
        // Предложение — до ближайшего знака конца после найденного фрагмента
        var end = m.Index + m.Length;
        while (end < original.Length && !IsStop(end)) end++;
        // Длинное предложение (сообщение в мессенджере) — сокращаем до фрагмента между запятыми
        if (end - start > max)
        {
            var clauseStart = original.LastIndexOf(',', Math.Max(start, m.Index - 1), Math.Max(start, m.Index - 1) - start + 1);
            start = clauseStart >= start ? clauseStart + 1 : start;
            var clauseEnd = original.IndexOf(',', m.Index + m.Length, end - (m.Index + m.Length));
            end = clauseEnd >= 0 ? clauseEnd : end;
        }
        var q = Regex.Replace(original[start..end], @"\s+", " ").Trim().TrimEnd(',', ';');
        if (q.Length > max) q = q[..(max - 1)] + "…";
        return $"«{q}»";
    }

    /// <summary>Цитата из исходного текста вокруг совпадения — обоснование для пользователя.</summary>
    static string Quote(string original, Match m, int max = 90)
    {
        var start = Math.Max(0, m.Index);
        var length = Math.Min(original.Length - start, Math.Max(m.Length, 1));
        var q = original.Substring(start, length).Trim().TrimEnd(',', ';');
        q = Regex.Replace(q, @"\s+", " ");
        if (q.Length > max) q = q[..(max - 1)] + "…";
        return $"«{q}»";
    }
}
