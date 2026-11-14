using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Y700Switch2V60Viiper;
public partial class ControllerConnectionView : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ShowHintProperty=DependencyProperty.Register(nameof(ShowHint),typeof(bool),typeof(ControllerConnectionView),new PropertyMetadata(true));
    public bool ShowHint { get=>(bool)GetValue(ShowHintProperty);set=>SetValue(ShowHintProperty,value); }
    public static readonly DependencyProperty ShowHeadingProperty=DependencyProperty.Register(nameof(ShowHeading),typeof(bool),typeof(ControllerConnectionView),new PropertyMetadata(true));
    public bool ShowHeading { get=>(bool)GetValue(ShowHeadingProperty);set=>SetValue(ShowHeadingProperty,value); }
    public static readonly DependencyProperty CompactProperty=DependencyProperty.Register(nameof(Compact),typeof(bool),typeof(ControllerConnectionView),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool Compact { get=>(bool)GetValue(CompactProperty);set=>SetValue(CompactProperty,value); }
    private Window? owner;
    public ControllerConnectionView()
    {
        InitializeComponent();
        Loaded+=async(_,_)=>
        {
            owner=Window.GetWindow(this);
            if(owner!=null) owner.StateChanged+=OwnerStateChanged;
            if(Window.GetWindow(this)?.DataContext is MainViewModel vm)
                await vm.InitializeControllerChoicesAsync();
        };
        Unloaded+=(_,_)=>
        {
            if(owner!=null) owner.StateChanged-=OwnerStateChanged;
            owner=null; ResetAllMotion();
        };
        IsVisibleChanged+=(_,_)=>{if(!IsVisible) ResetAllMotion();};
        IsEnabledChanged+=(_,_)=>{if(!IsEnabled) ResetAllMotion();};
    }
    private void OwnerStateChanged(object? sender,EventArgs e) { if(owner?.WindowState==WindowState.Minimized) ResetAllMotion(); }
    private void CardInteractionChanged(object sender,System.Windows.Input.MouseEventArgs e) { if(sender is ListBoxItem item) AnimateCard(item,false); }
    private void CardFocusChanged(object sender,KeyboardFocusChangedEventArgs e) { if(sender is ListBoxItem item) AnimateCard(item,false); }
    private void CardUnloaded(object sender,RoutedEventArgs e) { if(sender is ListBoxItem item) ResetMotion(item); }
    private void ConnectionChoices_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        // Selection changes only the existing saved input choice. It never opens hardware.
        if(!IsLoaded) return;
        foreach(var value in e.AddedItems)
            if(ConnectionChoices.ItemContainerGenerator.ContainerFromItem(value) is ListBoxItem item) AnimateCard(item,true);
    }
    private void AnimateCard(ListBoxItem item,bool selected)
    {
        if(!IsLoaded||!IsVisible||!IsEnabled||!SystemParameters.ClientAreaAnimation||owner?.WindowState==WindowState.Minimized)
        { ResetMotion(item); return; }
        if(item.Template.FindName("Artwork",item) is not FrameworkElement artwork) return;
        // WPF may freeze a transform shared by the control template. Give the
        // interactive card its own transform before animating it.
        if(artwork.RenderTransform is not TranslateTransform move||move.IsFrozen)
        { move=new TranslateTransform();artwork.RenderTransform=move; }
        double target=item.IsMouseOver||item.IsKeyboardFocusWithin ? -2 : 0;
        double start=selected ? 3 : move.Y;
        move.BeginAnimation(TranslateTransform.YProperty,null);
        move.Y=target;
        move.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(start,target,TimeSpan.FromMilliseconds(180))
        {EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop});
        if(selected)
            artwork.BeginAnimation(OpacityProperty,new DoubleAnimation(.72,1,TimeSpan.FromMilliseconds(180)){FillBehavior=FillBehavior.Stop});
    }
    private void ResetAllMotion()
    {
        if(ConnectionChoices==null) return;
        foreach(var value in ConnectionChoices.Items)
            if(ConnectionChoices.ItemContainerGenerator.ContainerFromItem(value) is ListBoxItem item) ResetMotion(item);
    }
    private static void ResetMotion(ListBoxItem item)
    {
        if(item.Template?.FindName("Artwork",item) is not FrameworkElement artwork) return;
        artwork.BeginAnimation(OpacityProperty,null);
        if(artwork.RenderTransform is TranslateTransform {IsFrozen:false} move) {move.BeginAnimation(TranslateTransform.YProperty,null);move.Y=0;}
    }
    private void Pairing_Click(object sender,RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth"){UseShellExecute=true}); }
        catch(Exception error) { if(Window.GetWindow(this)?.DataContext is MainViewModel vm) vm.Status="无法打开 Windows 蓝牙设置："+error.Message; }
    }
}
