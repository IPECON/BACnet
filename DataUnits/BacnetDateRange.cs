namespace System.IO.BACnet;

/*
BACnetDateRange ::= SEQUENCE {
 StartDate Date,
 endDate Date
 }
*/
public class BacnetDateRange : ASN1.IEncode, ASN1.IDecode
{
    public BacnetDate StartDate;
    public BacnetDate EndDate;

    public BacnetDateRange()
    {
        
    }

    public BacnetDateRange(BacnetDate start, BacnetDate end)
    {
        StartDate = start;
        EndDate = end;
    }

    public BacnetDateRange(DateTime start, DateTime end)
    {
        StartDate = new BacnetDate(start);
        EndDate = new BacnetDate(end);
    }

    public void Encode(EncodeBuffer buffer)
    {
        ASN1.encode_tag(buffer, (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE, false, 4);
        StartDate.Encode(buffer);
        ASN1.encode_tag(buffer, (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE, false, 4);
        EndDate.Encode(buffer);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        var len = 1; // opening tag
        StartDate = new BacnetDate();
        len += StartDate.Decode(buffer, offset + len, count);
        len++;
        EndDate = new BacnetDate();
        len += EndDate.Decode(buffer, offset + len, count);
        return len;
    }

    public bool IsAFittingDate(DateTime date)
    {
        date = new DateTime(date.Year, date.Month, date.Day);
        return date >= StartDate.ToDateTime() && date <= EndDate.ToDateTime();
    }

    public override string ToString()
    {
        string ret;

        if (StartDate.Day != 255)
            ret = "From " + StartDate;
        else
            ret = "From **/**/**";

        if (EndDate.Day != 255)
            ret = ret + " to " + EndDate;
        else
            ret += " to **/**/**";

        return ret;
    }
};
