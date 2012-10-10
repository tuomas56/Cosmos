﻿using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Cosmos.Assembler;
using Cosmos.Assembler.x86;
using Cosmos.Build.Common;
using Cosmos.Debug.Common;
using Cosmos.IL2CPU.Plugs;
using Mono.Cecil;

namespace Cosmos.IL2CPU {
  public class AppAssembler {
    public const string EndOfMethodLabelNameNormal = ".END__OF__METHOD_NORMAL";
    public const string EndOfMethodLabelNameException = ".END__OF__METHOD_EXCEPTION";
    protected const string InitStringIDsLabel = "___INIT__STRINGS_TYPE_ID_S___";
    protected List<LOCAL_ARGUMENT_INFO> mLocals_Arguments_Infos = new List<LOCAL_ARGUMENT_INFO>();
    protected ILOp[] mILOpsLo = new ILOp[256];
    protected ILOp[] mILOpsHi = new ILOp[256];
    public bool ShouldOptimize = false;
    public DebugInfo DebugInfo { get; set; }
    protected System.IO.TextWriter mLog;
    protected Dictionary<string, ModuleDefinition> mLoadedModules = new Dictionary<string, ModuleDefinition>();
    protected DebugInfo.SequencePoint[] mSequences = new DebugInfo.SequencePoint[0];
    public TraceAssemblies TraceAssemblies;
    public bool DebugEnabled = false;
    public DebugMode DebugMode;
    public bool IgnoreDebugStubAttribute;
    protected static HashSet<string> mDebugLines = new HashSet<string>();
    protected List<MethodIlOp> mSymbols = new List<MethodIlOp>();
    public readonly Cosmos.Assembler.Assembler Assembler;
    //
    protected string mCurrentMethodLabel;
    protected Guid mCurrentMethodLabelEndGuid;
    protected Guid mCurrentMethodGuid;

    public AppAssembler(int aComPort) {
      Assembler = new Cosmos.Assembler.Assembler(aComPort);
      mLog = new System.IO.StreamWriter("Cosmos.Assembler.Log");
      InitILOps();
    }

