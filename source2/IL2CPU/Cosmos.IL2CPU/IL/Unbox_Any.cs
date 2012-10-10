using System;
using CPU = Cosmos.Assembler.x86;
using CPUx86 = Cosmos.Assembler.x86;
using Cosmos.IL2CPU.ILOpCodes;
using Cosmos.Assembler;
using Cosmos.IL2CPU.IL.CustomImplementations.System;

namespace Cosmos.IL2CPU.X86.IL
{
    [Cosmos.IL2CPU.OpCode( ILOpCode.Code.Unbox_Any )]
    public class Unbox_Any : ILOp
    {
        public Unbox_Any( Cosmos.Assembler.Assembler aAsmblr )
            : base( aAsmblr )
        {
        }

        public override void Execute( MethodInfo aMethod, ILOpCode aOpCode )
        {
            OpType xType = ( OpType )aOpCode;
            string BaseLabel = GetLabel( aMethod, aOpCode ) + ".";

            string xTypeID = GetTypeIDLabel(xType.Value);

            var xTypeSize = SizeOfType( xType.Value );

            string mReturnNullLabel = BaseLabel + "_ReturnNull";
			new CPUx86.Compare { DestinationReg = CPUx86.Registers.ESP, DestinationIsIndirect = true, SourceValue = 0 };
			new CPUx86.ConditionalJump { Condition = CPUx86.ConditionalTestEnum.Zero, DestinationLabel = mReturnNullLabel };
			new CPUx86.Mov { DestinationReg = CPUx86.Registers.EAX, SourceReg = CPUx86.Registers.ESP, SourceIsIndirect = true };
            new CPUx86.Push { DestinationReg = CPUx86.Registers.EAX, DestinationIsIndirect = true };
            Assembler.Stack.Push( new StackContents.Item( 4, typeof( uint ) ) );
            new CPUx86.Push { DestinationRef = Cosmos.Assembler.ElementReference.New( xTypeID ), DestinationIsIndirect = true };
            Assembler.Stack.Push( new StackContents.Item( 4, typeof( uint ) ) );
            System.Reflection.MethodBase xMethodIsInstance = ReflectionUtilities.GetMethodBase( typeof( VTablesImpl ), "IsInstance", "System.Int32", "System.Int32" );
            new Call(Assembler).Execute(aMethod, new OpMethod(ILOpCode.Code.Call, 0, 0, xMethodIsInstance, aOpCode.CurrentExceptionHandler));

            new Label( BaseLabel + "_After_IsInstance_Call" );
            new CPUx86.Pop { DestinationReg = CPUx86.Registers.EAX };
            Assembler.Stack.Pop();
            new CPUx86.Compare { DestinationReg = CPUx86.Registers.EAX, SourceValue = 0 };
            new CPUx86.ConditionalJump { Condition = CPUx86.ConditionalTestEnum.Equal, DestinationLabel = mReturnNullLabel };
            new CPUx86.Pop { DestinationReg = CPUx86.Registers.EAX };
            uint xSize = xTypeSize;
            if( xSize % 4 > 0 )
            {
                xSize += 4 - ( xSize % 4 );
            }
            int xItems = ( int )xSize / 4;
            for( int i = xItems - 1; i >= 0; i-- )
            {
                new CPUx86.Push { DestinationReg = CPUx86.Registers.EAX, DestinationIsIndirect = true, DestinationDisplacement = ( ( i * 4 ) + ObjectImpl.FieldDataOffset ) };
            }
            Assembler.Stack.Push( new StackContents.Item(xTypeSize, xType.Value ) );
			new CPUx86.Jump { DestinationLabel = GetLabel(aMethod, aOpCode.NextPosition) };
            new Label( mReturnNullLabel );
            new CPUx86.Add { DestinationReg = CPUx86.Registers.ESP, SourceValue = 4 };
            new CPUx86.Push { DestinationValue = 0 };
            Assembler.Stack.Push( new StackContents.Item( 4, typeof( object ) ) );
        }
    }
}