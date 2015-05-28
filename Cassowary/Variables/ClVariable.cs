/*
  Cassowary.net: an incremental constraint solver for .NET
  (http://lumumba.uhasselt.be/jo/projects/cassowary.net/)
  
  Copyright (C) 2005-2006  Jo Vermeulen (jo.vermeulen@uhasselt.be)
  
  This program is free software; you can redistribute it and/or
  modify it under the terms of the GNU Lesser General Public License
  as published by the Free Software Foundation; either version 2.1
  of  the License, or (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
  GNU Lesser General Public License for more details.

  You should have received a copy of the GNU Lesser General Public License
  along with this program; if not, write to the Free Software
  Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using System;
using Cassowary.Constraints;

namespace Cassowary.Variables
{
#pragma warning disable 660,661
    // We are heavily using operator overloading here
    public sealed class ClVariable : ClAbstractVariable
#pragma warning restore 660,661
    {
        #region Fields

        private double value;

        #endregion

        #region Constructors

        public ClVariable()
            : this(0d)
        {
        }

        public ClVariable(double value)
            : base()
        {
            this.value = value;
        }

        public ClVariable(string name)
            : this(name, 0d)
        {
        }

        public ClVariable(string name, double value)
            : base(name)
        {
            this.value = value;
        }

        #endregion

        #region Properties

        /// <remarks>
        /// Change the value held -- should *not* use this if the variable is 
        /// in a solver -- instead use AddEditVar() and SuggestValue() interface
        /// </remarks>
        public double Value
        {
            get { return value; }
            set { this.value = value; }
        }

        public override bool IsDummy
        {
            get { return false; }
        }

        public override bool IsExternal
        {
            get { return true; }
        }

        public override bool IsPivotable
        {
            get { return false; }
        }

        public override bool IsRestricted
        {
            get { return false; }
        }

        #endregion

        #region Methods

        public override string ToString()
        {
            return string.Format("[{0}:{1}]", Name, value);
        }

        #endregion

        #region Operators

        #region +

        public static ClLinearExpression operator +(
            ClVariable a,
            ClVariable b)
        {
            return new ClLinearExpression(a) + new ClLinearExpression(b);
        }

        public static ClLinearExpression operator +(
            ClVariable a,
            double b)
        {
            return new ClLinearExpression(a) + new ClLinearExpression(b);
        }

        public static ClLinearExpression operator +(
            double a,
            ClVariable b)
        {
            return new ClLinearExpression(a) + new ClLinearExpression(b);
        }

        #endregion

        #region -

        public static ClLinearExpression operator -(
            ClVariable a,
            ClVariable b)
        {
            return new ClLinearExpression(a) - new ClLinearExpression(b);
        }

        public static ClLinearExpression operator -(
            ClVariable a,
            double b)
        {
            return new ClLinearExpression(a) - new ClLinearExpression(b);
        }

        public static ClLinearExpression operator -(
            double a,
            ClVariable b)
        {
            return new ClLinearExpression(a) - new ClLinearExpression(b);
        }

        #endregion

        #region *

        public static ClLinearExpression operator *(
            ClVariable a,
            double b)
        {
            return new ClLinearExpression(a, b);
        }

        public static ClLinearExpression operator *(
            double a,
            ClVariable b)
        {
            return new ClLinearExpression(b, a);
        }

        #endregion

        #region /

        public static ClLinearExpression operator /(
            ClVariable a,
            double b)
        {
            return new ClLinearExpression(a) / b;
        }

        #endregion

        #region ==

        public static ClLinearEquation operator ==(
            ClVariable a,
            ClVariable b)
        {
            var aExpression = new ClLinearExpression(a);
            var bExpression = new ClLinearExpression(b);
            return new ClLinearEquation(aExpression, bExpression);
        }

        public static ClLinearEquation operator !=(
            ClVariable a,
            ClVariable b)
        {
            throw new NotImplementedException();
        }

        public static ClLinearEquation operator ==(
            ClVariable a,
            double b)
        {
            var bExpression = new ClLinearExpression(b);
            return new ClLinearEquation(a, bExpression);
        }

        public static ClLinearEquation operator !=(
            ClVariable a,
            double b)
        {
            throw new NotImplementedException();
        }

        public static ClLinearEquation operator ==(
            double a,
            ClVariable b)
        {
            var aExpression = new ClLinearExpression(a);
            return new ClLinearEquation(aExpression, b);
        }

        public static ClLinearEquation operator !=(
            double a,
            ClVariable b)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region <= and >=

        public static ClLinearInequality operator <=(
            ClVariable a,
            ClVariable b)
        {
            return new ClLinearInequality(a, InequalityType.LessThanOrEqual, b);
        }

        public static ClLinearInequality operator >=(
            ClVariable a,
            ClVariable b)
        {
            return new ClLinearInequality(a, InequalityType.GreaterThanOrEqual, b);
        }

        public static ClLinearInequality operator <=(
            ClVariable a,
            double b)
        {
            var bExpression = new ClLinearExpression(b);
            return new ClLinearInequality(a, InequalityType.LessThanOrEqual, bExpression);
        }

        public static ClLinearInequality operator >=(
            ClVariable a,
            double b)
        {
            var bExpression = new ClLinearExpression(b);
            return new ClLinearInequality(a, InequalityType.GreaterThanOrEqual, bExpression);
        }

        public static ClLinearInequality operator <=(
            double a,
            ClVariable b)
        {
            var aExpression = new ClLinearExpression(a);
            return new ClLinearInequality(aExpression, InequalityType.LessThanOrEqual, b);
        }

        public static ClLinearInequality operator >=(
            double a,
            ClVariable b)
        {
            var aExpression = new ClLinearExpression(a);
            return new ClLinearInequality(aExpression, InequalityType.GreaterThanOrEqual, b);
        }

        #endregion

        #endregion
    }
}