using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ShablonRabot.Core;

/// <summary>Собирает .docx-шаблон по профилю: стили, нумерация, титульный лист, структура с подсказками.</summary>
public sealed class TemplateGenerator
{
    // Идентификаторы стилей (имена, которые видит пользователь, задаются отдельно)
    const string Normal = "Normal", H1 = "Heading1", H2 = "Heading2", H3 = "Heading3";
    const string Structural = "StructHeading", TocTitle = "TocTitle", AppendixTitle = "AppendixTitle";
    const string FigureBox = "FigureBox", FigureCaption = "FigureCaption", TableCaption = "TableCaption";
    const string TableText = "TableText", Formula = "Formula", FormulaWhere = "FormulaWhere", Title = "TitlePageText";
    const string DashList = "ListDash", NumList = "ListNumbered", RefList = "ListReferences", Footer = "Footer";

    readonly Profile _p;
    readonly Dictionary<string, string> _fields;
    readonly int _textWidth;
    readonly int _indent;
    Body _body = null!;

    TemplateGenerator(Profile profile)
    {
        _p = profile;
        _fields = Placeholders.Resolve(profile);
        var m = profile.Page.MarginsMm;
        _textWidth = Mm(profile.Page.WidthMm - m.Left - m.Right);
        _indent = Cm(profile.Text.FirstLineIndentCm);
    }

    public static void Generate(Profile profile, string path, bool updateFieldsOnOpen)
    {
        new TemplateGenerator(profile).Build(path, updateFieldsOnOpen);
    }

    // ------------------------------------------------------------ единицы

    static int Mm(double mm) => (int)Math.Round(mm * 56.6929);
    static int Cm(double cm) => Mm(cm * 10);
    static string Tw(double pt) => ((int)Math.Round(pt * 20)).ToString();
    static string HalfPt(double pt) => ((int)Math.Round(pt * 2)).ToString();

    static JustificationValues Jc(string align) => align switch
    {
        "center" => JustificationValues.Center,
        "right" => JustificationValues.Right,
        "justify" => JustificationValues.Both,
        _ => JustificationValues.Left,
    };

    string Fill(string s) => Placeholders.Fill(s, _fields);

    // ------------------------------------------------------------ сборка

    void Build(string path, bool updateFieldsOnOpen)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(_body = new Body());

        main.AddNewPart<StyleDefinitionsPart>().Styles = BuildStyles();
        main.AddNewPart<NumberingDefinitionsPart>().Numbering = BuildNumbering();
        main.AddNewPart<DocumentSettingsPart>().Settings = BuildSettings(updateFieldsOnOpen);

        TitlePageContent();
        StructureContent();
        _body.Append(BuildSection(main));