    protected void MethodBegin(MethodInfo aMethod) {
      new Comment("---------------------------------------------------------");
      new Comment("Assembly: " + aMethod.MethodBase.DeclaringType.Assembly.FullName);
      new Comment("Type: " + aMethod.MethodBase.DeclaringType.ToString());
      new Comment("Name: " + aMethod.MethodBase.Name);
      new Comment("Plugged: " + (aMethod.PlugMethod == null ? "No" : "Yes"));

      // Issue label that is used for calls etc.
      string xMethodLabel;
      if (aMethod.PluggedMethod != null) {
        xMethodLabel = "PLUG_FOR___" + LabelName.Get(aMethod.PluggedMethod.MethodBase);
      } else {
        xMethodLabel = LabelName.Get(aMethod.MethodBase);
      }
      new Cosmos.Assembler.Label(xMethodLabel);

      // We could use same GUID as MethodLabelStart, but its better to keep GUIDs unique globaly for items
      // so during debugging they can never be confused as to what they point to.
      mCurrentMethodGuid = Guid.NewGuid();

      // We issue a second label for GUID. This is increases label count, but for now we need a master label first.
      // We issue a GUID label to reduce amount of work and time needed to construct debugging DB.
      var xLabelGuid = Guid.NewGuid();
      new Cosmos.Assembler.Label("GUID_" + xLabelGuid.ToString("N"));

      mCurrentMethodLabel = "METHOD_" + xLabelGuid.ToString("N");
      Cosmos.Assembler.Label.LastFullLabel = mCurrentMethodLabel;

      mCurrentMethodLabelEndGuid = Guid.NewGuid();

      if (aMethod.MethodBase.IsStatic && aMethod.MethodBase is ConstructorInfo) {
        new Comment("Static constructor. See if it has been called already, return if so.");
        var xName = DataMember.FilterStringForIncorrectChars("CCTOR_CALLED__" + LabelName.GetFullName(aMethod.MethodBase.DeclaringType));
        var xAsmMember = new DataMember(xName, (byte)0);
        Assembler.DataMembers.Add(xAsmMember);
        new Compare { DestinationRef = Cosmos.Assembler.ElementReference.New(xName), DestinationIsIndirect = true, Size = 8, SourceValue = 1 };
        new ConditionalJump { Condition = ConditionalTestEnum.Equal, DestinationLabel = ".BeforeQuickReturn" };
        new Mov { DestinationRef = Cosmos.Assembler.ElementReference.New(xName), DestinationIsIndirect = true, Size = 8, SourceValue = 1 };
        new Jump { DestinationLabel = ".AfterCCTorAlreadyCalledCheck" };
        new Cosmos.Assembler.Label(".BeforeQuickReturn");
        new Mov { DestinationReg = RegistersEnum.ECX, SourceValue = 0 };
        new Return { };
        new Cosmos.Assembler.Label(".AfterCCTorAlreadyCalledCheck");
      }

      new Push { DestinationReg = Registers.EBP };
      new Mov { DestinationReg = Registers.EBP, SourceReg = Registers.ESP };

      if (DebugMode == DebugMode.Source) {
        // Would be nice to use xMethodSymbols.GetSourceStartEnd but we cant
        // because its not implemented by the unmanaged code underneath.
        //
        // This doesnt seem right to store as a field, but old code had it that way so we
        // continue using a field for now.
        mSequences = DebugInfo.GetSequencePoints(aMethod.MethodBase, true);
        if (mSequences.Length > 0) {
          DebugInfo.AddDocument(mSequences[0].Document);

          var xMethod = new Method() {
            ID = mCurrentMethodGuid,
            TypeToken = aMethod.MethodBase.DeclaringType.MetadataToken,
            MethodToken = aMethod.MethodBase.MetadataToken,

            LabelStartID = xLabelGuid,
            LabelEndID = mCurrentMethodLabelEndGuid,
            LabelCall = xMethodLabel,

            AssemblyFileID = DebugInfo.AssemblyGUIDs[aMethod.MethodBase.DeclaringType.Assembly],
            DocumentID = DebugInfo.DocumentGUIDs[mSequences[0].Document],
            
            // Storing Line + Col as one item makes comparisons MUCH easier, otherwise we have to 
            // check for things like col < start col but line > start line.
            //
            // () around << are VERY important.. + has precedence over <<
            LineColStart = ((Int64)mSequences[0].LineStart << 32) + mSequences[0].ColStart,
            LineColEnd = ((Int64)(mSequences[mSequences.Length - 1].LineEnd) << 32) + mSequences[mSequences.Length - 1].ColEnd
          };
          DebugInfo.AddMethod(xMethod);
        }
      }

      if (aMethod.MethodAssembler == null && aMethod.PlugMethod == null && !aMethod.IsInlineAssembler) {
        // the body of aMethod is getting emitted
        var xBody = aMethod.MethodBase.GetMethodBody();
        if (xBody != null) {
          var xLocalsOffset = mLocals_Arguments_Infos.Count;
          foreach (var xLocal in xBody.LocalVariables) {
            var xInfo = new LOCAL_ARGUMENT_INFO {
              METHODLABELNAME = mCurrentMethodLabel,
              IsArgument = false,
              INDEXINMETHOD = xLocal.LocalIndex,
              NAME = "Local" + xLocal.LocalIndex,
              OFFSET = 0 - (int)ILOp.GetEBPOffsetForLocalForDebugger(aMethod, xLocal.LocalIndex),
              TYPENAME = xLocal.LocalType.AssemblyQualifiedName
            };
            mLocals_Arguments_Infos.Add(xInfo);

            var xSize = ILOp.Align(ILOp.SizeOfType(xLocal.LocalType), 4);
            new Comment(String.Format("Local {0}, Size {1}", xLocal.LocalIndex, xSize));
            for (int i = 0; i < xSize / 4; i++) {
              new Push { DestinationValue = 0 };
            }
            //new Sub { DestinationReg = Registers.ESP, SourceValue = ILOp.Align(ILOp.SizeOfType(xLocal.LocalType), 4) };
          }
          var xCecilMethod = GetCecilMethodDefinitionForSymbolReading(aMethod.MethodBase);
          if (xCecilMethod != null && xCecilMethod.Body != null) {
            // mLocals_Arguments_Infos is one huge list, so ourlatest additions are at the end
            for (int i = 0; i < xCecilMethod.Body.Variables.Count; i++) {
              mLocals_Arguments_Infos[xLocalsOffset + i].NAME = xCecilMethod.Body.Variables[i].Name;
            }
            for (int i = xLocalsOffset + xCecilMethod.Body.Variables.Count - 1; i >= xLocalsOffset; i--) {
              if (mLocals_Arguments_Infos[i].NAME.Contains('$')) {
                mLocals_Arguments_Infos.RemoveAt(i);
              }
            }
          }
        }

        // debug info:
        var xIdxOffset = 0u;
        if (!aMethod.MethodBase.IsStatic) {
          mLocals_Arguments_Infos.Add(new LOCAL_ARGUMENT_INFO {
            METHODLABELNAME = mCurrentMethodLabel,
            IsArgument = true,
            NAME = "this:" + X86.IL.Ldarg.GetArgumentDisplacement(aMethod, 0),
            INDEXINMETHOD = 0,
            OFFSET = X86.IL.Ldarg.GetArgumentDisplacement(aMethod, 0),
            TYPENAME = aMethod.MethodBase.DeclaringType.AssemblyQualifiedName
          });

          xIdxOffset++;
        }

        var xParams = aMethod.MethodBase.GetParameters();
        var xParamCount = (ushort)xParams.Length;

        for (ushort i = 0; i < xParamCount; i++) {
          var xOffset = X86.IL.Ldarg.GetArgumentDisplacement(aMethod, (ushort)(i + xIdxOffset));
          // if last argument is 8 byte long, we need to add 4, so that debugger could read all 8 bytes from this variable in positiv direction
          xOffset -= (int)Cosmos.IL2CPU.ILOp.Align(ILOp.SizeOfType(xParams[i].ParameterType), 4) - 4;
          mLocals_Arguments_Infos.Add(new LOCAL_ARGUMENT_INFO {
            METHODLABELNAME = mCurrentMethodLabel,
            IsArgument = true,
            INDEXINMETHOD = (int)(i + xIdxOffset),
            NAME = xParams[i].Name,
            OFFSET = xOffset,
            TYPENAME = xParams[i].ParameterType.AssemblyQualifiedName
          });
        }
      }
    }

