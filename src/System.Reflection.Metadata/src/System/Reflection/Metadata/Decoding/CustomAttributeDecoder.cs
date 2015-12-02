﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics;

namespace System.Reflection.Metadata.Decoding
{
    /// <summary>
    /// Decodes custom attribute blobs.
    /// </summary>
    internal struct CustomAttributeDecoder<TType>
    {
        private ICustomAttributeTypeProvider<TType> _provider;
        private MetadataReader _reader;

        public CustomAttributeDecoder(ICustomAttributeTypeProvider<TType> provider, MetadataReader reader)
        {
            _reader = reader;
            _provider = provider;
        }

        public CustomAttributeValue<TType> DecodeValue(EntityHandle constructor, BlobHandle value)
        {
            BlobHandle signature;
            switch (constructor.Kind)
            {
                case HandleKind.MethodDefinition:
                    MethodDefinition definition = _reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
                    signature = definition.Signature;
                    break;

                case HandleKind.MemberReference:
                    MemberReference reference = _reader.GetMemberReference((MemberReferenceHandle)constructor);
                    signature = reference.Signature;
                    break;

                default:
                    throw new BadImageFormatException();
            }

            BlobReader signatureReader = _reader.GetBlobReader(signature);
            BlobReader valueReader = _reader.GetBlobReader(value);

            ushort prolog = valueReader.ReadUInt16();
            if (prolog != 1)
            {
                throw new BadImageFormatException();
            }

            SignatureHeader header = signatureReader.ReadSignatureHeader();
            if (header.Kind != SignatureKind.Method || header.IsGeneric)
            {
                throw new BadImageFormatException();
            }

            int parameterCount = signatureReader.ReadCompressedInteger();
            SignatureTypeCode returnType = signatureReader.ReadSignatureTypeCode();
            if (returnType != SignatureTypeCode.Void)
            {
                throw new BadImageFormatException();
            }

            ImmutableArray<CustomAttributeTypedArgument<TType>> fixedArguments = DecodeFixedArguments(ref signatureReader, ref valueReader, parameterCount);
            ImmutableArray<CustomAttributeNamedArgument<TType>> namedArguments = DecodeNamedArguments(ref valueReader);
            return new CustomAttributeValue<TType>(fixedArguments, namedArguments);
        }

        private ImmutableArray<CustomAttributeTypedArgument<TType>> DecodeFixedArguments(ref BlobReader signatureReader, ref BlobReader valueReader, int count)
        {
            if (count == 0)
            {
                return ImmutableArray<CustomAttributeTypedArgument<TType>>.Empty;
            }

            var arguments = new CustomAttributeTypedArgument<TType>[count];

            for (int i = 0; i < count; i++)
            {
                ArgumentTypeInfo info = DecodeFixedArgumentType(ref signatureReader);
                arguments[i] = DecodeArgument(ref valueReader, info);
            }

            return ImmutableArray.Create(arguments);
        }

        private ImmutableArray<CustomAttributeNamedArgument<TType>> DecodeNamedArguments(ref BlobReader valueReader)
        {
            int count = valueReader.ReadUInt16();
            if (count == 0)
            {
                return ImmutableArray<CustomAttributeNamedArgument<TType>>.Empty;
            }

            var arguments = new CustomAttributeNamedArgument<TType>[count];
            for (int i = 0; i < count; i++)
            {
                CustomAttributeNamedArgumentKind kind = (CustomAttributeNamedArgumentKind)valueReader.ReadSerializationTypeCode();
                if (kind != CustomAttributeNamedArgumentKind.Field && kind != CustomAttributeNamedArgumentKind.Property)
                {
                    throw new BadImageFormatException();
                }

                ArgumentTypeInfo info = DecodeNamedArgumentType(ref valueReader);
                string name = valueReader.ReadSerializedString();
                CustomAttributeTypedArgument<TType> argument = DecodeArgument(ref valueReader, info);
                arguments[i] = new CustomAttributeNamedArgument<TType>(name, kind, argument.Type, argument.Value);
            }

            return ImmutableArray.Create(arguments);
        }

