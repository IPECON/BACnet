namespace System.IO.BACnet;

//BACnetDailySchedule ::= SEQUENCE {
// day-schedule [0] SEQUENCE OF BACnetTimeValue
// } 
public class BacnetDailySchedule : ASN1.IEncode, ASN1.IDecode
{
    public List<BacnetTimeValue> DaySchedule { get; set; } = new();

    public void Encode(EncodeBuffer buffer)
    {
        ASN1.encode_opening_tag(buffer, 0);

        foreach (var timeValue in DaySchedule)
        {
            timeValue.Encode(buffer);
        }

        ASN1.encode_closing_tag(buffer, 0);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        int len = 0;

        len += ASN1.decode_tag_number_and_value(buffer, offset, out var tagNumber, out _);

        if (tagNumber != 0)
        {
            throw new Exception("Invalid decoder state.");
        }

        DaySchedule = new List<BacnetTimeValue>();
        while (offset + len < count - 1)
        {
            BacnetTimeValue timeValue = new BacnetTimeValue();
            len += timeValue.Decode(buffer, offset + len, count);
            DaySchedule.Add(timeValue);
            ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out _);

            if (tagNumber == 0)
            {
                // Closing tag
                len++;
                break;
            }
        }

        return len;
    }
}