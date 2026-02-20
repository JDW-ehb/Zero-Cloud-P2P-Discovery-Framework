using System.Net.Sockets;

namespace ZCL.Protocol.ZCSP
{
    public enum ConnectivityMode
    {
        Routed,
        Direct
    }

    public sealed class ConnectivityManager
    {
        private readonly string _serverHost;
        private readonly int _serverPort;

        public ConnectivityMode Mode { get; private set; } = ConnectivityMode.Direct;

        public event Action<ConnectivityMode>? ModeChanged;

        public ConnectivityManager(string serverHost, int serverPort)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
        }

        public async Task StartMonitoringAsync(CancellationToken ct = default)
        {
            while (!ct.IsCancellationRequested)
            {
                var reachable = await IsServerReachableAsync();

                var newMode = reachable
                    ? ConnectivityMode.Routed
                    : ConnectivityMode.Direct;

                if (newMode != Mode)
                {
                    Mode = newMode;
                    ModeChanged?.Invoke(Mode);
                }

                await Task.Delay(3000, ct);
            }
        }

        private async Task<bool> IsServerReachableAsync()
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(_serverHost, _serverPort);
                var timeout = Task.Delay(1000);

                var completed = await Task.WhenAny(connectTask, timeout);
                return completed == connectTask && client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }
}
