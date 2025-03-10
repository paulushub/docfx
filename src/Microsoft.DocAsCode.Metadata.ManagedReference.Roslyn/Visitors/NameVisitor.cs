// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Metadata.ManagedReference
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Diagnostics;
    using System.Reflection.Metadata;
    using System.Linq;
    using System.Text;

    using Microsoft.CodeAnalysis;

    public abstract class NameVisitorCreator
    {
        private static readonly CSharpNameVisitorCreator[] _csCreators =
            (from x in Enumerable.Range(0, (int)NameOptions.All + 1)
             select new CSharpNameVisitorCreator((NameOptions)x)).ToArray();
        private static readonly VBNameVisitorCreator[] _vbCreators =
            (from x in Enumerable.Range(0, (int)NameOptions.All + 1)
             select new VBNameVisitorCreator((NameOptions)x)).ToArray();

        protected NameVisitorCreator()
        {
        }

        public string GetName(ISymbol symbol)
        {
            var visitor = Create();
            symbol.Accept(visitor);
            return visitor.GetTypeName();
        }

        protected abstract NameVisitor Create();

        public static NameVisitorCreator GetCSharp(NameOptions option)
        {
            if (option < NameOptions.None || option > NameOptions.All)
            {
                throw new ArgumentOutOfRangeException("option");
            }
            return _csCreators[(int)option];
        }

        public static NameVisitorCreator GetVB(NameOptions option)
        {
            if (option < NameOptions.None || option > NameOptions.All)
            {
                throw new ArgumentOutOfRangeException("option");
            }
            return _vbCreators[(int)option];
        }
    }

    public abstract class NameVisitor : SymbolVisitor
    {
        private readonly StringBuilder sb;

        protected NameVisitor()
        {
            sb = new StringBuilder();
        }

        protected void Append(string text)
        {
            sb.Append(text);
        }

        internal string GetTypeName()
        {
            return sb.ToString();
        }
    }

    [Flags]
    public enum NameOptions
    {
        None = 0,
        UseAlias = 1,
        WithNamespace = 2,
        WithTypeGenericParameter = 4,
        WithParameter = 8,
        WithType = 16,
        WithMethodGenericParameter = 32,
        WithNullableAnnotations = 64,
        WithGenericParameter = WithTypeGenericParameter | WithMethodGenericParameter,
        Qualified = WithNamespace | WithType,
        All = UseAlias | WithNamespace | WithTypeGenericParameter | WithParameter | WithType | WithMethodGenericParameter | WithNullableAnnotations,
    }

    public class CSharpNameVisitorCreator : NameVisitorCreator
    {
        private readonly NameOptions _options;

        public CSharpNameVisitorCreator(NameOptions options)
        {
            _options = options;
        }

        protected override NameVisitor Create()
        {
            return new CSharpNameVisitor(_options);
        }
    }

    internal sealed class CSharpNameVisitor : NameVisitor
    {
        private readonly NameOptions Options;

        public CSharpNameVisitor(NameOptions options)
        {
            Options = options;
        }

        public override void VisitNamedType(INamedTypeSymbol symbol)
        {
            if ((Options & NameOptions.UseAlias) == NameOptions.UseAlias &&
                TrySpecialType(symbol))
            {
                return;
            }
            if (symbol.ContainingType != null)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            else if ((Options & NameOptions.WithNamespace) == NameOptions.WithNamespace)
            {
                if (!VisitorHelper.InGlobalNamespace(symbol))
                {
                    symbol.ContainingNamespace.Accept(this);
                    Append(".");
                }
            }
            if (symbol.IsTupleType)
            {
                if ((Options & NameOptions.Qualified) == NameOptions.Qualified)
                {
                    Append("ValueTuple");
                    symbol = symbol.TupleUnderlyingType ?? symbol;
                }
                else
                {
                    Append("(");
                    for (var i = 0; i < symbol.TupleElements.Length; i++)
                    {
                        if (i > 0)
                        {
                            Append(", ");
                        }
                        var tupleElement = symbol.TupleElements[i];
                        tupleElement.Type.Accept(this);
                        WriteNullableAnnotation(tupleElement.NullableAnnotation);

                        if (!tupleElement.IsImplicitlyDeclared)
                        {
                            Append(" ");
                            Append(tupleElement.Name);
                        }
                    }
                    Append(")");
                    return;
                }
            }
            else
            {
                Append(symbol.Name);
            }
            if ((Options & NameOptions.WithTypeGenericParameter) == NameOptions.WithTypeGenericParameter &&
                symbol.TypeParameters.Length > 0)
            {
                if (symbol.TypeArguments != null && symbol.TypeArguments.Length > 0)
                {
                    if (symbol.IsUnboundGenericType)
                    {
                        WriteGeneric(symbol.TypeArguments.Length);
                    }
                    else
                    {
                        WriteGeneric(symbol.TypeArguments, symbol.TypeArgumentNullableAnnotations);
                    }
                }
                else
                {
                    WriteGeneric(symbol.TypeParameters, ImmutableArray<NullableAnnotation>.Empty);
                }
            }
        }

        public override void VisitNamespace(INamespaceSymbol symbol)
        {
            if (symbol.IsGlobalNamespace)
            {
                Append(VisitorHelper.GlobalNamespaceId);
                return;
            }

            if (!symbol.ContainingNamespace.IsGlobalNamespace)
            {
                symbol.ContainingNamespace.Accept(this);
                Append(".");
            }
            Append(symbol.Name);
        }

        public override void VisitArrayType(IArrayTypeSymbol symbol)
        {
            symbol.ElementType.Accept(this);
            WriteNullableAnnotation(symbol.ElementNullableAnnotation);

            if (symbol.Rank == 1)
            {
                Append("[]");
            }
            else
            {
                Append("[");
                for (int i = 1; i < symbol.Rank; i++)
                {
                    Append(",");
                }
                Append("]");
            }
        }

        public override void VisitPointerType(IPointerTypeSymbol symbol)
        {
            symbol.PointedAtType.Accept(this);
            Append("*");
        }

        public override void VisitFunctionPointerType(IFunctionPointerTypeSymbol symbol)
        {
            Append("delegate*");

            var signature = symbol.Signature;
            
            if (signature.CallingConvention != SignatureCallingConvention.Default)
            {
                Append(" unmanaged");

                if ((signature.CallingConvention != SignatureCallingConvention.Unmanaged) || (signature.UnmanagedCallingConventionTypes.Length != 0))
                {
                    Append("[");

                    if (signature.UnmanagedCallingConventionTypes.Length != 0)
                    {
                        WriteUnmanagedCallConv(signature.UnmanagedCallingConventionTypes[0]);

                        for (int i = 1; i < signature.UnmanagedCallingConventionTypes.Length; i++)
                        {
                            Append(", ");
                            WriteUnmanagedCallConv(signature.UnmanagedCallingConventionTypes[i]);
                        }
                    }
                    else
                    {
                        WriteUnmanagedCallConv(signature.CallingConvention);
                    }

                    Append("]");
                }
            }

            Append("<");

            foreach (var parameter in signature.Parameters)
            {
                WriteRefKind(parameter.RefKind, isReturn: false);
                parameter.Type.Accept(this);
                Append(", ");
            }
            
            WriteRefKind(signature.RefKind, isReturn: true);
            signature.ReturnType.Accept(this);

            Append(">");
        }

        public override void VisitTypeParameter(ITypeParameterSymbol symbol)
        {
            Append(symbol.Name);
        }

        public override void VisitMethod(IMethodSymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            switch (symbol.MethodKind)
            {
                case MethodKind.Constructor:
                    Append(symbol.ContainingType.Name);
                    break;
                case MethodKind.Conversion:
                    if (symbol.Name == "op_Explicit")
                    {
                        Append("Explicit");
                    }
                    else if (symbol.Name == "op_Implicit")
                    {
                        Append("Implicit");
                    }
                    else
                    {
                        Debug.Fail("Unexpected conversion name.");
                        Append(symbol.Name);
                    }
                    break;
                case MethodKind.UserDefinedOperator:
                    if (symbol.Name.StartsWith("op_", StringComparison.Ordinal))
                    {
                        Append(symbol.Name.Substring(3));
                    }
                    else
                    {
                        Debug.Fail("Operator should start with 'op_'");
                        Append(symbol.Name);
                    }
                    break;
                default:
                    if (symbol.ExplicitInterfaceImplementations.Length == 0)
                    {
                        Append(symbol.Name);
                    }
                    else
                    {
                        var interfaceMethod = symbol.ExplicitInterfaceImplementations[0];
                        if ((Options & NameOptions.WithType) == NameOptions.None)
                        {
                            interfaceMethod.ContainingType.Accept(this);
                            Append(".");
                        }
                        interfaceMethod.Accept(this);
                        return;
                    }
                    break;
            }
            if (symbol.IsGenericMethod &&
                (Options & NameOptions.WithMethodGenericParameter) == NameOptions.WithMethodGenericParameter)
            {
                Append("<");
                var typeParams = symbol.TypeArguments.Length > 0 ? symbol.TypeArguments.CastArray<ISymbol>() : symbol.TypeParameters.CastArray<ISymbol>();
                var typeParamsNullable = symbol.TypeArguments.Length > 0 ? symbol.TypeArgumentNullableAnnotations : ImmutableArray<NullableAnnotation>.Empty;
                for (int i = 0; i < typeParams.Length; i++)
                {
                    if (i > 0)
                    {
                        Append(", ");
                    }
                    typeParams[i].Accept(this);
                    WriteNullableAnnotation(typeParamsNullable.ElementAtOrDefault(i));
                }
                Append(">");
            }
            if ((Options & NameOptions.WithParameter) == NameOptions.WithParameter)
            {
                Append("(");
                for (int i = 0; i < symbol.Parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        Append(", ");
                    }
                    symbol.Parameters[i].Accept(this);
                    if (symbol.MethodKind == MethodKind.Conversion && !symbol.ReturnsVoid)
                    {
                        Append(" to ");
                        symbol.ReturnType.Accept(this);
                    }
                }
                Append(")");
            }
        }

        public override void VisitProperty(IPropertySymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            if (symbol.ExplicitInterfaceImplementations.Length > 0)
            {
                var interfaceProperty = symbol.ExplicitInterfaceImplementations[0];
                if ((Options & NameOptions.WithType) == NameOptions.None)
                {
                    interfaceProperty.ContainingType.Accept(this);
                    Append(".");
                }
                interfaceProperty.Accept(this);
                return;
            }
            if (symbol.Parameters.Length > 0)
            {
                if ((Options & NameOptions.UseAlias) == NameOptions.UseAlias)
                {
                    Append("this");
                }
                else
                {
                    Append(symbol.MetadataName);
                }
                if ((Options & NameOptions.WithParameter) == NameOptions.WithParameter)
                {
                    Append("[");
                    for (int i = 0; i < symbol.Parameters.Length; i++)
                    {
                        if (i > 0)
                        {
                            Append(", ");
                        }
                        symbol.Parameters[i].Accept(this);
                    }
                    Append("]");
                }
            }
            else
            {
                Append(symbol.Name);
            }
        }

        public override void VisitEvent(IEventSymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            if (symbol.ExplicitInterfaceImplementations.Length > 0)
            {
                var interfaceEvent = symbol.ExplicitInterfaceImplementations[0];
                if ((Options & NameOptions.WithType) == NameOptions.None)
                {
                    interfaceEvent.ContainingType.Accept(this);
                    Append(".");
                }
                interfaceEvent.Accept(this);
            }
            else
            {
                Append(symbol.Name);
            }
        }

        public override void VisitField(IFieldSymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            Append(symbol.Name);
        }

        public override void VisitParameter(IParameterSymbol symbol)
        {
            WriteRefKind(symbol.RefKind, isReturn: false);
            symbol.Type.Accept(this);
            WriteNullableAnnotation(symbol.NullableAnnotation);
        }

        public override void VisitDynamicType(IDynamicTypeSymbol symbol)
        {
            if ((Options & NameOptions.UseAlias) == NameOptions.UseAlias)
            {
                Append("dynamic");
            }
            else if ((Options & NameOptions.WithNamespace) == NameOptions.WithNamespace)
            {
                Append(typeof(object).FullName);
            }
            else
            {
                Append(typeof(object).Name);
            }
        }

        private bool TrySpecialType(INamedTypeSymbol symbol)
        {
            switch (symbol.SpecialType)
            {
                case SpecialType.System_Object:
                    Append("object");
                    return true;
                case SpecialType.System_Void:
                    Append("void");
                    return true;
                case SpecialType.System_Boolean:
                    Append("bool");
                    return true;
                case SpecialType.System_Char:
                    Append("char");
                    return true;
                case SpecialType.System_SByte:
                    Append("sbyte");
                    return true;
                case SpecialType.System_Byte:
                    Append("byte");
                    return true;
                case SpecialType.System_Int16:
                    Append("short");
                    return true;
                case SpecialType.System_UInt16:
                    Append("ushort");
                    return true;
                case SpecialType.System_Int32:
                    Append("int");
                    return true;
                case SpecialType.System_UInt32:
                    Append("uint");
                    return true;
                case SpecialType.System_Int64:
                    Append("long");
                    return true;
                case SpecialType.System_UInt64:
                    Append("ulong");
                    return true;
                case SpecialType.System_Decimal:
                    Append("decimal");
                    return true;
                case SpecialType.System_Single:
                    Append("float");
                    return true;
                case SpecialType.System_Double:
                    Append("double");
                    return true;
                case SpecialType.System_String:
                    Append("string");
                    return true;
                case SpecialType.System_IntPtr:
                    if (symbol.IsNativeIntegerType)
                    {
                        Append("nint");
                        return true;
                    }
                    goto default;
                case SpecialType.System_UIntPtr:
                    if (symbol.IsNativeIntegerType)
                    {
                        Append("nuint");
                        return true;
                    }
                    goto default;
                default:
                    if (symbol.IsGenericType && !symbol.IsDefinition && symbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    {
                        symbol.TypeArguments[0].Accept(this);

                        // we only need to explicitly append ? if we're _not_ including nullable annotations
                        if ((Options & NameOptions.WithNullableAnnotations) == 0)
                        {
                            Append("?");
                        }
                        return true;
                    }
                    return false;
            }
        }

        private void WriteGeneric(int typeParameterCount)
        {
            Append("<");
            for (int i = 1; i < typeParameterCount; i++)
            {
                Append(",");
            }
            Append(">");
        }

        private void WriteGeneric(IReadOnlyList<ITypeSymbol> types, IReadOnlyList<NullableAnnotation> nullableAnnotations)
        {
            Append("<");
            for (int i = 0; i < types.Count; i++)
            {
                if (i > 0)
                {
                    Append(", ");
                }
                types[i].Accept(this);
                WriteNullableAnnotation(nullableAnnotations.ElementAtOrDefault(i));
            }
            Append(">");
        }

        private void WriteNullableAnnotation(NullableAnnotation nullableAnnotation)
        {
            if ((Options & NameOptions.WithNullableAnnotations) == 0)
            {
                return;
            }

            if (nullableAnnotation == NullableAnnotation.Annotated)
            {
                Append("?");
            }
        }

        private void WriteRefKind(RefKind kind, bool isReturn)
        {
            switch (kind)
            {
                case RefKind.None:
                {
                    break;
                }

                case RefKind.Ref:
                {
                    Append("ref ");
                    break;
                }

                case RefKind.Out:
                {
                    Append("out ");
                    break;
                }

                case RefKind.In:
                {
                    if (isReturn)
                    {
                        Append("ref readonly ");
                    }
                    else
                    {
                        Append("in ");
                    }
                    break;
                }
            }
        }

        private void WriteUnmanagedCallConv(SignatureCallingConvention callConv)
        {
            switch (callConv)
            {
                case SignatureCallingConvention.Default:
                case SignatureCallingConvention.Unmanaged:
                {
                    // nothing to print here
                    break;
                }

                case SignatureCallingConvention.CDecl:
                {
                    Append("Cdecl");
                    break;
                }

                case SignatureCallingConvention.StdCall:
                {
                    Append("Stdcall");
                    break;
                }

                case SignatureCallingConvention.ThisCall:
                {
                    Append("Thiscall");
                    break;
                }

                case SignatureCallingConvention.FastCall:
                {
                    Append("Fastcall");
                    break;
                }

                default:
                {
                    var name = callConv.ToString();
                    Append(name.Substring(0, 1));
                    Append(name.Substring(1).ToLower());
                    break;
                }
            }
        }

        private void WriteUnmanagedCallConv(INamedTypeSymbol symbol)
        {
            var name = symbol.Name;

            if (name.StartsWith("CallConv"))
            {
                Append(name.Substring("CallConv".Length));
            }
            else
            {
                // We should never encounter this type of call
                // conv but we'll handle it gracefully regardless
                symbol.Accept(this);
            }
        }
        
    }

    public class VBNameVisitorCreator : NameVisitorCreator
    {
        private readonly NameOptions _options;

        public VBNameVisitorCreator(NameOptions options)
        {
            _options = options;
        }

        protected override NameVisitor Create()
        {
            return new VBNameVisitor(_options);
        }
    }

    internal sealed class VBNameVisitor : NameVisitor
    {
        private readonly NameOptions Options;

        public VBNameVisitor(NameOptions options)
        {
            Options = options;
        }

        public override void VisitNamedType(INamedTypeSymbol symbol)
        {
            if ((Options & NameOptions.UseAlias) == NameOptions.UseAlias &&
                TrySpecialType(symbol))
            {
                return;
            }
            if (symbol.ContainingType != null)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            else if ((Options & NameOptions.WithNamespace) == NameOptions.WithNamespace)
            {
                if (!VisitorHelper.InGlobalNamespace(symbol))
                {
                    symbol.ContainingNamespace.Accept(this);
                    Append(".");
                }
            }
            if (symbol.IsTupleType)
            {
                if ((Options & NameOptions.Qualified) == NameOptions.Qualified)
                {
                    Append("ValueTuple");
                    symbol = symbol.TupleUnderlyingType ?? symbol;
                }
                else
                {
                    Append("(");
                    for (var i = 0; i < symbol.TupleElements.Length; i++)
                    {
                        if (i > 0)
                        {
                            Append(", ");
                        }
                        var tupleElement = symbol.TupleElements[i];
                        if (!tupleElement.IsImplicitlyDeclared)
                        {
                            Append(tupleElement.Name);
                            Append(" As ");
                        }
                        tupleElement.Type.Accept(this);
                    }
                    Append(")");
                }
            }
            else
            {
                Append(symbol.Name);
            }

            if ((Options & NameOptions.WithTypeGenericParameter) == NameOptions.WithTypeGenericParameter &&
                symbol.TypeParameters.Length > 0)
            {
                if (symbol.TypeArguments != null && symbol.TypeArguments.Length > 0)
                {
                    if (symbol.IsUnboundGenericType)
                    {
                        WriteGeneric(symbol.TypeArguments.Length);
                    }
                    else
                    {
                        WriteGeneric(symbol.TypeArguments);
                    }
                }
                else
                {
                    WriteGeneric(symbol.TypeParameters);
                }
            }
        }

        public override void VisitNamespace(INamespaceSymbol symbol)
        {
            if (symbol.IsGlobalNamespace)
            {
                return;
            }
            if (!symbol.ContainingNamespace.IsGlobalNamespace)
            {
                symbol.ContainingNamespace.Accept(this);
                Append(".");
            }
            Append(symbol.Name);
        }

        public override void VisitArrayType(IArrayTypeSymbol symbol)
        {
            symbol.ElementType.Accept(this);
            if (symbol.Rank == 1)
            {
                Append("()");
            }
            else
            {
                Append("(");
                for (int i = 1; i < symbol.Rank; i++)
                {
                    Append(",");
                }
                Append(")");
            }
        }

        public override void VisitPointerType(IPointerTypeSymbol symbol)
        {
            symbol.PointedAtType.Accept(this);
            Append("*");
        }

        public override void VisitTypeParameter(ITypeParameterSymbol symbol)
        {
            Append(symbol.Name);
        }

        public override void VisitMethod(IMethodSymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            switch (symbol.MethodKind)
            {
                case MethodKind.Constructor:
                    Append(symbol.ContainingType.Name);
                    break;
                case MethodKind.Conversion:
                    if (symbol.Name == "op_Explicit")
                    {
                        Append("Narrowing");
                    }
                    else if (symbol.Name == "op_Implicit")
                    {
                        Append("Widening");
                    }
                    else
                    {
                        Debug.Fail("Unexpected conversion name.");
                        Append(symbol.Name);
                    }
                    break;
                case MethodKind.UserDefinedOperator:
                    if (symbol.Name.StartsWith("op_"))
                    {
                        Append(symbol.Name.Substring(3));
                    }
                    else
                    {
                        Debug.Fail("Operator should start with 'op_'");
                        Append(symbol.Name);
                    }
                    break;
                default:
                    Append(symbol.Name);
                    break;
            }
            if (symbol.IsGenericMethod &&
                (Options & NameOptions.WithMethodGenericParameter) == NameOptions.WithMethodGenericParameter)
            {
                Append("(Of ");
                var typeParams = symbol.TypeArguments.Length > 0 ? symbol.TypeArguments.CastArray<ISymbol>() : symbol.TypeParameters.CastArray<ISymbol>();
                for (int i = 0; i < typeParams.Length; i++)
                {
                    if (i > 0)
                    {
                        Append(", ");
                    }
                    typeParams[i].Accept(this);
                }
                Append(")");
            }
            if ((Options & NameOptions.WithParameter) == NameOptions.WithParameter)
            {
                Append("(");
                for (int i = 0; i < symbol.Parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        Append(", ");
                    }
                    symbol.Parameters[i].Accept(this);
                    if (symbol.MethodKind == MethodKind.Conversion && !symbol.ReturnsVoid)
                    {
                        Append(" to ");
                        symbol.ReturnType.Accept(this);
                    }
                }
                Append(")");
            }
        }

        public override void VisitProperty(IPropertySymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            Append(symbol.MetadataName);
            if ((Options & NameOptions.WithParameter) == NameOptions.WithParameter)
            {
                if (symbol.Parameters.Length > 0)
                {
                    Append("(");
                    for (int i = 0; i < symbol.Parameters.Length; i++)
                    {
                        if (i > 0)
                        {
                            Append(", ");
                        }
                        symbol.Parameters[i].Accept(this);
                    }
                    Append(")");
                }
            }
        }

        public override void VisitEvent(IEventSymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            Append(symbol.Name);
        }

        public override void VisitField(IFieldSymbol symbol)
        {
            if ((Options & NameOptions.WithType) == NameOptions.WithType)
            {
                symbol.ContainingType.Accept(this);
                Append(".");
            }
            Append(symbol.Name);
        }

        public override void VisitParameter(IParameterSymbol symbol)
        {
            if (symbol.RefKind != RefKind.None)
            {
                Append("ByRef ");
            }
            symbol.Type.Accept(this);
        }

        public override void VisitDynamicType(IDynamicTypeSymbol symbol)
        {
            if ((Options & NameOptions.WithNamespace) == NameOptions.WithNamespace)
            {
                Append(typeof(object).FullName);
            }
            else
            {
                Append(typeof(object).Name);
            }
        }

        private bool TrySpecialType(INamedTypeSymbol symbol)
        {
            switch (symbol.SpecialType)
            {
                case SpecialType.System_Object:
                    Append("Object");
                    return true;
                case SpecialType.System_Void:
                    return true;
                case SpecialType.System_Boolean:
                    Append("Boolean");
                    return true;
                case SpecialType.System_Char:
                    Append("Char");
                    return true;
                case SpecialType.System_SByte:
                    Append("SByte");
                    return true;
                case SpecialType.System_Byte:
                    Append("Byte");
                    return true;
                case SpecialType.System_Int16:
                    Append("Short");
                    return true;
                case SpecialType.System_UInt16:
                    Append("UShort");
                    return true;
                case SpecialType.System_Int32:
                    Append("Integer");
                    return true;
                case SpecialType.System_UInt32:
                    Append("UInteger");
                    return true;
                case SpecialType.System_Int64:
                    Append("Long");
                    return true;
                case SpecialType.System_UInt64:
                    Append("ULong");
                    return true;
                case SpecialType.System_DateTime:
                    Append("Date");
                    return true;
                case SpecialType.System_Decimal:
                    Append("Decimal");
                    return true;
                case SpecialType.System_Single:
                    Append("Single");
                    return true;
                case SpecialType.System_Double:
                    Append("Double");
                    return true;
                case SpecialType.System_String:
                    Append("String");
                    return true;
                default:
                    if (symbol.IsGenericType && !symbol.IsDefinition && symbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    {
                        symbol.TypeArguments[0].Accept(this);
                        Append("?");
                        return true;
                    }
                    return false;
            }
        }

        private void WriteGeneric(int typeParameterCount)
        {
            Append("(Of ");
            for (int i = 1; i < typeParameterCount; i++)
            {
                Append(",");
            }
            Append(")");
        }

        private void WriteGeneric(IReadOnlyList<ITypeSymbol> types)
        {
            Append("(Of ");
            for (int i = 0; i < types.Count; i++)
            {
                if (i > 0)
                {
                    Append(", ");
                }
                types[i].Accept(this);
            }
            Append(")");
        }
    }
}
