namespace System.IO.BACnet;

/*
BACnetSpecialEvent ::= SEQUENCE {
 period CHOICE {
 calendarEntry [0] BACnetCalendarEntry,
 calendarReference [1] BACnetObjectIdentifier
 },
 listOfTimeValues [2] SEQUENCE OF BACnetTimeValue,
 eventPriority [3] Unsigned (1..16)
 }
*/
public class BacnetSpecialEvent : ASN1.IEncode, ASN1.IDecode
{
    public BacnetCalendarEntry? CalendarEntry { get; set; }
    public BacnetObjectId? CalendarReference { get; set; }
    public List<BacnetTimeValue> ListOfTimeValues { get; set; }
    public uint Priority { get; set; }

    public void Encode(EncodeBuffer buffer)
    {
        if (CalendarEntry != null)
        {
            ASN1.encode_opening_tag(buffer, 0);
            CalendarEntry.Encode(buffer);
            ASN1.encode_closing_tag(buffer, 0);
        }
        else if (CalendarReference != null)
        {
            ASN1.encode_context_object_id(buffer, 1, CalendarReference.Value.Type, CalendarReference.Value.Instance);
        }
        else
        {
            throw new InvalidOperationException();
        }

        ASN1.encode_opening_tag(buffer, 2);
        foreach (var bacnetTimeValue in ListOfTimeValues)
        {
            bacnetTimeValue.Encode(buffer);
        }

        ASN1.encode_closing_tag(buffer, 2);
        ASN1.encode_context_unsigned(buffer, 3, Priority);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        int len = 0;
        len += ASN1.decode_tag_number(buffer, offset, out var tagName);

        if (tagName == 0)
        {
            CalendarEntry = new BacnetCalendarEntry();
            len += CalendarEntry.Decode(buffer, offset + len, count);
        }
        else if (tagName == 1)
        {
            len += ASN1.decode_object_id(buffer, offset + len, out BacnetObjectTypes objectType, out var instance);
            CalendarReference = new BacnetObjectId(objectType, instance);
        }
        else
        {
            throw new NotSupportedException();
        }

        len += ASN1.decode_tag_number(buffer, offset + len, out tagName);

        if (tagName == 2)
        {
            ListOfTimeValues = new List<BacnetTimeValue>();
            while (true)
            {
                BacnetTimeValue value = new BacnetTimeValue();
                len += value.Decode(buffer, offset + len, count);
                ListOfTimeValues.Add(value);

                int tagLen = ASN1.decode_tag_number(buffer, offset + len, out tagName);
                if (tagName == 2)
                {
                    // Closing tag
                    len += tagLen;
                    break;
                }
            }
        }

        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagName, out var lenValue);
        if (tagName == 3)
        {
            len += ASN1.decode_unsigned(buffer, offset + len, lenValue, out var priority);
            Priority = priority;
        }

        return len;
    }

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        if (CalendarEntry != null)
        {
            sb.Append(CalendarEntry);
        }
        else if (CalendarReference != null)
        {
            sb.Append(CalendarReference);
        }

        sb.Append(" - ");
        sb.Append($"{ListOfTimeValues.Count} time values");
        sb.Append(" - ");
        sb.Append($"Priority: {Priority}");
        return sb.ToString();
    }
}