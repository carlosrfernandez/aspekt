﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;
using System;
using System.Linq;

namespace Aspekt.Bootstrap
{

    public static class Bootstrap
    {

        // EnumerateMethods
        private static void EnumerateMethodAttributes(ModuleDefinition module, Action<TypeDefinition, MethodDefinition, CustomAttribute> enumFn, Predicate<CustomAttribute> pred)
        {
            foreach (var t in module.Types)
                foreach (var meth in t.Methods)
                    foreach (var attr in meth.CustomAttributes)
                    {
                        if(pred!=null && pred(attr))
                            enumFn(t, meth, attr);
                    }
        }

        private static bool HasCustomAttributeType(Collection<CustomAttribute> attributes, Type t)
        {
            foreach (CustomAttribute attr in attributes)
            {
                if (attr.AttributeType.FullName == t.FullName)
                    return true;
            }
            return false;
        }

        private static PropertyDefinition GetAttributeProperty(CustomAttribute attr, Predicate<PropertyDefinition> pred)
        {
            foreach (var p in attr.AttributeType.Resolve().Properties)
            {
                if (pred(p))
                    return p;
            }
            return null;
        }


        // Generating IL

        private static VariableDefinition CaptureMethodArguments(InstructionHelper ic, MethodDefinition md)
        {
            if (md.Parameters.Count == 0)
                return null;
            // first we need something to store the parameters in
            var argList = new VariableDefinition(md.Module.Import(typeof(Arguments)));
            md.Body.Variables.Add(argList);

            ic.Next(OpCodes.Ldc_I4, md.Parameters.Count);
            ic.NewObj<Arguments>(typeof(Int32)); // this will create the object we want on the stack
            ic.Next(OpCodes.Stloc, argList);
              

            for (int i = 0; i < md.Parameters.Count; ++i)
            {
                var p = md.Parameters[i];
                var pType = p.ParameterType;
                ic.Next(OpCodes.Ldloc, argList);
                ic.Next(OpCodes.Ldarg, p);
                // I don't think I care if it

                if (pType.IsValueType || pType.IsGenericParameter)
                    ic.Next(OpCodes.Box, pType);

                ic.CallVirt<Arguments>("Add", typeof(object));

            }

            return argList;
        }

        private static VariableDefinition GenerateMethodArgs(InstructionHelper ic, VariableDefinition argList, MethodDefinition md)
        {
            var methArgs = ic.NewVariable<MethodArguments>();
            ic.Next(OpCodes.Ldstr, md.Name);
            ic.Next(OpCodes.Ldstr, md.FullName);
            if (argList == null)
                ic.Next(OpCodes.Ldnull);
            else
                ic.Next(OpCodes.Ldloc, argList);
            ic.NewObj<MethodArguments>(typeof(String), typeof(String), typeof(Arguments));
            ic.Next(OpCodes.Stloc, methArgs);
            return methArgs;
        }

        private static void LoadArg(InstructionHelper ic, CustomAttributeArgument arg)
        {
            var type = arg.Type.MetadataType;
            switch (type)
            {
                case MetadataType.Void:
                    throw new Exception("no support for void ctor");
                case MetadataType.Boolean:
                    ic.Next(OpCodes.Ldc_I4, (bool)arg.Value);
                    return;
                case MetadataType.ValueType:
                case MetadataType.SByte:
                case MetadataType.Byte:
                case MetadataType.Char:
                case MetadataType.Int16:
                case MetadataType.Int32:
                case MetadataType.UInt16:
                case MetadataType.UInt32:
                    ic.Next(OpCodes.Ldc_I4, (int)arg.Value);
                    return;
		        case MetadataType.Int64:
                case MetadataType.UInt64:
                    ic.Next(OpCodes.Ldc_I8, (long)arg.Value);
                    return;
                case MetadataType.Double:
                    ic.Next(OpCodes.Ldc_R8, (double)arg.Value);
                    return;
                case MetadataType.String:
                    ic.Next(OpCodes.Ldstr, (string)arg.Value);
                    return;
                case MetadataType.Class:
                    // If it's a class type AND System.Type then I need to load the type. Otherwise not sure.
                    if (arg.Type.FullName == typeof(System.Type).FullName)
                    {
                        ic.Next(OpCodes.Ldtoken, (TypeReference)arg.Value);
                        ic.Call<Type>("GetTypeFromHandle", typeof(RuntimeTypeHandle));
                        return;
                    }
                    else
                        throw new Exception("unknown type");

                case MetadataType.Single:
                case MetadataType.Pointer:
                case MetadataType.ByReference:
                case MetadataType.Var:
                case MetadataType.Array:
                case MetadataType.GenericInstance:
                case MetadataType.TypedByReference:
                case MetadataType.IntPtr:
                case MetadataType.UIntPtr:
                case MetadataType.FunctionPointer:
                case MetadataType.Object:
                case MetadataType.MVar:
                case MetadataType.RequiredModifier:
                case MetadataType.OptionalModifier:
                case MetadataType.Sentinel:
                case MetadataType.Pinned:
                default:
                    throw new Exception("unknown type");
            }
        }

