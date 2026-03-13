using System.Text.Json;

namespace ZCL.API
{
    public sealed class Config
    {
        public static Config Instance { get; } = new();

        public string DBFileName { get; set; } = "services.db";
        public int DiscoveryPort { get; set; } = 2600;
        public string MulticastAddress { get; set; } = "224.0.0.26";
        public ushort ZCDPProtocolVersion { get; set; } = 0;
        public int DiscoveryTimeoutMS { get; set; } = 3 * 1000;
        public string PeerName { get; set; } = Environment.MachineName;

        public string NetworkSecret { get; set; } = "CHANGE_ME_NETWORK_SECRET";

        public string AppDataDirectory { get; set; } =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        private const string ConfigFileName = "zc_config.json";

        private Config() { }

        private sealed class ConfigDto
        {
            public string? DBFileName { get; set; }
            public int? DiscoveryPort { get; set; }
            public string? MulticastAddress { get; set; }
            public ushort? ZCDPProtocolVersion { get; set; }
            public int? DiscoveryTimeoutMS { get; set; }
            public string? PeerName { get; set; }
            public string? NetworkSecret { get; set; }
        }

        public void Load()
        {
            var path = Path.Combine(AppDataDirectory, ConfigFileName);
            if (!File.Exists(path))
                return;

            try
            {
                var json = File.ReadAllText(path);

                var loaded = JsonSerializer.Deserialize<ConfigDto>(json);
                if (loaded == null)
                    return;

                if (!string.IsNullOrWhiteSpace(loaded.DBFileName))
                    DBFileName = loaded.DBFileName;

                if (loaded.DiscoveryPort.HasValue)
                    DiscoveryPort = loaded.DiscoveryPort.Value;

                if (!string.IsNullOrWhiteSpace(loaded.MulticastAddress))
                    MulticastAddress = loaded.MulticastAddress;

                if (loaded.ZCDPProtocolVersion.HasValue)
                    ZCDPProtocolVersion = loaded.ZCDPProtocolVersion.Value;

                if (loaded.DiscoveryTimeoutMS.HasValue)
                    DiscoveryTimeoutMS = loaded.DiscoveryTimeoutMS.Value;

                if (!string.IsNullOrWhiteSpace(loaded.PeerName))
                    PeerName = loaded.PeerName;

                if (!string.IsNullOrWhiteSpace(loaded.NetworkSecret))
                    NetworkSecret = loaded.NetworkSecret;
            }
            catch
            {
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(AppDataDirectory);

            var path = Path.Combine(AppDataDirectory, ConfigFileName);

            var dto = new ConfigDto
            {
                DBFileName = DBFileName,
                DiscoveryPort = DiscoveryPort,
                MulticastAddress = MulticastAddress,
                ZCDPProtocolVersion = ZCDPProtocolVersion,
                DiscoveryTimeoutMS = DiscoveryTimeoutMS,
                PeerName = PeerName,
                NetworkSecret = NetworkSecret
            };

            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(path, json);
        }
    }
}