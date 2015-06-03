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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CassowaryNET.Constraints;
using CassowaryNET.Exceptions;
using CassowaryNET.Utils;
using CassowaryNET.Variables;

namespace CassowaryNET
{
    public sealed class CassowarySolver
    {
        #region Fields

        private const double Epsilon = 1e-8;

        private readonly Tableau tableau;
        private readonly ObjectiveVariable objective;

        /// <summary>
        /// The array of negative error vars for the stay constraints
        /// (need both positive and negative since they have only non-negative
        /// values).
        /// </summary>
        private readonly List<SlackVariable> stayMinusErrorVars;

        /// <summary>
        /// The array of positive error vars for the stay constraints
        /// (need both positive and negative since they have only non-negative
        /// values).
        /// </summary>
        private readonly List<SlackVariable> stayPlusErrorVars;

        /// <summary>
        /// Give error variables for a non-required constraints,
        /// maps to ClSlackVariable-s.
        /// </summary>
        /// <remarks>
        /// Map ClConstraint to Set (of ClVariable).
        /// </remarks>
        private readonly Dictionary<Constraint, HashSet<SlackVariable>> errorVariables;

        /// <summary>
        /// Return a lookup table giving the marker variable for
        /// each constraints (used when deleting a constraint).
        /// </summary>
        /// <remarks>
        /// Map ClConstraint to ClVariable.
        /// </remarks>
        private readonly Dictionary<Constraint, AbstractVariable> markerVariables;

        /// <summary>
        /// Map edit variables to ClEditInfo-s.
        /// </summary>
        /// <remarks>
        /// ClEditInfo instances contain all the information for an
        /// edit constraints (the edit plus/minus vars, the index [for old-style
        /// resolve(ArrayList...)] interface), and the previous value.
        /// (ClEditInfo replaces the parallel vectors from the Smalltalk impl.)
        /// </remarks>
        private readonly Dictionary<Variable, EditInfo> editVarMap;

        private readonly Stack<int> editVariablesCountStack;

        private bool needsSolving = false;

        #endregion

        #region Constructors

        /// <remarks>
        /// Constructor initializes the fields, and creaties the objective row.
        /// </remarks>
        public CassowarySolver()
        {
            tableau = new Tableau();

            objective = new ObjectiveVariable("Z");
            tableau.Rows.Add(objective, new LinearExpression(0d));

            stayMinusErrorVars = new List<SlackVariable>();
            stayPlusErrorVars = new List<SlackVariable>();
            errorVariables = new Dictionary<Constraint, HashSet<SlackVariable>>();
            markerVariables = new Dictionary<Constraint, AbstractVariable>();
            editVarMap = new Dictionary<Variable, EditInfo>();

            AutoSolve = true;

            editVariablesCountStack = new Stack<int>();
            editVariablesCountStack.Push(0);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Controls wether optimization and setting of external variables is done
        /// automatically or not.
        /// </summary>
        /// <remarks>
        /// By default it is done automatically and <see cref="Solve"/> never needs
        /// to be explicitly called by client code. If <see cref="AutoSolve"/> is
        /// put to false, then <see cref="Solve"/> needs to be invoked explicitly
        /// before using variables' values. 
        /// (Turning off <see cref="AutoSolve"/> while addings lots and lots
        /// of constraints [ala the AddDel test in ClTests] saved about 20 % in
        /// runtime, from 60sec to 54sec for 900 constraints, with 126 failed adds).
        /// </remarks>
        public bool AutoSolve { get; set; }

        #endregion

        #region Methods

        /// <summary>
        /// Convenience function for creating a linear inequality constraint.
        /// </summary>
        public void AddLowerBound(AbstractVariable variable, double lower)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            var constraint = new LinearInequality(
                variable,
                InequalityType.GreaterThanOrEqual,
                new LinearExpression(lower));
            AddConstraint(constraint);
        }

        /// <summary>
        /// Convenience function for creating a linear inequality constraint.
        /// </summary>
        public void AddUpperBound(AbstractVariable variable, double upper)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            var constraint = new LinearInequality(
                variable,
                InequalityType.LessThanOrEqual,
                new LinearExpression(upper));
            AddConstraint(constraint);
        }

