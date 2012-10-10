﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cosmos.IL2CPU.ILOpCodes {
  public class OpSig : ILOpCode {
    public readonly Int32 Value;

    public OpSig(Code aOpCode, int aPos, int aNextPos, Int32 aValue, System.Reflection.ExceptionHandlingClause aCurrentExceptionHandler)
      : base(aOpCode, aPos, aNextPos, aCurrentExceptionHandler) {
      Value = aValue;
    }
  }
}
