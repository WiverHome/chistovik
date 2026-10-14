using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ShablonRabot.App.Views;

public partial class PreviewPane : UserControl
{
    public PreviewPane()
    {
        Resources["Modes"] = new[] { new ViewModels.Choice(0, "Титул"), new ViewModels.Choice(1, "Страница"), new ViewModels.Choice(2, "Содержание") };
        InitializeComponent();
    }

    public static readonly IValueConverter ModeTip = new Func<object, object>(v => v switch
    {
        0 => "Титульный лист",
        1 => "Поля, отступы и образец текста",
        _ => "Содержание по структуре работы",
    }).ToConverter();

    /// <summary>Подсказка Ctrl+Enter — пока нет последнего файла и Word установлен.</summary>
    public static readonly IMultiValueConverter HintVisibility = new HintConverter();

    sealed class HintConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values is [true, false] ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
    }
}
