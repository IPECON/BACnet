namespace System.IO.BACnet;

/*
BACnetCalendarEntry ::= CHOICE {
 date[0] Date,
 dateRange[1] BACnetDateRange,
 weekNDay[2] BACnetWeekNDay
 }
*/
public class BacnetCalendarEntry : ASN1.IEncode, ASN1.IDecode
{
    public BacnetDate? Date { get; set; }
    public BacnetDateRange? DateRange { get; set; }
    public BacnetWeekNDay? WeekNDay { get; set; }

    public BacnetCalendarEntry()
    {
        
    }

    public BacnetCalendarEntry(BacnetDate date)
    {
        Date = date;
    }

    public BacnetCalendarEntry(BacnetDateRange dateRange)
    {
        DateRange = dateRange;
    }

    public BacnetCalendarEntry(BacnetWeekNDay weekNDay)
    {
        WeekNDay = weekNDay;
    }

    public void Encode(EncodeBuffer buffer)
    {
        if (Date is not null)
        {
            ASN1.encode_tag(buffer, 0, true, 4);
            Date.Value.Encode(buffer);
            return;
        }

        if (DateRange is not null)
        {
            ASN1.encode_opening_tag(buffer, 1);
            DateRange.Value.Encode(buffer);
            ASN1.encode_closing_tag(buffer, 1);
            return;
        }

        if (WeekNDay is not null)
        {
            ASN1.encode_tag(buffer, 2, true, 3);
            WeekNDay.Value.Encode(buffer);
            return;
        }

        throw new Exception($"Invalid {nameof(BacnetCalendarEntry)}");
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        var len = 0;

        len += ASN1.decode_tag_number(buffer, offset, out byte tagNumber);

        switch (tagNumber)
        {
            case 0:
                var bdt = new BacnetDate();
                len += bdt.Decode(buffer, offset + len, count);
                Date = bdt;
                // Date does NOT have a closing tag
                break;
            case 1:
                var bdr = new BacnetDateRange();
                len += bdr.Decode(buffer, offset + len, count);
                DateRange = bdr;
                len++; // Date Range has a closing tag
                break;
            case 2:
                var bwd = new BacnetWeekNDay();
                len += bwd.Decode(buffer, offset + len, count);
                WeekNDay = bwd;
                // Date does NOT have a closing tag
                break;
        }

        return len + 1; // Closing tag of BacnetCalendarEntry
    }

    public override string ToString()
    {
        if (Date != null)
        {
            return Date.ToString();
        }

        if (DateRange != null)
        {
            return DateRange.ToString();
        }

        if (WeekNDay != null)
        {
            return WeekNDay.ToString();
        }

        return base.ToString();
    }
}
