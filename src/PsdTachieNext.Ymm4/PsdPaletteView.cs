using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace PsdTachieNext.Ymm4;

/// <summary>One virtualized logical tree. The host preview remains the final image surface.</summary>
public sealed class PsdPaletteView : UserControl
{
    private PsdPaletteViewModel? attached;
    public PsdPaletteView()
    {
        MinWidth = 340; MinHeight = 240;
        var root = new DockPanel { Margin = new Thickness(8) };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top);
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PsdPaletteViewModel.Header)));
        heading.Children.Add(title);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        status.SetBinding(TextBlock.TextProperty, new Binding(nameof(PsdPaletteViewModel.Status)));
        heading.Children.Add(status);
        var refresh = Button("対象を再確認", nameof(PsdPaletteViewModel.ReloadCommand));
        refresh.HorizontalAlignment = HorizontalAlignment.Left; heading.Children.Add(refresh);
        var orientation = new TextBlock { Margin = new Thickness(0, 6, 0, 2) };
        orientation.SetBinding(TextBlock.TextProperty, new Binding(nameof(PsdPaletteViewModel.Orientation))); heading.Children.Add(orientation);
        var flips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        flips.SetBinding(IsEnabledProperty, new Binding(nameof(PsdPaletteViewModel.CanEdit)));
        foreach (var (label, command) in new[] { ("向きを継承", nameof(PsdPaletteViewModel.InheritFlipCommand)),
            ("通常", nameof(PsdPaletteViewModel.NoneCommand)), ("左右反転", nameof(PsdPaletteViewModel.XCommand)),
            ("上下反転", nameof(PsdPaletteViewModel.YCommand)), ("両方反転", nameof(PsdPaletteViewModel.XYCommand)) })
            flips.Children.Add(Button(label, command));
        heading.Children.Add(flips); root.Children.Add(heading);
        var rows = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        rows.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(PsdPaletteViewModel.Rows)));
        VirtualizingStackPanel.SetIsVirtualizing(rows, true);
        VirtualizingStackPanel.SetVirtualizationMode(rows, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(rows, true);
        AutomationProperties.SetName(rows, "PSDの部分設定。チェックでこの対象の表示を指定し、継承に戻すで指定を解除します");
        rows.ItemTemplate = (DataTemplate)XamlReader.Parse("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Margin="0,2">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                <CheckBox Content="{Binding Label}" IsChecked="{Binding Visible,Mode=OneWay}" Command="{Binding ToggleCommand}"
                  IsEnabled="{Binding ToggleEnabled}" VerticalAlignment="Center" />
                <TextBlock Grid.Column="1" Text="{Binding Ownership}" VerticalAlignment="Center" Margin="8,0" />
                <Button Grid.Column="2" Content="継承に戻す" Command="{Binding InheritCommand}" Padding="4,1" />
              </Grid>
            </DataTemplate>
            """);
        root.Children.Add(rows); Content = root;
        Loaded += (_, _) => Attach(DataContext as PsdPaletteViewModel);
        DataContextChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OldValue, attached)) (e.OldValue as PsdPaletteViewModel)?.Suspend();
            Attach(IsLoaded ? e.NewValue as PsdPaletteViewModel : null);
            if (!IsLoaded) (e.NewValue as PsdPaletteViewModel)?.Suspend();
        };
        Unloaded += (_, _) => Attach(null);
    }
    private void Attach(PsdPaletteViewModel? next)
    {
        if (ReferenceEquals(attached, next)) return;
        attached?.Suspend(); attached = next; attached?.Resume();
    }
    private static Button Button(string label, string command)
    {
        var result = new Button { Content = label, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 0) };
        result.SetBinding(System.Windows.Controls.Button.CommandProperty, new Binding(command));
        AutomationProperties.SetName(result, label); return result;
    }
}
