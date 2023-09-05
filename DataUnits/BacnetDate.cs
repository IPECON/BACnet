namespace System.IO.BACnet;

/*
Date ::= [APPLICATION 10] OCTET STRING (SIZE(4)) -- see 20.2.12
-- first octet year minus 1900 X'FF' = unspecified
-- second octet month (1.. 14) 1 = January
-- 13 = odd months
-- 14 = even months
-- X'FF' = unspecified
-- third octet day of month (1..32), 32 = last day of month
-- X'FF' = unspecified
-- fourth octet day of week (1..7) 1 = Monday
-- 7 = Sunday
-- X'FF' = unspecified 
*/
public struct BacnetDate : ASN1.IEncode, ASN1.IDecode
{
    public byte Year;     /* 255 any */
    public byte Month;      /* 1=Jan; 255 any, 13 Odd, 14 Even */
    public byte Day;        /* 1..31; 32 last day of the month; 255 any */
    public byte Wday;       /* 1=Monday-7=Sunday, 255 any */

    public bool IsPeriodic => Year == 255 || Month > 12 || Day == 255;

    public BacnetDate(byte year, byte month, byte day, byte wday = 255)
    {
        Year = year;
        Month = month;
        Day = day;
        Wday = wday;
    }

    public BacnetDate(DateTime? dateTime)
    {
        if (dateTime == null || dateTime == DateTime.MinValue)
        {
            Year = 0xFF;
            Month = 0xFF;
            Day = 0xFF;
            Wday = 0xFF;
            return;
        }

        Year = (byte)(dateTime.Value.Year - 1900);
        Month = (byte)dateTime.Value.Month;
        Day = (byte)dateTime.Value.Day;
        Wday = dateTime.Value.DayOfWeek == DayOfWeek.Sunday ? (byte)7 : (byte)dateTime.Value.DayOfWeek;
    }

    public void Encode(EncodeBuffer buffer)
    {
        buffer.Add(Year);
        buffer.Add(Month);
        buffer.Add(Day);
        buffer.Add(Wday);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        Year = buffer[offset];
        Month = buffer[offset + 1];
        Day = buffer[offset + 2];
        Wday = buffer[offset + 3];
        return 4;
    }

    public bool IsAFittingDate(DateTime date)
    {
        if (date.Year != Year + 1900 && Year != 255)
            return false;

        if (date.Month != Month && Month != 255 && Month != 13 && Month != 14)
            return false;
        if (Month == 13 && (date.Month & 1) != 1)
            return false;
        if (Month == 14 && (date.Month & 1) == 1)
            return false;

        if (date.Day != Day && Day != 255)
            return false;
        // day 32 todo

        if (Wday == 255)
            return true;

        if (Wday == 7 && date.DayOfWeek == 0)  // Sunday 7 for Bacnet, 0 for .NET
            return true;

        if (Wday == (int)date.DayOfWeek)
            return true;

        return false;
    }

    public DateTime ToDateTime() // Not every time possible, too much complex (any month, any year ...)
    {
        if (!IsPeriodic && (Day > 31 || Month > 12 || Year == 0xFF || Wday > 7))
        {
            throw new InvalidOperationException($"This instance of {nameof(BacnetDate)} cannot be converted do {nameof(DateTime)}");
        }

        return IsPeriodic
            ? new DateTime(1, 1, 1)
            : new DateTime(Year + 1900, Month, Day);
    }

    private static string GetDayName(int day)
    {
        if (day == 7)
            day = 0;

        return CultureInfo.CurrentCulture.DateTimeFormat.DayNames[day];
    }

    public override string ToString()
    {
        string ret;

        if (Wday != 255)
            ret = GetDayName(Wday) + " ";
        else
            ret = "";

        if (Day != 255)
            ret = ret + Day + "/";
        else
            ret += "**/";

        switch (Month)
        {
            case 13:
                ret += "odd/";
                break;
            case 14:
                ret += "even/";
                break;
            case 255:
                ret += "**/";
                break;
            default:
                ret = ret + Month + "/";
                break;
        }


        if (Year != 255)
            ret += Year + 1900;
        else
            ret += "****";

        return ret;
    }
}
