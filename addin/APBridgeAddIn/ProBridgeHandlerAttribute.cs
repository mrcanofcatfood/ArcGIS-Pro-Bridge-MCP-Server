using System;

namespace APBridgeAddIn
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class ProBridgeHandlerAttribute : Attribute
    {
        public string Op { get; }
        public ProBridgeHandlerAttribute(string op) => Op = op;
    }
}
