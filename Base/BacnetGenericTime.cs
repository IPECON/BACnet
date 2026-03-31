namespace System.IO.BACnet;

public struct BacnetGenericTime
{
    public BacnetTimestampTags Tag { get; set; }
    public DateTime Time { get; set; }
    public ushort Sequence { get; set; }

    public BacnetGenericTime(DateTime time, BacnetTimestampTags tag, ushort sequence = 0)
    {
        Time = time;
        Tag = tag;
        Sequence = sequence;
    }

    public override string ToString()
    {
        return $"{Time}";
    }
}
