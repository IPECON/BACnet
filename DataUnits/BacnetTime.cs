namespace System.IO.BACnet;

/*
Time ::= [APPLICATION 11] OCTET STRING (SIZE(4)) -- see 20.2.13
-- first octet hour (0..23), (X'FF') = unspecified
-- second octet minute (0..59), (X'FF') = unspecified
-- third octet second (0..59), (X'FF') = unspecified
-- fourth octet hundredths (0..99), (X'FF') = unspecified 
*/
public class BacnetTime : ASN1.IEncode, ASN1.IDecode
{
    public byte Hour { get; set; }
    public byte Minute { get; set; }
    public byte Second { get; set; }
    public byte Hundreths { get; set; }

    public BacnetTime()
    {
        
    }

    public BacnetTime(byte hour, byte minute, byte second, byte hundreths)
    {
        Hour = hour;
        Minute = minute;
        Second = second;
        Hundreths = hundreths;
    }

    public BacnetTime(TimeSpan timeSpan)
    {
        Hour = (byte)timeSpan.Hours;
        Minute = (byte)timeSpan.Minutes;
        Second = (byte)timeSpan.Seconds;
        Hundreths = (byte)(timeSpan.Milliseconds / 10);
    }

    public BacnetTime(DateTime dateTime) : this(dateTime.TimeOfDay)
    {
    }

    public TimeSpan ToTimeSpan()
    {
        TimeSpan timeSpan = TimeSpan.Zero;

        if (Hour != 0xFF)
        {
            timeSpan = timeSpan.Add(TimeSpan.FromHours(Hour));
        }

        if (Minute != 0xFF)
        {
            timeSpan = timeSpan.Add(TimeSpan.FromMinutes(Minute));
        }

        if (Second != 0xFF)
        {
            timeSpan = timeSpan.Add(TimeSpan.FromSeconds(Second));
        }

        if (Hundreths != 0xFF)
        {
            timeSpan = timeSpan.Add(TimeSpan.FromMilliseconds(Hundreths * 10));
        }

        return timeSpan;
    }

    public void Encode(EncodeBuffer buffer)
    {
        ASN1.encode_octetString(buffer, new[] { Hour, Minute, Second, Hundreths }, 0, 4);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        Hour = buffer[offset];
        Minute = buffer[offset + 1];
        Second = buffer[offset + 2];
        Hundreths = buffer[offset + 3];
        return 4;
    }

    public override string ToString()
    {
        return new StringBuilder()
            .Append(Hour != 0xff ? Hour.ToString("00") : "*")
            .Append(":")
            .Append(Minute != 0xff ? Minute.ToString("00") : "*")
            .Append(":")
            .Append(Second != 0xff ? Second.ToString("00") : "*")
            .Append(".")
            .Append(Hundreths != 0xff ? (Hundreths * 10).ToString("000") : "*")
            .ToString();
    }
}
