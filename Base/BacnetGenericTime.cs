namespace System.IO.BACnet;

/// <summary>
/// ASHRAE: BACnetTimeStamp
/// </summary>
public struct BacnetGenericTime
{
    public BacnetTimestampTags Tag; // [2]
    public DateTime Time;   // [0]
    public ushort Sequence; // [1]

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
