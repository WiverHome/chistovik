using System.Windows.Controls;
using System.Windows.Data;
using ShablonRabot.App.ViewModels;

namespace ShablonRabot.App.Views;

public partial class ZoomDialogView : UserControl
{
    public ZoomDialogView()
    {
        Resources["Modes"] = new[] { new Choice(0, "Титул"), new Choice(1, "Страница"), new Choice(2, "Содержание") };
        InitializeComponent();
    }

    /// <summary>Лист A4 (1 : 1,414) на всю высоту диалога + поля.</summary>
    public static readonly IValueConverter WidthFromHeight = new Func<object, object>(v => ((double)v * 0.9 - 48 - 80) * 0.707 + 48).ToConverter();

    public static readonly IValueConverter NinetyPercent = new Func<object, object>(v => (double)v * 0.9 - 48).ToConverter();
}
