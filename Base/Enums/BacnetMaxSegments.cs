namespace System.IO.BACnet;

public enum BacnetMaxSegments : byte
{
    MAX_SEG0 = 0b000,
    MAX_SEG2 = 0b001,
    MAX_SEG4 = 0b010,
    MAX_SEG8 = 0b011,
    MAX_SEG16 = 0b100,
    MAX_SEG32 = 0b101,
    MAX_SEG64 = 0b110,
    MAX_SEG65 = 0b111
}

public static class BacnetMaxSegmentsExtensions
{
    public static byte ToByte(this BacnetMaxSegments maxSegments)
    {
        switch (maxSegments)
        {
            case BacnetMaxSegments.MAX_SEG0:
                return 0;
            case BacnetMaxSegments.MAX_SEG2:
                return 2;
            case BacnetMaxSegments.MAX_SEG4:
                return 4;
            case BacnetMaxSegments.MAX_SEG8:
                return 8;
            case BacnetMaxSegments.MAX_SEG16:
                return 16;
            case BacnetMaxSegments.MAX_SEG32:
                return 32;
            case BacnetMaxSegments.MAX_SEG64:
                return 64;
            case BacnetMaxSegments.MAX_SEG65:
                return 0xFF;
            default:
                throw new Exception("Not an option");
        }
    }

    public static BacnetMaxSegments ToBacnetMaxSegments(byte maxSegments)
    {
        if (maxSegments == 0)
            return BacnetMaxSegments.MAX_SEG0;
        if (maxSegments <= 2)
            return BacnetMaxSegments.MAX_SEG2;
        if (maxSegments <= 4)
            return BacnetMaxSegments.MAX_SEG4;
        if (maxSegments <= 8)
            return BacnetMaxSegments.MAX_SEG8;
        if (maxSegments <= 16)
            return BacnetMaxSegments.MAX_SEG16;
        if (maxSegments <= 32)
            return BacnetMaxSegments.MAX_SEG32;
        if (maxSegments <= 64)
            return BacnetMaxSegments.MAX_SEG64;

        return BacnetMaxSegments.MAX_SEG65;
    }
}
