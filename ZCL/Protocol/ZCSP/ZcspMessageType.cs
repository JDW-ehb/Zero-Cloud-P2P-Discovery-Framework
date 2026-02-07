namespace ZCL.Protocol.ZCSP.Protocol
{
    public enum ZcspMessageType : byte
    {
        ServiceRequest = 1,
        ServiceResponse = 2,
        SessionData = 3,
        SessionClose = 4
    }
}
