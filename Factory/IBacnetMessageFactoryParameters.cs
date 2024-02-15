namespace System.IO.BACnet.Factory
{
    public interface IBacnetMessageFactoryParameters
    {
        BacnetMaxSegments MaxSegments { get; }
        BacnetMaxAdpu MaxApduLength { get; }
        BacnetPduTypes PduConfirmedServiceRequest();
    }
}