        private struct ArgumentTypeInfo
        {
            public TType Type;
            public TType ElementType;
            public SerializationTypeCode TypeCode;
            public SerializationTypeCode ElementTypeCode;
        }

        // Decodes a fixed argument type of a custom attribute from its constructor signature.
        //
        // Note that we do not decode the full constructor signature using DecodeMethodSignature
        // but instead decode one parameter at a time as we read the value blob. This is both
        // better perf-wise, but even more important is that we can't actually reason about
        // a method signature with opaque TType values without adding some unecessary chatter
        // with the provider.
        private ArgumentTypeInfo DecodeFixedArgumentType(ref BlobReader signatureReader, bool isElementType = false)
        {
            SignatureTypeCode signatureTypeCode = signatureReader.ReadSignatureTypeCode();

            var info = new ArgumentTypeInfo
            {
                TypeCode = (SerializationTypeCode)signatureTypeCode,
            };

            switch (signatureTypeCode)
            {
                case SignatureTypeCode.Boolean:
                case SignatureTypeCode.Byte:
                case SignatureTypeCode.Char:
                case SignatureTypeCode.Double:
                case SignatureTypeCode.Int16:
                case SignatureTypeCode.Int32:
                case SignatureTypeCode.Int64:
                case SignatureTypeCode.SByte:
                case SignatureTypeCode.Single:
                case SignatureTypeCode.String:
                case SignatureTypeCode.UInt16:
                case SignatureTypeCode.UInt32:
                case SignatureTypeCode.UInt64:
                    info.Type = _provider.GetPrimitiveType((PrimitiveTypeCode)signatureTypeCode);
                    break;

                case SignatureTypeCode.Object:
                    info.TypeCode = SerializationTypeCode.TaggedObject;
                    info.Type = _provider.GetPrimitiveType(PrimitiveTypeCode.Object);
                    break;

                case SignatureTypeCode.TypeHandle:
                    // Parameter is type def or ref and is only allowed to be System.Type or Enum.
                    EntityHandle handle = signatureReader.ReadTypeHandle();
                    info.Type = GetTypeFromHandle(handle);
                    info.TypeCode = _provider.IsSystemType(info.Type) ? SerializationTypeCode.Type : (SerializationTypeCode)_provider.GetUnderlyingEnumType(info.Type);
                    break;

                case SignatureTypeCode.SZArray:
                    if (isElementType)
                    {
                        // jagged arrays are not allowed.
                        throw new BadImageFormatException();
                    }

                    var elementInfo = DecodeFixedArgumentType(ref signatureReader, isElementType: true);
                    info.ElementType = elementInfo.Type;
                    info.ElementTypeCode = elementInfo.TypeCode;
                    info.Type = _provider.GetSZArrayType(info.ElementType);
                    break;

                default:
                    throw new BadImageFormatException();
            }

            return info;
        }

        private ArgumentTypeInfo DecodeNamedArgumentType(ref BlobReader valueReader, bool isElementType = false)
        {
            var info = new ArgumentTypeInfo
            {
                TypeCode = valueReader.ReadSerializationTypeCode(),
            };

            switch (info.TypeCode)
            {
                case SerializationTypeCode.Boolean:
                case SerializationTypeCode.Byte:
                case SerializationTypeCode.Char:
                case SerializationTypeCode.Double:
                case SerializationTypeCode.Int16:
                case SerializationTypeCode.Int32:
                case SerializationTypeCode.Int64:
                case SerializationTypeCode.SByte:
                case SerializationTypeCode.Single:
                case SerializationTypeCode.String:
                case SerializationTypeCode.UInt16:
                case SerializationTypeCode.UInt32:
                case SerializationTypeCode.UInt64:
                    info.Type = _provider.GetPrimitiveType((PrimitiveTypeCode)info.TypeCode);
                    break;

                case SerializationTypeCode.Type:
                    info.Type = _provider.GetSystemType();
                    break;

                case SerializationTypeCode.TaggedObject:
                    info.Type = _provider.GetPrimitiveType(PrimitiveTypeCode.Object);
                    break;

                case SerializationTypeCode.SZArray:
                    if (isElementType)
                    {
                        // jagged arrays are not allowed.
                        throw new BadImageFormatException();
                    }

                    var elementInfo = DecodeNamedArgumentType(ref valueReader, isElementType: true);
                    info.ElementType = elementInfo.Type;
                    info.ElementTypeCode = elementInfo.TypeCode;
                    info.Type = _provider.GetSZArrayType(info.ElementType);
                    break;

                case SerializationTypeCode.Enum:
                    string typeName = valueReader.ReadSerializedString();
                    info.Type = _provider.GetTypeFromSerializedName(typeName);
                    info.TypeCode = (SerializationTypeCode)_provider.GetUnderlyingEnumType(info.Type);
                    break;

                default:
                    throw new BadImageFormatException();
            }

            return info;
        }