        /// <summary>
        /// Convenience function for creating a pair of linear inequality constraints.
        /// </summary>
        public void AddBounds(AbstractVariable variable, double lower, double upper)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            AddLowerBound(variable, lower);
            AddUpperBound(variable, upper);
        }

        public void AddConstraint(EditConstraint constraint)
        {
            AddConstraintCore(constraint);
        }

        public void AddConstraint(StayConstraint constraint)
        {
            AddConstraintCore(constraint);
        }

        public void AddConstraint(LinearEquality constraint)
        {
            AddConstraintCore(constraint);
        }

        public void AddConstraint(LinearInequality constraint)
        {
            AddConstraintCore(constraint);
        }

        public void AddConstraint(Constraint constraint)
        /* throws ExClRequiredFailure, ExClInternalError */
        {
            // NOTE: really not sure whether I like this...

            if (constraint is EditConstraint)
                AddConstraint((EditConstraint)constraint);
            else if (constraint is StayConstraint)
                AddConstraint((StayConstraint)constraint);
            else if (constraint is LinearEquality)
                AddConstraint((LinearEquality)constraint);
            else if (constraint is LinearInequality)
                AddConstraint((LinearInequality)constraint);
            else
                throw new ArgumentException();
        }

        private void AddConstraintCore(Constraint constraint)
        {
            SlackVariable eplus;
            SlackVariable eminus;
            double prevEConstant;
            var expression = NewExpression(
                constraint,
                out eplus,
                out eminus,
                out prevEConstant);

            var addedDirectly = TryAddingDirectly(expression);
            if (!addedDirectly)
            {
                AddWithArtificialVariable(expression);
            }

            needsSolving = true;

            if (constraint.IsEditConstraint)
            {
                int i = editVarMap.Count;
                var cnEdit = (EditConstraint) constraint;

                editVarMap.Add(
                    cnEdit.Variable,
                    new EditInfo(
                        cnEdit,
                        eplus,
                        eminus,
                        prevEConstant,
                        i));
            }

            MaybeAutoSolve();
        }

        /// <summary>
        /// Add an edit constraint for a variable with a given strength.
        /// <param name="v">Variable to add an edit constraint to.</param>
        /// <param name="strength">Strength of the edit constraint.</param>
        /// </summary>
        public void AddEditVar(Variable v, Strength strength)
            /* throws ExClInternalError */
        {
            // we get problems here is strength == Required

            try
            {
                var cnEdit = new EditConstraint(v, strength);
                AddConstraint(cnEdit);
            }
            catch (CassowaryRequiredConstraintFailureException)
            {
                // should not get this
                throw new CassowaryInternalException(
                    "Required failure when adding an edit variable");
            }
        }

        /// <remarks>
        /// Add an edit constraint with strength ClStrength#Strong.
        /// </remarks>
        public void AddEditVar(Variable v)
        {
            /* throws ExClInternalError */
            AddEditVar(v, Strength.Strong);
        }

        /// <summary>
        /// Marks the start of an edit session.
        /// </summary>
        /// <remarks>
        /// BeginEdit should be called before sending Resolve()
        /// messages, after adding the appropriate edit variables.
        /// </remarks>
        public void BeginEdit()
            /* throws ExClInternalError */
        {
            // TODO: Make this return some form of IDisposable

            Debug.Assert(editVarMap.Count > 0, "_editVarMap.Count > 0");
            // may later want to do more in here
            tableau.InfeasibleRows.Clear();
            ResetStayConstants();
            editVariablesCountStack.Push(editVarMap.Count);
        }

        /// <summary>
        /// Marks the end of an edit session.
        /// </summary>
        /// <remarks>
        /// EndEdit should be called after editing has finished for now, it
        /// just removes all edit variables.
        /// </remarks>
        public void EndEdit()
            /* throws ExClInternalError */
        {
            Debug.Assert(editVarMap.Count > 0, "_editVarMap.Count > 0");
            Resolve();

            editVariablesCountStack.Pop();
            var n = editVariablesCountStack.Peek();

            RemoveEditVarsTo(n);
            // may later want to do more in hore
        }

        /// <summary>
        /// Remove the last added edit vars to leave only
        /// a specific number left.
        /// <param name="n">
        /// Number of edit variables to keep.
        /// </param>
        /// </summary>
        private void RemoveEditVarsTo(int n)
            /* throws ExClInternalError */
        {
            var toRemove = new List<Variable>();
            try
            {
                foreach (var v in editVarMap.Keys)
                {
                    var cei = editVarMap[v];
                    if (cei.Index >= n)
                    {
                        toRemove.Add(v);
                    }
                }

                foreach (var v in toRemove)
                {
                    RemoveEditVar(v);
                }

                Debug.Assert(editVarMap.Count == n, "_editVarMap.Count == n");
            }
            catch (CassowaryConstraintNotFoundException)
            {
                // should not get this
                throw new CassowaryInternalException(
                    "Constraint not found in RemoveEditVarsTo");
            }
        }

        /// <summary>
        /// Remove the edit constraint previously added.
        /// <param name="v">Variable to which the edit constraint was added before.</param>
        /// </summary>
        private void RemoveEditVar(Variable v)
            /* throws ExClInternalError, ExClConstraintNotFound */
        {
            var editInfo = editVarMap[v];
            var constraint = editInfo.Constraint;
            RemoveConstraint(constraint);
        }
        
        /// <summary>
        /// Add a stay of the given strength (default to ClStrength#Weak)
        /// of a variable to the tableau..
        /// <param name="v">
        /// Variable to add the stay constraint to.
        /// </param>
        /// </summary>
        public void AddStay(Variable v, Strength strength, double weight)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            var constraint = new StayConstraint(v, strength, weight);

            AddConstraint(constraint);
        }

        /// <remarks>
        /// Default to weight 1.0.
        /// </remarks>
        public void AddStay(Variable v, Strength strength)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            AddStay(v, strength, 1.0);
        }

        /// <remarks>
        /// Default to strength ClStrength#Weak.
        /// </remarks>
        public void AddStay(Variable v)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            AddStay(v, Strength.Weak, 1.0);
        }

        /// <summary>
        /// Remove a constraint from the tableau.
        /// Also remove any error variable associated with it.
        /// </summary>
        public void RemoveConstraint(Constraint constraint)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            needsSolving = true;

            ResetStayConstants();

            var slackVariables = errorVariables.GetOrDefault(constraint);
            if (slackVariables != null)
            {
                errorVariables.Remove(constraint);

                var objectiveRow = tableau.Rows[objective];

                foreach (var variable in slackVariables)
                {
                    var coeff = -constraint.Weight*
                                constraint.Strength.Weight.Value;

                    var variableRow = tableau.Rows.GetOrDefault(variable);

                    LinearExpression newObjectiveRow;
                    if (Equals(variableRow, null))
                    {
                        newObjectiveRow = objectiveRow + (variable*coeff);
                    }
                    else
                    {
                        // the error variable was in the basis
                        newObjectiveRow = objectiveRow + (variableRow*coeff);
                    }

                    // overwrite the objective row
                    tableau.Rows[objective] = newObjectiveRow;

                    var addedVariables = newObjectiveRow.Variables
                        .Except(objectiveRow.Variables);
                    var removedVariables = objectiveRow.Variables
                        .Except(newObjectiveRow.Variables);

                    foreach (var addedVariable in addedVariables)
                    {
                        tableau.NoteAddedVariable(addedVariable, objective);
                    }
                    foreach (var removedVariable in removedVariables)
                    {
                        tableau.NoteRemovedVariable(removedVariable, objective);
                    }
                }
            }

            var marker = markerVariables.GetOrDefault(constraint);
            if (Equals(marker, null))
                throw new CassowaryConstraintNotFoundException();

            markerVariables.Remove(constraint);

            if (Equals(tableau.RowExpression(marker), null))
            {
                // not in the basis, so need to do some more work
                var markerColumn = tableau.Columns[marker];

                // must pivot...
                AbstractVariable exitVar = null;
                double minRatio = 0d;

                foreach (var variable in markerColumn)
                {
                    if (variable.IsRestricted)
                    {
                        var rowExpression = tableau.RowExpression(variable);
                        var coefficient = rowExpression.CoefficientFor(marker);

                        if (coefficient >= 0d)
                            continue;

                        var ratio = -rowExpression.Constant/coefficient;
                        if (Equals(exitVar, null) || ratio < minRatio)
                        {
                            minRatio = ratio;
                            exitVar = variable;
                        }
                    }
                }

                if (Equals(exitVar, null))
                {
                    // no restriced variables in markerColumn 
                    // OR all coefficients >= 0
                    foreach (var variable in markerColumn)
                    {
                        if (!variable.IsRestricted)
                            continue;

                        var rowExpression = tableau.RowExpression(variable);
                        var coefficient = rowExpression.CoefficientFor(marker);

                        var ratio = rowExpression.Constant/coefficient;
                        if (Equals(exitVar, null) || ratio < minRatio)
                        {
                            minRatio = ratio;
                            exitVar = variable;
                        }
                    }
                }

                if (Equals(exitVar, null))
                {
                    if (markerColumn.Count == 0)
                    {
                        tableau.RemoveColumn(marker);
                    }
                    else
                    {
                        exitVar = markerColumn.FirstOrDefault();
                    }
                }

                if (!Equals(exitVar, null))
                {
                    Pivot(marker, exitVar);
                }
            }

            if (!Equals(tableau.RowExpression(marker), null))
            {
                tableau.RemoveRow(marker);
            }

            if (slackVariables != null)
            {
                foreach (var slackVariable in slackVariables)
                {
                    if (!Equals(slackVariable, marker))
                    {
                        tableau.RemoveColumn(slackVariable);
                    }
                }
            }

            if (constraint.IsStayConstraint)
            {
                if (slackVariables != null)
                {
                    for (int i = 0; i < stayPlusErrorVars.Count; i++)
                    {
                        slackVariables.Remove(stayPlusErrorVars[i]);
                        slackVariables.Remove(stayMinusErrorVars[i]);
                    }
                }
            }
            else if (constraint.IsEditConstraint)
            {
                Debug.Assert(slackVariables != null, "eVars != null");
                var editConstraint = (EditConstraint) constraint;
                var variable = editConstraint.Variable;
                var editInfo = editVarMap[variable];
                var clvEditMinus = editInfo.ClvEditMinus;
                tableau.RemoveColumn(clvEditMinus);
                editVarMap.Remove(variable);
            }

            MaybeAutoSolve();
        }

        private void MaybeAutoSolve()
        {
            if (!AutoSolve)
                return;

            Optimize(objective);
            SetExternalVariables();
        }

        /// <summary>
        /// Re-initialize this solver from the original constraints, thus
        /// getting rid of any accumulated numerical problems 
        /// </summary>
        /// <remarks>
        /// Actually, we haven't definitely observed any such problems yet.
        /// </remarks>
        public void Reset()
            /* throws ExClInternalError */
        {
            throw new CassowaryInternalException("Reset not implemented");
        }

        /// <summary>
        /// Re-solve the current collection of constraints, given the new
        /// values for the edit variables that have already been
        /// suggested (see <see cref="SuggestValue"/> method).
        /// </summary>
        public void Resolve()
            /* throws ExClInternalError */
        {
            DualOptimize();
            SetExternalVariables();
            tableau.InfeasibleRows.Clear();
            ResetStayConstants();
        }

        /// <summary>
        /// Suggest a new value for an edit variable. 
        /// </summary>
        /// <remarks>
        /// The variable needs to be added as an edit variable and 
        /// BeginEdit() needs to be called before this is called.
        /// The tableau will not be solved completely until after Resolve()
        /// has been called.
        /// </remarks>
        public void SuggestValue(Variable v, double x)
            /* throws ExClError */
        {
            var cei = editVarMap[v];
            if (cei == null)
            {
                Console.Error.WriteLine(
                    "SuggestValue for variable " + v +
                    ", but var is not an edit variable\n");

                throw new CassowaryException();
            }

            SlackVariable clvEditPlus = cei.ClvEditPlus;
            SlackVariable clvEditMinus = cei.ClvEditMinus;
            double delta = x - cei.PrevEditConstant;
            cei.PrevEditConstant = x;
            DeltaEditConstant(delta, clvEditPlus, clvEditMinus);
        }

        public void Solve()
            /* throws ExClInternalError */
        {
            if (needsSolving)
            {
                Optimize(objective);
                SetExternalVariables();
            }
        }

        public void SetEditedValue(Variable variable, double value)
            /* throws ExClInternalError */
        {
            if (!ContainsVariable(variable))
            {
                throw new InvalidOperationException();
                //variable.Value = value;
                //return;
            }

            if (MathHelper.Approx(value, variable.Value))
                return;

            AddEditVar(variable);
            BeginEdit();
            try
            {
                SuggestValue(variable, value);
            }
            catch (CassowaryException)
            {
                // just added it above, so we shouldn't get an error
                throw new CassowaryInternalException("Error in SetEditedValue");
            }
            EndEdit();
        }

        public bool ContainsVariable(Variable v)
            /* throws ExClInternalError */
        {
            return tableau.HasColumnKey(v) || !Equals(tableau.RowExpression(v), null);
        }

        public void AddVar(Variable v)
            /* throws ExClInternalError */
        {
            if (ContainsVariable(v))
                return;

            try
            {
                AddStay(v);
            }
            catch (CassowaryRequiredConstraintFailureException)
            {
                // cannot have a required failure, since we add w/ weak
                throw new CassowaryInternalException(
                    "Error in AddVar -- required failure is impossible");
            }
        }

        /// <summary>
        /// Returns information about the solver's internals.
        /// </summary>
        /// <remarks>
        /// Originally from Michael Noth <noth@cs.washington.edu>
        /// </remarks>
        /// <returns>
        /// String containing the information.
        /// </returns>
        public string GetInternalInfo()
        {
            string result = tableau.GetInternalInfo();

            result += "\nSolver info:\n";
            result += "Stay Error Variables: ";
            result += stayPlusErrorVars.Count + stayMinusErrorVars.Count;
            result += " (" + stayPlusErrorVars.Count + " +, ";
            result += stayMinusErrorVars.Count + " -)\n";
            result += "Edit Variables: " + editVarMap.Count;
            result += "\n";

            return result;
        }

        public override string ToString()
        {
            string result = tableau.ToString();

            result += "\n_stayPlusErrorVars: ";
            result += stayPlusErrorVars;
            result += "\n_stayMinusErrorVars: ";
            result += stayMinusErrorVars;
            result += "\n";

            return result;
        }

        /// <summary>
        /// Add the constraint expr=0 to the inequality tableau using an
        /// artificial variable.
        /// </summary>
        /// <remarks>
        /// To do this, create an artificial variable av and add av=expr
        /// to the inequality tableau, then make av be 0 (raise an exception
        /// if we can't attain av=0).
        /// </remarks>
        private void AddWithArtificialVariable(LinearExpression expression)
            /* throws ExClRequiredFailure, ExClInternalError */
        {
            var av = new SlackVariable("a");
            var az = new ObjectiveVariable("az");

            var expressionClone = Cloneable.Clone(expression);

            tableau.AddRow(az, expressionClone);
            tableau.AddRow(av, expression);

            Optimize(az);

            var azRowExpression = tableau.RowExpression(az);

            if (!MathHelper.Approx(azRowExpression.Constant, 0.0))
            {
                tableau.RemoveRow(az);
                tableau.RemoveColumn(av);
                throw new CassowaryRequiredConstraintFailureException();
            }

            // see if av is a basic variable
            var avRowExpression = tableau.RowExpression(av);

            if (!Equals(avRowExpression, null))
            {
                // find another variable in this row and pivot,
                // so that av becomes parametric
                if (avRowExpression.IsConstant)
                {
                    // if there isn't another variable in the row
                    // then the tableau contains the equation av=0 --
                    // just delete av's row
                    tableau.RemoveRow(av);
                    tableau.RemoveRow(az);
                    return;
                }

                var entryVar = avRowExpression.GetAnyPivotableVariable();
                Pivot(entryVar, av);
            }

            Debug.Assert(Equals(tableau.RowExpression(av), null), "tableau.RowExpression(av) == null)");

            tableau.RemoveColumn(av);
            tableau.RemoveRow(az);
        }

        /// <summary>
        /// Try to add expr directly to the tableau without creating an
        /// artificial variable.
        /// </summary>
        /// <remarks>
        /// We are trying to add the constraint expr=0 to the appropriate
        /// tableau.
        /// </remarks>
        /// <returns>
        /// True if successful and false if not.
        /// </returns>
        private bool TryAddingDirectly(LinearExpression expression)
            /* throws ExClRequiredFailure */
        {
            var subject = ChooseSubject(expression);
            if (Equals(subject, null))
            {
                return false;
            }

            expression = expression.WithSubject(subject);
            if (tableau.HasColumnKey(subject))
            {
                tableau.SubstituteOut(subject, expression);
            }

            tableau.AddRow(subject, expression);

            return true; // succesfully added directly
        }

        /// <summary>
        /// Try to choose a subject (a variable to become basic) from
        /// among the current variables in expr.
        /// </summary>
        /// <remarks>
        /// We are trying to add the constraint expr=0 to the tableaux.
        /// If expr constains any unrestricted variables, then we must choose
        /// an unrestricted variable as the subject. Also if the subject is
        /// new to the solver, we won't have to do any substitutions, so we
        /// prefer new variables to ones that are currently noted as parametric.
        /// If expr contains only restricted variables, if there is a restricted
        /// variable with a negative coefficient that is new to the solver we can
        /// make that the subject. Otherwise we can't find a subject, so return nil.
        /// (In this last case we have to add an artificial variable and use that
        /// variable as the subject -- this is done outside this method though.)
        /// </remarks>
        private AbstractVariable ChooseSubject(LinearExpression expression)
            /* ExClRequiredFailure */
        {
            AbstractVariable subject = null; // the current best subject, if any

            bool foundUnrestricted = false;
            bool foundNewRestricted = false;

            foreach (var v in expression.Terms.Keys)
            {
                double c = expression.Terms[v];

                if (foundUnrestricted)
                {
                    if (!v.IsRestricted)
                    {
                        if (!tableau.HasColumnKey(v))
                        {
                            return v;
                        }
                    }
                }
                else
                {
                    // we haven't found an restricted variable yet
                    if (v.IsRestricted)
                    {
                        if (!foundNewRestricted && !v.IsDummy && c < 0.0)
                        {
                            var col = tableau.Columns.GetOrDefault(v);

                            if (col == null ||
                                (col.Count == 1 && tableau.HasColumnKey(objective)))
                            {
                                subject = v;
                                foundNewRestricted = true;
                            }
                        }
                    }
                    else
                    {
                        subject = v;
                        foundUnrestricted = true;
                    }
                }
            }

            if (!Equals(subject, null))
            {
                return subject;
            }

            double coeff = 0.0;

            foreach (var v in expression.Terms.Keys)
            {
                double c = expression.Terms[v];

                if (!v.IsDummy)
                {
                    return null; // nope, no luck
                }

                if (!tableau.HasColumnKey(v))
                {
                    subject = v;
                    coeff = c;
                }
            }

            if (!MathHelper.Approx(expression.Constant, 0.0))
            {
                throw new CassowaryRequiredConstraintFailureException();
            }
            
            return subject;
        }

        private LinearExpression NewExpression(
            Constraint cn,
            out SlackVariable eplus_,
            out SlackVariable eminus_,
            out double prevEConstant)
        {
            eplus_ = null;
            eminus_ = null;
            prevEConstant = 0d;

            var cnExpr = cn.Expression;
            var expr = new LinearExpression(cnExpr.Constant);

            foreach (AbstractVariable v in cnExpr.Terms.Keys)
            {
                double c = cnExpr.Terms[v];
                LinearExpression e = tableau.RowExpression(v);
                if (Equals(e, null))
                {
                    expr = expr + (v*c);
                }
                else
                {
                    expr = expr + (c*e);
                }
            }

            if (cn.IsInequality)
            {
                var slackVar = new SlackVariable("s");
                expr = expr.WithVariableSetTo(slackVar, -1);
                markerVariables.Add(cn, slackVar);

                if (cn.Strength != Strength.Required)
                {
                    var eminus = new SlackVariable("em");
                    expr = expr.WithVariableSetTo(eminus, 1.0);
                    var sw = cn.Strength.Weight.Value * cn.Weight;

                    var zRow = tableau.Rows[objective];
                    zRow = zRow.WithVariableSetTo(eminus, sw);
                    tableau.Rows[objective] = zRow;

                    InsertErrorVar(cn, eminus);
                    tableau.NoteAddedVariable(eminus, objective);
                }
            }
            else
            {
                // cn is an equality
                if (cn.Strength == Strength.Required)
                {
                    var dummyVar = new DummyVariable("d");
                    expr = expr.WithVariableSetTo(dummyVar, 1.0);
                    markerVariables.Add(cn, dummyVar);
                }
                else
                {
                    var eplus = new SlackVariable("ep");
                    var eminus = new SlackVariable("em");

                    expr = expr.WithVariableSetTo(eplus, -1.0);
                    expr = expr.WithVariableSetTo(eminus, 1.0);
                    markerVariables.Add(cn, eplus);
                    double swCoeff = cn.Strength.Weight.Value * cn.Weight;

                    var zRow = tableau.Rows[objective];
                    zRow = zRow.WithVariableSetTo(eplus, swCoeff);
                    zRow = zRow.WithVariableSetTo(eminus, swCoeff);
                    tableau.Rows[objective] = zRow;

                    tableau.NoteAddedVariable(eplus, objective);
                    tableau.NoteAddedVariable(eminus, objective);

                    InsertErrorVar(cn, eminus);
                    InsertErrorVar(cn, eplus);

                    if (cn.IsStayConstraint)
                    {
                        stayPlusErrorVars.Add(eplus);
                        stayMinusErrorVars.Add(eminus);
                    }
                    else if (cn.IsEditConstraint)
                    {
                        eplus_ = eplus;
                        eminus_ = eminus;

                        prevEConstant = cnExpr.Constant;
                    }
                }
            }

            if (expr.Constant < 0)
            {
                expr = -expr;
            }

            return expr;
        }

        /// <summary>
        /// Minimize the value of the objective.
        /// </summary>
        /// <remarks>
        /// The tableau should already be feasible.
        /// </remarks>
        private void Optimize(ObjectiveVariable zVariable)
            /* throws ExClInternalError */
        {
            var zRow = tableau.RowExpression(zVariable);
            Debug.Assert(!Equals(zRow, null), "zRow != null");

            // NOTE: should this be inside the loop?
            AbstractVariable entryVariable = null;

            while (true)
            {
                var objectiveCoeff = 0d;
                foreach (var v in zRow.Terms.Keys)
                {
                    double c = zRow.Terms[v];
                    if (v.IsPivotable && c < objectiveCoeff)
                    {
                        objectiveCoeff = c;
                        entryVariable = v;
                    }
                }

                if (objectiveCoeff >= -Epsilon || Equals(entryVariable, null))
                    return;

                var exitVariable = GetExitVariable(entryVariable);

                Debug.Assert(!Equals(exitVariable, null));

                Pivot(entryVariable, exitVariable);
            }
        }

        private AbstractVariable GetExitVariable(AbstractVariable entryVariable)
        {
            AbstractVariable exitVariable = null;
            var minRatio = double.MaxValue;
            var minFound = false;

            var columnVariables = tableau.Columns[entryVariable];
            foreach (var variable in columnVariables)
            {
                if (!variable.IsPivotable)
                    continue;

                var rowExpression = tableau.RowExpression(variable);
                var coefficient = rowExpression.CoefficientFor(entryVariable);

                if (coefficient >= 0d)
                    continue;

                var ratio = - rowExpression.Constant/coefficient;
                if (ratio < minRatio)
                {
                    minFound = true;
                    minRatio = ratio;
                    exitVariable = variable;
                }
            }

            if (!minFound)
            {
                throw new CassowaryInternalException(
                    "Objective function is unbounded in Optimize");
            }

            return exitVariable;
        }

        /// <summary>
        /// Fix the constants in the equations representing the edit constraints.
        /// </summary>
        /// <remarks>
        /// Each of the non-required edits will be represented by an equation
        /// of the form:
        ///   v = c + eplus - eminus
        /// where v is the variable with the edit, c is the previous edit value,
        /// and eplus and eminus are slack variables that hold the error in 
        /// satisfying the edit constraint. We are about to change something,
        /// and we want to fix the constants in the equations representing
        /// the edit constraints. If one of eplus and eminus is basic, the other
        /// must occur only in the expression for that basic error variable. 
        /// (They can't both be basic.) Fix the constant in this expression.
        /// Otherwise they are both non-basic. Find all of the expressions
        /// in which they occur, and fix the constants in those. See the
        /// UIST paper for details.
        /// (This comment was for ResetEditConstants(), but that is now
        /// gone since it was part of the screwey vector-based interface
        /// to resolveing. --02/16/99 gjb)
        /// </remarks>
        private void DeltaEditConstant(
            double delta,
            AbstractVariable plusErrorVariable,
            AbstractVariable minusErrorVariable)
        {
            var plusErrorRow = tableau.Rows.GetOrDefault(plusErrorVariable);
            if (!Equals(plusErrorRow, null))
            {
                plusErrorRow = plusErrorRow.WithConstantIncrementedBy(delta);
                tableau.Rows[plusErrorVariable] = plusErrorRow;

                if (plusErrorRow.Constant < 0d)
                {
                    tableau.InfeasibleRows.Add(plusErrorVariable);
                }
                return;
            }

            var minusErrorRow = tableau.Rows.GetOrDefault(minusErrorVariable);
            if (!Equals(minusErrorRow, null))
            {
                minusErrorRow = minusErrorRow.WithConstantIncrementedBy(-delta);
                tableau.Rows[minusErrorVariable] = minusErrorRow;

                if (minusErrorRow.Constant < 0d)
                {
                    tableau.InfeasibleRows.Add(minusErrorVariable);
                }
                return;
            }

            var minusErrorColumn = tableau.Columns[minusErrorVariable];
            foreach (var variable in minusErrorColumn)
            {
                var variableRow = tableau.Rows[variable];

                var coefficient = variableRow.CoefficientFor(minusErrorVariable);
                variableRow = variableRow.WithConstantIncrementedBy(coefficient*delta);
                tableau.Rows[variable] = variableRow;

                if (variable.IsRestricted && variableRow.Constant < 0d)
                {
                    tableau.InfeasibleRows.Add(variable);
                }
            }
        }

        /// <summary>
        /// Re-optimize using the dual simplex algorithm.
        /// </summary>
        /// <remarks>
        /// We have set new values for the constants in the edit constraints.
        /// </remarks>
        private void DualOptimize()
            /* throws ExClInternalError */
        {
            LinearExpression zRow = tableau.RowExpression(objective);

            while (tableau.InfeasibleRows.Count != 0)
            {
                var enumIfRows = tableau.InfeasibleRows.GetEnumerator();
                enumIfRows.MoveNext();
                var exitVar = enumIfRows.Current;

                tableau.InfeasibleRows.Remove(exitVar);
                AbstractVariable entryVar = null;
                LinearExpression expr = tableau.RowExpression(exitVar);

                if (Equals(expr, null))
                    continue;

                if (expr.Constant >= 0d)
                    continue;

                double ratio = Double.MaxValue;
                double r;

                foreach (AbstractVariable v in expr.Terms.Keys)
                {
                    double c = expr.Terms[v];
                    if (c > 0.0 && v.IsPivotable)
                    {
                        double zc = zRow.CoefficientFor(v);
                        r = zc/c; // FIXME: zc / c or zero, as ClSymbolicWeigth-s

                        if (r < ratio)
                        {
                            entryVar = v;
                            ratio = r;
                        }
                    }
                }

                if (ratio == double.MaxValue)
                {
                    throw new CassowaryInternalException(
                        "ratio == nil (Double.MaxValue) in DualOptimize");
                }

                Pivot(entryVar, exitVar);
            }
        }

        /// <summary>
        /// Do a pivot. Move entryVar into the basis and move exitVar 
        /// out of the basis.
        /// </summary>
        /// <remarks>
        /// We could for example make entryVar a basic variable and
        /// make exitVar a parametric variable.
        /// </remarks>
        private void Pivot(
            AbstractVariable entryVariable,
            AbstractVariable exitVariable)
            /* throws ExClInternalError */
        {
            // the entryVar might be non-pivotable if we're doing a 
            // RemoveConstraint -- otherwise it should be a pivotable
            // variable -- enforced at call sites, hopefully

            var expression = tableau.RemoveRow(exitVariable);

            expression = expression.WithSubjectChangedTo(exitVariable, entryVariable);

            tableau.SubstituteOut(entryVariable, expression);
            tableau.AddRow(entryVariable, expression);
        }

        /// <summary>
        /// Fix the constants in the equations representing the stays.
        /// </summary>
        /// <remarks>
        /// Each of the non-required stays will be represented by an equation
        /// of the form
        ///   v = c + eplus - eminus
        /// where v is the variable with the stay, c is the previous value
        /// of v, and eplus and eminus are slack variables that hold the error
        /// in satisfying the stay constraint. We are about to change something,
        /// and we want to fix the constants in the equations representing the
        /// stays. If both eplus and eminus are nonbasic they have value 0
        /// in the current solution, meaning the previous stay was exactly
        /// satisfied. In this case nothing needs to be changed. Otherwise one
        /// of them is basic, and the other must occur only in the expression
        /// for that basic error variable. Reset the constant of this
        /// expression to 0.
        /// </remarks>
        private void ResetStayConstants()
        {
            for (int i = 0; i < stayPlusErrorVars.Count; i++)
            {
                {
                    var stayPlusErrorVar = stayPlusErrorVars[i];
                    var expression = tableau.Rows.GetOrDefault(stayPlusErrorVar);
                    if (!Equals(expression, null))
                    {
                        expression = expression.WithConstantSetTo(0d);
                        tableau.Rows[stayPlusErrorVar] = expression;
                        continue;
                    }
                }

                {
                    var stayMinusErrorVar = stayMinusErrorVars[i];
                    var expression = tableau.Rows.GetOrDefault(stayMinusErrorVar);
                    if (!Equals(expression, null))
                    {
                        expression = expression.WithConstantSetTo(0d);
                        tableau.Rows[stayMinusErrorVar] = expression;
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Set the external variables known to this solver to their appropriate values.
        /// </summary>
        /// <remarks>
        /// Set each external basic variable to its value, and set each external parametric
        /// variable to 0. (It isn't clear that we will ever have external parametric
        /// variables -- every external variable should either have a stay on it, or have an
        /// equation that defines it in terms of other external variables that do have stays.
        /// For the moment I'll put this in though.) Variables that are internal to the solver
        /// don't actually store values -- their values are just implicit in the tableau -- so
        /// we don't need to set them.
        /// </remarks>
        private void SetExternalVariables()
        {
            foreach (var variable in tableau.ExternalParametricVars)
            {
                var rowExpression = tableau.RowExpression(variable);

                if (!Equals(rowExpression, null))
                {
                    Debug.WriteLine(
                        "Error: variable {0}in _externalParametricVars is basic",
                        variable);
                    continue;
                }

                variable.Value = 0d;
            }

            foreach (var variable in tableau.ExternalRows)
            {
                var rowExpression = tableau.RowExpression(variable);
                variable.Value = rowExpression.Constant;
            }

            needsSolving = false;
        }

        /// <summary>
        /// Protected convenience function to insert an error variable
        /// into the _errorVars set, creating the mapping with Add as necessary.
        /// </summary>
        private void InsertErrorVar(Constraint constraint, SlackVariable variable)
        {
            HashSet<SlackVariable> constraintVariables;
            if (!errorVariables.TryGetValue(constraint, out constraintVariables))
            {
                constraintVariables = new HashSet<SlackVariable>();
                errorVariables.Add(constraint, constraintVariables);
            }

            constraintVariables.Add(variable);
        }

        #endregion
    }
}