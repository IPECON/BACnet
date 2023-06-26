namespace System.IO.BACnet
{
    public class BacnetIHaveData
    {
        public BacnetObjectId DeviceId { get; }
        public BacnetObjectId ObjectId { get; }
        public string ObjectName { get; }

        public BacnetIHaveData(BacnetObjectId deviceId, BacnetObjectId objectId, string objectName)
        {
            DeviceId = deviceId;
            ObjectId = objectId;
            ObjectName = objectName;
        }
    }
}
