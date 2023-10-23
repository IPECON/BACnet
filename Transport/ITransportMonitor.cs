namespace System.IO.BACnet
{
    public interface ITransportMonitor
    {
        void FrameReceived(int size, IPEndPoint ipEndpoint);
        void FrameSent(int size, IPEndPoint ipEndpoint);
    }
}
