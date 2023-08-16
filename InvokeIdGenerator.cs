namespace System.IO.BACnet
{
    internal class InvokeIdGenerator
    {
        private byte _invokeId = 1;
        private readonly object _lock = new();

        public byte GetNext()
        {
            lock (_lock)
            {
                if (_invokeId == byte.MaxValue)
                {
                    _invokeId = 1;
                }
                else
                {
                    _invokeId++;
                }

                return _invokeId;
            }
        }
    }
}
