namespace ZCM.Security;

public static class SqlCipherInitializer
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
            return;

        SQLitePCL.Batteries_V2.Init();
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

        _initialized = true;
    }
}