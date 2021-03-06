﻿#region COPYRIGHT

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
using JetBrains.Annotations;

namespace NYurik.TimeSeriesDb
{
    /// <summary>
    /// The implementor of this interface can execute <see cref="IGenericCallable{TDst,TArg}.Run{T}"/> method.
    /// </summary>
    public interface IGenericInvoker
    {
        /// <summary>
        /// Calls a factory method without explicitly specifying the type of the sub-item.
        /// </summary>
        TDst RunGenericMethod<TDst, TArg>([NotNull] IGenericCallable<TDst, TArg> callable, TArg arg);
    }

    /// <summary>
    /// This interface is used to run a generic method without referencing the generic subtype
    /// </summary>
    public interface IGenericCallable<out TDst, in TArg>
    {
        TDst Run<T>([NotNull] IGenericInvoker source, TArg arg);
    }

    /// <summary>
    /// The implementor of this interface can execute <see cref="IGenericCallable2{TDst,TArg}.Run{T1,T2}"/> method.
    /// </summary>
    public interface IGenericInvoker2
    {
        /// <summary>
        /// Calls a factory method without explicitly specifying the two types of the sub-items.
        /// </summary>
        TDst RunGenericMethod<TDst, TArg>([NotNull] IGenericCallable2<TDst, TArg> callable, TArg arg);
    }

    /// <summary>
    /// This interface is used to run a generic method without referencing two generic subtypes
    /// </summary>
    public interface IGenericCallable2<out TDst, in TArg>
    {
        TDst Run<TInd, TVal>([NotNull] IGenericInvoker2 source, TArg arg)
            where TInd : IComparable<TInd>;
    }
}