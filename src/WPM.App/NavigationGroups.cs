using System.Windows;

namespace WPM.App;

public static class NavigationGroups
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.RegisterAttached(
        "Title", typeof(string), typeof(NavigationGroups), new PropertyMetadata(null));

    public static string? GetTitle(DependencyObject element) => (string?)element.GetValue(TitleProperty);

    public static void SetTitle(DependencyObject element, string? value) => element.SetValue(TitleProperty, value);
}