        private static void PlaceOnExitCalls(ILProcessor il, ModuleDefinition module, MethodDefinition md, VariableDefinition attrVar, VariableDefinition methArgs)
        {
            for(int i=0; i<md.Body.Instructions.Count; ++i)
            {
                Instruction inst = md.Body.Instructions[i];
                if (inst.OpCode == OpCodes.Ret)
                {
                    Instruction rep = inst;
                    if (md.ReturnType.FullName != typeof(void).FullName)
                        rep = inst.Previous; // get the previous instruction, because this SHOULD be the retval

                    var ih = new InstructionHelper(module, il, rep, InstructionHelper.Insert.Before);
                    ih.Next(OpCodes.Ldloc, attrVar);
                    ih.Next(OpCodes.Ldloc, methArgs);
                    ih.CallVirt<Aspect>("OnExit", typeof(MethodArguments));
                    i = i + 3;  // We just added 3 instructions
                }

            }
        }

        private static void InsertOnEntryCalls(InstructionHelper ih, VariableDefinition attrVar, VariableDefinition methArgs)
        {
            ih.Next(OpCodes.Ldloc, attrVar);
            ih.Next(OpCodes.Ldloc, methArgs);
            ih.CallVirt<Aspect>("OnEntry", typeof(MethodArguments));
        }

        private static VariableDefinition CreateAttribute(InstructionHelper ic, VariableDefinition methodArgs, MethodDefinition md, CustomAttribute attr)
        {
            var attrVar = ic.NewVariable(attr.AttributeType);
            // put the arguments on the stack. What about calling convention???
            foreach(var arg in attr.ConstructorArguments)
            {
                LoadArg(ic, arg);
            }
            ic.NewObj(attr.Constructor);
            ic.Next(OpCodes.Stloc, attrVar);
            return attrVar;
        }

        public static void Apply(String targetFileName)
        {
            var module = ModuleDefinition.ReadModule(targetFileName);

            // I know that right now, if we have multiple attributes, we're going to generate multiple method arguments.
            // I know how to deal with this.
            EnumerateMethodAttributes(module, (classType, meth, attr) =>
             {
                 meth.Body.SimplifyMacros();
                 var il = meth.Body.GetILProcessor();

                 var fi = meth.Body.Instructions.First();

                 var ih = new InstructionHelper(module, il, fi, InstructionHelper.Insert.Before);
                 var args = CaptureMethodArguments(ih, meth);
                 var methArgs = GenerateMethodArgs(ih, args, meth);

                 
                 // if the attribute overrides the method, we will put the call in.
                 // Otherwise, we will not.
                 var attrVar = CreateAttribute(ih, methArgs, meth, attr);

                 // What about static??
                 //
                 var prop = GetAttributeProperty(attr, (p) => { return HasCustomAttributeType(p.CustomAttributes, typeof(UseThisAttribute)); });
                 if (prop != null)
                 {
                     // set the thing
                     // if it has a setter and it's a convertible type?? I don't know how to do that.
                     if (prop.SetMethod != null && (prop.PropertyType.FullName == classType.FullName || prop.PropertyType.FullName == typeof(object).FullName))
                     {
                         ih.Next(OpCodes.Ldloc, attrVar);
                         ih.Next(OpCodes.Ldarg_0);
                         ih.Call(prop.SetMethod);
                     }
                 }

                 InsertOnEntryCalls(ih, attrVar, methArgs);

                 // walk the instructions looking for returns, based on what teh function is returning 
                 // is where we inject the OnExit instructions.
                 // so how do I tell what the function returns?
                 PlaceOnExitCalls(il,module,meth,attrVar,methArgs);

                 var ret = il.Create(OpCodes.Ret);
                 var leave = il.Create(OpCodes.Leave, ret);
                 
                 var exception = new VariableDefinition("ex", meth.Module.Import(typeof(Exception)));
                 meth.Body.Variables.Add(exception);
                 var c = new InstructionHelper(module, il, meth.Body.Instructions.Last());
                 c.Next(il.Create(OpCodes.Stloc_S, exception))
                     .Next(OpCodes.Ldloc, attrVar)
                     .Next(OpCodes.Ldloc, methArgs)
                     .Next(OpCodes.Ldloc_S, exception)
                     .CallVirt<Aspect>("OnException", typeof(MethodArguments), typeof(Exception))
                     .Next(OpCodes.Rethrow)
                     .Next(OpCodes.Ret);
                 
                 var handler = new ExceptionHandler(ExceptionHandlerType.Catch)
                 {
                     TryStart = fi,
                     TryEnd = c.FirstInstruction,
                     HandlerStart = c.FirstInstruction,
                     HandlerEnd = c.LastInstruction,
                     CatchType = module.Import(typeof(Exception)),
                 };

                 meth.Body.ExceptionHandlers.Add(handler);

                 meth.Body.OptimizeMacros();
             }, (attr) =>
             {
                 try
                 {
                     return attr.AttributeType.Resolve().BaseType.FullName == (typeof(Aspect).FullName);
                 }
                 catch (Exception)
                 {
                     return false; // if we can't resolve, then we just skip it.
                 }
             });


            module.Write(targetFileName);
        }
    }


}
