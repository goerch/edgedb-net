using EdgeDB.Binary;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EdgeDB.Binary.Codecs
{
    internal sealed class ObjectCodec
        : BaseArgumentCodec<object>, IMultiWrappingCodec, ICacheableCodec
    {
        public ICodec[] InnerCodecs;
        public readonly string[] PropertyNames;

        public EdgeDBTypeDeserializeInfo? DeserializerInfo;
        internal Type? TargetType;
        internal bool Initialized;

        private TypeDeserializerFactory? _factory;
        private readonly ILogger _logger;

        internal ObjectCodec(ILogger logger, ObjectShapeDescriptor descriptor, List<ICodec> codecs)
        {
            _logger = logger;
            InnerCodecs = new ICodec[descriptor.Shapes.Length];
            PropertyNames = new string[descriptor.Shapes.Length];

            for(int i = 0; i != descriptor.Shapes.Length; i++)
            {
                var shape = descriptor.Shapes[i];
                InnerCodecs[i] = codecs[shape.TypePos];
                PropertyNames[i] = shape.Name;
            }
        }

        internal ObjectCodec(ILogger logger, NamedTupleTypeDescriptor descriptor, List<ICodec> codecs)
        {
            _logger = logger;
            InnerCodecs = new ICodec[descriptor.Elements.Length];
            PropertyNames = new string[descriptor.Elements.Length];

            for (int i = 0; i != descriptor.Elements.Length; i++)
            {
                var shape = descriptor.Elements[i];
                InnerCodecs[i] = codecs[shape.TypePos];
                PropertyNames[i] = shape.Name;
            }
        }

        internal ObjectCodec(ILogger logger, ICodec[] innerCodecs, string[] propertyNames)
        {
            _logger = logger;
            InnerCodecs = innerCodecs;
            PropertyNames = propertyNames;
        }

        public void Initialize(Type target)
        {
            if (Initialized && target == TargetType)
                return;

            TargetType = target;

            try
            {
                _factory = TypeBuilder.GetDeserializationFactory(target);
                DeserializerInfo = TypeBuilder.TypeInfo[target];
                Initialized = true;
            }
            catch (Exception) when (TargetType == typeof(object))
            {
                _factory = (ref ObjectEnumerator enumerator) => enumerator.ToDynamic();
            }
        }

        public override object? Deserialize(ref PacketReader reader)
        {
            if (!Initialized || _factory is null || TargetType is null)
                Initialize(typeof(object));
            
            // reader is being copied if we just pass it as 'ref reader' to our object enumerator,
            // so we need to pass the underlying data as a reference and wrap a new reader ontop.
            // This method ensures we're not copying the packet in memory again but the downside is
            // our 'reader' variable isn't kept up to data with the reader in the object enumerator.
            var enumerator = new ObjectEnumerator(ref reader.Data, reader.Position, PropertyNames, InnerCodecs, DeserializerInfo);
            
            try
            {
                return _factory!(ref enumerator);
            }
            catch(Exception x)
            {
                throw new EdgeDBException($"Failed to deserialize object to {TargetType}", x);
            }
            finally
            {
                // set the readers position to the enumerators' readers position.
                reader.Position = enumerator.Reader.Position;
            }
        }

        public override void SerializeArguments(ref PacketWriter writer, object? value)
        {
            object?[]? values = null;

            if (value is IDictionary<string, object?> dict)
                values = PropertyNames.Select(x => dict[x]).ToArray();
            else if (value is object?[] arr)
                value = arr;

            if (values is null)
            {
                throw new ArgumentException($"Expected dynamic object or array but got {value?.GetType()?.Name ?? "null"}");
            }

            writer.Write(values.Length);

            var visitor = new TypeVisitor(_logger);

            for (int i = 0; i != values.Length; i++)
            {
                var element = values[i];

                // reserved
                writer.Write(0);

                // encode
                if (element is null)
                {
                    writer.Write(-1);
                }
                else
                { 
                    var innerCodec = InnerCodecs[i];

                    // special case for enums
                    if (element.GetType().IsEnum && innerCodec is TextCodec)
                        element = element.ToString();
                    else
                    {
                        visitor.SetTargetType(element.GetType());
                        visitor.Visit(ref innerCodec);
                        visitor.Reset();
                    }
                        

                    writer.WriteToWithInt32Length((ref PacketWriter innerWriter) => innerCodec.Serialize(ref innerWriter, element));
                }
            }
        }

        internal void UpdateFactory(TypeDeserializerFactory factory)
        {
            _factory = factory;
        }

        public override void Serialize(ref PacketWriter writer, object? value) => throw new NotSupportedException();

        public override string ToString()
        {
            return $"ObjectCodec<{string.Join(", ", InnerCodecs.Zip(PropertyNames).Select(x => $"[{x.Second}: {x.First}]"))}>";
        }

        ICodec[] IMultiWrappingCodec.InnerCodecs
        {
            get => InnerCodecs;
            set
            {
                InnerCodecs = value;
            }
        }
    }
}
