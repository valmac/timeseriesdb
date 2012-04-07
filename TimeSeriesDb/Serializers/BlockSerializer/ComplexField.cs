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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using JetBrains.Annotations;
using NYurik.TimeSeriesDb.Common;

namespace NYurik.TimeSeriesDb.Serializers.BlockSerializer
{
    public class ComplexField : BaseField
    {
        private IList<SubFieldInfo> _fields;

        [UsedImplicitly]
        protected ComplexField()
        {
        }

        public ComplexField([NotNull] IStateStore stateStore, [NotNull] Type fieldType, string stateName)
            : base(Versions.Ver0, stateStore, fieldType, stateName)
        {
            if (fieldType.IsArray || fieldType.IsPrimitive)
                throw new SerializerException("Unsupported type {0}", fieldType);

            FieldInfo[] fis = fieldType.GetFields(TypeUtils.AllInstanceMembers);
            _fields = new List<SubFieldInfo>(fis.Length);
            foreach (FieldInfo fi in fis)
            {
                string name = stateName + "." + fi.Name;

                if (fi.FieldType.IsNested)
                {
                    object[] ca = fi.GetCustomAttributes(typeof (FixedBufferAttribute), false);
                    if (ca.Length > 0)
                    {
                        // ((FixedBufferAttribute)ca[0]).Length;
                        throw new NotImplementedException("Fixed arrays are not supported at this time");
                    }
                }

                BaseField fld = stateStore.CreateField(fi.FieldType, name, true);
                _fields.Add(new SubFieldInfo(fi, fld));
            }
        }

        public SubFieldInfo this[string memberInfoName]
        {
            get { return Fields.FirstOrDefault(i => i.MemberInfo.Name == memberInfoName); }
            set
            {
                ThrowOnInitialized();
                for (int i = 0; i < Fields.Count; i++)
                {
                    if (Fields[i].MemberInfo.Name == memberInfoName)
                    {
                        if (value == null)
                            Fields.RemoveAt(i);
                        else
                            Fields[i] = value;
                        return;
                    }
                }

                if (value != null)
                    Fields.Add(value);
            }
        }

        public IList<SubFieldInfo> Fields
        {
            get { return _fields; }
            set
            {
                ThrowOnInitialized();
                _fields = value.ToList();
            }
        }

        public override int MaxByteSize
        {
            get { return _fields.Sum(fld => fld.Field.MaxByteSize); }
        }

        protected override void InitNewField(BinaryWriter writer)
        {
            base.InitNewField(writer);

            writer.Write(_fields.Count);
            foreach (SubFieldInfo field in _fields)
                field.InitNew(writer);
        }

        protected override void InitExistingField(BinaryReader reader, Func<string, Type> typeResolver)
        {
            base.InitExistingField(reader, typeResolver);

            var fields = new SubFieldInfo[reader.ReadInt32()];
            for (int i = 0; i < fields.Length; i++)
                fields[i] = new SubFieldInfo(StateStore, reader, typeResolver);
            _fields = fields;
        }

        protected override bool IsValidVersion(Version ver)
        {
            return ver == Versions.Ver0;
        }

        protected override void MakeReadonly()
        {
            _fields = new ReadOnlyCollection<SubFieldInfo>(_fields);
            base.MakeReadonly();
        }

        protected override Tuple<Expression, Expression> GetSerializerExp(Expression valueExp, Expression codec)
        {
            // result = writeDelta1() && writeDelta2() && ...
            var initExp = new List<Expression>();

            Expression nextExp = null;
            foreach (SubFieldInfo member in _fields)
            {
                Tuple<Expression, Expression> t = member.Field.GetSerializer(
                    Expression.MakeMemberAccess(valueExp, member.MemberInfo), codec);

                initExp.Add(t.Item1);

                Expression exp = Expression.IsTrue(t.Item2);
                nextExp = nextExp == null ? exp : Expression.And(nextExp, exp);
            }

            return
                new Tuple<Expression, Expression>(
                    Expression.Block(initExp),
                    nextExp ?? Expression.Constant(true));
        }

        protected override Tuple<Expression, Expression> GetDeSerializerExp(Expression codec)
        {
            // T current;
            ParameterExpression currentVar = Expression.Variable(FieldType, "current");

            // (class)  T current = (T) FormatterServices.GetUninitializedObject(typeof(T));
            // (struct) T current = default(T);
            BinaryExpression assignNewT = Expression.Assign(
                currentVar,
                FieldType.IsValueType
                    ? (Expression) Expression.Default(FieldType)
                    : Expression.Convert(
                        Expression.Call(
                            typeof (FormatterServices), "GetUninitializedObject", null,
                            Expression.Constant(FieldType)), FieldType));

            var readAllInit = new List<Expression> {assignNewT};
            var readAllNext = new List<Expression> {assignNewT};

            foreach (SubFieldInfo member in _fields)
            {
                Tuple<Expression, Expression> srl = member.Field.GetDeSerializer(codec);

                Expression field = Expression.MakeMemberAccess(currentVar, member.MemberInfo);
                readAllInit.Add(Expression.Assign(field, srl.Item1));
                readAllNext.Add(Expression.Assign(field, srl.Item2));
            }

            readAllInit.Add(currentVar);
            readAllNext.Add(currentVar);

            return new Tuple<Expression, Expression>(
                Expression.Block(new[] {currentVar}, readAllInit),
                Expression.Block(new[] {currentVar}, readAllNext));
        }

        protected override bool Equals(BaseField baseOther)
        {
            return _fields.SequenceEqual(((ComplexField) baseOther)._fields);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                // ReSharper disable NonReadonlyFieldInGetHashCode
                var hashCode = base.GetHashCode();
                foreach (var f in _fields)
                    hashCode = (hashCode*397) ^ f.GetHashCode();
                return hashCode;
                // ReSharper restore NonReadonlyFieldInGetHashCode
            }
        }
    }
}