        private CustomAttributeTypedArgument<TType> DecodeArgument(ref BlobReader valueReader, ArgumentTypeInfo info)
        {
            if (info.TypeCode == SerializationTypeCode.TaggedObject)
            {
                info = DecodeNamedArgumentType(ref valueReader);
            }

            // PERF_TODO: Cache/reuse common arguments to avoid boxing (small integers, true, false).
            object value;
            switch (info.TypeCode)
            {
                case SerializationTypeCode.Boolean:
                    value = valueReader.ReadBoolean();
                    break;

                case SerializationTypeCode.Byte:
                    value = valueReader.ReadByte();
                    break;

                case SerializationTypeCode.Char:
                    value = valueReader.ReadChar();
                    break;

                case SerializationTypeCode.Double:
                    value = valueReader.ReadDouble();
                    break;

                case SerializationTypeCode.Int16:
                    value = valueReader.ReadInt16();
                    break;

                case SerializationTypeCode.Int32:
                    value = valueReader.ReadInt32();
                    break;

                case SerializationTypeCode.Int64:
                    value = valueReader.ReadInt64();
                    break;

                case SerializationTypeCode.SByte:
                    value = valueReader.ReadSByte();
                    break;

                case SerializationTypeCode.Single:
                    value = valueReader.ReadSingle();
                    break;

                case SerializationTypeCode.UInt16:
                    value = valueReader.ReadUInt16();
                    break;

                case SerializationTypeCode.UInt32:
                    value = valueReader.ReadUInt32();
                    break;

                case SerializationTypeCode.UInt64:
                    value = valueReader.ReadUInt64();
                    break;

                case SerializationTypeCode.String:
                    value = valueReader.ReadSerializedString();
                    break;

                case SerializationTypeCode.Type:
                    string typeName = valueReader.ReadSerializedString();
                    value = _provider.GetTypeFromSerializedName(typeName);
                    break;

                case SerializationTypeCode.SZArray:
                    value = DecodeArrayArgument(ref valueReader, info);
                    break;

                default:
                    throw new BadImageFormatException();
            }

            return new CustomAttributeTypedArgument<TType>(info.Type, value);
        }

        private ImmutableArray<CustomAttributeTypedArgument<TType>>? DecodeArrayArgument(ref BlobReader blobReader, ArgumentTypeInfo info)
        {
            int count = blobReader.ReadInt32();
            if (count == -1)
            {
                return null;
            }

            if (count == 0)
            {
                return ImmutableArray<CustomAttributeTypedArgument<TType>>.Empty;
            }

            if (count < 0)
            {
                throw new BadImageFormatException();
            }

            var elementInfo = new ArgumentTypeInfo
            {
                Type = info.ElementType,
                TypeCode = info.ElementTypeCode,
            };

            var array = new CustomAttributeTypedArgument<TType>[count];

            for (int i = 0; i < count; i++)
            {
                array[i] = DecodeArgument(ref blobReader, elementInfo);
            }

            return ImmutableArray.Create(array);
        }

        private TType GetTypeFromHandle(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return _provider.GetTypeFromDefinition(_reader, (TypeDefinitionHandle)handle, SignatureTypeHandleCode.Unresolved);

                case HandleKind.TypeReference:
                    return _provider.GetTypeFromReference(_reader, (TypeReferenceHandle)handle, SignatureTypeHandleCode.Unresolved);

                default:
                    throw new BadImageFormatException(SR.NotTypeDefOrRefHandle);
            }
        }
    }
}
