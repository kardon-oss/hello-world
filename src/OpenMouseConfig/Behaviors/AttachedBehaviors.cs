using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenMouseConfig.Behaviors;

/// <summary>Small attached properties for TextBox: commit-on-Enter and run-command-on-Enter.</summary>
public static class TextBoxBehaviors
{
    /// <summary>Pushes the Text binding to the source when Enter is pressed (for UpdateSourceTrigger=LostFocus boxes).</summary>
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(TextBoxBehaviors), new PropertyMetadata(false, OnCommitOnEnterChanged));

    /// <summary>Executes a command when Enter is pressed.</summary>
    public static readonly DependencyProperty EnterCommandProperty = DependencyProperty.RegisterAttached(
        "EnterCommand", typeof(ICommand), typeof(TextBoxBehaviors), new PropertyMetadata(null, OnEnterCommandChanged));

    public static bool GetCommitOnEnter(DependencyObject d) => (bool)d.GetValue(CommitOnEnterProperty);

    public static void SetCommitOnEnter(DependencyObject d, bool value) => d.SetValue(CommitOnEnterProperty, value);

    public static ICommand? GetEnterCommand(DependencyObject d) => (ICommand?)d.GetValue(EnterCommandProperty);

    public static void SetEnterCommand(DependencyObject d, ICommand? value) => d.SetValue(EnterCommandProperty, value);

    private static void OnCommitOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.KeyDown -= CommitOnEnter;
        box.GotKeyboardFocus -= SelectAllOnFocus;
        if (e.NewValue is true)
        {
            box.KeyDown += CommitOnEnter;
            box.GotKeyboardFocus += SelectAllOnFocus;
        }
    }

    private static void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private static void SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.SelectAll();
        }
    }

    private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.KeyDown -= ExecuteOnEnter;
        if (e.NewValue is not null)
        {
            box.KeyDown += ExecuteOnEnter;
        }
    }

    private static void ExecuteOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && sender is TextBox box && GetEnterCommand(box) is { } command)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        }
    }
}

/// <summary>Keeps a ListBox scrolled to its newest item (console log).</summary>
public static class ListBoxBehaviors
{
    public static readonly DependencyProperty AutoScrollProperty = DependencyProperty.RegisterAttached(
        "AutoScroll", typeof(bool), typeof(ListBoxBehaviors), new PropertyMetadata(false, OnAutoScrollChanged));

    public static bool GetAutoScroll(DependencyObject d) => (bool)d.GetValue(AutoScrollProperty);

    public static void SetAutoScroll(DependencyObject d, bool value) => d.SetValue(AutoScrollProperty, value);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || e.NewValue is not true)
        {
            return;
        }

        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && list.Items.Count > 0 && list.IsVisible)
            {
                list.ScrollIntoView(list.Items[^1]);
            }
        };
    }
}
