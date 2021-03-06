#region COPYRIGHT

/*
 *     Copyright 2009-2012 Yuri Astrakhan  (<Firstname><Lastname>@gmail.com)
 *
 *     This file is part of TimeSeriesDb library
 * 
 *  TimeSeriesDb is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 * 
 *  TimeSeriesDb is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 * 
 *  You should have received a copy of the GNU General Public License
 *  along with TimeSeriesDb.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

#endregion

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;

namespace NYurik.TimeSeriesDb.Serializers.BlockSerializer
{
    public interface IStateStore
    {
        /// <summary>
        /// Get a state variable expression of a given type by its name.
        /// In case the variable has not been created before, wasCreated will be set to true,
        /// in which case the caller must generate code to initialize it first.
        /// </summary>
        ParameterExpression GetOrCreateStateVar(string name, Type valueType, out bool wasCreated);

        /// <summary>
        /// Create a default Field for the given type. The resulting field might be incomplete,
        /// and will throw exception on initialization unless configured properly.
        /// In case of the complex type, a <see cref="ComplexField"/> is created recursivelly.
        /// </summary>
        /// <param name="valueType">Type of the variable</param>
        /// <param name="name">State name</param>
        /// <param name="allowCustom">If true, do not call custom field factory.
        /// This parameter is needed to avoid infinite recursion by custom field fatories.
        /// If <see cref="DynamicSerializer{T}"/> is created with a non-null factory, and that factory needs
        /// to create a default field before customizing it, set this parameter to false.</param>
        BaseField CreateField([NotNull] Type valueType, [NotNull] string name, bool allowCustom);
    }

    internal abstract class DynamicSerializer : Initializable, IStateStore
    {
        protected static readonly ConcurrentDictionary<DynamicSerializer, Lazy<Tuple<Delegate, Delegate>>>
            SerializerCache = new ConcurrentDictionary<DynamicSerializer, Lazy<Tuple<Delegate, Delegate>>>();

        protected readonly Dictionary<string, ParameterExpression> StateVariables =
            new Dictionary<string, ParameterExpression>();

        private readonly Func<IStateStore, Type, string, BaseField> _fieldFactory;

        private BaseField _rootField;

        protected DynamicSerializer(Func<IStateStore, Type, string, BaseField> fieldFactory)
        {
            _fieldFactory = fieldFactory;
        }

        public BaseField RootField
        {
            get { return _rootField; }
            set
            {
                ThrowOnInitialized();
                if (value == null) throw new ArgumentNullException("value");
                _rootField = value;
            }
        }

        #region IStateStore Members

        ParameterExpression IStateStore.GetOrCreateStateVar(string name, Type valueType, out bool wasCreated)
        {
            ThrowOnInitialized();
            ParameterExpression stateVar;
            if (StateVariables.TryGetValue(name, out stateVar))
            {
                if (stateVar.Type != valueType)
                    throw new SerializerException(
                        "State '{0}' was requested as type {1}, but was previously created as type {2}",
                        name, valueType, stateVar.Type);
                wasCreated = false;
                return stateVar;
            }

            stateVar = Expression.Variable(valueType, name);
            StateVariables.Add(name, stateVar);
            wasCreated = true;
            return stateVar;
        }

        public BaseField CreateField(Type valueType, string name, bool allowCustom)
        {
            if (valueType == null)
                throw new ArgumentNullException("valueType");
            if (name == null)
                throw new ArgumentNullException("name");

            if (allowCustom && _fieldFactory != null)
            {
                BaseField fld = _fieldFactory(this, valueType, name);
                if (fld != null)
                    return fld;
            }

            if (valueType.IsArray)
                throw new SerializerException("Arrays are not supported ({0})", valueType);

            if (valueType.IsPrimitive)
            {
                switch (Type.GetTypeCode(valueType))
                {
                    case TypeCode.SByte:
                    case TypeCode.Byte:
                    case TypeCode.Boolean:
                        return new SimpleField(this, valueType, name);

                    case TypeCode.Char:
                    case TypeCode.Int16:
                    case TypeCode.UInt16:
                    case TypeCode.Int32:
                    case TypeCode.UInt32:
                    case TypeCode.Int64:
                    case TypeCode.UInt64:
                        return new ScaledDeltaIntField(this, valueType, name);

                    case TypeCode.Single:
                    case TypeCode.Double:
                        return new ScaledDeltaFloatField(this, valueType, name);

                    default:
                        throw new SerializerException("Unsupported primitive type " + valueType);
                }
            }

            var srlzr = valueType.GetSingleAttribute<FieldAttribute>();
            if (srlzr != null)
            {
                try
                {
                    return CreateFieldWithCtor(srlzr.Serializer, valueType, name);
                }
                catch (Exception ex)
                {
                    throw new SerializerException(
                        ex, "Custom field serializer {0} attached to type {1} failed.", srlzr.Serializer.ToDebugStr(),
                        valueType.ToDebugStr());
                }
            }

            return new ComplexField(this, valueType, name);
        }

        /// <summary>
        /// Create field using common 3 parameter constructor (IStateStore, Type, string)
        /// </summary>
        internal BaseField CreateFieldWithCtor(Type fieldType, Type valueType, string name)
        {
            return (BaseField) Activator.CreateInstance(fieldType, this, valueType, name);
        }

        #endregion

        public virtual void MakeReadonly()
        {
            IsInitialized = true;
        }
    }

    internal sealed class DynamicSerializer<T> : DynamicSerializer
    {
        private Action<CodecReader, Buffer<T>, int> _deSerialize;
        private Func<CodecWriter, IEnumerator<T>, bool> _serialize;

        private DynamicSerializer() : base(null)
        {
        }

        public DynamicSerializer(Func<IStateStore, Type, string, BaseField> fieldFactory)
            : base(fieldFactory)
        {
            RootField = CreateField(typeof (T), "root", true);
        }

        /// <summary>
        ///  * codec - writes output to this codec using Write*() methods
        /// * enumerator to go through the input T values.
        ///     MoveNext() had to be called on it and returned true before passing it in.
        /// * returns false if no more items, or true if there are more items but the codec buffer is full
        /// </summary>
        public Func<CodecWriter, IEnumerator<T>, bool> Serialize
        {
            get
            {
                ThrowOnNotInitialized();
                return _serialize;
            }
        }

        /// <summary>
        /// * codec to read the values from
        /// * result will get all the generated values
        /// * maxItemCount - maximum number of items to be deserialized
        /// </summary>
        public Action<CodecReader, Buffer<T>, int> DeSerialize
        {
            get
            {
                ThrowOnNotInitialized();
                return _deSerialize;
            }
        }

        public override void MakeReadonly()
        {
            if (IsInitialized)
                return;

            var srls = SerializerCache.GetOrAdd(
                this,
                root =>
                new Lazy<Tuple<Delegate, Delegate>>(
                    () =>
                        {
                            try
                            {
                                StateVariables.Clear();

                                ParameterExpression[] parameters;
                                ParameterExpression[] localVars;
                                IEnumerable<Expression> methodBody = GenerateSerializer(out parameters, out localVars);

                                Expression<Func<CodecWriter, IEnumerator<T>, bool>> serializeExp =
                                    Expression.Lambda<Func<CodecWriter, IEnumerator<T>, bool>>(
                                        Expression.Block(
                                            StateVariables.Values.Concat(localVars),
                                            methodBody),
                                        "Serialize", parameters);

                                StateVariables.Clear();
                                methodBody = GenerateDeSerializer(out parameters, out localVars);

                                Expression<Action<CodecReader, Buffer<T>, int>> deSerializeExp =
                                    Expression.Lambda<Action<CodecReader, Buffer<T>, int>>(
                                        Expression.Block(StateVariables.Values.Concat(localVars), methodBody),
                                        "DeSerialize", parameters);

                                return new Tuple<Delegate, Delegate>(serializeExp.Compile(), deSerializeExp.Compile());
                            }
                            finally
                            {
                                StateVariables.Clear();
                            }
                        }));

            _serialize = (Func<CodecWriter, IEnumerator<T>, bool>) srls.Value.Item1;
            _deSerialize = (Action<CodecReader, Buffer<T>, int>) srls.Value.Item2;

            base.MakeReadonly();
        }

        /// <summary>
        /// Generate bool Serialize(CodecWriter codec, IEnumerator&lt;T> enumerator) method
        /// </summary>
        /// <remarks>
        /// Generated code:
        /// 
        /// 
        /// * codec - writes output to this codec using Write*() methods
        /// * enumerator to go through the input T values.
        ///     MoveNext() had to be called on it before passing it in.
        /// * returns false if no more items, or true if there are more items but the codec buffer is full
        /// 
        /// <![CDATA[
        /// bool Serialize(CodecWriter codec, IEnumerator<T> enumerator)
        /// {
        ///     bool moveNext;
        ///     int count = 1;
        ///     int codecPos;
        ///     T current = enumerator.Current;
        ///     
        ///     var state1, state2, ...;
        /// 
        ///     codec.Write(state1);
        ///     codec.Write(state2);
        ///     ...
        /// 
        ///     while (true) {
        /// 
        ///         moveNext = enumerator.MoveNext();
        ///         if (!moveNext)
        ///             break;
        /// 
        ///         codecPos = codec.Count;
        ///         if (! (codec.Write(delta1) && codec.Write(delta2) && ...) ) {
        ///             codec.Count = codecPos;
        ///             break;
        ///         }
        /// 
        ///         count++;
        ///     }
        /// 
        ///     codec.FinishBlock(count, moveNext);
        /// 
        ///     return moveNext;
        /// }]]>
        /// </remarks>
        private IEnumerable<Expression> GenerateSerializer(
            out ParameterExpression[] parameters,
            out ParameterExpression[] localVars)
        {
            // param: codec
            ParameterExpression codecParam = Expression.Parameter(typeof (CodecWriter), "codec");

            // param: IEnumerator<T> data
            ParameterExpression enumeratorParam = Expression.Parameter(typeof (IEnumerator<T>), "enumerator");

            // bool moveNext;
            ParameterExpression moveNextVar = Expression.Variable(typeof (bool), "moveNext");

            // int count;
            ParameterExpression countVar = Expression.Variable(typeof (int), "count");

            // T current;
            ParameterExpression currentVar = Expression.Variable(typeof (T), "current");

            // current = enumerator.Current;
            BinaryExpression setCurrentExp = Expression.Assign(
                currentVar, Expression.PropertyOrField(enumeratorParam, "Current"));

            LabelTarget breakLabel = Expression.Label("loopBreak");


            Tuple<Expression, Expression> srl = RootField.GetSerializer(currentVar, codecParam);

            // int codecPos;
            ParameterExpression codecPosExp = Expression.Parameter(typeof (int), "codecPos");

            // codec.Count
            MemberExpression codecBufferPos = Expression.PropertyOrField(codecParam, "Count");

            // parameter codec, IEnumerator<T>
            parameters = new[] {codecParam, enumeratorParam};

            // local variables used by the method body (excluding the state variables)
            localVars = new[] {currentVar, countVar, moveNextVar};

            return
                new[]
                    {
                        // count = 1;
                        Expression.Assign(countVar, Expression.Constant(1)),
                        // current = enumerator.Current;
                        setCurrentExp,
                        // var state1 = current.Field1; codec.Write(state1); ...
                        srl.Item1,
                        // while (true)
                        Expression.Loop(
                            Expression.Block(
                                new Expression[]
                                    {
                                        // moveNext = enumerator.MoveNext();
                                        Expression.Assign(
                                            moveNextVar,
                                            Expression.Call(enumeratorParam, typeof (IEnumerator).GetMethod("MoveNext")))
                                        ,
                                        // if
                                        Expression.IfThen(
                                            // (!moveNext)
                                            Expression.IsFalse(moveNextVar),
                                            // break;
                                            Expression.Break(breakLabel)),
                                        // current = enumerator.Current;
                                        setCurrentExp,
                                        Expression.Block(
                                            // int codecPos;
                                            new[] {codecPosExp},
                                            // codecPos = codec.Count
                                            Expression.Assign(codecPosExp, codecBufferPos),
                                            // if (!writeDeltas)
                                            Expression.IfThen(
                                                Expression.Not(srl.Item2),
                                                Expression.Block(
                                                    // codec.Count = codecPos;
                                                    Expression.Assign(codecBufferPos, codecPosExp),
                                                    // break;
                                                    Expression.Break(breakLabel)
                                                    )
                                                )),
                                        // count++;
                                        Expression.PreIncrementAssign(countVar)
                                    }),
                            breakLabel),
                        // codec.FinishBlock();
                        Expression.Call(codecParam, "FinishBlock", null, countVar, moveNextVar),
                        // return moveNext;
                        moveNextVar
                    };
        }

        /// <summary>
        /// Generate void DeSerialize(CodecReader codec, Buffer&lt;T> result, int maxItemCount) method
        /// </summary>
        /// <remarks> 
        /// Generated code:
        /// 
        /// * codec to read the values from
        /// * result will get all the generated values
        /// * maxItemCount - maximum number of items to be deserialized
        /// 
        /// <![CDATA[
        /// void DeSerialize(CodecReader codec, Buffer<T> result, int maxItemCount)
        /// {
        ///     int count = codec.ReadHeader();
        ///     if (count > maxItemCount)
        ///         count = maxItemCount;
        ///     
        ///     var state1, state2, ...;
        /// 
        ///     result.Add(ReadField(codec, ref state));
        /// 
        ///     while (true) {
        /// 
        ///         count--;
        ///         if (count == 0)
        ///             break;
        /// 
        ///         result.Add(ReadField(codec, ref state));
        ///     }
        /// }]]>
        /// </remarks>
        private IEnumerable<Expression> GenerateDeSerializer(
            out ParameterExpression[] parameters,
            out ParameterExpression[] localVars)
        {
            // param: codec
            ParameterExpression codecParam = Expression.Parameter(typeof (CodecReader), "codec");

            // param: Buffer<T> result
            ParameterExpression resultParam = Expression.Parameter(typeof (Buffer<T>), "result");

            // param: int maxItemCount
            ParameterExpression maxItemCountParam = Expression.Parameter(typeof (int), "maxItemCount");

            // int count;
            ParameterExpression countVar = Expression.Variable(typeof (int), "count");

            LabelTarget breakLabel = Expression.Label("loopBreak");

            Tuple<Expression, Expression> srl = RootField.GetDeSerializer(codecParam);

            // parameter CodecReader codec, Buffer<T> result, int maxItemCount
            parameters = new[] {codecParam, resultParam, maxItemCountParam};

            localVars = new[] {countVar};

            return
                new Expression[]
                    {
                        // count = codec.ReadHeader();
                        Expression.Assign(countVar, Expression.Call(codecParam, "ReadHeader", null)),
                        // if (maxItemCount < count)
                        //     count = maxItemCount;
                        Expression.IfThen(
                            Expression.LessThan(maxItemCountParam, countVar),
                            Expression.Assign(countVar, maxItemCountParam)),
                        // result.Add(ReadField(codec, ref state));
                        Expression.Call(resultParam, "Add", null, srl.Item1),
                        // while (true)
                        Expression.Loop(
                            // {
                            Expression.Block(
                                new Expression[]
                                    {
                                        // count--;
                                        Expression.PreDecrementAssign(countVar),
                                        // if (
                                        Expression.IfThen(
                                            // count == 0)
                                            Expression.Equal(countVar, Expression.Constant(0)),
                                            // break;
                                            Expression.Break(breakLabel)),
                                        // result.Add(ReadField(codec, ref state));
                                        Expression.Call(resultParam, "Add", null, srl.Item2)
                                    }),
                            breakLabel)
                    };
        }

        public static DynamicSerializer<T> CreateFromReader(BinaryReader reader, Func<string, Type> typeResolver)
        {
            var srl = new DynamicSerializer<T>();
            srl.RootField = BaseField.FieldFromReader(srl, reader, typeResolver);
            srl.MakeReadonly();
            return srl;
        }

        public void WriteCustomHeader(BinaryWriter writer)
        {
            RootField.InitNew(writer);
            MakeReadonly();
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;

            var other = (DynamicSerializer<T>) obj;
            return RootField.Equals(other.RootField);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return RootField.GetHashCode();
            }
        }
    }
}