    protected void MethodEnd(MethodInfo aMethod) {
      new Comment("End Method: " + aMethod.MethodBase.Name);

      uint xReturnSize = 0;
      var xMethInfo = aMethod.MethodBase as System.Reflection.MethodInfo;
      if (xMethInfo != null) {
        xReturnSize = ILOp.Align(ILOp.SizeOfType(xMethInfo.ReturnType), 4);
      }
      if (aMethod.PlugMethod == null && !aMethod.IsInlineAssembler) {
        new Cosmos.Assembler.Label(ILOp.GetMethodLabel(aMethod) + EndOfMethodLabelNameNormal);
      }
      new Mov { DestinationReg = Registers.ECX, SourceValue = 0 };
      var xTotalArgsSize = (from item in aMethod.MethodBase.GetParameters()
                            select (int)ILOp.Align(ILOp.SizeOfType(item.ParameterType), 4)).Sum();
      if (!aMethod.MethodBase.IsStatic) {
        if (aMethod.MethodBase.DeclaringType.IsValueType) {
          xTotalArgsSize += 4; // only a reference is passed
        } else {
          xTotalArgsSize += (int)ILOp.Align(ILOp.SizeOfType(aMethod.MethodBase.DeclaringType), 4);
        }
      }

      if (aMethod.PluggedMethod != null) {
        xReturnSize = 0;
        xMethInfo = aMethod.PluggedMethod.MethodBase as System.Reflection.MethodInfo;
        if (xMethInfo != null) {
          xReturnSize = ILOp.Align(ILOp.SizeOfType(xMethInfo.ReturnType), 4);
        }
        xTotalArgsSize = (from item in aMethod.PluggedMethod.MethodBase.GetParameters()
                          select (int)ILOp.Align(ILOp.SizeOfType(item.ParameterType), 4)).Sum();
        if (!aMethod.PluggedMethod.MethodBase.IsStatic) {
          if (aMethod.PluggedMethod.MethodBase.DeclaringType.IsValueType) {
            xTotalArgsSize += 4; // only a reference is passed
          } else {
            xTotalArgsSize += (int)ILOp.Align(ILOp.SizeOfType(aMethod.PluggedMethod.MethodBase.DeclaringType), 4);
          }
        }
      }

      if (xReturnSize > 0) {
        var xOffset = GetResultCodeOffset(xReturnSize, (uint)xTotalArgsSize);
        for (int i = 0; i < xReturnSize / 4; i++) {
          new Pop { DestinationReg = Registers.EAX };
          new Mov {
            DestinationReg = Registers.EBP,
            DestinationIsIndirect = true,
            DestinationDisplacement = (int)(xOffset + ((i + 0) * 4)),
            SourceReg = Registers.EAX
          };
        }
        // extra stack space is the space reserved for example when a "public static int TestMethod();" method is called, 4 bytes is pushed, to make room for result;
      }
      new Cosmos.Assembler.Label(ILOp.GetMethodLabel(aMethod) + EndOfMethodLabelNameException);
      if (aMethod.MethodAssembler == null && aMethod.PlugMethod == null && !aMethod.IsInlineAssembler) {
        var xBody = aMethod.MethodBase.GetMethodBody();
        if (xBody != null) {
          uint xLocalsSize = 0;
          for (int j = xBody.LocalVariables.Count - 1; j >= 0; j--) {
            xLocalsSize += ILOp.Align(ILOp.SizeOfType(xBody.LocalVariables[j].LocalType), 4);

            if (xLocalsSize >= 256) {
              new Add {
                DestinationReg = Registers.ESP,
                SourceValue = 255
              };
              xLocalsSize -= 255;
            }
          }
          if (xLocalsSize > 0) {
            new Add {
              DestinationReg = Registers.ESP,
              SourceValue = xLocalsSize
            };
          }
        }
      }
      new Cosmos.Assembler.Label(ILOp.GetMethodLabel(aMethod) + EndOfMethodLabelNameException + "__2");
      new Pop { DestinationReg = Registers.EBP };
      var xRetSize = ((int)xTotalArgsSize) - ((int)xReturnSize);
      if (xRetSize < 0) {
        xRetSize = 0;
      }
      WriteDebug(aMethod.MethodBase, (uint)xRetSize, X86.IL.Call.GetStackSizeToReservate(aMethod.MethodBase));
      new Return { DestinationValue = (uint)xRetSize };

      // Final, after all code. Points to op AFTER method.
      new Cosmos.Assembler.Label("GUID_" + mCurrentMethodLabelEndGuid.ToString("N"));
    }

    public void FinalizeDebugInfo() {
      DebugInfo.AddMethod(null, true);
      DebugInfo.WriteAllLocalsArgumentsInfos(mLocals_Arguments_Infos);
    }

    public static uint GetResultCodeOffset(uint aResultSize, uint aTotalArgumentSize) {
      uint xOffset = 8;
      if ((aTotalArgumentSize > 0) && (aTotalArgumentSize >= aResultSize)) {
        xOffset += aTotalArgumentSize;
        xOffset -= aResultSize;
      }
      return xOffset;
    }

    public void ProcessMethod(MethodInfo aMethod, List<ILOpCode> aOpCodes) {
      // We check this here and not scanner as when scanner makes these
      // plugs may still have not yet been scanned that it will depend on.
      // But by the time we make it here, they have to be resolved.
      if (aMethod.Type == MethodInfo.TypeEnum.NeedsPlug && aMethod.PlugMethod == null) {
        throw new Exception("Method needs plug, but no plug was assigned.");
      }

      // todo: MtW: how to do this? we need some extra space.
      //		see ConstructLabel for extra info
      if (aMethod.UID > 0x00FFFFFF) {
        throw new Exception("Too many methods.");
      }

      MethodBegin(aMethod);
      Assembler.Stack.Clear();
      mLog.WriteLine("Method '{0}'", aMethod.MethodBase.GetFullName());
      mLog.Flush();
      if (aMethod.MethodAssembler != null) {
        mLog.WriteLine("Emitted using MethodAssembler", aMethod.MethodBase.GetFullName());
        mLog.Flush();
        var xAssembler = (AssemblerMethod)Activator.CreateInstance(aMethod.MethodAssembler);
        xAssembler.AssembleNew(Assembler, aMethod.PluggedMethod);
      } else if (aMethod.IsInlineAssembler) {
        mLog.WriteLine("Emitted using Inline MethodAssembler", aMethod.MethodBase.GetFullName());
        mLog.Flush();
        aMethod.MethodBase.Invoke("", new object[aMethod.MethodBase.GetParameters().Length]);
      } else {
        foreach (var xOpCode in aOpCodes) {
          ushort xOpCodeVal = (ushort)xOpCode.OpCode;
          ILOp xILOp;
          if (xOpCodeVal <= 0xFF) {
            xILOp = mILOpsLo[xOpCodeVal];
          } else {
            xILOp = mILOpsHi[xOpCodeVal & 0xFF];
          }
          mLog.WriteLine("\t{0} {1}", Assembler.Stack.Count, xILOp.GetType().Name);
          mLog.Flush();

          BeforeOp(aMethod, xOpCode);
          new Comment(xILOp.ToString());
          var xNextPosition = xOpCode.Position + 1;
          #region Exception handling support code
          ExceptionHandlingClause xCurrentHandler = null;
          var xBody = aMethod.MethodBase.GetMethodBody();
          // todo: add support for nested handlers using a stack or so..
          foreach (ExceptionHandlingClause xHandler in xBody.ExceptionHandlingClauses) {
            if (xHandler.TryOffset > 0) {
              if (xHandler.TryOffset <= xNextPosition && (xHandler.TryLength + xHandler.TryOffset) > xNextPosition) {
                if (xCurrentHandler == null) {
                  xCurrentHandler = xHandler;
                  continue;
                } else if (xHandler.TryOffset > xCurrentHandler.TryOffset && (xHandler.TryLength + xHandler.TryOffset) < (xCurrentHandler.TryLength + xCurrentHandler.TryOffset)) {
                  // only replace if the current found handler is narrower
                  xCurrentHandler = xHandler;
                  continue;
                }
              }
            }
            if (xHandler.HandlerOffset > 0) {
              if (xHandler.HandlerOffset <= xNextPosition && (xHandler.HandlerOffset + xHandler.HandlerLength) > xNextPosition) {
                if (xCurrentHandler == null) {
                  xCurrentHandler = xHandler;
                  continue;
                } else if (xHandler.HandlerOffset > xCurrentHandler.HandlerOffset && (xHandler.HandlerOffset + xHandler.HandlerLength) < (xCurrentHandler.HandlerOffset + xCurrentHandler.HandlerLength)) {
                  // only replace if the current found handler is narrower
                  xCurrentHandler = xHandler;
                  continue;
                }
              }
            }
            if ((xHandler.Flags & ExceptionHandlingClauseOptions.Filter) > 0) {
              if (xHandler.FilterOffset > 0) {
                if (xHandler.FilterOffset <= xNextPosition) {
                  if (xCurrentHandler == null) {
                    xCurrentHandler = xHandler;
                    continue;
                  } else if (xHandler.FilterOffset > xCurrentHandler.FilterOffset) {
                    // only replace if the current found handler is narrower
                    xCurrentHandler = xHandler;
                    continue;
                  }
                }
              }
            }
          }
          #endregion
          var xNeedsExceptionPush = (xCurrentHandler != null) && (((xCurrentHandler.HandlerOffset > 0 && xCurrentHandler.HandlerOffset == xOpCode.Position) || ((xCurrentHandler.Flags & ExceptionHandlingClauseOptions.Filter) > 0 && xCurrentHandler.FilterOffset > 0 && xCurrentHandler.FilterOffset == xOpCode.Position)) && (xCurrentHandler.Flags == ExceptionHandlingClauseOptions.Clause));
          if (xNeedsExceptionPush) {
            Push(DataMember.GetStaticFieldName(ExceptionHelperRefs.CurrentExceptionRef), true);
            Assembler.Stack.Push(4, typeof(Exception));
          }

          xILOp.Execute(aMethod, xOpCode);

          AfterOp(aMethod, xOpCode);
          //mLog.WriteLine( " end: " + Stack.Count.ToString() );
        }
      }
      MethodEnd(aMethod);
    }

