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
