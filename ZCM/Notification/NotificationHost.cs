namespace ZCM.Notifications;

public static class NotificationHost
{
    private static Grid? _layer;

    public static void Initialize(Grid layer)
    {
        _layer = layer;
    }

    public static Grid? Layer => _layer;
}