    protected void InitILOps() {
      InitILOps(typeof(ILOp));
    }

    protected virtual void InitILOps(Type aAssemblerBaseOp) {
      foreach (var xType in aAssemblerBaseOp.Assembly.GetExportedTypes()) {
        if (xType.IsSubclassOf(aAssemblerBaseOp)) {
          var xAttribs = (OpCodeAttribute[])xType.GetCustomAttributes(typeof(OpCodeAttribute), false);
          foreach (var xAttrib in xAttribs) {
            var xOpCode = (ushort)xAttrib.OpCode;
            var xCtor = xType.GetConstructor(new Type[] { typeof(Cosmos.Assembler.Assembler) });
            var xILOp = (ILOp)xCtor.Invoke(new Object[] { Assembler });
            if (xOpCode <= 0xFF) {
              mILOpsLo[xOpCode] = xILOp;
            } else {
              mILOpsHi[xOpCode & 0xFF] = xILOp;
            }
          }
        }
      }
    }

    protected void Move(string aDestLabelName, int aValue) {
      new Mov {
        DestinationRef = ElementReference.New(aDestLabelName),
        DestinationIsIndirect = true,
        SourceValue = (uint)aValue
      };
    }

    protected void Push(uint aValue) {
      new Push {
        DestinationValue = aValue
      };
    }

    protected void Pop() {
      new Add { DestinationReg = Registers.ESP, SourceValue = (uint)Assembler.Stack.Pop().Size };
    }

    protected void Push(string aLabelName, bool isIndirect = false) {
      new Push {
        DestinationRef = ElementReference.New(aLabelName),
        DestinationIsIndirect = isIndirect
      };
    }

    protected void Call(MethodBase aMethod) {
      new Cosmos.Assembler.x86.Call {
        DestinationLabel = LabelName.Get(aMethod)
      };
    }

    protected void Jump(string aLabelName) {
      new Cosmos.Assembler.x86.Jump {
        DestinationLabel = aLabelName
      };
    }

    protected void Ldarg(MethodInfo aMethod, int aIndex) {
      X86.IL.Ldarg.DoExecute(Assembler, aMethod, (ushort)aIndex);
    }

    protected void Call(MethodInfo aMethod, MethodInfo aTargetMethod) {
      var xSize = X86.IL.Call.GetStackSizeToReservate(aTargetMethod.MethodBase);
      if (xSize > 0) {
        new Sub { DestinationReg = Registers.ESP, SourceValue = xSize };
      }
      new Call { DestinationLabel = ILOp.GetMethodLabel(aTargetMethod) };
    }

    protected void Ldflda(MethodInfo aMethod, string aFieldId) {
      X86.IL.Ldflda.DoExecute(Assembler, aMethod, aMethod.MethodBase.DeclaringType, aFieldId, false);
    }

    protected int GetVTableEntrySize() {
      return 16; // todo: retrieve from actual type info
    }

