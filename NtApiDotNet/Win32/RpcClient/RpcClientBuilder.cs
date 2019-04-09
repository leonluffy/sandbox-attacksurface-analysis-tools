﻿//  Copyright 2019 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using Microsoft.CSharp;
using NtApiDotNet.Ndr;
using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace NtApiDotNet.Win32.RpcClient
{
    /// <summary>
    /// Builder to create an RPC client from an RpcServer class.
    /// </summary>
    public sealed class RpcClientBuilder
    {
        #region Private Members
        private static readonly Dictionary<Tuple<RpcServer, RpcClientBuilderArguments>, Assembly> _compiled_clients
            = new Dictionary<Tuple<RpcServer, RpcClientBuilderArguments>, Assembly>();
        private readonly Dictionary<NdrBaseTypeReference, RpcTypeDescriptor> _type_descriptors;
        private readonly RpcServer _server;
        private readonly RpcClientBuilderArguments _args;
        private readonly HashSet<string> _proc_names;

        private bool HasFlag(RpcClientBuilderFlags flag)
        {
            return (_args.Flags & flag) == flag;
        }

        private RpcTypeDescriptor GetTypeDescriptorInternal(NdrBaseTypeReference type, MarshalHelperBuilder marshal_helper)
        {
            if (type is NdrSimpleTypeReference)
            {
                switch (type.Format)
                {
                    case NdrFormatCharacter.FC_BYTE:
                    case NdrFormatCharacter.FC_USMALL:
                        return new RpcTypeDescriptor(typeof(byte), "ReadByte", "WriteByte", type);
                    case NdrFormatCharacter.FC_SMALL:
                    case NdrFormatCharacter.FC_CHAR:
                        return new RpcTypeDescriptor(typeof(sbyte), "ReadSByte", "WriteSByte", type);
                    case NdrFormatCharacter.FC_WCHAR:
                        return new RpcTypeDescriptor(typeof(char), "ReadChar", "WriteChar", type);
                    case NdrFormatCharacter.FC_SHORT:
                    case NdrFormatCharacter.FC_ENUM16:
                        return new RpcTypeDescriptor(typeof(short), "ReadInt16", "WriteInt16", type);
                    case NdrFormatCharacter.FC_USHORT:
                        return new RpcTypeDescriptor(typeof(ushort), "ReadUInt16", "WriteUInt16", type);
                    case NdrFormatCharacter.FC_LONG:
                    case NdrFormatCharacter.FC_ENUM32:
                        return new RpcTypeDescriptor(typeof(int), "ReadInt32", "WriteInt32", type);
                    case NdrFormatCharacter.FC_ULONG:
                    case NdrFormatCharacter.FC_ERROR_STATUS_T:
                        return new RpcTypeDescriptor(typeof(uint), "ReadUInt32", "WriteUInt32", type);
                    case NdrFormatCharacter.FC_FLOAT:
                        return new RpcTypeDescriptor(typeof(float), "ReadFloat", "WriteFloat", type);
                    case NdrFormatCharacter.FC_HYPER:
                        return new RpcTypeDescriptor(typeof(long), "ReadInt64", "WriteInt64", type);
                    case NdrFormatCharacter.FC_DOUBLE:
                        return new RpcTypeDescriptor(typeof(double), "ReadDouble", "WriteDouble", type);
                    case NdrFormatCharacter.FC_INT3264:
                        return new RpcTypeDescriptor(typeof(NdrInt3264), "ReadInt3264", "WriteInt3264", type);
                    case NdrFormatCharacter.FC_UINT3264:
                        return new RpcTypeDescriptor(typeof(NdrUInt3264), "ReadUInt3264", "WriteUInt3264", type);
                    case NdrFormatCharacter.FC_C_WSTRING:
                        return new RpcTypeDescriptor(typeof(string), "ReadConformantString", "WriteConformantString", type);
                    case NdrFormatCharacter.FC_C_CSTRING:
                        return new RpcTypeDescriptor(typeof(string), "ReadAnsiConformantString", "WriteAnsiConformantString", type);
                    case NdrFormatCharacter.FC_CSTRING:
                    case NdrFormatCharacter.FC_WSTRING:
                        break;
                }
            }
            else if (type is NdrKnownTypeReference known_type)
            {
                switch (known_type.KnownType)
                {
                    case NdrKnownTypes.GUID:
                        return new RpcTypeDescriptor(typeof(Guid), "ReadGuid", "WriteGuid", type);
                    case NdrKnownTypes.BSTR:
                    case NdrKnownTypes.HSTRING:
                        // Implement these custom marshallers.
                        break;
                }
            }
            else if (type is NdrBaseStringTypeReference)
            {
                if (type is NdrConformantStringTypeReference conformant_str)
                {
                    if (conformant_str.Format == NdrFormatCharacter.FC_C_CSTRING)
                    {
                        return new RpcTypeDescriptor(typeof(string), "ReadAnsiConformantString", "WriteAnsiConformantString", type);
                    }
                    return new RpcTypeDescriptor(typeof(string), "ReadConformantString", "WriteConformantString", type);
                }
            }
            else if (type is NdrSystemHandleTypeReference system_handle)
            {
                return new RpcTypeDescriptor(system_handle.GetSystemHandleType(),
                    "ReadSystemHandle", marshal_helper, "WriteSystemHandle", type, null, null, 
                    new AdditionalArguments(true), new AdditionalArguments(true));
            }
            else if (type is NdrSimpleArrayTypeReference simple_array)
            {
                RpcTypeDescriptor element_type = GetTypeDescriptor(simple_array.ElementType, marshal_helper);
                CodeExpression arg = CodeGenUtils.GetPrimitive(simple_array.ElementCount);
                if (element_type.BuiltinType == typeof(char))
                {
                    var args = new AdditionalArguments(false, arg);
                    return new RpcTypeDescriptor(typeof(string), "ReadFixedString", marshal_helper, "WriteFixedString", type, null, null, args, args)
                    {
                        FixedCount = simple_array.ElementCount
                    };
                }
                else if (element_type.BuiltinType == typeof(byte))
                {
                    var args = new AdditionalArguments(false, arg);
                    return new RpcTypeDescriptor(typeof(byte[]), "ReadBytes", marshal_helper, "WriteFixedBytes", type, null, null, args, args)
                    {
                        FixedCount = simple_array.ElementCount
                    };
                }
                else if (element_type.BuiltinType != null && element_type.BuiltinType.IsPrimitive)
                {
                    var args = new AdditionalArguments(true, arg);

                    return new RpcTypeDescriptor(element_type.CodeType.ToRefArray(), true,
                        "ReadFixedPrimitiveArray", marshal_helper, "WriteFixedPrimitiveArray", type, 
                        null, null, args, args)
                    {
                        FixedCount = simple_array.ElementCount
                    };
                }
            }
            else if (type is NdrPointerTypeReference pointer)
            {
                var desc = GetTypeDescriptor(pointer.Type, marshal_helper);
                RpcPointerType pointer_type = RpcPointerType.None;
                switch (pointer.Format)
                {
                    case NdrFormatCharacter.FC_UP:
                        pointer_type = RpcPointerType.Unique;
                        break;
                    case NdrFormatCharacter.FC_RP:
                        pointer_type = RpcPointerType.Reference;
                        break;
                    default:
                        pointer_type = RpcPointerType.Full;
                        break;
                }
                return new RpcTypeDescriptor(desc, pointer_type);
            }
            else if (type is NdrSupplementTypeReference supp)
            {
                return GetTypeDescriptor(supp.SupplementType, marshal_helper);
            }
            else if (type is NdrHandleTypeReference handle)
            {
                if (handle.Format == NdrFormatCharacter.FC_BIND_CONTEXT)
                {
                    return new RpcTypeDescriptor(typeof(NdrContextHandle), "ReadContextHandle", "WriteContextHandle", type);
                }
            }
            else if (type is NdrRangeTypeReference range)
            {
                return GetTypeDescriptor(range.RangeType, marshal_helper);
            }
            else if (type is NdrBogusArrayTypeReference bogus_array)
            {
                RpcTypeDescriptor element_type = GetTypeDescriptor(bogus_array.ElementType, marshal_helper);
                if (bogus_array.VarianceDescriptor.IsValid && bogus_array.VarianceDescriptor.ValidateCorrelation() 
                    && !bogus_array.ConformanceDescriptor.IsValid && element_type.Constructed )
                {
                    // For now we only support constructed types with variance and no conformance.
                    // The variance also needs to be a constant or a normal correlation.
                    return new RpcTypeDescriptor(new CodeTypeReference(element_type.CodeType, 1), false,
                        "ReadVaryingBogusArrayStruct", marshal_helper, "WriteVaryingBogusArrayStruct", 
                        type, null, bogus_array.VarianceDescriptor, new AdditionalArguments(true, typeof(long).ToRef()), 
                        new AdditionalArguments(true))
                    {
                        FixedCount = bogus_array.ElementCount
                    };
                }
            }
            
            var type_name_arg = CodeGenUtils.GetPrimitive(type.Format.ToString());
            AdditionalArguments additional_args = new AdditionalArguments(false, type_name_arg);
            return new RpcTypeDescriptor(typeof(NdrUnsupported), "ReadUnsupported", marshal_helper, 
                "WriteUnsupported", type, null, null, additional_args, additional_args);
        }

        // Should implement this for each type rather than this.
        private RpcTypeDescriptor GetTypeDescriptor(NdrBaseTypeReference type, MarshalHelperBuilder marshal_helper)
        {
            if (type == null)
            {
                return new RpcTypeDescriptor(typeof(void), "Unsupported", "Unsupported", null);
            }

            if (!_type_descriptors.ContainsKey(type))
            {
                _type_descriptors[type] = GetTypeDescriptorInternal(type, marshal_helper);
            }
            return _type_descriptors[type];
        }

        private const string MARSHAL_NAME = "m";
        private const string UNMARSHAL_NAME = "u";
        private const string CONSTRUCTOR_STRUCT_NAME = "_Constructors";
        private const string ARRAY_CONSTRUCTOR_STRUCT_NAME = "_Array_Constructors";
        private const string UNMARSHAL_HELPER_NAME = "_Unmarshal_Helper";
        private const string MARSHAL_HELPER_NAME = "_Marshal_Helper";

        private int GenerateComplexTypes(CodeNamespace ns, MarshalHelperBuilder marshal_helper)
        {
            int type_count = 0;

            // First populate the type cache.
            foreach (var complex_type in _server.ComplexTypes)
            {
                if (complex_type is NdrBaseStructureTypeReference struct_type)
                {
                    var type_desc = new RpcTypeDescriptor(complex_type.Name, true,
                         "ReadStruct", marshal_helper, "WriteStruct", complex_type, null, null,
                         new AdditionalArguments(true), new AdditionalArguments(true));
                    _type_descriptors[complex_type] = type_desc;
                    type_count++;
                }
            }

            if (type_count == 0)
            {
                return 0;
            }

            bool create_constructor_properties = HasFlag(RpcClientBuilderFlags.GenerateConstructorProperties);
            CodeTypeDeclaration constructor_type = null;
            CodeTypeDeclaration array_constructor_type = null;

            if (create_constructor_properties)
            {
                constructor_type = ns.AddType(CONSTRUCTOR_STRUCT_NAME);
                constructor_type.AddStartRegion("Constructors");
                constructor_type.IsStruct = true;
                array_constructor_type = ns.AddType(ARRAY_CONSTRUCTOR_STRUCT_NAME);
                array_constructor_type.IsStruct = true;
                array_constructor_type.AddEndRegion();
            }

            CodeTypeDeclaration start_type = null;
            CodeTypeDeclaration end_type = null;

            // Now generate the complex types.
            foreach (var complex_type in _server.ComplexTypes)
            {
                if (!(complex_type is NdrBaseStructureTypeReference struct_type))
                {
                    ns.Comments.Add(new CodeCommentStatement($"Unsupported type {complex_type.GetType()} {complex_type.Name}"));
                    continue;
                }

                var s_type = ns.AddType(complex_type.Name);
                if (start_type == null)
                {
                    start_type = s_type;
                }
                end_type = s_type;
                s_type.IsStruct = true;
                s_type.BaseTypes.Add(new CodeTypeReference(typeof(INdrStructure)));

                var marshal_method = s_type.AddMarshalMethod(MARSHAL_NAME, marshal_helper);
                marshal_method.AddAlign(MARSHAL_NAME, struct_type.Alignment + 1);
                
                var unmarshal_method = s_type.AddUnmarshalMethod(UNMARSHAL_NAME, marshal_helper);
                unmarshal_method.AddAlign(UNMARSHAL_NAME, struct_type.Alignment + 1);

                var offset_to_name =
                    struct_type.Members.Select(m => Tuple.Create(m.Offset, m.Name)).ToList();
                var default_initialize_expr = new Dictionary<string, CodeExpression>();
                var member_parameters = new List<Tuple<CodeTypeReference, string>>();

                foreach (var member in struct_type.Members)
                {
                    var f_type = GetTypeDescriptor(member.MemberType, marshal_helper);
                    s_type.AddField(f_type.GetStructureType(), member.Name, MemberAttributes.Public);
                    member_parameters.Add(Tuple.Create(f_type.GetParameterType(), member.Name));

                    List<RpcMarshalArgument> extra_marshal_args = new List<RpcMarshalArgument>();
                    if (f_type.VarianceDescriptor.IsValid)
                    {
                        extra_marshal_args.Add(f_type.VarianceDescriptor.CalculateCorrelationArgument(member.Offset, offset_to_name));
                    }

                    if (f_type.Pointer)
                    {
                        marshal_method.AddDeferredMarshalCall(f_type, MARSHAL_NAME, member.Name, extra_marshal_args.ToArray());
                        unmarshal_method.AddDeferredEmbeddedUnmarshalCall(f_type, UNMARSHAL_NAME, member.Name);
                    }
                    else
                    {
                        if (!f_type.ValueType)
                        {
                            marshal_method.AddNullCheck(MARSHAL_NAME, member.Name);
                        }

                        marshal_method.AddMarshalCall(f_type, MARSHAL_NAME, member.Name, extra_marshal_args.ToArray());
                        unmarshal_method.AddUnmarshalCall(f_type, UNMARSHAL_NAME, member.Name);
                    }

                    if (!f_type.Pointer || f_type.PointerType == RpcPointerType.Reference)
                    {
                        if (f_type.CodeType.ArrayRank > 0)
                        {
                            default_initialize_expr.Add(member.Name, new CodeArrayCreateExpression(f_type.CodeType, CodeGenUtils.GetPrimitive(f_type.FixedCount)));
                        }
                        else if (f_type.BuiltinType == typeof(string) && f_type.FixedCount > 0)
                        {
                            default_initialize_expr.Add(member.Name, new CodeObjectCreateExpression(f_type.CodeType, CodeGenUtils.GetPrimitive('\0'), 
                                CodeGenUtils.GetPrimitive(f_type.FixedCount)));
                        }
                    }
                }

                var p_type = _type_descriptors[complex_type];

                if (!create_constructor_properties)
                {
                    s_type.AddDefaultConstructorMethod("CreateDefault", MemberAttributes.Public | MemberAttributes.Static, p_type, default_initialize_expr);
                    s_type.AddConstructorMethod(p_type, member_parameters);
                }
                else
                {
                    constructor_type.AddDefaultConstructorMethod(complex_type.Name, MemberAttributes.Public | MemberAttributes.Final, p_type, default_initialize_expr);
                    constructor_type.AddConstructorMethod(complex_type.Name, p_type, member_parameters);
                    array_constructor_type.AddArrayConstructorMethod(complex_type.Name, p_type);
                }
            }

            if (type_count > 0)
            {
                start_type.AddStartRegion("Complex Types");
                end_type.AddEndRegion();
            }

            return type_count;
        }

        private void GenerateClient(string name, CodeNamespace ns, int complex_type_count, MarshalHelperBuilder marshal_helper)
        {
            CodeTypeDeclaration type = ns.AddType(name);
            type.AddStartRegion("Client Implementation");
            type.AddEndRegion();
            type.IsClass = true;
            type.TypeAttributes = TypeAttributes.Public | TypeAttributes.Sealed;
            type.BaseTypes.Add(typeof(RpcAlpcClientBase));

            CodeConstructor constructor = type.AddConstructor(MemberAttributes.Public | MemberAttributes.Final);
            constructor.BaseConstructorArgs.Add(CodeGenUtils.GetPrimitive(_server.InterfaceId.ToString()));
            constructor.BaseConstructorArgs.Add(CodeGenUtils.GetPrimitive(_server.InterfaceVersion.Major));
            constructor.BaseConstructorArgs.Add(CodeGenUtils.GetPrimitive(_server.InterfaceVersion.Minor));

            CodeTypeReference unmarshal_helper_type = complex_type_count > 0 
                ? new CodeTypeReference(CodeGenUtils.MakeIdentifier(UNMARSHAL_HELPER_NAME)) : null;

            foreach (var proc in _server.Procedures)
            {
                string proc_name = proc.Name;
                if(!_proc_names.Add(proc_name))
                {
                    proc_name = $"{proc_name}_{proc.ProcNum}";
                    if (!_proc_names.Add(proc_name))
                    {
                        throw new ArgumentException($"Duplicate name {proc.Name}");
                    }
                }

                var method = type.AddMethod(proc_name, MemberAttributes.Public | MemberAttributes.Final);
                RpcTypeDescriptor return_type = GetTypeDescriptor(proc.ReturnValue?.Type, marshal_helper);
                if (return_type == null)
                {
                    method.ThrowNotImplemented("Return type unsupported.");
                    continue;
                }

                var offset_to_name =
                    proc.Params.Select(p => Tuple.Create(p.Offset, p.Name)).ToList();

                method.ReturnType = return_type.CodeType;
                method.CreateMarshalObject(MARSHAL_NAME, marshal_helper);
                foreach (var p in proc.Params)
                {
                    if (p == proc.Handle)
                    {
                        continue;
                    }
                    RpcTypeDescriptor p_type = GetTypeDescriptor(p.Type, marshal_helper);

                    List<RpcMarshalArgument> extra_marshal_args = new List<RpcMarshalArgument>();
                    if (p_type.VarianceDescriptor.IsValid)
                    {
                        extra_marshal_args.Add(p_type.VarianceDescriptor.CalculateCorrelationArgument(p.Offset, offset_to_name));
                    }

                    var p_obj = method.AddParam(p_type.GetParameterType(), p.Name);
                    p_obj.Direction = p.GetDirection();
                    if (!p.IsIn)
                    {
                        continue;
                    }
                    if (p_type.Pointer)
                    {
                        if (p_type.PointerType == RpcPointerType.Reference)
                        {
                            method.AddNullCheck(MARSHAL_NAME, p.Name);
                        }
                        else
                        {
                            method.AddWriteReferent(MARSHAL_NAME, p.Name);
                        }
                    }
                    else if (!p_type.ValueType)
                    {
                        method.AddNullCheck(MARSHAL_NAME, p.Name);
                    }
                    method.AddMarshalCall(p_type, MARSHAL_NAME, p.Name, extra_marshal_args.ToArray());
                    // If it's a constructed type then ensure any deferred writes are flushed.
                    if (p_type.Constructed)
                    {
                        method.AddFlushDeferredWrites(MARSHAL_NAME);
                    }
                }

                method.SendReceive(MARSHAL_NAME, UNMARSHAL_NAME, proc.ProcNum, marshal_helper);

                foreach (var p in proc.Params.Where(x => x.IsOut))
                {
                    if (p == proc.Handle)
                    {
                        continue;
                    }

                    RpcTypeDescriptor p_type = GetTypeDescriptor(p.Type, marshal_helper);
                    if (p_type.Pointer)
                    {
                        method.AddPointerUnmarshalCall(p_type, UNMARSHAL_NAME, p.Name);
                    }
                    else
                    {
                        method.AddUnmarshalCall(p_type, UNMARSHAL_NAME, p.Name);
                    }
                    if (p_type.Constructed)
                    {
                        method.AddPopluateDeferredPointers(UNMARSHAL_NAME);
                    }
                }

                method.AddUnmarshalReturn(return_type, UNMARSHAL_NAME);
            }

            if (complex_type_count > 0 && HasFlag(RpcClientBuilderFlags.GenerateConstructorProperties))
            {
                var constructor_type = new CodeTypeReference(CodeGenUtils.MakeIdentifier(CONSTRUCTOR_STRUCT_NAME));
                var prop = type.AddProperty("New", constructor_type, MemberAttributes.Public | MemberAttributes.Final,
                    new CodeMethodReturnStatement(new CodeObjectCreateExpression(constructor_type)));
                constructor_type = new CodeTypeReference(CodeGenUtils.MakeIdentifier(ARRAY_CONSTRUCTOR_STRUCT_NAME));
                type.AddProperty("NewArray", constructor_type, MemberAttributes.Public | MemberAttributes.Final, 
                    new CodeMethodReturnStatement(new CodeObjectCreateExpression(constructor_type)));
            }
        }

        private static string GenerateSourceCode(CodeDomProvider provider, CodeGeneratorOptions options, CodeCompileUnit unit)
        {
            StringBuilder builder = new StringBuilder();
            TextWriter writer = new StringWriter(builder);
            provider.GenerateCodeFromCompileUnit(unit, writer, options);
            return builder.ToString();
        }

        private void AddServerComment(CodeCompileUnit unit)
        {
            CodeNamespace ns = unit.AddNamespace(string.Empty);

            ns.AddComment($"Source Executable: {_server.FilePath}");
            ns.AddComment($"Interface ID: {_server.InterfaceId}");
            ns.AddComment($"Interface Version: {_server.InterfaceVersion}");
            ns.AddComment($"Client Generated: {DateTime.Now}");
            ns.AddComment($"NtApiDotNet Version: {NtObjectUtils.GetVersion()}");
        }

        private CodeCompileUnit Generate()
        {
            CodeCompileUnit unit = new CodeCompileUnit();
            string ns_name = _args.NamespaceName;
            if (string.IsNullOrWhiteSpace(ns_name))
            {
                ns_name = $"rpc_{_server.InterfaceId.ToString().Replace('-', '_')}_{_server.InterfaceVersion.Major}_{_server.InterfaceVersion.Minor}";
            }
            string name = _args.ClientName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Client";
            }
            AddServerComment(unit);
            CodeNamespace ns = unit.AddNamespace(ns_name);
            MarshalHelperBuilder marshal_helper = new MarshalHelperBuilder(ns, MARSHAL_HELPER_NAME, UNMARSHAL_HELPER_NAME);
            int complex_type_count = GenerateComplexTypes(ns, marshal_helper);
            GenerateClient(name, ns, complex_type_count, marshal_helper);

            return unit;
        }

        private Assembly Compile(CodeCompileUnit unit, CodeDomProvider provider)
        {
            CompilerParameters compile_params = new CompilerParameters();
            TempFileCollection temp_files = new TempFileCollection(Path.GetTempPath());

            bool enable_debugging = HasFlag(RpcClientBuilderFlags.EnableDebugging);

            compile_params.GenerateExecutable = false;
            compile_params.GenerateInMemory = true;
            compile_params.IncludeDebugInformation = enable_debugging;
            compile_params.TempFiles = temp_files;
            temp_files.KeepFiles = enable_debugging;
            compile_params.ReferencedAssemblies.Add(typeof(RpcClientBuilder).Assembly.Location);
            CompilerResults results = provider.CompileAssemblyFromDom(compile_params, unit);
            if (results.Errors.HasErrors)
            {
                foreach (CompilerError e in results.Errors)
                {
                    System.Diagnostics.Debug.WriteLine(e.ToString());
                }
                throw new InvalidOperationException("Internal error compiling RPC source code");
            }
            return results.CompiledAssembly;
        }

        #endregion

        #region Constructors

        private RpcClientBuilder(RpcServer server, RpcClientBuilderArguments args)
        {
            _server = server;
            _type_descriptors = new Dictionary<NdrBaseTypeReference, RpcTypeDescriptor>();
            _args = args;
            _proc_names = new HashSet<string>();
        }

        #endregion

        #region Static Methods

        /// <summary>
        /// Build a source file for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <param name="options">The code genearation options, can be null.</param>
        /// <param name="provider">The code dom provider, such as CSharpDomProvider</param>
        /// <returns>The source code file.</returns>
        public static string BuildSource(RpcServer server, RpcClientBuilderArguments args, CodeDomProvider provider, CodeGeneratorOptions options)
        {
            return GenerateSourceCode(provider, options, new RpcClientBuilder(server, args).Generate());
        }

        /// <summary>
        /// Build a C# source file for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <returns>The C# source code file.</returns>
        public static string BuildSource(RpcServer server, RpcClientBuilderArguments args)
        {
            CodeDomProvider provider = new CSharpCodeProvider();
            CodeGeneratorOptions options = new CodeGeneratorOptions
            {
                IndentString = "    ",
                BlankLinesBetweenMembers = false,
                VerbatimOrder = true,
                BracingStyle = "C"
            };
            return BuildSource(server, args, provider, options);
        }

        /// <summary>
        /// Build a C# source file for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <returns>The C# source code file.</returns>
        public static string BuildSource(RpcServer server)
        {
            return BuildSource(server, new RpcClientBuilderArguments());
        }

        /// <summary>
        /// Compile an in-memory assembly for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <param name="ignore_cache">True to ignore cached assemblies.</param>
        /// <param name="provider">Code DOM provider to compile the assembly.</param>
        /// <returns>The compiled assembly.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static Assembly BuildAssembly(RpcServer server, RpcClientBuilderArguments args, bool ignore_cache, CodeDomProvider provider)
        {
            var builder = new RpcClientBuilder(server, args);
            if (ignore_cache)
            {
                return builder.Compile(builder.Generate(), provider);
            }

            var key = Tuple.Create(server, args);
            if (!_compiled_clients.ContainsKey(key))
            {
                _compiled_clients[key] = builder.Compile(builder.Generate(), provider);
            }
            return _compiled_clients[key];
        }

        /// <summary>
        /// Compile an in-memory assembly for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <param name="ignore_cache">True to ignore cached assemblies.</param>
        /// <returns>The compiled assembly.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static Assembly BuildAssembly(RpcServer server, RpcClientBuilderArguments args, bool ignore_cache)
        {
            return BuildAssembly(server, args, ignore_cache, new CSharpCodeProvider());
        }

        /// <summary>
        /// Compile an in-memory assembly for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <returns>The compiled assembly.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static Assembly BuildAssembly(RpcServer server, RpcClientBuilderArguments args)
        {
            return BuildAssembly(server, args, false);
        }

        /// <summary>
        /// Compile an in-memory assembly for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="ignore_cache">True to ignore cached assemblies.</param>
        /// <returns>The compiled assembly.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static Assembly BuildAssembly(RpcServer server, bool ignore_cache)
        {
            return BuildAssembly(server, new RpcClientBuilderArguments(), ignore_cache);
        }

        /// <summary>
        /// Compile an in-memory assembly for the RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <returns>The compiled assembly.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static Assembly BuildAssembly(RpcServer server)
        {
            return BuildAssembly(server, false);
        }

        /// <summary>
        /// Create an instance of an RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="ignore_cache">True to ignore cached assemblies.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <param name="provider">Code DOM provider to compile the assembly.</param>
        /// <returns>The created RPC client.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static RpcAlpcClientBase CreateClient(RpcServer server, RpcClientBuilderArguments args, bool ignore_cache, CodeDomProvider provider)
        {
            Type type = BuildAssembly(server, args, ignore_cache, provider ?? new CSharpCodeProvider()).GetTypes().Where(t => typeof(RpcAlpcClientBase).IsAssignableFrom(t)).First();
            return (RpcAlpcClientBase)Activator.CreateInstance(type);
        }

        /// <summary>
        /// Create an instance of an RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="ignore_cache">True to ignore cached assemblies.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <returns>The created RPC client.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static RpcAlpcClientBase CreateClient(RpcServer server, RpcClientBuilderArguments args, bool ignore_cache)
        {
            return CreateClient(server, args, ignore_cache, null);
        }

        /// <summary>
        /// Create an instance of an RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <param name="args">Additional builder arguments.</param>
        /// <returns>The created RPC client.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static RpcAlpcClientBase CreateClient(RpcServer server, RpcClientBuilderArguments args)
        {
            return CreateClient(server, args, false);
        }

        /// <summary>
        /// Create an instance of an RPC client.
        /// </summary>
        /// <param name="server">The RPC server to base the client on.</param>
        /// <returns>The created RPC client.</returns>
        /// <remarks>This method will cache the results of the compilation against the RpcServer.</remarks>
        public static RpcAlpcClientBase CreateClient(RpcServer server)
        {
            return CreateClient(server, new RpcClientBuilderArguments());
        }

        #endregion
    }
}
