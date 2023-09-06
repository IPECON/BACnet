namespace System.IO.BACnet;

public enum MonthOptions : byte
{
    January = 1,
    February = 2,
    March = 3,
    April = 4,
    May = 5,
    June = 6,
    July = 7,
    August = 8,
    September = 9,
    October = 10,
    November = 11,
    December = 12,
    OddMonths = 13,
    EvenMonths = 14,
    AnyMonth = 0xFF,
}

public enum WeekOfMonthOptions : byte
{
    DaysNumbered1To7 = 1,
    DaysNumbered8To14 = 2,
    DaysNumbered15To21 = 3,
    DaysNumbered22To28 = 4,
    DaysNumbered29To31 = 5,
    Last7DaysOfThisMonth = 6,
    AnyWeekOfThisMonth = 0xFF,
}

public enum DayOfWeekOptions : byte
{
    Monday = 1,
    Tuesday = 2,
    Wednesday = 3,
    Thursday = 4,
    Friday = 5,
    Saturday = 6,
    Sunday = 7,
    AnyDayOfWeek = 0xFF,
}

/*
BACnetWeekNDay ::= OCTET STRING (SIZE (3))
-- first octet month (1..14) 1 =January
-- 13 = odd months
-- 14 = even months
-- X'FF' = any month
-- second octet weekOfMonth where: 1 = days numbered 1-7
-- 2 = days numbered 8-14
-- 3 = days numbered 15-21
-- 4 = days numbered 22-28
-- 5 = days numbered 29-31
-- 6 = last 7 days of this month
-- X'FF' = any week of this month
-- third octet dayOfWeek (1..7) where 1 = Monday
-- 7 = Sunday
-- X'FF' = any day of week 
*/
public class BacnetWeekNDay : ASN1.IEncode, ASN1.IDecode
{
    public MonthOptions Month;
    public WeekOfMonthOptions WeekOfMonth;
    public DayOfWeekOptions DayOfWeek;

    public BacnetWeekNDay()
    {
        
    }

    public BacnetWeekNDay(byte wday, byte month, byte week = 255)
    {
        DayOfWeek = (DayOfWeekOptions)wday;
        Month = (MonthOptions)month;
        WeekOfMonth = (WeekOfMonthOptions)week;
    }

    public BacnetWeekNDay(DayOfWeekOptions wday, MonthOptions month, WeekOfMonthOptions week = WeekOfMonthOptions.AnyWeekOfThisMonth)
    {
        DayOfWeek = wday;
        Month = month;
        WeekOfMonth = week;
    }

    public BacnetWeekNDay(byte[] data)
    {
        if (data.Length != 3)
        {
            throw new ArgumentException("Byte array must have 3 elements.");
        }

        Month = (MonthOptions)data[0];
        WeekOfMonth = (WeekOfMonthOptions)data[1];
        DayOfWeek = (DayOfWeekOptions)data[2];
    }

    public void Encode(EncodeBuffer buffer)
    {
        buffer.Add((byte)Month);
        buffer.Add((byte)WeekOfMonth);
        buffer.Add((byte)DayOfWeek);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        Month = (MonthOptions)buffer[offset++];
        WeekOfMonth = (WeekOfMonthOptions)buffer[offset++];
        DayOfWeek = (DayOfWeekOptions)buffer[offset];
        return 3;
    }

    public static string GetDayName(DayOfWeekOptions day)
    {
        if (day == DayOfWeekOptions.Sunday)
            day = 0;

        return CultureInfo.CurrentCulture.DateTimeFormat.DayNames[(int)day];
    }

    public override string ToString()
    {
        return $"{Month}/{WeekOfMonth}/{DayOfWeek}";
    }

    public bool IsAFittingDate(DateTime date)
    {
        if (date.Month != (byte)Month && Month != MonthOptions.AnyMonth && Month != MonthOptions.OddMonths && Month != MonthOptions.EvenMonths)
            return false;
        if (Month == MonthOptions.OddMonths && (date.Month & 1) != 1)
            return false;
        if (Month == MonthOptions.EvenMonths && (date.Month & 1) == 1)
            return false;

        if (DayOfWeek == DayOfWeekOptions.AnyDayOfWeek)
            return true;
        if (DayOfWeek == DayOfWeekOptions.Sunday && date.DayOfWeek == 0)  // Sunday 7 for Bacnet, 0 for .NET
            return true;
        if ((byte)DayOfWeek == (byte)date.DayOfWeek)
            return true;

        return false;
    }
}