        doc.PackageProperties.Title = Fill(_fields.GetValueOrDefault("topic", ""));
        doc.PackageProperties.Creator = Fill(_fields.GetValueOrDefault("student", ""));
    }

    Settings BuildSettings(bool updateFieldsOnOpen)
    {
        var s = new Settings(new DefaultTabStop { Val = 708 });
        if (_p.Text.Hyphenation)
            s.Append(new AutoHyphenation());
        if (updateFieldsOnOpen)
            s.Append(new UpdateFieldsOnOpen());
        s.Append(new Compatibility(new CompatibilitySetting
        {
            Name = CompatSettingNameValues.CompatibilityMode,
            Uri = "http://schemas.microsoft.com/office/word",
            Val = "15",
        }));
        return s;
    }

    SectionProperties BuildSection(MainDocumentPart main)
    {
        var num = _p.Page.Numbering;
        // «center», «right» — внизу; «top-center», «top-right» — вверху (ГОСТ Р 7.0.11)
        var top = num.Align.StartsWith("top-", StringComparison.Ordinal);
        var pageNum = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = Footer },
            new Justification { Val = Jc(top ? num.Align[4..] : num.Align) }));
        pageNum.Append(FieldRuns("PAGE", "2", () => new RunProperties(new FontSize { Val = HalfPt(num.SizePt) },
            new FontSizeComplexScript { Val = HalfPt(num.SizePt) })));

        var sect = new SectionProperties();
        if (top)
        {
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(pageNum);
            sect.Append(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(header) });
            // Пустой верхний колонтитул титульного листа, чтобы номер на нём не показывался
            if (!num.ShowOnTitle)
            {
                var firstHeader = main.AddNewPart<HeaderPart>();
                firstHeader.Header = new Header(new Paragraph());
                sect.Append(new HeaderReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(firstHeader) });
            }
        }
        var footer = main.AddNewPart<FooterPart>();
        footer.Footer = new DocumentFormat.OpenXml.Wordprocessing.Footer(top ? new Paragraph() : pageNum);
        sect.Append(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footer) });

        if (!num.ShowOnTitle)
        {
            var first = main.AddNewPart<FooterPart>();
            var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = Title },
                new Justification { Val = JustificationValues.Center }));
            var lines = _p.TitlePage.BottomLines.Select(Fill).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0) p.Append(new Run(new Break()));
                p.Append(TextRun(lines[i]));
            }
            first.Footer = new DocumentFormat.OpenXml.Wordprocessing.Footer(p);
            sect.Append(new FooterReference { Type = HeaderFooterValues.First, Id = main.GetIdOfPart(first) });
        }

        var m = _p.Page.MarginsMm;
        sect.Append(
            new PageSize { Width = (UInt32Value)(uint)Mm(_p.Page.WidthMm), Height = (UInt32Value)(uint)Mm(_p.Page.HeightMm) },
            new PageMargin
            {
                Left = (UInt32Value)(uint)Mm(m.Left), Right = (UInt32Value)(uint)Mm(m.Right),
                Top = Mm(m.Top), Bottom = Mm(m.Bottom),
                Header = 567, Footer = 567, Gutter = 0,
            });
        if (!num.ShowOnTitle)
            sect.Append(new DocumentFormat.OpenXml.Wordprocessing.TitlePage());
        return sect;
    }

    // ------------------------------------------------------------ стили

    sealed record Fmt
    {
        public double? Size { get; init; }
        public bool Bold { get; init; }
        public bool Italic { get; init; }
        public string Align { get; init; } = "left";
        public double FirstLineCm { get; init; }
        public double LeftCm { get; init; }
        public double? LineSpacing { get; init; }
        public double Before { get; init; }
        public double After { get; init; }
        public bool PageBreak { get; init; }
        public bool KeepNext { get; init; }
        public bool NoHyphens { get; init; }
        public int? Outline { get; init; }
        public (int NumId, int Level)? Numbering { get; init; }
        public Tabs? Tabs { get; init; }
    }

    Style ParaStyle(string id, string name, Fmt f, string? basedOn = Normal, bool custom = true)
    {
        var ppr = new StyleParagraphProperties();
        if (f.KeepNext) ppr.KeepNext = new KeepNext();
        if (f.PageBreak) ppr.PageBreakBefore = new PageBreakBefore();
        ppr.WidowControl = new WidowControl();
        if (f.Numbering is { } n)
            ppr.NumberingProperties = new NumberingProperties(new NumberingLevelReference { Val = n.Level }, new NumberingId { Val = n.NumId });
        if (f.Tabs != null) ppr.Tabs = f.Tabs;
        if (f.NoHyphens) ppr.SuppressAutoHyphens = new SuppressAutoHyphens();
        ppr.SpacingBetweenLines = new SpacingBetweenLines
        {
            Before = Tw(f.Before), After = Tw(f.After),
            Line = ((int)Math.Round(240 * (f.LineSpacing ?? _p.Text.LineSpacing))).ToString(),
            LineRule = LineSpacingRuleValues.Auto,
        };
        ppr.Indentation = new Indentation { Left = Cm(f.LeftCm).ToString(), Right = "0", FirstLine = Cm(f.FirstLineCm).ToString() };
        ppr.Justification = new Justification { Val = Jc(f.Align) };
        if (f.Outline is { } o) ppr.OutlineLevel = new OutlineLevel { Val = o };

        var size = f.Size ?? _p.Text.SizePt;
        var rpr = new StyleRunProperties { RunFonts = Fonts() };
        if (f.Bold) { rpr.Bold = new Bold(); rpr.BoldComplexScript = new BoldComplexScript(); }
        else { rpr.Bold = new Bold { Val = false }; rpr.BoldComplexScript = new BoldComplexScript { Val = false }; }
        if (f.Italic) { rpr.Italic = new Italic(); rpr.ItalicComplexScript = new ItalicComplexScript(); }
        rpr.Color = new Color { Val = "000000" };
        rpr.FontSize = new FontSize { Val = HalfPt(size) };
        rpr.FontSizeComplexScript = new FontSizeComplexScript { Val = HalfPt(size) };
        rpr.Languages = new Languages { Val = _p.Text.Language, EastAsia = _p.Text.Language };

        var style = new Style { Type = StyleValues.Paragraph, StyleId = id, CustomStyle = custom ? true : null };
        style.Append(new StyleName { Val = name });
        if (basedOn != null) style.Append(new BasedOn { Val = basedOn });
        style.Append(new NextParagraphStyle { Val = Normal });
        style.Append(new UIPriority { Val = 1 });
        style.Append(new PrimaryStyle());
        style.Append(ppr, rpr);
        return style;
    }

    RunFonts Fonts() => new()
    {
        Ascii = _p.Text.Font, HighAnsi = _p.Text.Font, EastAsia = _p.Text.Font, ComplexScript = _p.Text.Font,
    };

    Fmt HeadingFmt(HeadingStyle h, int outline, (int, int)? numbering = null)
    {
        var centered = h.Align == "center";
        return new Fmt
        {
            Size = h.SizePt, Bold = h.Bold, Align = centered ? "center" : "left",
            FirstLineCm = centered ? 0 : _p.Text.FirstLineIndentCm,
            Before = h.SpaceBeforePt, After = h.SpaceAfterPt, PageBreak = h.NewPage,
            KeepNext = true, NoHyphens = true, Outline = outline, Numbering = numbering,
        };
    }

    Styles BuildStyles()
    {
        var t = _p.Text;
        var cap = _p.Captions;
        var hd = _p.Headings;
        var styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(Fonts(),
                new FontSize { Val = HalfPt(t.SizePt) }, new FontSizeComplexScript { Val = HalfPt(t.SizePt) },
                new Languages { Val = t.Language, EastAsia = t.Language })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "0" }))));

        var normal = ParaStyle(Normal, "Normal", new Fmt { Align = t.Align, FirstLineCm = t.FirstLineIndentCm }, basedOn: null, custom: false);
        normal.Default = true;
        styles.Append(normal);

        // Заголовки 1–3 с автонумерацией 1 / 1.1 / 1.1.1 (numId 1)
        string[] ids = [H1, H2, H3];
        for (var i = 0; i < 3; i++)
            styles.Append(ParaStyle(ids[i], $"heading {i + 1}", HeadingFmt(hd.Levels[i], i, (1, i)), custom: false));

        styles.Append(ParaStyle(Structural, "Структурный заголовок", HeadingFmt(hd.Structural, 0)));
        styles.Append(ParaStyle(TocTitle, "Заголовок содержания", HeadingFmt(hd.Structural, 9)));
        styles.Append(ParaStyle(AppendixTitle, "Название приложения",
            new Fmt { Bold = true, Align = "center", After = 12, KeepNext = true, NoHyphens = true }));
        for (var i = 1; i <= 3; i++)
            styles.Append(ParaStyle($"TOC{i}", $"toc {i}", new Fmt { LeftCm = 0.5 * (i - 1) }, custom: false));

        styles.Append(ParaStyle(FigureBox, "Рисунок", new Fmt { Align = "center", KeepNext = true, LineSpacing = 1.0, Before = 6, After = 6 }));
        styles.Append(ParaStyle(FigureCaption, "Подпись рисунка",
            new Fmt { Size = cap.Figure.SizePt, Align = cap.Figure.Align, After = cap.Figure.SpaceAfterPt, NoHyphens = true }));
        styles.Append(ParaStyle(TableCaption, "Подпись таблицы",
            new Fmt { Size = cap.Table.SizePt, Align = cap.Table.Align, KeepNext = true, Before = 6, NoHyphens = true }));
        styles.Append(ParaStyle(TableText, "Текст таблицы", new Fmt { Size = cap.TableTextSizePt, LineSpacing = 1.0 }));
        styles.Append(ParaStyle(Formula, "Формула", new Fmt
        {
            Before = 6, After = 6,
            Tabs = new Tabs(new TabStop { Val = TabStopValues.Center, Position = _textWidth / 2 },
                            new TabStop { Val = TabStopValues.Right, Position = _textWidth }),
        }));
        styles.Append(ParaStyle(FormulaWhere, "Пояснение к формуле",
            new Fmt { Align = t.Align, FirstLineCm = cap.FormulaWhereIndent ? t.FirstLineIndentCm : 0 }));
        styles.Append(ParaStyle(Title, "Титульный лист",
            new Fmt { Size = _p.TitlePage.FontSizePt, Align = "center", LineSpacing = 1.0, NoHyphens = true }));
        styles.Append(ParaStyle(Footer, "footer", new Fmt { Align = "center", LineSpacing = 1.0 }, custom: false));

        styles.Append(ParaStyle(DashList, "Список тире", new Fmt { Align = t.Align, FirstLineCm = t.FirstLineIndentCm, Numbering = (2, 0) }));
        styles.Append(ParaStyle(NumList, "Список нумерованный", new Fmt { Align = t.Align, FirstLineCm = t.FirstLineIndentCm, Numbering = (3, 0) }));
        styles.Append(ParaStyle(RefList, "Список источников", new Fmt { Align = t.Align, FirstLineCm = t.FirstLineIndentCm, Numbering = (4, 0) }));

        // Таблицы: обычная (по умолчанию, без рамок) и «Сетка таблицы»
        var cellMargins = new TableCellMarginDefault(
            new TopMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
            new TableCellLeftMargin { Width = 108, Type = TableWidthValues.Dxa },
            new BottomMargin { Width = "0", Type = TableWidthUnitValues.Dxa },
            new TableCellRightMargin { Width = 108, Type = TableWidthValues.Dxa });
        styles.Append(new Style(new StyleName { Val = "Normal Table" }, new UIPriority { Val = 99 }, new SemiHidden(),
            new StyleTableProperties(new TableIndentation { Width = 0, Type = TableWidthUnitValues.Dxa }, cellMargins))
        { Type = StyleValues.Table, StyleId = "TableNormal", Default = true });
        BorderType B<TB>() where TB : BorderType, new() => new TB { Val = BorderValues.Single, Size = 4, Space = 0, Color = "000000" };
        styles.Append(new Style(new StyleName { Val = "Table Grid" }, new BasedOn { Val = "TableNormal" }, new UIPriority { Val = 39 },
            new StyleTableProperties(new TableBorders(B<TopBorder>(), B<LeftBorder>(), B<BottomBorder>(), B<RightBorder>(),
                B<InsideHorizontalBorder>(), B<InsideVerticalBorder>())))
        { Type = StyleValues.Table, StyleId = "TableGrid" });
        return styles;
    }

    // ------------------------------------------------------------ нумерация

    Level Lvl(int ilvl, NumberFormatValues fmt, string text, int indent, string? styleId = null, bool bold = false)
    {
        var lvl = new Level { LevelIndex = ilvl };
        lvl.Append(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = fmt });
        if (styleId != null) lvl.Append(new ParagraphStyleIdInLevel { Val = styleId });
        lvl.Append(new LevelSuffix { Val = LevelSuffixValues.Space }, new LevelText { Val = text },
            new LevelJustification { Val = LevelJustificationValues.Left },
            new PreviousParagraphProperties(new Indentation { Left = "0", FirstLine = indent.ToString() }));
        var rpr = new NumberingSymbolRunProperties(Fonts());
        if (bold) rpr.Append(new Bold(), new BoldComplexScript());
        lvl.Append(rpr);
        return lvl;
    }

    Numbering BuildNumbering()
    {
        var hd = _p.Headings;
        string[] texts = ["%1", "%1.%2", "%1.%2.%3"];
        string[] ids = [H1, H2, H3];
        var headings = new AbstractNum(new MultiLevelType { Val = MultiLevelValues.Multilevel }) { AbstractNumberId = 1 };
        for (var i = 0; i < 3; i++)
            headings.Append(Lvl(i, NumberFormatValues.Decimal, texts[i], hd.Levels[i].Align == "center" ? 0 : _indent, ids[i], hd.Levels[i].Bold));

        AbstractNum Single(int id, NumberFormatValues fmt, string text) =>
            new(new MultiLevelType { Val = MultiLevelValues.SingleLevel }, Lvl(0, fmt, text, _indent)) { AbstractNumberId = id };

        var numbering = new Numbering(headings,
            Single(2, NumberFormatValues.Bullet, _p.Lists.Bullet),
            Single(3, NumberFormatValues.Decimal, "%1)"),
            Single(4, NumberFormatValues.Decimal, "%1."));
        for (var i = 1; i <= 4; i++)
            numbering.Append(new NumberingInstance(new AbstractNumId { Val = i }) { NumberID = i });
        return numbering;
    }

    // ------------------------------------------------------------ абзацы и поля

    static Run TextRun(string text, RunProperties? rpr = null)
    {
        var run = new Run();
        if (rpr != null) run.Append(rpr);
        run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    static IEnumerable<Run> FieldRuns(string instr, string placeholder, Func<RunProperties>? rpr = null)
    {
        Run R(OpenXmlElement child)
        {
            var r = new Run();
            if (rpr != null) r.Append(rpr());
            r.Append(child);
            return r;
        }
        yield return R(new FieldChar { FieldCharType = FieldCharValues.Begin });
        yield return R(new FieldCode($" {instr} ") { Space = SpaceProcessingModeValues.Preserve });
        yield return R(new FieldChar { FieldCharType = FieldCharValues.Separate });
        yield return R(new Text(placeholder) { Space = SpaceProcessingModeValues.Preserve });
        yield return R(new FieldChar { FieldCharType = FieldCharValues.End });
    }

    Paragraph Para(string? styleId, string? text = null, ParagraphProperties? ppr = null)
    {
        ppr ??= new ParagraphProperties();
        if (styleId != null) ppr.ParagraphStyleId = new ParagraphStyleId { Val = styleId };
        var p = new Paragraph(ppr);
        if (!string.IsNullOrEmpty(text)) p.Append(TextRun(text));
        _body.Append(p);
        return p;
    }

    string Case(string text, HeadingStyle h) => h.Case == "upper" ? text.ToUpperInvariant() : text;

    // ------------------------------------------------------------ титульный лист

    void TitlePageContent()
    {
        foreach (var block in _p.TitlePage.Blocks)
        {
            if (block.Type == "signatures")
            {
                SignatureTable(block);
                continue;
            }
            for (var i = 0; i < block.Lines.Count; i++)
            {
                var ppr = new ParagraphProperties();
                if (i == 0 && block.SpaceBeforePt > 0) ppr.SpacingBetweenLines = new SpacingBetweenLines { Before = Tw(block.SpaceBeforePt) };
                if (block.IndentLeftCm > 0) ppr.Indentation = new Indentation { Left = Cm(block.IndentLeftCm).ToString() };
                ppr.Justification = new Justification { Val = Jc(block.Align) };
                var p = Para(Title, null, ppr);
                var text = Fill(block.Lines[i]);
                if (text.Length == 0) continue;
                var rpr = new RunProperties();
                if (block.Bold) rpr.Append(new Bold(), new BoldComplexScript());
                if (block.SizePt is { } s) rpr.Append(new FontSize { Val = HalfPt(s) }, new FontSizeComplexScript { Val = HalfPt(s) });
                p.Append(TextRun(text, rpr.HasChildren ? rpr : null));
            }
        }
    }

    void SignatureTable(TitleBlock block)
    {
        Para(Title, null, new ParagraphProperties { SpacingBetweenLines = new SpacingBetweenLines { Before = Tw(block.SpaceBeforePt) } });
        var widths = block.ColWidthsCm.Select(Cm).ToList();
        var table = new Table(new TableProperties(
            new TableWidth { Width = widths.Sum().ToString(), Type = TableWidthUnitValues.Dxa },
            new TableLayout { Type = TableLayoutValues.Fixed }));
        table.Append(new TableGrid(widths.Select(w => new GridColumn { Width = w.ToString() })));
        foreach (var row in block.Rows)
        {
            var tr = new TableRow();
            for (var c = 0; c < row.Count; c++)
            {
                var tcPr = new TableCellProperties(new TableCellWidth { Width = widths[c].ToString(), Type = TableWidthUnitValues.Dxa });
                if (c == row.Count - 1) tcPr.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Bottom });
                var cell = new TableCell(tcPr);
                var lines = Fill(row[c]).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var ppr = new ParagraphProperties(new ParagraphStyleId { Val = Title })
                    {
                        SpacingBetweenLines = new SpacingBetweenLines { After = i == lines.Length - 1 ? "240" : "0" },
                        Justification = new Justification { Val = JustificationValues.Left },
                    };
                    cell.Append(new Paragraph(ppr, TextRun(lines[i])));
                }
                tr.Append(cell);
            }
            table.Append(tr);
        }
        _body.Append(table);
    }

    // ------------------------------------------------------------ основная часть

    void StructureContent()
    {
        var hd = _p.Headings;
        foreach (var item in _p.Structure)
        {
            switch (item.Type)
            {
                case "toc":
                    Para(TocTitle, Case(item.Title, hd.Structural));
                    var p = Para(null, null, new ParagraphProperties { Indentation = new Indentation { FirstLine = "0" } });
                    p.Append(FieldRuns($"TOC \\o \"1-{hd.TocLevels}\" \\h \\z \\u",
                        "Содержание обновится при открытии в Word (или: правой кнопкой → «Обновить поле»)."));
                    break;
                case "structural":
                    Para(Structural, Case(item.Title, hd.Structural));
                    BodyContent(item.Body);
                    break;
                case "chapter":
                    Para(H1, Case(item.Title, hd.Level1));
                    foreach (var sec in item.Sections ?? [])
                    {
                        Para(H2, Case(sec.Title, hd.Level2));
                        BodyContent(sec.Body);
                    }
                    break;
                case "references":
                    Para(Structural, Case(item.Title, hd.Structural));
                    foreach (var x in item.Items ?? []) Para(RefList, x);
                    break;
                case "appendix":
                    Para(Structural, Case($"Приложение {item.Letter}", hd.Structural));
                    Para(AppendixTitle, item.Title);
                    BodyContent(item.Body);
                    break;
            }
        }
    }

    void BodyContent(List<BodyItem>? items)
    {
        foreach (var item in items ?? [])
        {
            switch (item.Kind)
            {
                case BodyKind.Text: Para(null, item.Text); break;
                case BodyKind.Dash: item.Items.ForEach(x => Para(DashList, x)); break;
                case BodyKind.Num: item.Items.ForEach(x => Para(NumList, x)); break;
                case BodyKind.H3: Para(H3, Case(item.Text, _p.Headings.Level3)); break;
                case BodyKind.Figure: FigureContent(item.Text); break;
                case BodyKind.Table: TableContent(item.Table!); break;
                case BodyKind.Formula: FormulaContent(item.Formula!); break;
            }
        }
    }

    void Caption(string styleId, string prefix, string seq, string title)
    {
        var p = Para(styleId);
        p.Append(TextRun(prefix + " "));
        p.Append(FieldRuns($"SEQ {seq} \\* ARABIC", "1"));
        p.Append(TextRun(_p.Captions.Separator + title));
    }

    void FigureContent(string title)
    {
        BorderType B<TB>() where TB : BorderType, new() => new TB { Val = BorderValues.Dashed, Size = 6, Space = 24, Color = "808080" };
        Para(FigureBox, "[Место для рисунка]", new ParagraphProperties
        {
            ParagraphBorders = new ParagraphBorders(B<TopBorder>(), B<LeftBorder>(), B<BottomBorder>(), B<RightBorder>()),
            SpacingBetweenLines = new SpacingBetweenLines { Before = "600", After = "600" },
            Indentation = new Indentation { Left = Cm(4).ToString(), Right = Cm(4).ToString() },
        });
        Caption(FigureCaption, _p.Captions.Figure.Prefix, "Рисунок", title);
    }

    void TableContent(TableSpec spec)
    {
        Caption(TableCaption, _p.Captions.Table.Prefix, "Таблица", spec.Caption);
        var n = spec.Header.Count;
        var colW = _textWidth / n;
        var table = new Table(new TableProperties(
            new TableStyle { Val = "TableGrid" },
            new TableWidth { Width = (colW * n).ToString(), Type = TableWidthUnitValues.Dxa },
            new TableJustification { Val = TableRowAlignmentValues.Center },
            new TableLayout { Type = TableLayoutValues.Fixed }));
        table.Append(new TableGrid(Enumerable.Range(0, n).Select(_ => new GridColumn { Width = colW.ToString() })));

        TableCell Cell(string text, bool header)
        {
            var ppr = new ParagraphProperties(new ParagraphStyleId { Val = TableText });
            if (header) ppr.Justification = new Justification { Val = JustificationValues.Center };
            return new TableCell(new TableCellProperties(new TableCellWidth { Width = colW.ToString(), Type = TableWidthUnitValues.Dxa }),
                new Paragraph(ppr, TextRun(text, header ? new RunProperties(new Bold(), new BoldComplexScript()) : null)));
        }

        // Шапка повторяется на каждой странице
        var header = new TableRow(new TableRowProperties(new TableHeader()));
        header.Append(spec.Header.Select(h => Cell(h, true)));
        table.Append(header);
        foreach (var row in spec.Rows)
            table.Append(new TableRow(row.Select(c => Cell(c, false))));
        _body.Append(table);
        Para(null);
    }

    void FormulaContent(FormulaSpec spec)
    {
        var p = Para(Formula);
        p.Append(new Run(new TabChar()));
        p.Append(TextRun(spec.Expr, new RunProperties(new Italic(), new ItalicComplexScript())));
        p.Append(new Run(new TabChar()), TextRun("("));
        p.Append(FieldRuns("SEQ Формула \\* ARABIC", "1"));
        p.Append(TextRun(")"));
        foreach (var line in spec.Where) Para(FormulaWhere, line);
    }
}
