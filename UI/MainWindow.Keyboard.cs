using System.Windows;
using System.Windows.Input;

namespace RAM;

public partial class MainWindow
{
    private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            SendButton_Click(sender, new RoutedEventArgs());
        }
    }
}