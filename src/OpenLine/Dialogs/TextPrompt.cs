using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenLine.Dialogs;

public static class TextPrompt
{
    public static async Task<string?> AskAsync(XamlRoot root, string title, string initial, string confirm)
    {
        var box = new TextBox { Text = initial, MaxLength = 40 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = title,
            Content = box,
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        var text = box.Text.Trim();
        return result == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }
}
