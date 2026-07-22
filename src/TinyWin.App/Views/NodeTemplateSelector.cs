using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyWin.App.ViewModels;

namespace TinyWin.App.Views;

/// <summary>Picks the category or component template for a row of the Customize tree.</summary>
public sealed partial class NodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Category { get; set; }

    public DataTemplate? Component { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        CategoryNode => Category,
        ComponentNode => Component,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
