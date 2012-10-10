using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XSharp.Nasm
{
	public interface IAssembler
	{
		void EmitComment(string comment);
		void EmitNewLine();

		void EmitLabel(string label);
		void EmitExitLabel(string @namespace, string function);

		void EmitConditionJump(string condition, string target);
		void EmitConditionJump(string condition, bool invert, string target);

		void Call(string target);
		void Jump(string target);
		void IRet();
		void Ret();

		void EmitInstruction(string instruction);
		void EmitInstruction(string instruction, string operand1);
		void EmitInstruction(string instruction, string operand1, string operand2);
		void EmitInstruction(string instruction, string operand1, string operand2, string operand3);

		void EmitConstant(string label, object value);

		// FUTURE -
		// TODO: 1) pass line number
		// TODO: 2) enumerate jump conditions with enum type
		// TODO: 3) enumerate instructions with enum type
		// TODO: 4) normalize operands
		// TODO: 5) add more instruction methods
	}
}
