using System.Windows.Input;

namespace ZCM.Controls;

public class DrawerHost : Grid
{
    const uint AnimationMs = 180;

    readonly Grid _drawerContainer;
    readonly Grid _contentContainer;

    public DrawerHost()
    {
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        _drawerContainer = new Grid
        {
            IsVisible = false,
            TranslationX = 0
        };

        _contentContainer = new Grid();

        base.Children.Add(_drawerContainer);
        Grid.SetColumn(_drawerContainer, 0);

        base.Children.Add(_contentContainer);
        Grid.SetColumn(_contentContainer, 1);
    }

    public static readonly BindableProperty DrawerWidthProperty =
        BindableProperty.Create(nameof(DrawerWidth), typeof(double), typeof(DrawerHost), 240d);

    public double DrawerWidth
    {
        get => (double)GetValue(DrawerWidthProperty);
        set => SetValue(DrawerWidthProperty, value);
    }

    public static readonly BindableProperty DrawerProperty =
        BindableProperty.Create(nameof(Drawer), typeof(View), typeof(DrawerHost), null, propertyChanged: OnDrawerChanged);

    public View? Drawer
    {
        get => (View?)GetValue(DrawerProperty);
        set => SetValue(DrawerProperty, value);
    }

    static void OnDrawerChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var host = (DrawerHost)bindable;
        host._drawerContainer.Children.Clear();

        if (newValue is View v)
        {
            v.WidthRequest = host.DrawerWidth;
            v.MinimumWidthRequest = host.DrawerWidth;
            host._drawerContainer.Children.Add(v);
        }
    }

    public static readonly BindableProperty MainProperty =
        BindableProperty.Create(nameof(Main), typeof(View), typeof(DrawerHost), null, propertyChanged: OnMainChanged);

    public View? Main
    {
        get => (View?)GetValue(MainProperty);
        set => SetValue(MainProperty, value);
    }

    static void OnMainChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var host = (DrawerHost)bindable;
        host._contentContainer.Children.Clear();

        if (newValue is View v)
            host._contentContainer.Children.Add(v);
    }

    public static readonly BindableProperty IsOpenProperty =
        BindableProperty.Create(nameof(IsOpen), typeof(bool), typeof(DrawerHost), false, propertyChanged: OnIsOpenChanged);

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    static async void OnIsOpenChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var host = (DrawerHost)bindable;
        if ((bool)newValue)
            await host.OpenAsync();
        else
            await host.CloseAsync();
    }

    public ICommand ToggleCommand => new Command(() => IsOpen = !IsOpen);

    async Task OpenAsync()
    {
        _drawerContainer.IsVisible = true;

        // Put main content on top of drawer and slide it right
        Grid.SetColumn(_contentContainer, 0);
        Grid.SetColumnSpan(_contentContainer, 2);

        await _contentContainer.TranslateTo(DrawerWidth, 0, AnimationMs, Easing.CubicOut);
    }

    async Task CloseAsync()
    {
        await _contentContainer.TranslateTo(0, 0, AnimationMs, Easing.CubicIn);

        Grid.SetColumn(_contentContainer, 1);
        Grid.SetColumnSpan(_contentContainer, 1);

        _drawerContainer.IsVisible = false;
    }
}