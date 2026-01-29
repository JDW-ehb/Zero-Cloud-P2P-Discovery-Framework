namespace ZCL.Protocols.ZCSP
{
    public interface IZcspConnector
    {
        Task ConnectAsync(string host, int port, string serviceName);
    }
}
