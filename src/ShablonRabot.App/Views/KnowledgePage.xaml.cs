using System.ComponentModel;
using System.Windows.Controls;
using ShablonRabot.App.Controls;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class KnowledgePage : UserControl
{
    MainViewModel? _main;

    public KnowledgePage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_main != null) _main.Knowledge.PropertyChanged -= OnKnowledgeChanged;
            _main = e.NewValue as MainViewModel;
            if (_main == null) return;
            _main.Knowledge.PropertyChanged += OnKnowledgeChanged;
            Render();
        };
        Unloaded += (_, _) => { if (_main != null) _main.Knowledge.PropertyChanged -= OnKnowledgeChanged; };
    }

    void OnKnowledgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KnowledgeViewModel.Selected)) Render();
    }

    void Render()
    {
        if (_main?.Knowledge.Selected is not { } article) { Reader.Document = null; return; }
        Reader.Document = MarkdownDocument.Build(article.Markdown, _main.OpenLink);
        Reader.ScrollViewer()?.ScrollToTop();
    }
}

static class FlowViewerExtensions
{
    /// <summary>Внутренний ScrollViewer просмотрщика — чтобы новая статья открывалась с начала.</summary>
    public static ScrollViewer? ScrollViewer(this FlowDocumentScrollViewer viewer) =>
        viewer.Template?.FindName("PART_ContentHost", viewer) as ScrollViewer;
}
