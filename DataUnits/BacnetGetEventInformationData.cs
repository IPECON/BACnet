namespace System.IO.BACnet;

/*
GetEventInformation-ACK ::= SEQUENCE {
 listOfEventSummaries [0] SEQUENCE OF SEQUENCE {
 objectIdentifier [0] BACnetObjectIdentifier,
 eventState [1] BACnetEventState,
 acknowledgedTransitions [2] BACnetEventTransitionBits,
 eventTimeStamps [3] SEQUENCE SIZE (3) OF BACnetTimeStamp,
 notifyType [4] BACnetNotifyType,
 eventEnable [5] BACnetEventTransitionBits,
 eventPriorities [6] SEQUENCE SIZE (3) OF Unsigned
 },
 moreEvents [1] BOOLEAN
}
*/
public class BacnetGetEventInformationData
{
    public BacnetObjectId objectIdentifier;
    public BacnetEventStates eventState;
    public BacnetBitString acknowledgedTransitions;
    public BacnetGenericTime[] eventTimeStamps;    //3
    public BacnetNotifyTypes notifyType;
    public BacnetBitString eventEnable;
    public uint[] eventPriorities;     //3
}
