﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Reflection;

namespace Cosmos.IL2CPU.ILOpCodes {
  public class OpToken : ILOpCode {
      public readonly Int32 Value;
    public readonly FieldInfo ValueField;
    public readonly Type ValueType;

    public bool ValueIsType
    {
        get
        {
            if ((Value & 0x02000000) != 0)
            {
                return true;
            }
            if ((Value & 0x01000000) != 0)
            {
                return true;
            }
            if ((Value & 0x1B000000) != 0)
            {
                return true;
            }
            return false;
        }
    }
    public bool ValueIsField
    {
        get
        {
            if ((Value & 0x04000000) != 0)
            {
                return true;
            }
            return false;
        }
    }

    public OpToken(Code aOpCode, int aPos, int aNextPos, Int32 aValue, Module aModule, Type[] aTypeGenericArgs, Type[] aMethodGenericArgs, System.Reflection.ExceptionHandlingClause aCurrentExceptionHandler)
      : base(aOpCode, aPos, aNextPos, aCurrentExceptionHandler) {
      Value = aValue;
      if (ValueIsField)
      {
          ValueField = aModule.ResolveField(Value, aTypeGenericArgs, aMethodGenericArgs);
      }
      if (ValueIsType)
      {
          ValueType = aModule.ResolveType(Value, aTypeGenericArgs, aMethodGenericArgs);
      }

    }
  }
}
