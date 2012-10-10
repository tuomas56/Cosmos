using System;
using CPUx86 = Cosmos.Assembler.x86;

namespace Cosmos.IL2CPU.X86.IL
{
    [Cosmos.IL2CPU.OpCode(ILOpCode.Code.Pop)]
    public class Pop : ILOp
    {
        public Pop(Cosmos.Assembler.Assembler aAsmblr)
            : base(aAsmblr)
        {
        }

        public override void Execute(MethodInfo aMethod, ILOpCode aOpCode)
        {
            // todo: implement exception support.
            if (Assembler.Stack.Count > 0)
            {
                var xItem = Assembler.Stack.Pop();
                new CPUx86.Add { DestinationReg = CPUx86.Registers.ESP, SourceValue = ILOp.Align((uint)xItem.Size, 4) };
            }
            else
            {
                throw new Exception("Cosmos.IL2CPU.X86->IL->Pop.cs: Cannot pop from the stack, because there isn't anything on the stack to pop!");
            }
        }

    }
}