    public const string InitVMTCodeLabel = "___INIT__VMT__CODE____";
    public virtual void GenerateVMTCode(HashSet<Type> aTypesSet, HashSet<MethodBase> aMethodsSet, Func<Type, uint> aGetTypeID, Func<MethodBase, uint> aGetMethodUID) {
      new Comment("---------------------------------------------------------");
      new Cosmos.Assembler.Label(InitVMTCodeLabel);
      new Push { DestinationReg = Registers.EBP };
      new Mov { DestinationReg = Registers.EBP, SourceReg = Registers.ESP };
      mSequences = new DebugInfo.SequencePoint[0];

      var xSetTypeInfoRef = VTablesImplRefs.SetTypeInfoRef;
      var xSetMethodInfoRef = VTablesImplRefs.SetMethodInfoRef;
      var xLoadTypeTableRef = VTablesImplRefs.LoadTypeTableRef;
      var xTypesFieldRef = VTablesImplRefs.VTablesImplDef.GetField("mTypes",
                                                                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
      string xTheName = DataMember.GetStaticFieldName(xTypesFieldRef);
      DataMember xDataMember = (from item in Cosmos.Assembler.Assembler.CurrentInstance.DataMembers
                                where item.Name == xTheName
                                select item).FirstOrDefault();
      if (xDataMember != null) {
        Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Remove((from item in Cosmos.Assembler.Assembler.CurrentInstance.DataMembers
                                                                       where item == xDataMember
                                                                       select item).First());
      }
      var xData = new byte[16 + (aTypesSet.Count * GetVTableEntrySize())];
      var xTemp = BitConverter.GetBytes(aGetTypeID(typeof(Array)));
      xTemp = BitConverter.GetBytes(0x80000002);
      Array.Copy(xTemp, 0, xData, 4, 4);
      xTemp = BitConverter.GetBytes(aTypesSet.Count);
      Array.Copy(xTemp, 0, xData, 8, 4);
      xTemp = BitConverter.GetBytes(GetVTableEntrySize());
      Array.Copy(xTemp, 0, xData, 12, 4);
      Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(new DataMember(xTheName + "__Contents", xData));
      Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(new DataMember(xTheName, Cosmos.Assembler.ElementReference.New(xTheName + "__Contents")));
#if VMT_DEBUG
        using (var xVmtDebugOutput = XmlWriter.Create(@"e:\vmt_debug.xml"))
        {
            xVmtDebugOutput.WriteStartDocument();
            xVmtDebugOutput.WriteStartElement("VMT");
#endif
      //Push((uint)aTypesSet.Count);
      //Call(xLoadTypeTableRef);
      foreach (var xType in aTypesSet) {
#if VMT_DEBUG
                xVmtDebugOutput.WriteStartElement("Type");
                xVmtDebugOutput.WriteAttributeString("TypeId", aGetTypeID(xType).ToString());
                if (xType.BaseType != null)
                {
                    xVmtDebugOutput.WriteAttributeString("BaseTypeId", aGetTypeID(xType.BaseType).ToString());
                }
                xVmtDebugOutput.WriteAttributeString("Name", xType.FullName);
#endif
        // value contains true if the method is an interface method definition
        SortedList<MethodBase, bool> xEmittedMethods = new SortedList<MethodBase, bool>(new MethodBaseComparer());
        foreach (MethodBase xMethod in xType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
          if (aMethodsSet.Contains(xMethod)) { //) && !xMethod.IsAbstract) {
            xEmittedMethods.Add(xMethod, false);
          }
        }
        foreach (MethodBase xCtor in xType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
          if (aMethodsSet.Contains(xCtor)) { // && !xCtor.IsAbstract) {
            xEmittedMethods.Add(xCtor, false);
          }
        }
        foreach (var xIntf in xType.GetInterfaces()) {
          foreach (var xMethodIntf in xIntf.GetMethods()) {
            var xActualMethod = xType.GetMethod(xIntf.FullName + "." + xMethodIntf.Name,
                                                (from xParam in xMethodIntf.GetParameters()
                                                 select xParam.ParameterType).ToArray());

            if (xActualMethod == null) {
              // get private implemenation
              xActualMethod = xType.GetMethod(xMethodIntf.Name,
                                              (from xParam in xMethodIntf.GetParameters()
                                               select xParam.ParameterType).ToArray());
            }
            if (xActualMethod == null) {
              try {
                var xMap = xType.GetInterfaceMap(xIntf);
                for (int k = 0; k < xMap.InterfaceMethods.Length; k++) {
                  if (xMap.InterfaceMethods[k] == xMethodIntf) {
                    xActualMethod = xMap.TargetMethods[k];
                    break;
                  }
                }
              } catch {
              }
            }
            if (aMethodsSet.Contains(xMethodIntf)) {
              if (!xEmittedMethods.ContainsKey(xMethodIntf)) {
                xEmittedMethods.Add(xMethodIntf, true);
              }
            }

          }
        }
        if (!xType.IsInterface) {
          Push(aGetTypeID(xType));
        }
        int? xBaseIndex = null;
        if (xType.BaseType == null) {
          xBaseIndex = (int)aGetTypeID(xType);
        } else {
          for (int t = 0; t < aTypesSet.Count; t++) {
            // todo: optimize check
            var xItem = aTypesSet.Skip(t).First();
            if (xItem.ToString() == xType.BaseType.ToString()) {
              xBaseIndex = (int)aGetTypeID(xItem);
              break;
            }
          }
        }
        if (xBaseIndex == null) {
          throw new Exception("Base type not found!");
        }
        for (int x = xEmittedMethods.Count - 1; x >= 0; x--) {
          if (!aMethodsSet.Contains(xEmittedMethods.Keys[x])) {
            xEmittedMethods.RemoveAt(x);
          }
        }
        if (!xType.IsInterface) {
          Move("VMT__TYPE_ID_HOLDER__" + DataMember.FilterStringForIncorrectChars(LabelName.GetFullName(xType) + " ASM_IS__" + xType.Assembly.GetName().Name), (int)aGetTypeID(xType));
          Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(
              new DataMember("VMT__TYPE_ID_HOLDER__" + DataMember.FilterStringForIncorrectChars(LabelName.GetFullName(xType) + " ASM_IS__" + xType.Assembly.GetName().Name), new int[] { (int)aGetTypeID(xType) }));
          Push((uint)xBaseIndex.Value);
          xData = new byte[16 + (xEmittedMethods.Count * 4)];
          xTemp = BitConverter.GetBytes(aGetTypeID(typeof(Array)));
          Array.Copy(xTemp, 0, xData, 0, 4);
          xTemp = BitConverter.GetBytes(0x80000002); // embedded array
          Array.Copy(xTemp, 0, xData, 4, 4);
          xTemp = BitConverter.GetBytes(xEmittedMethods.Count); // embedded array
          Array.Copy(xTemp, 0, xData, 8, 4);
          xTemp = BitConverter.GetBytes(4); // embedded array
          Array.Copy(xTemp, 0, xData, 12, 4);
          string xDataName = "____SYSTEM____TYPE___" + DataMember.FilterStringForIncorrectChars(LabelName.GetFullName(xType) + " ASM_IS__" + xType.Assembly.GetName().Name) + "__MethodIndexesArray";
          Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(new DataMember(xDataName, xData));
          Push(xDataName);
          xDataName = "____SYSTEM____TYPE___" + DataMember.FilterStringForIncorrectChars(LabelName.GetFullName(xType) + " ASM_IS__" + xType.Assembly.GetName().Name) + "__MethodAddressesArray";
          Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(new DataMember(xDataName, xData));
          Push(xDataName);
          xData = new byte[16 + Encoding.Unicode.GetByteCount(xType.FullName + ", " + xType.Module.Assembly.GetName().FullName)];
          xTemp = BitConverter.GetBytes(aGetTypeID(typeof(Array)));
          Array.Copy(xTemp, 0, xData, 0, 4);
          xTemp = BitConverter.GetBytes(0x80000002); // embedded array
          Array.Copy(xTemp, 0, xData, 4, 4);
          xTemp = BitConverter.GetBytes((xType.FullName + ", " + xType.Module.Assembly.GetName().FullName).Length);
          Array.Copy(xTemp, 0, xData, 8, 4);
          xTemp = BitConverter.GetBytes(2); // embedded array
          Array.Copy(xTemp, 0, xData, 12, 4);
          xDataName = "____SYSTEM____TYPE___" + DataMember.FilterStringForIncorrectChars(LabelName.GetFullName(xType) + " ASM_IS__" + xType.Assembly.GetName().Name);
          Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(new DataMember(xDataName, xData));
          Push("0" + xEmittedMethods.Count.ToString("X") + "h");
          Call(xSetTypeInfoRef);
        }
        for (int j = 0; j < xEmittedMethods.Count; j++) {
          MethodBase xMethod = xEmittedMethods.Keys[j];
#if VMT_DEBUG
                    xVmtDebugOutput.WriteStartElement("Method");
                    xVmtDebugOutput.WriteAttributeString("Id", aGetMethodUID(xMethod).ToString());
                    xVmtDebugOutput.WriteAttributeString("Name", xMethod.GetFullName());
                    xVmtDebugOutput.WriteEndElement();
#endif
          var xMethodId = aGetMethodUID(xMethod);
          if (!xType.IsInterface) {
            if (xEmittedMethods.Values[j]) {
              var xNewMethod = xType.GetMethod(xMethod.DeclaringType.FullName + "." + xMethod.Name,
                                                  (from xParam in xMethod.GetParameters()
                                                   select xParam.ParameterType).ToArray());

              if (xNewMethod == null) {
                // get private implementation
                xNewMethod = xType.GetMethod(xMethod.Name,
                                                (from xParam in xMethod.GetParameters()
                                                 select xParam.ParameterType).ToArray());
              }
              if (xNewMethod == null) {
                try {
                  var xMap = xType.GetInterfaceMap(xMethod.DeclaringType);
                  for (int k = 0; k < xMap.InterfaceMethods.Length; k++) {
                    if (xMap.InterfaceMethods[k] == xMethod) {
                      xNewMethod = xMap.TargetMethods[k];
                      break;
                    }
                  }
                } catch {
                }
              }
              xMethod = xNewMethod;
            }

            Push((uint)aGetTypeID(xType));
            Push((uint)j);

            Push((uint)xMethodId);
            if (xMethod.IsAbstract) {
              // abstract methods dont have bodies, oiw, are not emitted
              Push(0);
            } else {
              Push(ILOp.GetMethodLabel(xMethod));
            }
            Push(0);
            Call(VTablesImplRefs.SetMethodInfoRef);
          }
        }
#if VMT_DEBUG
                xVmtDebugOutput.WriteEndElement(); // type
#endif
      }
#if VMT_DEBUG
                    xVmtDebugOutput.WriteEndElement(); // types
                    xVmtDebugOutput.WriteEndDocument();
        }
#endif

      new Cosmos.Assembler.Label("_END_OF_" + InitVMTCodeLabel);
      new Pop { DestinationReg = Registers.EBP };
      new Return();
    }

    public void ProcessField(FieldInfo aField) {
      string xFieldName = LabelName.GetFullName(aField);
      xFieldName = DataMember.GetStaticFieldName(aField);
      if (Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Count(x => x.Name == xFieldName) == 0) {
        var xItemList = (from item in aField.GetCustomAttributes(false)
                         where item.GetType().FullName == "ManifestResourceStreamAttribute"
                         select item).ToList();

        object xItem = null;
        if (xItemList.Count > 0)
          xItem = xItemList[0];
        string xManifestResourceName = null;
        if (xItem != null) {
          var xItemType = xItem.GetType();
          xManifestResourceName = (string)xItemType.GetField("ResourceName").GetValue(xItem);
        }
        if (xManifestResourceName != null) {
          // todo: add support for manifest streams again
          //RegisterType(xCurrentField.FieldType);
          //string xFileName = Path.Combine(mOutputDir,
          //                                (xCurrentField.DeclaringType.Assembly.FullName + "__" + xManifestResourceName).Replace(",",
          //                                                                                                                       "_") + ".res");
          //using (var xStream = xCurrentField.DeclaringType.Assembly.GetManifestResourceStream(xManifestResourceName)) {
          //    if (xStream == null) {
          //        throw new Exception("Resource '" + xManifestResourceName + "' not found!");
          //    }
          //    using (var xTarget = File.Create(xFileName)) {
          //        // todo: abstract this array code out.
          //        xTarget.Write(BitConverter.GetBytes(Engine.RegisterType(Engine.GetType("mscorlib",
          //                                                                               "System.Array"))),
          //                      0,
          //                      4);
          //        xTarget.Write(BitConverter.GetBytes((uint)InstanceTypeEnum.StaticEmbeddedArray),
          //                      0,
          //                      4);
          //        xTarget.Write(BitConverter.GetBytes((int)xStream.Length), 0, 4);
          //        xTarget.Write(BitConverter.GetBytes((int)1), 0, 4);
          //        var xBuff = new byte[128];
          //        while (xStream.Position < xStream.Length) {
          //            int xBytesRead = xStream.Read(xBuff, 0, 128);
          //            xTarget.Write(xBuff, 0, xBytesRead);
          //        }
          //    }
          //}
          //Assembler.DataMembers.Add(new DataMember("___" + xFieldName + "___Contents",
          //                                          "incbin",
          //                                          "\"" + xFileName + "\""));
          //Assembler.DataMembers.Add(new DataMember(xFieldName,
          //                                          "dd",
          //                                          "___" + xFieldName + "___Contents"));
          throw new NotImplementedException();
        } else {
          uint xTheSize;
          //string theType = "db";
          Type xFieldTypeDef = aField.FieldType;
          if (!xFieldTypeDef.IsClass || xFieldTypeDef.IsValueType) {
            xTheSize = GetSizeOfType(aField.FieldType);
          } else {
            xTheSize = 4;
          }
          byte[] xData = new byte[xTheSize];
          try {
            object xValue = aField.GetValue(null);
            if (xValue != null) {
              try {
                xData = new byte[xTheSize];
                if (xValue.GetType().IsValueType) {
                  for (int x = 0; x < xTheSize; x++) {
                    xData[x] = Marshal.ReadByte(xValue,
                                                x);
                  }
                }
              } catch {
              }
            }
          } catch {
          }
          Cosmos.Assembler.Assembler.CurrentInstance.DataMembers.Add(new DataMember(xFieldName, xData));
        }
      }
    }

    public uint GetSizeOfType(Type aType) {
      return ILOp.SizeOfType(aType);
    }

    internal void GenerateMethodForward(MethodInfo aFrom, MethodInfo aTo) {
      // todo: completely get rid of this kind of trampoline code
      MethodBegin(aFrom);
      {
        var xParams = aTo.MethodBase.GetParameters().ToArray();

        if (aTo.MethodAssembler != null) {
          xParams = aFrom.MethodBase.GetParameters();
        }

        //if (aFrom.MethodBase.GetParameters().Length > 0 || !aFrom.MethodBase.IsStatic) {
        //  Ldarg(aFrom, 0);
        //  Pop();
        //}

        int xCurParamIdx = 0;
        if (!aFrom.MethodBase.IsStatic) {
          Ldarg(aFrom, 0);
          xCurParamIdx++;
          if (aTo.MethodAssembler == null) {
            xParams = xParams.Skip(1).ToArray();
          }
        }
        foreach (var xParam in xParams) {
          FieldAccessAttribute xFieldAccessAttrib = null;
          foreach (var xAttrib in xParam.GetCustomAttributes(typeof(FieldAccessAttribute), true)) {
            xFieldAccessAttrib = xAttrib as FieldAccessAttribute;
          }

          if (xFieldAccessAttrib != null) {
            // field access                                                                                        
            new Comment("Loading address of field '" + xFieldAccessAttrib.Name + "'");
            Ldarg(aFrom, 0);
            Ldflda(aFrom, xFieldAccessAttrib.Name);
          } else {
            // normal field access
            new Comment("Loading parameter " + xCurParamIdx);
            Ldarg(aFrom, xCurParamIdx);
            xCurParamIdx++;
          }
        }
        Call(aFrom, aTo);
      }
      MethodEnd(aFrom);
    }

    protected static void WriteDebug(MethodBase aMethod, uint aSize, uint aSize2) {
      var xLine = String.Format("{0}\t{1}\t{2}", LabelName.GenerateFullName(aMethod), aSize, aSize2);
    }

    // These are all temp functions until we move to the new assembler.
    // They are used to clean up the old assembler slightly while retaining compatibiltiy for now
    public static string TmpPosLabel(MethodInfo aMethod, int aOffset) {
      return ILOp.GetLabel(aMethod, aOffset);
    }

    public static string TmpPosLabel(MethodInfo aMethod, ILOpCode aOpCode) {
      return TmpPosLabel(aMethod, aOpCode.Position);
    }

    public static string TmpBranchLabel(MethodInfo aMethod, ILOpCode aOpCode) {
      return TmpPosLabel(aMethod, ((ILOpCodes.OpBranch)aOpCode).Value);
    }

    public void EmitEntrypoint(MethodBase aEntrypoint) {
      // at the time the datamembers for literal strings are created, the type id for string is not yet determined. 
      // for now, we fix this at runtime.
      new Cosmos.Assembler.Label(InitStringIDsLabel);
      new Push { DestinationReg = Registers.EBP };
      new Mov { DestinationReg = Registers.EBP, SourceReg = Registers.ESP };
      new Mov { DestinationReg = Registers.EAX, SourceRef = Cosmos.Assembler.ElementReference.New(ILOp.GetTypeIDLabel(typeof(String))), SourceIsIndirect = true };
      foreach (var xDataMember in Assembler.DataMembers) {
        if (!xDataMember.Name.StartsWith("StringLiteral")) {
          continue;
        }
        if (xDataMember.Name.EndsWith("__Contents")) {
          continue;
        }
        new Mov { DestinationRef = Cosmos.Assembler.ElementReference.New(xDataMember.Name), DestinationIsIndirect = true, SourceReg = Registers.EAX };
      }
      new Pop { DestinationReg = Registers.EBP };
      new Return();

      new Cosmos.Assembler.Label(Cosmos.Assembler.Assembler.EntryPointName);
      new Push { DestinationReg = Registers.EBP };
      new Mov { DestinationReg = Registers.EBP, SourceReg = Registers.ESP };
      new Call { DestinationLabel = InitVMTCodeLabel };
      Cosmos.Assembler.Assembler.WriteDebugVideo("Initializing string IDs.");
      new Call { DestinationLabel = InitStringIDsLabel };

      // we now need to do "newobj" on the entry point, and after that, call .Start on it
      var xCurLabel = Cosmos.Assembler.Assembler.EntryPointName + ".CreateEntrypoint";
      new Cosmos.Assembler.Label(xCurLabel);
      X86.IL.Newobj.Assemble(Cosmos.Assembler.Assembler.CurrentInstance, null, null, xCurLabel, aEntrypoint.DeclaringType, aEntrypoint);
      xCurLabel = Cosmos.Assembler.Assembler.EntryPointName + ".CallStart";
      new Cosmos.Assembler.Label(xCurLabel);
      X86.IL.Call.DoExecute(Assembler, null, aEntrypoint.DeclaringType.BaseType.GetMethod("Start"), null, xCurLabel, Cosmos.Assembler.Assembler.EntryPointName + ".AfterStart");
      new Cosmos.Assembler.Label(Cosmos.Assembler.Assembler.EntryPointName + ".AfterStart");
      new Pop { DestinationReg = Registers.EBP };
      new Return();

      if (ShouldOptimize) {
        Orvid.Optimizer.Optimize(Assembler);
      }
    }

    protected void AfterOp(MethodInfo aMethod, ILOpCode aOpCode) {
      var xContents = "";
      foreach (var xStackItem in Assembler.Stack) {
        xContents += ILOp.Align((uint)xStackItem.Size, 4);
        xContents += ", ";
      }
      if (xContents.EndsWith(", ")) {
        xContents = xContents.Substring(0, xContents.Length - 2);
      }
      new Comment("Stack contains " + Assembler.Stack.Count + " items: (" + xContents + ")");
    }

    protected void BeforeOp(MethodInfo aMethod, ILOpCode aOpCode) {
      string xLabel = TmpPosLabel(aMethod, aOpCode);
      Assembler.CurrentIlLabel = xLabel;
      new Cosmos.Assembler.Label(xLabel);

      if (mSymbols != null) {
        var xMLSymbol = new MethodIlOp();
        xMLSymbol.LabelName = TmpPosLabel(aMethod, aOpCode);

        var xStackSize = (from item in Assembler.Stack
                          let xSize = (item.Size % 4u == 0u) ? item.Size : (item.Size + (4u - (item.Size % 4u)))
                          select xSize).Sum();
        xMLSymbol.StackDiff = -1;
        if (aMethod.MethodBase != null) {
          var xBody = aMethod.MethodBase.GetMethodBody();
          if (xBody != null) {
            var xLocalsSize = (from item in xBody.LocalVariables
                               select ILOp.Align(ILOp.SizeOfType(item.LocalType), 4)).Sum();
            xMLSymbol.StackDiff = checked((int)(xLocalsSize + xStackSize));
          }
        }
        xMLSymbol.IlOffset = aOpCode.Position;
        xMLSymbol.MethodID = mCurrentMethodGuid;

        mSymbols.Add(xMLSymbol);
        DebugInfo.AddSymbols(mSymbols);
      }
      DebugInfo.AddSymbols(mSymbols, true);

      EmitTracer(aMethod, aOpCode, aMethod.MethodBase.DeclaringType.Namespace);
    }

    protected void EmitTracer(MethodInfo aMethod, ILOpCode aOp, string aNamespace) {
      // NOTE - These if statements can be optimized down - but clarity is
      // more important the optimizations. Furthermoer the optimazations available
      // would not offer much benefit

      // Determine if a new DebugStub should be emitted

      if (aOp.OpCode == ILOpCode.Code.Nop) {
        // Skip NOOP's so we dont have breakpoints on them
        //TODO: Each IL op should exist in IL, and descendants in IL.X86.
        // Because of this we have this hack
        return;
      } else if (DebugEnabled == false) {
        return;
      } else if (DebugMode == DebugMode.Source) {
        // If the current position equals one of the offsets, then we have
        // reached a new atomic C# statement
        var xSP = mSequences.SingleOrDefault(q => q.Offset == aOp.Position);
        if (xSP == null) {
          return;
        } else if (xSP.LineStart == 0xFEEFEE) {
          // 0xFEEFEE means hiddenline -> we dont want to stop there
          return;
        }
      }

      // Check if the DebugStub has been disabled for this method
      if ((!IgnoreDebugStubAttribute) && (aMethod.DebugStubOff)) {
        return;
      }

      // This test fixes issue #15638
      if (null != aNamespace)
      {
          // Check options for Debug Level
          // Set based on TracedAssemblies
          if (TraceAssemblies == TraceAssemblies.Cosmos || TraceAssemblies == TraceAssemblies.User)
          {
              if (aNamespace.StartsWith("System.", StringComparison.InvariantCultureIgnoreCase)) {
                  return;
              }
              else if (aNamespace.ToLower() == "system") {
                  return;
              }
              else if (aNamespace.StartsWith("Microsoft.", StringComparison.InvariantCultureIgnoreCase)) {
                  return;
              }
              if (TraceAssemblies == TraceAssemblies.User)
              {
                  //TODO: Maybe an attribute that could be used to turn tracing on and off
                  //TODO: This doesnt match Cosmos.Kernel exact vs Cosmos.Kernel., so a user 
                  // could do Cosmos.KernelMine and it will fail. Need to fix this
                  if (aNamespace.StartsWith("Cosmos.Kernel", StringComparison.InvariantCultureIgnoreCase)) {
                      return;
                  }
                  else if (aNamespace.StartsWith("Cosmos.Sys", StringComparison.InvariantCultureIgnoreCase)) {
                      return;
                  }
                  else if (aNamespace.StartsWith("Cosmos.Hardware", StringComparison.InvariantCultureIgnoreCase)) {
                      return;
                  }
                  else if (aNamespace.StartsWith("Cosmos.IL2CPU", StringComparison.InvariantCultureIgnoreCase)) {
                      return;
                  }
              }
          }
      }

      // If we made it this far without a return, emit the Tracer
      new INT3();
    }

    protected MethodDefinition GetCecilMethodDefinitionForSymbolReading(MethodBase methodBase) {
      var xMethodBase = methodBase;
      if (xMethodBase.IsGenericMethod) {
        var xMethodInfo = (System.Reflection.MethodInfo)xMethodBase;
        xMethodBase = xMethodInfo.GetGenericMethodDefinition();
        if (xMethodBase.IsGenericMethod) {
          // apparently, a generic method can be derived from a generic method..
          throw new Exception("Make recursive");
        }
      }
      var xLocation = xMethodBase.DeclaringType.Assembly.Location;
      ModuleDefinition xModule = null;
      if (!mLoadedModules.TryGetValue(xLocation, out xModule)) {
        // if not in cache, try loading.
        if (xMethodBase.DeclaringType.Assembly.GlobalAssemblyCache || !File.Exists(xLocation)) {
          // file doesn't exist, so assume no symbols
          mLoadedModules.Add(xLocation, null);
          return null;
        } else {
          try {
            xModule = ModuleDefinition.ReadModule(xLocation, new ReaderParameters { ReadSymbols = true, SymbolReaderProvider = new Mono.Cecil.Pdb.PdbReaderProvider() });
          } catch (InvalidOperationException) {
            throw new Exception("Please check that dll and pdb file is matching on location: " + xLocation);
          }
          if (xModule.HasSymbols) {
            mLoadedModules.Add(xLocation, xModule);
          } else {
            mLoadedModules.Add(xLocation, null);
            return null;
          }
        }
      }
      if (xModule == null) {
        return null;
      }
      // todo: cache MethodDefinition ?
      return xModule.LookupToken(xMethodBase.MetadataToken) as MethodDefinition;
    }
  }
}
