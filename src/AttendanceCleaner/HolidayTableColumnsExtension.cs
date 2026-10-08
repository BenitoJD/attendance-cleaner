using Microsoft.Maui.Controls.Xaml;

namespace AttendanceCleaner;

/// <summary>Creates independent columns for each virtualized row and its header.</summary>
[AcceptEmptyServiceProvider]
public sealed class HolidayTableColumnsExtension : IMarkupExtension<ColumnDefinitionCollection>
{
    public ColumnDefinitionCollection ProvideValue(IServiceProvider serviceProvider) =>
    [
        new(new GridLength(190)),
        new(GridLength.Star),
        new(GridLength.Star),
        new(new GridLength(168)),
    ];

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
