using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XSharp.Nasm
{
	public class Assembler : IAssembler
	{
		public List<string> Data = new List<string>();
		public List<string> Code = new List<string>();

		#region Helpers

		protected string InvertComparison(string compareOp)
		{
			if (compareOp == "<")
			{
				return ">=";
			}
			else if (compareOp == ">")
			{
				return "<=";
			}
			else if (compareOp == "=")
			{
				return "!=";
			}
			else if (compareOp == "0")
			{
				// Same as JE, but implies intent in .asm better
				return "!0";
			}
			else if (compareOp == "!0")
			{
				// Same as JE, but implies intent in .asm better
				return "0";
			}
			else if (compareOp == "!=")
			{
				return "=";
			}
			else if (compareOp == "<=")
			{
				return ">";
			}
			else if (compareOp == ">=")
			{
				return "<";
			}
			else
			{
				throw new Exception("Unrecognized symbol in conditional: " + compareOp);
			}
		}

		protected string GetJumpInstruction(string compareOp)
		{
			if (compareOp == "<")
			{
				return "JB";  // unsigned
			}
			else if (compareOp == ">")
			{
				return "JA";  // unsigned
			}
			else if (compareOp == "=")
			{
				return "JE";
			}
			else if (compareOp == "0")
			{
				// Same as JE, but implies intent in .asm better
				return "JZ";
			}
			else if (compareOp == "!=")
			{
				return "JNE";
			}
			else if (compareOp == "!0")
			{
				// Same as JNE, but implies intent in .asm better
				return "JNZ";
			}
			else if (compareOp == "<=")
			{
				return "JBE"; // unsigned
			}
			else if (compareOp == ">=")
			{
				return "JAE"; // unsigned
			}
			else
			{
				throw new Exception("Unrecognized symbol in conditional: " + compareOp);
			}
		}

		#endregion

		#region Legacy

		//public static Assembler operator +(Assembler aThis, string aThat) {
		//  aThis.Code.Add(aThat);
		//  return aThis;
		//}

		public void Mov(string aDst, string aSrc)
		{
			Mov("", aDst, aSrc);
		}

		public void Mov(string aSize, string aDst, string aSrc)
		{
			Code.Add("Mov " + (aSize + " ").TrimStart() + aDst + ", " + aSrc);
		}

		public void Cmp(string aDst, string aSrc)
		{
			Cmp("", aDst, aSrc);
		}

		public void Cmp(string aSize, string aDst, string aSrc)
		{
			Code.Add("Cmp " + (aSize + " ").TrimStart() + aDst + ", " + aSrc);
		}

		#endregion

		public void EmitNewLine()
		{
			Code.Add(string.Empty);
		}

		public void IRet()
		{
			Code.Add("IRet");
		}

		public void Ret()
		{
			Code.Add("Ret");
		}

		public void EmitLabel(string label)
		{
			Code.Add(label + ":");
		}

		public void EmitExitLabel(string @namespace, string function)
		{
			Code.Add(@namespace + "_" + function + "_Exit:");
		}

		public void EmitConditionJump(string condition, string target)
		{
			Code.Add(GetJumpInstruction(condition) + " " + target);
		}

		public void EmitConditionJump(string condition, bool invert, string target)
		{
			if (invert)
			{
				EmitConditionJump(InvertComparison(condition), target);
			}
			else
			{
				EmitConditionJump(condition, target);
			}
		}

		public void EmitComment(string comment)
		{
			Code.Add("; " + comment);
		}

		public void Call(string target)
		{
			Code.Add("Call " + target);
		}

		public void Jump(string target)
		{
			Code.Add("Jmp " + target);
		}

		// TODO: The goal is to replace all calls to this with specific methods for each instruction
		[System.Obsolete()]
		public void EmitRaw(string raw)
		{
			Code.Add(raw);
		}

		public void EmitConstant(string name, object value)
		{
			// TODO
		}

		public void EmitInstruction(string instruction)
		{
			// TODO
		}

		public void EmitInstruction(string instruction, string operand1)
		{
			// TODO
		}

		public void EmitInstruction(string instruction, string operand1, string operand2)
		{
			// TODO
		}

		public void EmitInstruction(string instruction, string operand1, string operand2, string operand3)
		{
			// TODO
		}
	}
}
